using RdpPool.Config;

namespace RdpPoolSetup;

sealed class SetupForm : Form
{
    readonly TextBox _apiUrl = new() { Width = 360 };
    readonly TextBox _login = new() { Width = 360 };
    readonly TextBox _password = new() { Width = 360, UseSystemPasswordChar = true };
    readonly ComboBox _display = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    readonly CheckBox _skipMstsc = new()
    {
        Text = "【调试】不启动远程桌面（SkipMstsc — 勾选后客户端选组会立刻退出）",
        AutoSize = true
    };
    readonly CheckBox _autoRelease = new() { Text = "退出时自动释放会话", AutoSize = true, Checked = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0) };
    readonly Button _btnSave = new() { Text = "保存", Width = 90, Height = 28 };
    readonly Button _btnSaveDisplay = new() { Text = "仅保存显示", Width = 100, Height = 28 };
    readonly Button _btnCancel = new() { Text = "取消", Width = 90, Height = 28 };
    readonly string _configPath;
    bool _busy;

    public SetupForm()
    {
        _configPath = ResolveConfigPath();

        Text = "RDP 资源池 - 配置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 420);
        Font = new Font("Microsoft YaHei UI", 9F);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            Icon = SystemIcons.Application;
        }

        foreach (var (value, label) in LocalDisplay.Options)
            _display.Items.Add(new DisplayOpt(value, label));
        _display.DisplayMember = nameof(DisplayOpt.Label);
        _display.ValueMember = nameof(DisplayOpt.Value);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 10
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
        AddRow(3, "显示分辨率", _display);

        layout.SetColumnSpan(_skipMstsc, 2);
        layout.Controls.Add(_skipMstsc, 0, 4);
        layout.SetColumnSpan(_autoRelease, 2);
        layout.Controls.Add(_autoRelease, 0, 5);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false
        };
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnSaveDisplay.Click += (_, _) => SaveDisplayOnly();
        _btnCancel.Click += (_, _) => Close();
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
        buttons.Controls.Add(_btnSave);
        buttons.Controls.Add(_btnSaveDisplay);
        buttons.Controls.Add(_btnCancel);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 6);

        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 7);

        var hint = new Label
        {
            Text = "「保存」会先校验账密再加密写入。「仅保存显示」只改本机分辨率，无需重新登录。",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.Gray
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 8);

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

    void SelectDisplay(string? value)
    {
        var n = LocalDisplay.Normalize(value);
        for (var i = 0; i < _display.Items.Count; i++)
        {
            if (_display.Items[i] is DisplayOpt o && o.Value == n)
            {
                _display.SelectedIndex = i;
                return;
            }
        }
        if (_display.Items.Count > 0)
            _display.SelectedIndex = 0;
    }

    string SelectedDisplayValue()
    {
        if (_display.SelectedItem is DisplayOpt o)
            return o.Value;
        return LocalDisplay.Default;
    }

    void LoadExisting()
    {
        _apiUrl.Text = "http://127.0.0.1:8081";
        SelectDisplay(LocalDisplay.Default);
        if (!File.Exists(_configPath))
        {
            _status.Text = "尚未配置，将新建：" + _configPath;
            return;
        }

        var existing = ClientConfigStore.TryLoadExisting(_configPath);
        if (existing == null)
        {
            SelectDisplay(ClientConfigStore.ReadDisplayResolution(_configPath));
            _status.Text = "已有配置文件但无法完整读取，请重新填写账密。显示设置仍可单独保存。";
            return;
        }

        _apiUrl.Text = existing.ApiBaseUrl;
        _login.Text = existing.Login;
        _password.Text = existing.Password;
        _skipMstsc.Checked = existing.SkipMstsc;
        _autoRelease.Checked = existing.AutoRelease;
        SelectDisplay(existing.DisplayResolution);
        _status.Text = string.IsNullOrEmpty(existing.Password) && string.IsNullOrEmpty(existing.Login)
            ? "已加载服务器地址；请填写用户名和密码后保存。"
            : "已加载现有配置（「保存」仍会重新校验登录；改分辨率可用「仅保存显示」）。";
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _btnSave.Enabled = !busy;
        _btnSaveDisplay.Enabled = !busy;
        _btnCancel.Enabled = !busy;
        _apiUrl.Enabled = !busy;
        _login.Enabled = !busy;
        _password.Enabled = !busy;
        _display.Enabled = !busy;
        _skipMstsc.Enabled = !busy;
        _autoRelease.Enabled = !busy;
        UseWaitCursor = busy;
    }

    void SaveDisplayOnly()
    {
        if (_busy) return;
        try
        {
            ClientConfigStore.PatchDisplayResolution(_configPath, SelectedDisplayValue());
            _status.ForeColor = Color.ForestGreen;
            _status.Text = "已保存显示设置（未改动账密）：" + LocalDisplay.LabelFor(SelectedDisplayValue());
            MessageBox.Show(this,
                "显示分辨率已保存到本机配置。\n无需重新登录。\n\n下次连接远程桌面时生效。",
                "已保存显示设置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存显示设置失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task SaveAsync()
    {
        if (_busy) return;

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

        if (_skipMstsc.Checked)
        {
            var confirm = MessageBox.Show(this,
                "已勾选「不启动远程桌面」(SkipMstsc)。\n\n" +
                "保存后运行客户端：登录并选组会成功，但不会打开 RDP，进程会马上退出（常被误认为闪退）。\n\n" +
                "确定要保存该调试选项吗？\n（正常使用请点「否」并取消勾选。）",
                "确认 SkipMstsc",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
                return;
        }

        // 记录保存前文件快照，失败时确保未写入
        string? before = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;
        var beforeWriteTime = File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : (DateTime?)null;

        SetBusy(true);
        _status.ForeColor = Color.DimGray;
        _status.Text = "正在校验登录…";

        AuthVerifyResult verify;
        try
        {
            verify = await ControlPlaneAuth.VerifyLoginAsync(api, login, password);
        }
        finally
        {
            SetBusy(false);
        }

        if (!verify.Ok)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "登录校验失败，未保存配置。";
            MessageBox.Show(this,
                "无法保存：控制面登录校验未通过。\n\n" + verify.Message,
                "登录失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            // 防御：若意外写过文件则回滚
            EnsureConfigUnchanged(before, beforeWriteTime);
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
        baseSettings.DisplayResolution = SelectedDisplayValue();

        try
        {
            ClientConfigStore.Save(_configPath, baseSettings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "登录已通过，但写入配置失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _status.ForeColor = Color.ForestGreen;
        _status.Text = "登录校验通过，已加密保存：" + _configPath;
        MessageBox.Show(this,
            "登录校验成功，配置已保存。\n\n请运行 RdpPoolClient.exe 使用资源池。",
            "保存成功",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    void EnsureConfigUnchanged(string? before, DateTime? beforeWriteTime)
    {
        try
        {
            if (before is null)
            {
                if (File.Exists(_configPath))
                    File.Delete(_configPath);
                return;
            }

            if (!File.Exists(_configPath)) return;
            var afterTime = File.GetLastWriteTimeUtc(_configPath);
            if (beforeWriteTime.HasValue && afterTime > beforeWriteTime.Value.AddMilliseconds(5))
                File.WriteAllText(_configPath, before);
        }
        catch
        {
            // ignore rollback errors
        }
    }

    sealed record DisplayOpt(string Value, string Label);
}
