using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>
/// 拦截 OCX 全屏连接栏（BBarWindowClass）右侧关闭按钮点击。
/// MSDN：OnConfirmClose 仅在容器调用 RequestClose 时触发；连接栏 X 不会走该事件。
/// </summary>
sealed class ConnectionBarCloseGuard : IDisposable
{
    const int WmLButtonDown = 0x0201;
    const int WmLButtonUp = 0x0202;
    const int WmLButtonDblClk = 0x0203;
    const int CloseHitWidthPx = 40;

    readonly int _pid;
    readonly Control _marshalTo;
    readonly Action _onCloseClicked; // 已吞掉原生关闭；调用方负责确认+断开
    readonly Action<string> _log;
    Native.WndProc? _wndProcKeepAlive;
    IntPtr _bbar = IntPtr.Zero;
    IntPtr _oldWndProc = IntPtr.Zero;
    bool _subclassed;
    bool _clickQueued;
    bool _disposed;

    public ConnectionBarCloseGuard(int pid, Control marshalTo, Action onCloseClicked, Action<string>? log = null)
    {
        _pid = pid;
        _marshalTo = marshalTo;
        _onCloseClicked = onCloseClicked;
        _log = log ?? (_ => { });
    }

    public bool IsSubclassed => _subclassed && _bbar != IntPtr.Zero && Native.IsWindow(_bbar);

    public bool TryAttach()
    {
        if (_disposed) return false;
        if (IsSubclassed) return true;

        DetachSubclassOnly();
        var found = FindBBar(_pid);
        if (found == IntPtr.Zero)
        {
            _log("bbar=notFound");
            return false;
        }

        _wndProcKeepAlive = SubclassProc;
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
        Marshal.SetLastPInvokeError(0);
        var prev = Native.SetWindowLongPtr(found, Native.GwlpWndProc, newProc);
        var err = Marshal.GetLastPInvokeError();
        // prev==0 也可能是合法旧 WndProc；仅当显式 Win32 错误时判失败
        if (prev == IntPtr.Zero && err != 0)
        {
            _log("bbar=subclassFail hwnd=0x" + found.ToInt64().ToString("X") + " err=" + err);
            _wndProcKeepAlive = null;
            return false;
        }

        _bbar = found;
        _oldWndProc = prev;
        _subclassed = true;
        _log("bbar=subclassed hwnd=0x" + found.ToInt64().ToString("X") + " cls=" + GetClass(found));
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachSubclassOnly();
        _wndProcKeepAlive = null;
    }

    void DetachSubclassOnly()
    {
        if (_subclassed && _bbar != IntPtr.Zero && _oldWndProc != IntPtr.Zero && Native.IsWindow(_bbar))
        {
            try { Native.SetWindowLongPtr(_bbar, Native.GwlpWndProc, _oldWndProc); }
            catch { /* ignore */ }
        }
        _subclassed = false;
        _bbar = IntPtr.Zero;
        _oldWndProc = IntPtr.Zero;
    }

    IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WmLButtonDown or WmLButtonUp or WmLButtonDblClk)
        {
            if (IsCloseHit(hWnd, lParam))
            {
                // 永远吞掉关闭热区点击，避免 OCX 无确认 Disconnect
                if (msg == WmLButtonUp && !_clickQueued && !_disposed)
                {
                    _clickQueued = true;
                    _log("bbar=closeHit-queue");
                    try
                    {
                        if (_marshalTo.IsHandleCreated && !_marshalTo.IsDisposed)
                        {
                            _marshalTo.BeginInvoke(new Action(() =>
                            {
                                try { _onCloseClicked(); }
                                catch (Exception ex) { _log("bbar=closeCbErr=" + ex.Message); }
                                finally { _clickQueued = false; }
                            }));
                        }
                        else
                        {
                            _clickQueued = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _clickQueued = false;
                        _log("bbar=queueErr=" + ex.Message);
                    }
                }
                return IntPtr.Zero;
            }
        }

        return Native.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    static bool IsCloseHit(IntPtr hWnd, IntPtr lParam)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        if (!Native.GetClientRect(hWnd, out var rc)) return false;
        var width = rc.Right - rc.Left;
        if (width <= 0) return false;
        // 连接栏关闭钮在最右侧；RTL 极罕见，忽略
        return x >= width - CloseHitWidthPx;
    }

    static IntPtr FindBBar(int pid)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var wpid);
            if ((int)wpid != pid) return true;
            if (IsBBarClass(hwnd))
            {
                found = hwnd;
                return false;
            }
            // BBar 有时是 TscShellContainer / UIMain 的子窗
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                if (IsBBarClass(child))
                {
                    found = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    static bool IsBBarClass(IntPtr hwnd)
    {
        var cls = GetClass(hwnd);
        return cls.Equals("BBarWindowClass", StringComparison.OrdinalIgnoreCase)
               || cls.Equals("BBar", StringComparison.OrdinalIgnoreCase);
    }

    static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static class Native
    {
        public const int GwlpWndProc = -4;

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }
    }
}
