using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>强制把窗口拉到前台（绕过 Windows 前台锁，尽量盖住资源管理器）。</summary>
static class ForegroundUtil
{
    const int SwRestore = 9;
    const uint AsfwAny = 0xFFFFFFFF;

    public static void AllowThisProcess()
    {
        try { AllowSetForegroundWindow(AsfwAny); } catch { /* ignore */ }
        try { AllowSetForegroundWindow((uint)Process.GetCurrentProcess().Id); } catch { /* ignore */ }
    }

    public static void Activate(Form form)
    {
        if (form == null || form.IsDisposed) return;
        try
        {
            AllowThisProcess();
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            form.Show();
            form.Activate();
            form.BringToFront();
            ForceForeground(form.Handle);
        }
        catch { /* ignore */ }
    }

    /// <summary>全屏后宿主已藏起：在本进程中找最大的可见顶层窗（OCX 全屏表面）并置前。</summary>
    public static void ActivateOcxFullscreenSurface(int preferPid, string? titleHint)
    {
        try
        {
            AllowThisProcess();
            var best = IntPtr.Zero;
            var bestArea = 0;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if ((int)pid != preferPid) return true;
                GetWindowRect(hwnd, out var rc);
                var w = rc.Right - rc.Left;
                var h = rc.Bottom - rc.Top;
                if (w < 400 || h < 300) return true;
                var area = w * h;
                // 优先标题含 host 的窗；否则取面积最大
                var title = GetWindowText(hwnd);
                var hintHit = !string.IsNullOrEmpty(titleHint) &&
                              title.IndexOf(titleHint, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hintHit && area >= bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }
                else if (best == IntPtr.Zero && area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }
                else if (!hintHit && area > bestArea * 1.1)
                {
                    bestArea = area;
                    best = hwnd;
                }
                return true;
            }, IntPtr.Zero);

            if (best != IntPtr.Zero)
                ForceForeground(best);
        }
        catch { /* ignore */ }
    }

    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SwRestore);
            var fg = GetForegroundWindow();
            var fgTid = GetWindowThreadProcessId(fg, out _);
            var curTid = GetCurrentThreadId();
            if (fgTid != 0 && fgTid != curTid)
            {
                AttachThreadInput(fgTid, curTid, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(fgTid, curTid, false);
                }
            }
            else
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
        }
        catch { /* ignore */ }
    }

    static string GetWindowText(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
}
