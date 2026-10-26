using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>
/// 订阅 IMsTscAxEvents。注意：OnConfirmClose 仅在容器调用 IMsRdpClient::RequestClose 时触发，
/// 连接栏 X 不会触发该事件（见 MSDN）。连接栏 X 由 ConnectionBarCloseGuard 拦截。
/// </summary>
static class MsTscEvents
{
    /// <summary>mstscax.dll 类型库中的真实 IID（勿用网上误抄的 C6C43507…）。</summary>
    public static readonly Guid EventsIid = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    public const int DispidOnConfirmClose = 15;

    public readonly struct AdviseResult
    {
        public readonly IDisposable? Cookie;
        public readonly string Detail;
        public AdviseResult(IDisposable? cookie, string detail)
        {
            Cookie = cookie;
            Detail = detail;
        }
    }

    /// <param name="allowClose">返回 true=允许关闭并断开；false=取消，会话继续。</param>
    public static AdviseResult AdviseConfirmClose(object ocx, Func<bool> allowClose)
    {
        // 1) AxHost.ConnectionPointCookie + 正确事件 IID 的接口
        try
        {
            var sink = new ConfirmCloseSink(allowClose);
            var cookie = new AxHost.ConnectionPointCookie(ocx, sink, typeof(IMsTscAxEvents));
            return new AdviseResult(new CookieDisposable(cookie), "cookie+iface");
        }
        catch (Exception ex)
        {
            // fall through
            var cookieEx = ex.GetType().Name;
            try
            {
                var sink = new ConfirmCloseSink(allowClose);
                if (TryAdvise(ocx, sink, out var manual) && manual != null)
                    return new AdviseResult(manual, "cpc+iface afterCookieFail=" + cookieEx);
            }
            catch (Exception ex2)
            {
                try
                {
                    var sink = new DispatchConfirmSink(allowClose);
                    if (TryAdvise(ocx, sink, out var manual) && manual != null)
                        return new AdviseResult(manual, "cpc+idispatch after=" + cookieEx + "/" + ex2.GetType().Name);
                }
                catch (Exception ex3)
                {
                    return new AdviseResult(null, "adviseFailed cookie=" + cookieEx
                        + " iface=" + ex2.GetType().Name
                        + " idisp=" + ex3.GetType().Name + ":" + ex3.Message);
                }
            }
        }

        // 2) 手动 CPC（若 cookie 未抛但返回了空——不应发生）
        try
        {
            var sink = new ConfirmCloseSink(allowClose);
            if (TryAdvise(ocx, sink, out var manual) && manual != null)
                return new AdviseResult(manual, "cpc+iface");
        }
        catch { /* fall through */ }

        try
        {
            var sink = new DispatchConfirmSink(allowClose);
            if (TryAdvise(ocx, sink, out var manual) && manual != null)
                return new AdviseResult(manual, "cpc+idispatch");
        }
        catch (Exception ex)
        {
            return new AdviseResult(null, "adviseFailed " + ex.GetType().Name + ":" + ex.Message);
        }

        return new AdviseResult(null, "adviseFailed no-cp");
    }

    static bool TryAdvise(object ocx, object sink, out IDisposable? cookie)
    {
        cookie = null;
        if (!TryGetCpc(ocx, out var cpc) || cpc == null) return false;
        var iid = EventsIid;
        cpc.FindConnectionPoint(ref iid, out var cp);
        if (cp == null) return false;
        cp.Advise(sink, out var id);
        cookie = new ManualCookie(cp, id);
        return true;
    }

    sealed class CookieDisposable : IDisposable
    {
        readonly AxHost.ConnectionPointCookie _cookie;
        public CookieDisposable(AxHost.ConnectionPointCookie cookie) => _cookie = cookie;
        public void Dispose()
        {
            try { _cookie.Disconnect(); } catch { /* ignore */ }
        }
    }

