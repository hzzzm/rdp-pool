using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpPool.Config;

namespace RdpPoolClient;

/// <summary>
/// MsTscAx ActiveX：通过 AdvancedSettings*.ClearTextPassword（或 IMsTscNonScriptable）注入明文密码，
/// 避免 mstsc + CredentialUIBroker 交互框。
/// </summary>
internal static class RdpAxSession
{
    static readonly string[] ProgIds =
    {
        "MsTscAx.MsTscAx.12", "MsTscAx.MsTscAx.11", "MsTscAx.MsTscAx.10",
        "MsTscAx.MsTscAx.9", "MsTscAx.MsTscAx.8", "MsTscAx.MsTscAx"
    };

    /// <param name="holdConnectedSec">Connected 后保持秒数再关窗；0=等到用户关窗。</param>
    /// <param name="display">本地显示设置；null 则全屏（兼容旧行为）。</param>
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
        try
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? default;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                localW = bounds.Width;
                localH = bounds.Height;
            }
        }
        catch { /* keep defaults */ }

        var apply = display ?? LocalDisplay.Resolve(LocalDisplay.FullScreen, localW, localH);

        var localDetail = "progid=" + used +
                          $"; display={(apply.FullScreen ? "fullscreen" : $"{apply.Width}x{apply.Height}")}";
        Exception? connectError = null;
        var connected = false;

        var form = new Form
        {
            Text = host + " — RDP Pool",
            StartPosition = FormStartPosition.CenterScreen
        };
        if (apply.FullScreen)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.WindowState = FormWindowState.Maximized;
            form.Bounds = Screen.PrimaryScreen?.Bounds ?? form.Bounds;
        }
        else
        {
            form.FormBorderStyle = FormBorderStyle.Sizable;
            // ClientSize 接近远程桌面尺寸；边框额外高度由系统处理
            form.ClientSize = new System.Drawing.Size(apply.Width, apply.Height);
            form.MinimumSize = new System.Drawing.Size(640, 480);
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

        form.Shown += (_, _) =>
        {
            try
            {
                var ocx = ((OcxHost)ax!).GetOcxObject()
                          ?? throw new InvalidOperationException("ActiveX OCX null");
                SetComProp(ocx, "Server", host);
                SetComProp(ocx, "UserName", username);
                try { SetComProp(ocx, "Domain", host); } catch { /* optional */ }

                TrySetDesktopSize(ocx, apply.Width, apply.Height);
                TrySetFullScreen(ocx, apply.FullScreen);

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
                        // 1 = connected, 2 = connecting
                        if (c == 1 && !connected)
                        {
                            connected = true;
                            localDetail += "; connected=1";
                            if (holdConnectedSec > 0)
                            {
                                pollTimer.Stop();
                                holdTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, holdConnectedSec) * 1000 };
                                holdTimer.Tick += (_, _) =>
                                {
                                    holdTimer.Stop();
                                    form.Close();
                                };
                                holdTimer.Start();
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
                form.Close();
            }
        };

        form.FormClosing += (_, _) =>
        {
            pollTimer?.Stop();
            holdTimer?.Stop();
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

    static void TrySetDesktopSize(object ocx, int width, int height)
    {
        try { SetComProp(ocx, "DesktopWidth", width); } catch { /* optional */ }
        try { SetComProp(ocx, "DesktopHeight", height); } catch { /* optional */ }
    }

    static void TrySetFullScreen(object ocx, bool fullScreen)
    {
        try { SetComProp(ocx, "FullScreen", fullScreen); } catch { /* optional */ }
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

/// <summary>IID_IMsTscNonScriptable = c1e6743a-41c1-4a74-832a-0dd06c1c7a0e (MsTscAx.dll).</summary>
[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsTscNonScriptable
{
    string ClearTextPassword { set; }
}
