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

        void CloseHost()
        {
            if (closing || form.IsDisposed) return;
            closing = true;
            try
            {
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(() => { try { form.Close(); } catch { /* ignore */ } }));
                else
                    form.Close();
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
                form.Activate();
                form.BringToFront();
                try { NativeFlash.BringToFront(form.Handle); } catch { /* optional */ }

                localDetail += "; restoredWindowed=1";
            }
            catch (Exception ex)
            {
                localDetail += "; restoreErr=" + ex.Message;
                // 还原失败则断开，避免永久卡死
                CloseHost();
            }
        }

        void EnterOcxFullscreen(object ocx)
        {
            TrySetKeyboardHookMode(ocx, 1);
            try
            {
                SetComProp(ocx, "FullScreen", true);
                ocxFsActive = true;
                localDetail += "; ocxFullScreen=1";
            }
            catch (Exception ex)
            {
                localDetail += "; ocxFullScreenErr=" + ex.Message;
            }
            HideHostAfterOcxFullscreen();
        }

        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape || (e.Control && e.Shift && e.KeyCode == Keys.F4))
            {
                e.Handled = true;
                CloseHost();
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

                            if (holdConnectedSec > 0)
                            {
                                holdTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, holdConnectedSec) * 1000 };
                                holdTimer.Tick += (_, _) =>
                                {
                                    holdTimer.Stop();
                                    CloseHost();
                                };
                                holdTimer.Start();
                            }
                        }
                        else if (connected && c == 0)
                        {
                            localDetail += "; disconnected=0";
                            pollTimer.Stop();
                            CloseHost();
                            return;
                        }

                        if (wantOcxFullscreen && connected)
                        {
                            bool fs;
                            try { fs = Convert.ToBoolean(GetComProp(ocx, "FullScreen")); }
                            catch { return; }

                            if (ocxFsActive && !fs)
                            {
                                // 用户点了连接栏「窗口化」
                                RestoreHostToWindowed(ocx);
                            }
                            else if (!ocxFsActive && fs && !hostHidden)
                            {
                                // 再次进入全屏（若 OCX 支持）
                                ocxFsActive = true;
                                HideHostAfterOcxFullscreen();
                                TrySetKeyboardHookMode(ocx, 1);
                            }
                        }
                    }
                    catch { /* ignore poll errors */ }
                };
                pollTimer.Start();

                InvokeCom(ocx, "Connect");
                localDetail += "; connect_called";
            }
            catch (Exception ex)
            {
                connectError = ex;
                localDetail += "; err=" + ex.Message;
                CloseHost();
            }
        };

        form.FormClosing += (_, _) =>
        {
            closing = true;
            pollTimer?.Stop();
            holdTimer?.Stop();
            try
            {
                var ocx = ocxRef ?? ((OcxHost)ax!).GetOcxObject();
                if (ocx != null)
                {
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

static class NativeFlash
{
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
    }
}

[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsTscNonScriptable
{
    string ClearTextPassword { set; }
}