    static bool TryGetCpc(object ocx, out IConnectionPointContainer? cpc)
    {
        cpc = ocx as IConnectionPointContainer;
        if (cpc != null) return true;
        var unk = Marshal.GetIUnknownForObject(ocx);
        try
        {
            var iid = typeof(IConnectionPointContainer).GUID;
            var hr = Marshal.QueryInterface(unk, ref iid, out var p);
            if (hr != 0 || p == IntPtr.Zero) return false;
            cpc = (IConnectionPointContainer)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            return true;
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    sealed class ManualCookie : IDisposable
    {
        readonly IConnectionPoint _cp;
        readonly int _cookie;
        public ManualCookie(IConnectionPoint cp, int cookie) { _cp = cp; _cookie = cookie; }
        public void Dispose()
        {
            try { _cp.Unadvise(_cookie); } catch { /* ignore */ }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IMsTscAxEvents))]
    sealed class ConfirmCloseSink : IMsTscAxEvents
    {
        readonly Func<bool> _allowClose;
        public ConfirmCloseSink(Func<bool> allowClose) => _allowClose = allowClose;

        public void OnConnecting() { }
        public void OnConnected() { }
        public void OnLoginComplete() { }
        public void OnDisconnected(int discReason) { }
        public void OnEnterFullScreenMode() { }
        public void OnLeaveFullScreenMode() { }
        public void OnChannelReceivedData(string channelName, string data) { }
        public void OnRequestGoFullScreen() { }
        public void OnRequestLeaveFullScreen() { }
        public void OnFatalError(int errorCode) { }
        public void OnWarning(int warningCode) { }
        public void OnRemoteDesktopSizeChange(int width, int height) { }
        public void OnIdleTimeoutNotification() { }
        public void OnRequestContainerMinimize() { }

        public void OnConfirmClose([MarshalAs(UnmanagedType.VariantBool)] ref bool pfAllowClose)
        {
            try { pfAllowClose = _allowClose(); }
            catch { pfAllowClose = false; }
        }

        public void OnReceivedTSPublicKey(string publicKey, ref bool pfContinueLogon) { pfContinueLogon = true; }
        public void OnAutoReconnecting(int disconnectReason, int attemptCount, ref AutoReconnectContinueState state) { }
        public void OnAuthenticationWarningDisplayed() { }
        public void OnAuthenticationWarningDismissed() { }
        public void OnRemoteProgramResult(string bstrRemoteProgram, RemoteProgramResult lError, bool vbIsExecutable) { }
        public void OnRemoteProgramDisplayed(bool vbDisplayed, uint uDisplayInformation) { }
        public void OnRemoteWindowDisplayed(bool vbDisplayed, ref FreeRdpRect wnd, RemoteWindowDisplayedAttribute windowAttribute) { }
        public void OnLogonError(int lError) { }
        public void OnFocusReleased(int iDirection) { }
        public void OnUserNameAcquired(string bstrUserName) { }
        public void OnMouseInputModeChanged(bool fMouseModeRelative) { }
        public void OnServiceMessageReceived(string serviceMessage) { }
        public void OnConnectionBarPullDown() { }
        public void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt) { }
        public void OnDevicesButtonPressed() { }
        public void OnAutoReconnected() { }
        public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount, ref AutoReconnectContinueState state) { }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    sealed class DispatchConfirmSink : IDispatch
    {
        readonly Func<bool> _allowClose;
        public DispatchConfirmSink(Func<bool> allowClose) => _allowClose = allowClose;

        public int GetTypeInfoCount(out int pctinfo) { pctinfo = 0; return 0; }
        public int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo) { ppTInfo = IntPtr.Zero; return 0; }
        public int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId) =>
            unchecked((int)0x80020006);

        public int Invoke(int dispIdMember, ref Guid riid, int lcid, short wFlags, ref DISPPARAMS pDispParams,
            IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr)
        {
            if (dispIdMember != DispidOnConfirmClose)
                return 0;

            var allow = false;
            try { allow = _allowClose(); }
            catch { allow = false; }

            WriteAllowClose(ref pDispParams, allow);
            return 0;
        }

        static void WriteAllowClose(ref DISPPARAMS p, bool allow)
        {
            if (p.cArgs < 1 || p.rgvarg == IntPtr.Zero) return;
            var vtBool = allow ? (short)-1 : (short)0;
            try
            {
                var v = Marshal.PtrToStructure<Variant>(p.rgvarg);
                if ((v.vt & 0x4000) != 0 && v.ptr != IntPtr.Zero)
                {
                    if ((v.vt & 0x0FFF) == 12)
                    {
                        Marshal.WriteInt16(v.ptr, 0, 11);
                        Marshal.WriteInt16(v.ptr, 8, vtBool);
                    }
                    else
                    {
                        Marshal.WriteInt16(v.ptr, vtBool);
                    }
                    return;
                }
                if (v.vt == 11)
                    Marshal.WriteInt16(p.rgvarg, 8, vtBool);
            }
            catch
            {
                try { Marshal.WriteInt16(p.rgvarg, 8, vtBool); } catch { /* ignore */ }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Variant
        {
            public short vt;
            public short w1, w2, w3;
            public IntPtr ptr;
        }
    }

    public enum AutoReconnectContinueState
    {
        autoReconnectContinueAutomatic = 0,
        autoReconnectContinueStop = 1,
        autoReconnectContinueManual = 2
    }

    public enum RemoteProgramResult
    {
        remoteAppResultOk = 0
    }

    public enum RemoteWindowDisplayedAttribute
    {
        remoteWindowDisplayedAttributeNone = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FreeRdpRect
    {
        public int left, top, right, bottom;
    }

    /// <summary>DispId 与本机 mstscax.dll 类型库一致。</summary>
    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IMsTscAxEvents
    {
        [DispId(1)] void OnConnecting();
        [DispId(2)] void OnConnected();
        [DispId(3)] void OnLoginComplete();
        [DispId(4)] void OnDisconnected(int discReason);
        [DispId(5)] void OnEnterFullScreenMode();
        [DispId(6)] void OnLeaveFullScreenMode();
        [DispId(7)] void OnChannelReceivedData(string channelName, string data);
        [DispId(8)] void OnRequestGoFullScreen();
        [DispId(9)] void OnRequestLeaveFullScreen();
        [DispId(10)] void OnFatalError(int errorCode);
        [DispId(11)] void OnWarning(int warningCode);
        [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
        [DispId(13)] void OnIdleTimeoutNotification();
        [DispId(14)] void OnRequestContainerMinimize();
        [DispId(15)] void OnConfirmClose([MarshalAs(UnmanagedType.VariantBool)] ref bool pfAllowClose);
        [DispId(16)] void OnReceivedTSPublicKey(string publicKey, ref bool pfContinueLogon);
        [DispId(17)] void OnAutoReconnecting(int disconnectReason, int attemptCount, ref AutoReconnectContinueState state);
        [DispId(18)] void OnAuthenticationWarningDisplayed();
        [DispId(19)] void OnAuthenticationWarningDismissed();
        [DispId(20)] void OnRemoteProgramResult(string bstrRemoteProgram, RemoteProgramResult lError, bool vbIsExecutable);
        [DispId(21)] void OnRemoteProgramDisplayed(bool vbDisplayed, uint uDisplayInformation);
        [DispId(29)] void OnRemoteWindowDisplayed(bool vbDisplayed, ref FreeRdpRect wnd, RemoteWindowDisplayedAttribute windowAttribute);
        [DispId(22)] void OnLogonError(int lError);
        [DispId(23)] void OnFocusReleased(int iDirection);
        [DispId(24)] void OnUserNameAcquired(string bstrUserName);
        [DispId(26)] void OnMouseInputModeChanged(bool fMouseModeRelative);
        [DispId(28)] void OnServiceMessageReceived(string serviceMessage);
        [DispId(30)] void OnConnectionBarPullDown();
        [DispId(32)] void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt);
        [DispId(35)] void OnDevicesButtonPressed();
        [DispId(33)] void OnAutoReconnected();
        [DispId(34)] void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount, ref AutoReconnectContinueState state);
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDispatch
    {
        int GetTypeInfoCount(out int pctinfo);
        int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);
        int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId);
        int Invoke(int dispIdMember, ref Guid riid, int lcid, short wFlags, ref DISPPARAMS pDispParams,
            IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
    }
}
