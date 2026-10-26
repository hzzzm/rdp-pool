using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpPool.Config;

namespace RdpPoolClient;

/// <summary>
/// MsTscAx ActiveX：ClearTextPassword 注入。
/// <para>
/// 全屏：Connect 后 FullScreen=true（Win/Alt+Tab 进远程）+ 藏起宿主任务栏项。
/// 连接栏「窗口化」：FullScreen→false 时立刻还原为可见的 Sizable 宿主（有关闭按钮），
/// 避免 Ax 画在 Opacity=0 的隐形全屏 Form 上卡住。
/// </para>
/// </summary>
internal static class RdpAxSession
{
    static readonly string[] ProgIds =
    {
        "MsTscAx.MsTscAx.12", "MsTscAx.MsTscAx.11", "MsTscAx.MsTscAx.10",
        "MsTscAx.MsTscAx.9", "MsTscAx.MsTscAx.8", "MsTscAx.MsTscAx"
    };

    public static bool TryConnect(
        string host, int port, string username, string password, int holdConnectedSec,
        out string detail, DisplayApply? display = null)
    {
        detail = "";
        Type? prog = null;
        string? used = null;
        foreach (var id in ProgIds)
        {
            var t = Type.GetTypeFromProgID(id);
            if (t == null) continue;
            try
            {
                var probe = Activator.CreateInstance(t);
                if (probe != null)
                {
                    Marshal.FinalReleaseComObject(probe);
                    prog = t;
                    used = id;
                    break;
                }
            }
            catch
            {
                // ClassFactory unavailable — try next
            }
        }
        if (prog == null)
        {
            detail = "MsTscAx ProgID not creatable";
            return false;
        }

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch { /* may already be set */ }

        var localW = 1920;
        var localH = 1080;
        Rectangle screenBounds = default;
        try
        {
            screenBounds = Screen.PrimaryScreen?.Bounds ?? default;
            if (screenBounds.Width > 0 && screenBounds.Height > 0)
            {
                localW = screenBounds.Width;
                localH = screenBounds.Height;
            }
        }
        catch { /* keep defaults */ }

        var apply = display ?? LocalDisplay.Resolve(LocalDisplay.FullScreen, localW, localH);
        var wantOcxFullscreen = apply.FullScreen;
        var deskW = wantOcxFullscreen ? localW : apply.Width;
        var deskH = wantOcxFullscreen ? localH : apply.Height;

        var localDetail = "progid=" + used +
                          $"; display={(wantOcxFullscreen ? $"ocx-fullscreen {deskW}x{deskH}" : $"{deskW}x{deskH}")}";
        Exception? connectError = null;
        var connected = false;
        var hostHidden = false;
        var ocxFsActive = false;
        var closing = false;
        var userConfirmedClose = false;
        var confirmUiOpen = false; // MessageBox 期间禁止 poll 把全屏误判成「窗口化」
        // MessageBox 抢焦点后短暂禁止「FullScreen=true → 自动进全屏」（窗口化取消不得副作用回全屏）
        long suppressAutoFullscreenUntilMs = 0;
        // 冒烟自动退出 / 无 UI：不弹确认
        var skipCloseConfirm =
            holdConnectedSec > 0 ||
            string.Equals(Environment.GetEnvironmentVariable("RDP_POOL_NO_UI"), "1", StringComparison.Ordinal);

        var form = new Form
        {
            Text = host + " — RDP Pool",
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = true,
            KeyPreview = true,
            FormBorderStyle = FormBorderStyle.None,
            ControlBox = false,
            MaximizeBox = false,
            MinimizeBox = false,
            WindowState = FormWindowState.Normal
        };

        if (wantOcxFullscreen)
        {
            if (screenBounds.Width > 0)
                form.Bounds = screenBounds;
            else
            {
                form.Location = Point.Empty;
                form.ClientSize = new Size(deskW, deskH);
            }
        }
        else
        {
            ApplyWindowedChrome(form, deskW, deskH, screenBounds);
        }

        AxHost? ax = null;
        try
        {
            ax = new OcxHost(prog.GUID) { Dock = DockStyle.Fill };
            form.Controls.Add(ax);
        }
        catch (Exception ex)
        {
            form.Dispose();
            detail = localDetail + "; AxHost=" + ex.Message;
            return false;
        }

        System.Windows.Forms.Timer? pollTimer = null;
        System.Windows.Forms.Timer? holdTimer = null;
        object? ocxRef = null;
        IDisposable? eventAdvise = null;

        bool IsOcxConnected()
        {
            try
            {
                if (ocxRef == null) return false;
                return Convert.ToInt32(GetComProp(ocxRef, "Connected")) == 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// 取消关闭后的 UI：仅当取消时仍处于 OCX 全屏才恢复全屏；
        /// 若已是窗口化，什么都不做（绝不能 EnterOcxFullscreen / FullScreen=true）。
        /// </summary>
        void OnCloseCancelled(bool wasOcxFullscreen)
        {
            try
            {
                if (ocxRef == null || !IsOcxConnected()) return;
                if (wasOcxFullscreen)
                {
                    try { SetComProp(ocxRef, "FullScreen", true); } catch { /* ignore */ }
                    ocxFsActive = true;
                    hostHidden = false; // 允许 HideHost 再跑一次
                    HideHostAfterOcxFullscreen();
                    BringRdpToFront(ocxRef);
                    return;
                }

                // 窗口化取消：保持窗口化。禁止回全屏；压住 MessageBox 抢焦点带来的误 FullScreen=true。
                suppressAutoFullscreenUntilMs = Environment.TickCount64 + 1500;
                ocxFsActive = false;
                try { SetComProp(ocxRef, "FullScreen", false); } catch { /* ignore */ }
                if (!form.IsDisposed && form.Opacity > 0.5 && form.FormBorderStyle != FormBorderStyle.None)
                    ForegroundUtil.Activate(form);
            }
            catch { /* ignore */ }
        }

        bool ConfirmDisconnectUi()
        {
            if (skipCloseConfirm || userConfirmedClose) return true;
            confirmUiOpen = true;
            Form? topOwner = null;
            try
            {
                // OCX 全屏 HWND 会盖住普通 MessageBox：用 TopMost 空 owner 保证中文确认可见
                var needTopMost = ocxFsActive || hostHidden
                                  || form.IsDisposed || !form.Visible || form.Opacity < 0.5;
                if (needTopMost)
                {
                    topOwner = new Form
                    {
                        Text = "RDP 资源池客户端",
                        ShowInTaskbar = false,
                        FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.Manual,
                        Bounds = new Rectangle(-32000, -32000, 1, 1),
                        TopMost = true,
                        Opacity = 0
                    };
                    topOwner.Show();
                    topOwner.TopMost = true;
                }

                IWin32Window? owner = topOwner;
                if (owner == null && !form.IsDisposed && form.Visible && form.Opacity > 0.5)
                    owner = form;

                var r = owner != null
                    ? MessageBox.Show(owner, "确定要断开远程桌面并退出吗？", "RDP 资源池客户端",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                    : MessageBox.Show("确定要断开远程桌面并退出吗？", "RDP 资源池客户端",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                return r == DialogResult.Yes;
            }
            catch
            {
                // 确认框失败时不要静默断开（全屏 X 尤其危险）
                return false;
            }
            finally
            {
                confirmUiOpen = false;
                if (topOwner != null)
                {
                    try { topOwner.Close(); } catch { /* ignore */ }
                    try { topOwner.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>用户主动退出：先确认（仍保持连接），是才关窗；取消则保持当前模式。</summary>
        void CloseHost(bool askConfirm = true)
        {
            if (closing || form.IsDisposed) return;
            if (askConfirm)
            {
                var wasFs = ocxFsActive;
                if (!ConfirmDisconnectUi())
                {
                    OnCloseCancelled(wasFs);
                    return;
                }
                userConfirmedClose = true;
            }
            else
            {
                userConfirmedClose = true;
            }

            try
            {
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(() => { try { form.Close(); } catch { /* ignore */ } }));
                else
                    form.Close();
            }
            catch { /* ignore */ }
        }

        void BringRdpToFront(object? ocx)
        {
            try
            {
                ForegroundUtil.AllowThisProcess();
                if (ocxFsActive || hostHidden)
                {
                    // OCX 全屏表面：按本进程大窗口 / 标题提示置前
                    ForegroundUtil.ActivateOcxFullscreenSurface(
                        Environment.ProcessId, host);
                    // 稍后再试一次（全屏 HWND 可能略晚创建）
                    var t = new System.Windows.Forms.Timer { Interval = 250 };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        t.Dispose();
                        ForegroundUtil.ActivateOcxFullscreenSurface(Environment.ProcessId, host);
                    };
                    t.Start();
                }
                else
                {
                    ForegroundUtil.Activate(form);
                }
            }
            catch { /* ignore */ }
        }

        void HideHostAfterOcxFullscreen()
        {
            if (hostHidden || form.IsDisposed) return;
            hostHidden = true;
            try
            {
                // 不设 Visible=false，以免拆掉 OCX 全屏表面
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Size = new Size(1, 1);
                form.Location = new Point(-32000, -32000);
                localDetail += "; hostHidden=1";
            }
            catch (Exception ex)
            {
                localDetail += "; hostHideErr=" + ex.Message;
            }
        }

        /// <summary>
        /// 连接栏「窗口化」：OCX 退出 FullScreen 后，必须把宿主还原成可见的带边框窗口，
        /// 否则 Ax 画在隐形/无边框全屏 Form 上 → 卡住无法退出。
        /// </summary>
        void RestoreHostToWindowed(object? ocx)
        {
            if (form.IsDisposed) return;
            try
            {
                if (ocx != null)
                {
                    try { SetComProp(ocx, "FullScreen", false); } catch { /* already false */ }
                    // 窗口态下键盘钩回本地，避免 Win 键仍锁在远程
                    TrySetKeyboardHookMode(ocx, 0);
                }

                hostHidden = false;
                ocxFsActive = false;

                form.SuspendLayout();
                form.Opacity = 1;
                form.ShowInTaskbar = true;
                form.Visible = true;
                ApplyWindowedChrome(form, deskW, deskH, screenBounds);
                if (ax != null)
                {
                    ax.Dock = DockStyle.Fill;
                    ax.BringToFront();
                }
                form.ResumeLayout(true);
                form.Show();
                ForegroundUtil.Activate(form);

                localDetail += "; restoredWindowed=1";
            }
            catch (Exception ex)
            {
                localDetail += "; restoreErr=" + ex.Message;
                // 还原失败则断开，避免永久卡死
                CloseHost(askConfirm: false);
            }
        }

        var redirectingMaximize = false;

        void EnterOcxFullscreen(object ocx)
        {
            TrySetKeyboardHookMode(ocx, 1);
            try
            {
                // 勿把 Form.Maximized 当成全屏：先还原 Normal 再交 OCX
                redirectingMaximize = true;
                try
                {
                    if (form.WindowState != FormWindowState.Normal)
                        form.WindowState = FormWindowState.Normal;
                }
                finally { redirectingMaximize = false; }

                SetComProp(ocx, "FullScreen", true);
                ocxFsActive = true;
                localDetail += "; ocxFullScreen=1";
            }
            catch (Exception ex)
            {
                localDetail += "; ocxFullScreenErr=" + ex.Message;
            }
            HideHostAfterOcxFullscreen();
            BringRdpToFront(ocx);
        }

        // DisplayResolution=fullscreen 会话：标题栏最大化 / 双击标题栏 → 真 OCX FullScreen，不是 Form 最大化
        form.Resize += (_, _) =>
        {
            if (redirectingMaximize || closing || !wantOcxFullscreen || !connected || ocxFsActive)
                return;
            if (form.WindowState != FormWindowState.Maximized)
                return;
            var ocx = ocxRef;
            if (ocx == null) return;
            localDetail += "; maximize→ocxFullscreen";
            EnterOcxFullscreen(ocx);
        };

        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape || (e.Control && e.Shift && e.KeyCode == Keys.F4))
            {
                e.Handled = true;
                CloseHost();
            }
            // F11：窗口化 ↔ 真 OCX 全屏（仅 fullscreen 配置会话）
            else if (wantOcxFullscreen && connected && e.KeyCode == Keys.F11)
            {
                e.Handled = true;
                var ocx = ocxRef;
                if (ocx == null) return;
                if (ocxFsActive)
                    RestoreHostToWindowed(ocx);
                else
                    EnterOcxFullscreen(ocx);
            }
        };

        form.Shown += (_, _) =>
        {
            try
            {
                if (wantOcxFullscreen && screenBounds.Width > 0 && !hostHidden)
                    form.Bounds = screenBounds;

                var ocx = ((OcxHost)ax!).GetOcxObject()
                          ?? throw new InvalidOperationException("ActiveX OCX null");
                ocxRef = ocx;
                SetComProp(ocx, "Server", host);
                SetComProp(ocx, "UserName", username);
                try { SetComProp(ocx, "Domain", host); } catch { /* optional */ }

                TrySetDesktopSize(ocx, deskW, deskH);
                try { SetComProp(ocx, "FullScreen", false); } catch { /* optional */ }
                if (wantOcxFullscreen)
                    TrySetKeyboardHookMode(ocx, 1);
                else
                    TrySetKeyboardHookMode(ocx, 0);

                TrySetPort(ocx, port);
                TryEnableCredSsp(ocx);
                TryEnableDisplayConnectionBar(ocx); // 连接栏 X → OnConfirmClose
                if (!TrySetClearTextPassword(ocx, password, out var how))
                    throw new InvalidOperationException("ClearTextPassword unavailable: " + how);
                localDetail += "; password_set=" + how;

                // 较快轮询，及时接住连接栏「窗口化」
                pollTimer = new System.Windows.Forms.Timer { Interval = 150 };
                pollTimer.Tick += (_, _) =>
                {
                    try
                    {
                        var c = Convert.ToInt32(GetComProp(ocx, "Connected"));
                        if (c == 1 && !connected)
                        {
                            connected = true;
                            localDetail += "; connected=1";
                            if (wantOcxFullscreen)
                                EnterOcxFullscreen(ocx);
                            else
                                BringRdpToFront(ocx);

                            if (holdConnectedSec > 0)
                            {
                                holdTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, holdConnectedSec) * 1000 };
                                holdTimer.Tick += (_, _) =>
                                {
                                    holdTimer.Stop();
                                    CloseHost(askConfirm: false);
                                };
                                holdTimer.Start();
                            }
                        }
                        else if (connected && c == 0)
                        {
                            // 已断开（连接栏已拆会话 / 对端踢）：勿再弹「是否断开」——否也救不回
                            localDetail += "; disconnected=0";
                            pollTimer.Stop();
                            connected = false;
                            if (userConfirmedClose || closing)
                                CloseHost(askConfirm: false);
                            else
                            {
                                // 非本端确认的断开：直接收掉宿主，避免空白残留
                                CloseHost(askConfirm: false);
                            }
                            return;
                        }

                        // 确认框弹出期间勿处理全屏切换（避免 MessageBox 抢焦点导致误「窗口化」/误回全屏）
                        if (wantOcxFullscreen && connected && !confirmUiOpen)
                        {
                            bool fs;
                            try { fs = Convert.ToBoolean(GetComProp(ocx, "FullScreen")); }
                            catch { return; }

                            if (ocxFsActive && !fs)
                            {
                                // 用户点了连接栏「窗口化」
                                RestoreHostToWindowed(ocx);
                            }
                            else if (!ocxFsActive && fs)
                            {
                                // MessageBox 抢焦点后 OCX 可能误报 FullScreen=true —— 窗口化会话勿跟进
                                if (Environment.TickCount64 < suppressAutoFullscreenUntilMs)
                                {
                                    try { SetComProp(ocx, "FullScreen", false); } catch { /* ignore */ }
                                }
                                else
                                {
                                    // 连接栏/最大化/F11 合法再进全屏
                                    EnterOcxFullscreen(ocx);
                                }
                            }
                        }
                    }
                    catch { /* ignore poll errors */ }
                };
                pollTimer.Start();

                // 连接栏 X/关闭：OnConfirmClose 在 Disconnect 前询问（全屏时尤其关键）
                eventAdvise = MsTscEvents.AdviseConfirmClose(ocx, () =>
                {
                    if (skipCloseConfirm || userConfirmedClose) return true;
                    // 以当前是否 OCX 全屏为准（勿用 hostHidden 误判窗口化）
                    var wasFs = ocxFsActive;
                    bool ok;
                    try
                    {
                        // MsTsc 事件多在 UI STA；同步 Invoke 易与 COM 重入死锁 → 直接弹
                        ok = ConfirmDisconnectUi();
                    }
                    catch { ok = false; } // 失败则拒绝关闭，避免无确认断开

                    if (ok)
                    {
                        userConfirmedClose = true;
                        localDetail += "; confirmClose=yes";
                        return true; // 允许 OCX 关闭 → 随后 Disconnect
                    }
                    localDetail += "; confirmClose=no";
                    try
                    {
                        if (!form.IsDisposed)
                            form.BeginInvoke(new Action(() => OnCloseCancelled(wasFs)));
                        else
                            OnCloseCancelled(wasFs);
                    }
                    catch { OnCloseCancelled(wasFs); }
                    return false; // 拒绝关闭，会话继续
                });
                if (eventAdvise != null) localDetail += "; onConfirmClose=advised";
                else localDetail += "; onConfirmClose=adviseFailed";

                InvokeCom(ocx, "Connect");
                localDetail += "; connect_called";
                ForegroundUtil.Activate(form);
            }
            catch (Exception ex)
            {
                connectError = ex;
                localDetail += "; err=" + ex.Message;
                CloseHost(askConfirm: false);
            }
        };

        form.FormClosing += (_, e) =>
        {
            // 必须在 Disconnect 之前确认；取消：e.Cancel=true，窗口化保持窗口化
            if (!userConfirmedClose && !skipCloseConfirm)
            {
                var wasFs = ocxFsActive; // 弹确认前快照；窗口化时为 false → 取消不回全屏
                if (IsOcxConnected() && !ConfirmDisconnectUi())
                {
                    e.Cancel = true;
                    OnCloseCancelled(wasFs);
                    return;
                }
                userConfirmedClose = true;
            }

            closing = true;
            pollTimer?.Stop();
            holdTimer?.Stop();
            try { eventAdvise?.Dispose(); } catch { /* ignore */ }
            eventAdvise = null;
            try
            {
                var ocx = ocxRef ?? ((OcxHost)ax!).GetOcxObject();
                if (ocx != null)
                {
                    // 仅在用户确认之后才 Disconnect
                    try { SetComProp(ocx, "FullScreen", false); } catch { /* ignore */ }
                    try { InvokeCom(ocx, "Disconnect"); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        };

        using (form)
        {
            Application.Run(form);
        }
        detail = localDetail;
        if (connectError != null) return false;
        if (holdConnectedSec > 0) return connected;
        return true;
    }

    static void ApplyWindowedChrome(Form form, int deskW, int deskH, Rectangle screenBounds)
    {
        form.FormBorderStyle = FormBorderStyle.Sizable;
        form.ControlBox = true;
        form.MaximizeBox = true;
        form.MinimizeBox = true;
        form.WindowState = FormWindowState.Normal;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? screenBounds;
        if (wa.Width <= 0) wa = new Rectangle(0, 0, Math.Max(deskW, 1280), Math.Max(deskH, 720));

        // 舒适窗口：不超过工作区 90%，也不小于 1024×640
        var cw = Math.Min(Math.Max(deskW > 0 && deskW < wa.Width ? deskW : wa.Width * 9 / 10, 1024), wa.Width);
        var ch = Math.Min(Math.Max(deskH > 0 && deskH < wa.Height ? deskH : wa.Height * 9 / 10, 640), wa.Height);
        // 从全屏配置还原时 desk 往往等于整屏，强制缩到工作区 90%
        if (cw >= wa.Width - 16) cw = Math.Max(1024, wa.Width * 9 / 10);
        if (ch >= wa.Height - 16) ch = Math.Max(640, wa.Height * 9 / 10);

        form.ClientSize = new Size(cw, ch);
        form.MinimumSize = new Size(640, 480);
        form.Location = new Point(
            wa.Left + Math.Max(0, (wa.Width - form.Width) / 2),
            wa.Top + Math.Max(0, (wa.Height - form.Height) / 2));
    }

    static void TrySetDesktopSize(object ocx, int width, int height)
    {
        try { SetComProp(ocx, "DesktopWidth", width); } catch { /* optional */ }
        try { SetComProp(ocx, "DesktopHeight", height); } catch { /* optional */ }
    }

    static void TrySetKeyboardHookMode(object ocx, int mode)
    {
        foreach (var path in new[]
                 {
                     ("SecuredSettings", "KeyboardHookMode"),
                     ("SecuredSettings2", "KeyboardHookMode"),
                     ("AdvancedSettings9", "KeyboardHookMode"),
                     ("AdvancedSettings8", "KeyboardHookMode"),
                     ("AdvancedSettings7", "KeyboardHookMode"),
                     ("AdvancedSettings2", "KeyboardHookMode"),
                 })
        {
            try
            {
                var obj = GetComProp(ocx, path.Item1);
                if (obj == null) continue;
                SetComProp(obj, path.Item2, mode);
                return;
            }
            catch { /* try next */ }
        }
    }

    static void TrySetPort(object ocx, int port)
    {
        foreach (var prop in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2", "AdvancedSettings" })
        {
            try
            {
                var adv = GetComProp(ocx, prop);
                if (adv == null) continue;
                SetComProp(adv, "RDPPort", port);
                return;
            }
            catch { /* try next */ }
        }
    }

    static void TryEnableCredSsp(object ocx)
    {
        foreach (var prop in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2" })
        {
            try
            {
                var adv = GetComProp(ocx, prop);
                if (adv == null) continue;
                SetComProp(adv, "EnableCredSspSupport", true);
                return;
            }
            catch { /* try next */ }
        }
    }

    static void TryEnableDisplayConnectionBar(object ocx)
    {
        foreach (var prop in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2", "AdvancedSettings" })
        {
            try
            {
                var adv = GetComProp(ocx, prop);
                if (adv == null) continue;
                SetComProp(adv, "DisplayConnectionBar", true);
                return;
            }
            catch { /* try next */ }
        }
    }

    static bool TrySetClearTextPassword(object ocx, string password, out string how)
    {
        foreach (var prop in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2", "AdvancedSettings" })
        {
            try
            {
                var adv = GetComProp(ocx, prop);
                if (adv == null) continue;
                SetComProp(adv, "ClearTextPassword", password);
                how = prop;
                return true;
            }
            catch { /* try next */ }
        }

        try
        {
            IMsTscNonScriptable? nonScript = ocx as IMsTscNonScriptable;
            if (nonScript == null)
            {
                var pUnk = Marshal.GetIUnknownForObject(ocx);
                try
                {
                    var iid = typeof(IMsTscNonScriptable).GUID;
                    var hr = Marshal.QueryInterface(pUnk, ref iid, out var pNS);
                    if (hr == 0 && pNS != IntPtr.Zero)
                    {
                        nonScript = (IMsTscNonScriptable)Marshal.GetObjectForIUnknown(pNS);
                        Marshal.Release(pNS);
                    }
                }
                finally
                {
                    Marshal.Release(pUnk);
                }
            }
            if (nonScript != null)
            {
                nonScript.ClearTextPassword = password;
                how = "IMsTscNonScriptable";
                return true;
            }
            how = "QI failed";
        }
        catch (Exception ex)
        {
            how = ex.Message;
        }
        return false;
    }

    static object? GetComProp(object target, string name) =>
        target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null);

    static void SetComProp(object target, string name, object value) =>
        target.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, target, new[] { value });

    static void InvokeCom(object target, string name) =>
        target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, null);

    sealed class OcxHost : AxHost
    {
        public OcxHost(Guid clsid) : base(clsid.ToString("B")) { }
        public object? GetOcxObject() => GetOcx();
    }
}

[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsTscNonScriptable
{
    string ClearTextPassword { set; }
}
