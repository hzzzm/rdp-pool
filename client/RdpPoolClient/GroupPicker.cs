using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>
/// WinExe 多组时的选组对话框（架构：groups.length &gt; 1 必须展示选组）。
/// RDP_POOL_NO_UI=1 时跳过 UI，供自动化冒烟。
/// </summary>
static class GroupPicker
{
    static readonly Color Accent = Color.FromArgb(0x1A, 0x6B, 0x5A);
    static readonly Color AccentSoft = Color.FromArgb(0xE6, 0xF3, 0xEF);
    static readonly Color Surface = Color.FromArgb(0xF7, 0xF8, 0xF6);
    static readonly Color TextPrimary = Color.FromArgb(0x1C, 0x24, 0x22);
    static readonly Color TextMuted = Color.FromArgb(0x5A, 0x66, 0x62);

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
            Text = "RDP 资源池",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            ClientSize = new Size(480, 420),
            BackColor = Surface,
            Font = new Font("Segoe UI", 9.5F)
        };
        try
        {
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            form.Icon = SystemIcons.Application;
        }

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Accent,
            Padding = new Padding(20, 14, 20, 14)
        };
        header.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(
                header.ClientRectangle,
                Accent,
                Color.FromArgb(0x0F, 0x4A, 0x3E),
                LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillRectangle(brush, header.ClientRectangle);
        };
        var brand = new Label
        {
            Text = "RDP 资源池",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold),
            BackColor = Color.Transparent,
            Location = new Point(20, 12)
        };
        var brandSub = new Label
        {
            Text = "选择要连接的资源组",
            AutoSize = true,
            ForeColor = Color.FromArgb(220, 255, 255, 255),
            Font = new Font("Segoe UI", 9F),
            BackColor = Color.Transparent,
            Location = new Point(22, 40)
        };
        header.Controls.Add(brand);
        header.Controls.Add(brandSub);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16)
        };

        var hint = new Label
        {
            Text = "当前账号可用多个资源组。双击或选中后点「连接」。",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F)
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.White,
            ForeColor = TextPrimary,
            HideSelection = false
        };
        list.Columns.Add("资源组", 220);
        list.Columns.Add("空闲", 70);
        list.Columns.Add("占用", 70);
        list.Columns.Add("合计", 70);

        var prefIdx = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var total = g.IdleCount + g.BusyCount;
            var item = new ListViewItem(new[]
            {
                g.Name,
                g.IdleCount.ToString(),
                g.BusyCount.ToString(),
                total.ToString()
            })
            { Tag = g.Id };
            if (g.IdleCount > 0)
                item.ForeColor = Accent;
            list.Items.Add(item);
            if (!string.IsNullOrEmpty(preferredId) && g.Id == preferredId)
                prefIdx = i;
        }
        if (list.Items.Count > 0)
        {
            list.Items[prefIdx].Selected = true;
            list.EnsureVisible(prefIdx);
            list.FocusedItem = list.Items[prefIdx];
        }

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            Padding = new Padding(0, 8, 0, 0)
        };
        var ok = new Button
        {
            Text = "连接",
            DialogResult = DialogResult.OK,
            Size = new Size(108, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Location = new Point(footer.Width - 228, 10);
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(96, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 9.5F),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0xC5, 0xCE, 0xCA);
        cancel.Location = new Point(footer.Width - 108, 10);
        footer.Resize += (_, _) =>
        {
            ok.Location = new Point(footer.ClientSize.Width - 228, 10);
            cancel.Location = new Point(footer.ClientSize.Width - 108, 10);
        };
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);

        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count > 0)
                form.DialogResult = DialogResult.OK;
        };

        // Dock: Fill first, then Bottom, then Top (last Top wins uppermost among Top)
        body.Controls.Add(list);
        body.Controls.Add(footer);
        body.Controls.Add(hint);

        var strip = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = AccentSoft };
        form.Controls.Add(body);
        form.Controls.Add(strip);
        form.Controls.Add(header);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var result = form.ShowDialog();
        if (result != DialogResult.OK || list.SelectedItems.Count == 0)
            return null;
        return list.SelectedItems[0].Tag as string;
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
