using RdpPool.Config;

namespace RdpPoolClient;

/// <summary>仅改本地显示分辨率；写入 appsettings.json 明文字段，不校验登录。</summary>
sealed class DisplaySettingsForm : Form
{
    readonly ComboBox _combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly string _configPath;

    public DisplaySettingsForm(string configPath)
    {
        _configPath = configPath;
        Text = "显示分辨率";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 160);
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = true;

        var label = new Label
        {
            Text = "远程桌面显示（仅本机，与 mstsc 类似）：",
            AutoSize = true,
            Location = new Point(16, 16)
        };

        _combo.Location = new Point(16, 44);
        foreach (var (value, text) in LocalDisplay.Options)
            _combo.Items.Add(new Opt(value, text));
        _combo.DisplayMember = nameof(Opt.Label);
        _combo.ValueMember = nameof(Opt.Value);

        var current = ClientConfigStore.ReadDisplayResolution(_configPath);
        SelectValue(current);

        var hint = new Label
        {
            Text = "保存后立即写入配置；下次连接生效。无需重新登录。",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            ForeColor = Color.Gray,
            Location = new Point(16, 78)
        };

        var save = new Button
        {
            Text = "保存",
            Width = 88,
            Height = 28,
            Location = new Point(152, 116)
        };
        var cancel = new Button
        {
            Text = "取消",
            Width = 88,
            Height = 28,
            Location = new Point(248, 116),
            DialogResult = DialogResult.Cancel
        };
        save.Click += (_, _) => Save();
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(label);
        Controls.Add(_combo);
        Controls.Add(hint);
        Controls.Add(save);
        Controls.Add(cancel);
    }

    void SelectValue(string value)
    {
        var n = LocalDisplay.Normalize(value);
        for (var i = 0; i < _combo.Items.Count; i++)
        {
            if (_combo.Items[i] is Opt o && o.Value == n)
            {
                _combo.SelectedIndex = i;
                return;
            }
        }
        if (_combo.Items.Count > 0)
            _combo.SelectedIndex = 0;
    }

    void Save()
    {
        if (_combo.SelectedItem is not Opt opt)
        {
            MessageBox.Show(this, "请选择分辨率。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            ClientConfigStore.PatchDisplayResolution(_configPath, opt.Value);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show(this,
            "已保存显示设置：" + opt.Label + "\n\n下次启动远程桌面时生效。",
            "已保存",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>在 STA 线程弹出对话框；供托盘菜单调用。</summary>
    public static void ShowModal(string configPath)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("RDP_POOL_NO_UI"), "1", StringComparison.Ordinal))
            return;

        void Run()
        {
            try
            {
                Application.EnableVisualStyles();
            }
            catch { /* ignore */ }

            using var f = new DisplaySettingsForm(configPath);
            f.ShowDialog();
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Run();
            return;
        }

        Exception? err = null;
        var t = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw err;
    }

    sealed record Opt(string Value, string Label);
}
