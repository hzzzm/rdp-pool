using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>
/// WinExe 多组时的选组对话框（架构：groups.length &gt; 1 必须展示选组）。
/// RDP_POOL_NO_UI=1 时跳过 UI，供自动化冒烟。
/// </summary>
static class GroupPicker
{
    public static string? Pick(IReadOnlyList<GroupChoice> groups, string? preferredId)
    {
        if (groups == null || groups.Count == 0) return null;
        if (groups.Count == 1) return groups[0].Id;

        if (string.Equals(Environment.GetEnvironmentVariable("RDP_POOL_NO_UI"), "1", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(preferredId))
            {
                var pref = groups.FirstOrDefault(g => g.Id == preferredId);
                if (pref != null) return pref.Id;
            }
            return groups[0].Id;
        }

        using var form = new Form
        {
            Text = "选择资源组",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            ClientSize = new System.Drawing.Size(420, 280),
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F)
        };

        var hint = new Label
        {
            Text = "当前账号可用多个资源组，请选择后继续连接：",
            AutoSize = false,
            Location = new System.Drawing.Point(16, 12),
            Size = new System.Drawing.Size(388, 36)
        };

        var list = new ListBox
        {
            Location = new System.Drawing.Point(16, 52),
            Size = new System.Drawing.Size(388, 150),
            DisplayMember = nameof(GroupChoice.Display),
            ValueMember = nameof(GroupChoice.Id)
        };
        foreach (var g in groups)
            list.Items.Add(g);

        var prefIdx = 0;
        if (!string.IsNullOrEmpty(preferredId))
        {
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Id == preferredId)
                {
                    prefIdx = i;
                    break;
                }
            }
        }
        if (list.Items.Count > 0)
            list.SelectedIndex = prefIdx;

        var ok = new Button
        {
            Text = "连接",
            DialogResult = DialogResult.OK,
            Location = new System.Drawing.Point(220, 220),
            Size = new System.Drawing.Size(88, 32)
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new System.Drawing.Point(316, 220),
            Size = new System.Drawing.Size(88, 32)
        };

        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItem != null)
                form.DialogResult = DialogResult.OK;
        };

        form.Controls.Add(hint);
        form.Controls.Add(list);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var result = form.ShowDialog();
        if (result != DialogResult.OK || list.SelectedItem is not GroupChoice chosen)
            return null;
        return chosen.Id;
    }
}

sealed class GroupChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int IdleCount { get; init; }
    public int BusyCount { get; init; }

    public string Display =>
        $"{Name}  (空闲 {IdleCount} / 占用 {BusyCount})";
}
