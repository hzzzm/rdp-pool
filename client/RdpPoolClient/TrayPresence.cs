using RdpPool.Config;

namespace RdpPoolClient;

/// <summary>控制台会话期间的托盘常驻（NotifyIcon）。</summary>
static class TrayPresence
{
    public static IDisposable? Start(string tip, string? configPath = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            var holder = new TrayHolder(tip, configPath ?? ClientConfigStore.DefaultPath);
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
        readonly string _configPath;
        NotifyIcon? _icon;
        Exception? _startError;

        public TrayHolder(string tip, string configPath)
        {
            _configPath = configPath;
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

                    var menu = new ContextMenuStrip();
                    var displayItem = new ToolStripMenuItem("显示分辨率…");
                    displayItem.Click += (_, _) =>
                    {
                        try
                        {
                            using var f = new DisplaySettingsForm(_configPath);
                            f.ShowDialog();
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("无法打开显示设置：\n" + ex.Message, "RDP 资源池",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };
                    menu.Items.Add(displayItem);
                    menu.Items.Add(new ToolStripSeparator());
                    var aboutItem = new ToolStripMenuItem("关于");
                    aboutItem.Click += (_, _) =>
                    {
                        _icon?.ShowBalloonTip(2500, "RDP 资源池",
                            "客户端正在运行。可在此菜单修改本地显示分辨率（无需重新登录）。",
                            ToolTipIcon.Info);
                    };
                    menu.Items.Add(aboutItem);

                    _icon = new NotifyIcon
                    {
                        Icon = appIcon,
                        Text = tip.Length > 63 ? tip[..63] : tip,
                        Visible = true,
                        ContextMenuStrip = menu
                    };
                    _icon.DoubleClick += (_, _) =>
                    {
                        try
                        {
                            using var f = new DisplaySettingsForm(_configPath);
                            f.ShowDialog();
                        }
                        catch
                        {
                            _icon.ShowBalloonTip(2000, "RDP 资源池", "客户端正在运行，请查看控制台窗口。", ToolTipIcon.Info);
                        }
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
