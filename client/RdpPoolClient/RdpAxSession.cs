using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpPool.Config;

namespace RdpPoolClient;

/// <summary>
/// MsTscAx ActiveX：ClearTextPassword 注入；显示由宿主 Form 控制。
/// <para>
/// 真全屏（mstsc 风格）：FormBorderStyle.None + Bounds=主屏，Dock Fill OCX，
/// 顶部悬停连接条。绝不设置 MsTscAx.FullScreen=true（会另开 HWND → 空白孤儿窗）。
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
        var trueFullscreen = apply.FullScreen;
        var deskW = trueFullscreen ? localW : apply.Width;
        var deskH = trueFullscreen ? localH : apply.Height;

        var localDetail = "progid=" + used +
                          $"; display={(trueFullscreen ? $"true-fullscreen {deskW}x{deskH}" : $"{deskW}x{deskH}")}" +
                          "; ocxFullScreen=false";
        Exception? connectError = null;
        var connected = false;
        var closing = false;

        var form = new Form
        {
            Text = host + " — RDP Pool",
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = true,
            KeyPreview = true
        };

        Panel? connBar = null;
        System.Windows.Forms.Timer? edgeTimer = null;

        if (trueFullscreen)
        {
            // 真全屏：无标题栏/系统按钮，铺满主屏（单顶层窗）
            form.FormBorderStyle = FormBorderStyle.None;
            form.ControlBox = false;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.WindowState = FormWindowState.Normal;
            if (screenBounds.Width > 0)
                form.Bounds = screenBounds;
            else
            {
                form.Location = new System.Drawing.Point(0, 0);
                form.ClientSize = new System.Drawing.Size(deskW, deskH);
            }

            connBar = BuildConnectionBar(form, host, () => CloseHost());
            form.Controls.Add(connBar);
            connBar.BringToFront();

            edgeTimer = new System.Windows.Forms.Timer { Interval = 120 };
            edgeTimer.Tick += (_, _) => UpdateConnectionBarVisibility(form, connBar);
        }
        else
        {
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.MinimizeBox = true;
            form.MaximizeBox = true;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ClientSize = new System.Drawing.Size(deskW, deskH);
            form.MinimumSize = new System.Drawing.Size(640, 480);
        }

        AxHost? ax = null;
        try
        {
            ax = new OcxHost(prog.GUID) { Dock = DockStyle.Fill };
            form.Controls.Add(ax);
            ax.SendToBack();
        }
        catch (Exception ex)
        {
            form.Dispose();
            detail = localDetail + "; AxHost=" + ex.Message;
            return false;
        }

        System.Windows.Forms.Timer? pollTimer = null;
        System.Windows.Forms.Timer? holdTimer = null;

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

        form.KeyDown += (_, e) =>
        {
            // 全屏无系统关闭按钮时：Ctrl+Shift+F4 或 Esc 断开（Esc 仅全屏）
            if (e.Control && e.Shift && e.KeyCode == Keys.F4)
            {
                e.Handled = true;
                CloseHost();
            }
            else if (trueFullscreen && e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseHost();
            }
        };

        form.Shown += (_, _) =>
        {
            try
            {
                if (trueFullscreen && screenBounds.Width > 0)
                    form.Bounds = screenBounds;

                var ocx = ((OcxHost)ax!).GetOcxObject()
                          ?? throw new InvalidOperationException("ActiveX OCX null");
                SetComProp(ocx, "Server", host);
                SetComProp(ocx, "UserName", username);
                try { SetComProp(ocx, "Domain", host); } catch { /* optional */ }

                TrySetDesktopSize(ocx, deskW, deskH);
                TryForceOcxWindowed(ocx);
                TrySetContainerHandledFullScreen(ocx, trueFullscreen);

                TrySetPort(ocx, port);
                TryEnableCredSsp(ocx);
                if (!TrySetClearTextPassword(ocx, password, out var how))
                    throw new InvalidOperationException("ClearTextPassword unavailable: " + how);
                localDetail += "; password_set=" + how;

                pollTimer = new System.Windows.Forms.Timer { Interval = 400 };
                pollTimer.Tick += (_, _) =>
                {
                    try
                    {
                        var c = Convert.ToInt32(GetComProp(ocx, "Connected"));
                        if (c == 1 && !connected)
                        {
                            connected = true;
                            localDetail += "; connected=1";
                            if (holdConnectedSec > 0)
                            {
                                pollTimer.Stop();
                                edgeTimer?.Stop();
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
                            edgeTimer?.Stop();
                            CloseHost();
                        }
                    }
                    catch { /* ignore poll errors */ }
                };
                pollTimer.Start();
                edgeTimer?.Start();

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
            edgeTimer?.Stop();
            try
            {
                var ocx = ((OcxHost)ax!).GetOcxObject();
                if (ocx != null)
                {
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

    static Panel BuildConnectionBar(Form form, string host, Action onDisconnect)
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = Color.FromArgb(245, 32, 32, 32),
            Visible = false,
            Padding = new Padding(8, 2, 8, 2)
        };

        var label = new Label
        {
            Text = "远程桌面：" + host + "   （移开鼠标隐藏 · Esc 断开）",
            ForeColor = Color.White,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F)
        };

        var btn = new Button
        {
            Text = "断开",
            Dock = DockStyle.Right,
            Width = 72,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(255, 60, 60, 60)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += (_, _) => onDisconnect();

        bar.Controls.Add(label);
        bar.Controls.Add(btn);
        // 鼠标在条上时保持显示
        void Keep(object? s, EventArgs e) => bar.Tag = Environment.TickCount64;
        bar.MouseEnter += Keep;
        bar.MouseMove += Keep;
        label.MouseEnter += Keep;
        btn.MouseEnter += Keep;
        return bar;
    }

    static void UpdateConnectionBarVisibility(Form form, Panel bar)
    {
        if (form.IsDisposed || !form.Visible) return;
        try
        {
            var pt = form.PointToClient(Control.MousePosition);
            var nearTop = pt.Y >= 0 && pt.Y <= 8 && pt.X >= 0 && pt.X < form.ClientSize.Width;
            var overBar = bar.Visible && pt.Y >= 0 && pt.Y <= bar.Height + 4;
            var pinned = bar.Tag is long t && (Environment.TickCount64 - t) < 800;
            var show = nearTop || overBar || pinned;
            if (bar.Visible != show)
            {
                bar.Visible = show;
                if (show) bar.BringToFront();
            }
        }
        catch { /* ignore */ }
    }

    static void TrySetDesktopSize(object ocx, int width, int height)
    {
        try { SetComProp(ocx, "DesktopWidth", width); } catch { /* optional */ }
        try { SetComProp(ocx, "DesktopHeight", height); } catch { /* optional */ }
    }

    static void TryForceOcxWindowed(object ocx)
    {
        try { SetComProp(ocx, "FullScreen", false); } catch { /* optional */ }
    }

    /// <summary>告知控件由容器处理全屏，避免 OCX 自建全屏 HWND。</summary>
    static void TrySetContainerHandledFullScreen(object ocx, bool enable)
    {
        if (!enable) return;
        foreach (var prop in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2" })
        {
            try
            {
                var adv = GetComProp(ocx, prop);
                if (adv == null) continue;
                SetComProp(adv, "ContainerHandledFullScreen", 1);
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
