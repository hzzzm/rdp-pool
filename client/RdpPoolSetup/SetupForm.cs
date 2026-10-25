using RdpPool.Config;

namespace RdpPoolSetup;

sealed class SetupForm : Form
{
    readonly TextBox _apiUrl = new() { Width = 360 };
    readonly TextBox _login = new() { Width = 360 };
    readonly TextBox _password = new() { Width = 360, UseSystemPasswordChar = true };
    readonly CheckBox _skipMstsc = new() { Text = "仅验证 API（不启动远程桌面）", AutoSize = true };
    readonly CheckBox _autoRelease = new() { Text = "退出时自动释放会话", AutoSize = true, Checked = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0) };
    readonly string _configPath;

    public SetupForm()
    {
        _configPath = ResolveConfigPath();

        Text = "RDP 资源池 - 配置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(int row, string label, Control control)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(control, 1, row);
        }

        AddRow(0, "服务器地址", _apiUrl);
        AddRow(1, "用户名", _login);
        AddRow(2, "密码", _password);

        layout.SetColumnSpan(_skipMstsc, 2);
        layout.Controls.Add(_skipMstsc, 0, 3);
        layout.SetColumnSpan(_autoRelease, 2);
        layout.Controls.Add(_autoRelease, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false
        };
        var btnSave = new Button { Text = "保存", Width = 90, Height = 28 };
        var btnCancel = new Button { Text = "取消", Width = 90, Height = 28 };
        btnSave.Click += (_, _) => Save();
        btnCancel.Click += (_, _) => Close();
        AcceptButton = btnSave;
        CancelButton = btnCancel;
        buttons.Controls.Add(btnSave);
        buttons.Controls.Add(btnCancel);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 5);

        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 6);

        var hint = new Label
        {
            Text = "账密使用本机当前用户 DPAPI 加密后写入 appsettings.json；请先保存再运行 RdpPoolClient.exe。",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.Gray
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 7);

        Controls.Add(layout);
        LoadExisting();
    }

    static string ResolveConfigPath()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            return Path.GetFullPath(args[1]);
        return ClientConfigStore.DefaultPath;
    }

    void LoadExisting()
    {
        _apiUrl.Text = "http://127.0.0.1:8081";
        if (!File.Exists(_configPath))
        {
            _status.Text = "尚未配置，将新建：" + _configPath;
            return;
        }

        var existing = ClientConfigStore.TryLoadExisting(_configPath);
        if (existing == null)
        {
            _status.Text = "已有配置文件但无法完整读取，请重新填写账密。";
            return;
        }

        _apiUrl.Text = existing.ApiBaseUrl;
        _login.Text = existing.Login;
        _password.Text = existing.Password;
        _skipMstsc.Checked = existing.SkipMstsc;
        _autoRelease.Checked = existing.AutoRelease;
        _status.Text = string.IsNullOrEmpty(existing.Password) && string.IsNullOrEmpty(existing.Login)
            ? "已加载服务器地址；请填写用户名和密码后保存。"
            : "已加载现有配置（密码已解密到输入框，保存后仍以密文落盘）。";
    }

    void Save()
    {
        var api = _apiUrl.Text.Trim();
        var login = _login.Text.Trim();
        var password = _password.Text;

        if (string.IsNullOrWhiteSpace(api))
        {
            MessageBox.Show(this, "请填写服务器地址（ApiBaseUrl）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "服务器地址须为 http:// 或 https:// URL。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(login))
        {
            MessageBox.Show(this, "请填写用户名。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show(this, "请填写密码。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ClientSettings baseSettings;
        try
        {
            baseSettings = File.Exists(_configPath)
                ? ClientConfigStore.Load(_configPath, migratePlaintextToDisk: false)
                : new ClientSettings();
        }
        catch
        {
            baseSettings = new ClientSettings();
        }

        baseSettings.ApiBaseUrl = api;
        baseSettings.Login = login;
        baseSettings.Password = password;
        baseSettings.SkipMstsc = _skipMstsc.Checked;
        baseSettings.AutoRelease = _autoRelease.Checked;

        try
        {
            ClientConfigStore.Save(_configPath, baseSettings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _status.ForeColor = Color.ForestGreen;
        _status.Text = "已保存（账密已 DPAPI 加密）：" + _configPath;
        MessageBox.Show(this,
            "配置已保存。\n\n请运行 RdpPoolClient.exe 登录资源池。",
            "保存成功",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }
}
