using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpPool.Config;

namespace RdpPoolClient;

/// <summary>
/// MsTscAx ActiveX：ClearTextPassword 注入。
/// <para>
/// 全屏：Connect 成功后设 MsTscAx.FullScreen=true（与 mstsc 一样把 Win/Alt+Tab 交给远程），
/// 随即把宿主 Form 移出任务栏并藏起（Opacity=0 / 移出屏幕），避免空白第二窗。
/// 窗口化：FullScreen=false，固定 ClientSize。
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
        var closing = false;

        // 宿主仅作 ActiveX 容器；全屏成功后会藏起，只留 OCX 全屏表面在前台
        var form = new Form
        {
            Text = host + " — RDP Pool",
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = true,
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
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.ControlBox = true;
            form.MaximizeBox = true;
            form.MinimizeBox = true;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ClientSize = new Size(deskW, deskH);
            form.MinimumSize = new Size(640, 480);
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
                // 不设 Visible=false：部分环境下会连带拆掉 OCX 全屏表面
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

        void EnterOcxFullscreen(object ocx)
        {
            // KeyboardHookMode: 1=仅全屏时钩到远程（默认）；2=始终钩到远程
            TrySetKeyboardHookMode(ocx, 1);
            try
            {
                SetComProp(ocx, "FullScreen", true);
                localDetail += "; ocxFullScreen=1";
            }
            catch (Exception ex)
            {
                localDetail += "; ocxFullScreenErr=" + ex.Message;
            }
            HideHostAfterOcxFullscreen();
        }

        form.Shown += (_, _) =>
        {
            try
            {
                if (wantOcxFullscreen && screenBounds.Width > 0)
                    form.Bounds = screenBounds;

                var ocx = ((OcxHost)ax!).GetOcxObject()
                          ?? throw new InvalidOperationException("ActiveX OCX null");
                ocxRef = ocx;
                SetComProp(ocx, "Server", host);
                SetComProp(ocx, "UserName", username);
                try { SetComProp(ocx, "Domain", host); } catch { /* optional */ }

                TrySetDesktopSize(ocx, deskW, deskH);
                // Connect 前必须 FullScreen=false；连上后再进全屏
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
                            if (wantOcxFullscreen)
                                EnterOcxFullscreen(ocx);

                            if (holdConnectedSec > 0)
                            {
                                pollTimer.Stop();
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
                        }
                        else if (wantOcxFullscreen && connected && hostHidden)
                        {
                            // 用户从连接栏退出全屏：把宿主拉回可见，避免黑屏/找不到窗
                            try
                            {
                                var fs = Convert.ToBoolean(GetComProp(ocx, "FullScreen"));
                                if (!fs)
                                {
                                    RestoreHostFromHide();
                                    localDetail += "; leftFullscreen";
                                }
                            }
                            catch { /* ignore */ }
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

        void RestoreHostFromHide()
        {
            if (!hostHidden || form.IsDisposed) return;
            hostHidden = false;
            try
            {
                form.Opacity = 1;
                form.ShowInTaskbar = true;
                if (screenBounds.Width > 0)
                    form.Bounds = screenBounds;
                else
                    form.ClientSize = new Size(deskW, deskH);
                form.FormBorderStyle = FormBorderStyle.None;
                form.Activate();
            }
            catch { /* ignore */ }
        }

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

    static void TrySetDesktopSize(object ocx, int width, int height)
    {
        try { SetComProp(ocx, "DesktopWidth", width); } catch { /* optional */ }
        try { SetComProp(ocx, "DesktopHeight", height); } catch { /* optional */ }
    }

    /// <summary>
    /// SecuredSettings / AdvancedSettings2.KeyboardHookMode：
    /// 0=本地，1=全屏时钩到远程，2=始终钩到远程。
    /// </summary>
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

[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsTscNonScriptable
{
    string ClearTextPassword { set; }
}
