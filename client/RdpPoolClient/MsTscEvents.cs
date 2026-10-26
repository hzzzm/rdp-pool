using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>
/// 订阅 IMsTscAxEvents.OnConfirmClose：连接栏关闭/断开前先询问容器，可取消。
/// </summary>
static class MsTscEvents
{
    // IMsTscAxEvents
    public static readonly Guid EventsIid = new("336D5562-EFA8-482E-8CB3-C6C43507329C");
    public const int DispidOnConfirmClose = 15;
    public const int DispidOnDisconnected = 4;

    public static IDisposable? AdviseConfirmClose(object ocx, Func<bool> allowClose)
    {
        try
        {
            var sink = new DispatchSink(allowClose);
            if (ocx is not IConnectionPointContainer cpc)
            {
                // 部分包装不是直接 CPC，试 QI
                var unk = Marshal.GetIUnknownForObject(ocx);
                try
                {
                    var iid = typeof(IConnectionPointContainer).GUID;
                    var hr = Marshal.QueryInterface(unk, ref iid, out var p);
                    if (hr != 0 || p == IntPtr.Zero) return null;
                    cpc = (IConnectionPointContainer)Marshal.GetObjectForIUnknown(p);
                    Marshal.Release(p);
                }
                finally
                {
                    Marshal.Release(unk);
                }
            }

            var iidEv = EventsIid;
            cpc.FindConnectionPoint(ref iidEv, out var cp);
            if (cp == null) return null;
            cp.Advise(sink, out var cookie);
            return new Cookie(cp, cookie);
        }
        catch
        {
            return null;
        }
    }

    sealed class Cookie : IDisposable
    {
        readonly IConnectionPoint _cp;
        readonly int _cookie;
        public Cookie(IConnectionPoint cp, int cookie) { _cp = cp; _cookie = cookie; }
        public void Dispose()
        {
            try { _cp.Unadvise(_cookie); } catch { /* ignore */ }
        }
    }

    /// <summary>最小 IDispatch 槽：只处理 OnConfirmClose。</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    sealed class DispatchSink : IDispatch
    {
        readonly Func<bool> _allowClose;
        public DispatchSink(Func<bool> allowClose) => _allowClose = allowClose;

        public int GetTypeInfoCount(out int pctinfo) { pctinfo = 0; return 0; }
        public int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo) { ppTInfo = IntPtr.Zero; return 0; }
        public int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId) => unchecked((int)0x80020006);

        public int Invoke(int dispIdMember, ref Guid riid, int lcid, short wFlags, ref DISPPARAMS pDispParams,
            IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr)
        {
            if (dispIdMember != DispidOnConfirmClose)
                return 0;
            try
            {
                // OnConfirmClose([out] VARIANT_BOOL* pfAllowClose) — 最后一个参数是 out
                var allow = _allowClose();
                if (pDispParams.cArgs >= 1 && pDispParams.rgvarg != IntPtr.Zero)
                {
                    // 参数倒序；VARIANT_BOOL: -1 true, 0 false
                    var vtBool = allow ? (short)-1 : (short)0;
                    // rgvarg[0] 常为指向 VARIANT 的 out 引用；尽量写入 VT_BOOL
                    try
                    {
                        var v = Marshal.PtrToStructure<VariantBoolOut>(pDispParams.rgvarg);
                        // 若是 VT_BYREF|VT_BOOL
                        if ((v.vt & 0x4000) != 0 && v.pdata != IntPtr.Zero)
                            Marshal.WriteInt16(v.pdata, vtBool);
                    }
                    catch
                    {
                        try { Marshal.WriteInt16(pDispParams.rgvarg, 8, vtBool); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VariantBoolOut
    {
        public short vt;
        public short w1, w2, w3;
        public IntPtr pdata;
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
