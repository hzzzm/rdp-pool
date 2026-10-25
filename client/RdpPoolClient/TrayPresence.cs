namespace RdpPoolClient;

/// <summary>控制台会话期间的托盘常驻（NotifyIcon）。</summary>
static class TrayPresence
{
    public static IDisposable? Start(string tip)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            var holder = new TrayHolder(tip);
            return holder;
        }
        catch
        {
            return null;
        }
    }

    sealed class TrayHolder : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _ready = new(false);
        readonly ManualResetEventSlim _exit = new(false);
        NotifyIcon? _icon;
        Exception? _startError;

        public TrayHolder(string tip)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    Application.SetHighDpiMode(HighDpiMode.SystemAware);
                    Application.EnableVisualStyles();
                    Icon appIcon;
                    try
                    {
                        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath)
                                  ?? SystemIcons.Application;
                    }
                    catch
                    {
                        appIcon = SystemIcons.Application;
                    }

                    _icon = new NotifyIcon
                    {
                        Icon = appIcon,
                        Text = tip.Length > 63 ? tip[..63] : tip,
                        Visible = true
                    };
                    _icon.DoubleClick += (_, _) =>
                    {
                        // 仅提示；主流程在控制台
                        _icon.ShowBalloonTip(2000, "RDP 资源池", "客户端正在运行，请查看控制台窗口。", ToolTipIcon.Info);
                    };
                }
                catch (Exception ex)
                {
                    _startError = ex;
                }
                finally
                {
                    _ready.Set();
                }

                while (!_exit.Wait(200))
                    Application.DoEvents();

                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
            });
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(3000);
            if (_startError != null)
                throw _startError;
        }

        public void Dispose()
        {
            _exit.Set();
            if (!_thread.Join(3000))
            {
                try
                {
                    _icon?.Dispose();
                }
                catch { /* ignore */ }
            }
            _ready.Dispose();
            _exit.Dispose();
        }
    }
}
