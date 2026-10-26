using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace RdpPoolClient;

/// <summary>
/// 组内排队 UI：展示 ahead、轮询 queue/me，offered/可抢时 claim；可随时取消。
/// </summary>
sealed class QueueWaitForm : Form
{
    static readonly Color Accent = Color.FromArgb(0x1A, 0x6B, 0x5A);
    static readonly Color Surface = Color.FromArgb(0xF7, 0xF8, 0xF6);
    static readonly Color TextMuted = Color.FromArgb(0x5A, 0x66, 0x62);

    readonly HttpClient _http;
    readonly JsonSerializerOptions _json;
    readonly string _groupId;
    readonly string _groupName;
    readonly string _clientInstanceId;
    readonly Label _aheadLabel;
    readonly Label _detailLabel;
    readonly Label _statusLabel;
    readonly ProgressBar _bar;
    readonly System.Windows.Forms.Timer _poll;
    readonly System.Windows.Forms.Timer _pulse;
    int _pulseTick;
    bool _busy;
    bool _canceled;

    public AcquirePayload? Result { get; private set; }
    public bool WasCanceled => _canceled;

    public QueueWaitForm(
        HttpClient http,
        JsonSerializerOptions json,
        string groupId,
        string groupName,
        string clientInstanceId,
        int initialAhead)
    {
        _http = http;
        _json = json;
        _groupId = groupId;
        _groupName = groupName;
        _clientInstanceId = clientInstanceId;

        Text = "RDP 资源池 · 排队中";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(440, 300);
        BackColor = Surface;
        Font = new Font("Segoe UI", 9.5F);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            Icon = SystemIcons.Application;
        }

        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent };
        header.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(
                header.ClientRectangle, Accent, Color.FromArgb(0x0F, 0x4A, 0x3E),
                LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillRectangle(brush, header.ClientRectangle);
        };
        var title = new Label
        {
            Text = "排队等候资源",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 12)
        };
        var sub = new Label
        {
            Text = string.IsNullOrEmpty(groupName) ? groupId : groupName,
            ForeColor = Color.FromArgb(220, 255, 255, 255),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9F),
            AutoSize = true,
            Location = new Point(22, 38)
        };
        header.Controls.Add(title);
        header.Controls.Add(sub);

        _aheadLabel = new Label
        {
            Text = FormatAhead(initialAhead),
            Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold),
            ForeColor = Accent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(0, 12, 0, 0)
        };
        _detailLabel = new Label
        {
            Text = "前方有人释放后将自动为您分配并打开远程桌面。",
            ForeColor = TextMuted,
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(24, 0, 24, 0)
        };
        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Height = 8,
            Dock = DockStyle.Top,
            Margin = new Padding(40, 8, 40, 8)
        };
        var barHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(36, 10, 36, 10)
        };
        _bar.Dock = DockStyle.Fill;
        barHost.Controls.Add(_bar);

        _statusLabel = new Label
        {
            Text = "正在同步排队状态…",
            ForeColor = TextMuted,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 28
        };

        var cancel = new Button
        {
            Text = "取消排队",
            Size = new Size(120, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(0x1C, 0x24, 0x22),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.None
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0xC5, 0xCE, 0xCA);
        cancel.Click += async (_, _) => await CancelAsync();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        cancel.Anchor = AnchorStyles.None;
        footer.Controls.Add(cancel);
        footer.Resize += (_, _) =>
        {
            cancel.Location = new Point((footer.ClientSize.Width - cancel.Width) / 2, 10);
        };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        body.Controls.Add(_statusLabel);
        body.Controls.Add(barHost);
        body.Controls.Add(_detailLabel);
        body.Controls.Add(_aheadLabel);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        _poll = new System.Windows.Forms.Timer { Interval = 2000 };
        _poll.Tick += async (_, _) => await PollAsync();
        _pulse = new System.Windows.Forms.Timer { Interval = 500 };
        _pulse.Tick += (_, _) =>
        {
            _pulseTick++;
            var dots = new string('.', (_pulseTick % 3) + 1);
            if (!_busy && DialogResult == DialogResult.None)
                _statusLabel.Text = "排队中，自动刷新" + dots;
        };

        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK || _canceled) return;
            e.Cancel = true;
            _ = CancelAsync();
        };

        Shown += async (_, _) =>
        {
            _poll.Start();
            _pulse.Start();
            await PollAsync();
        };
    }

    static string FormatAhead(int ahead) =>
        ahead <= 0 ? "即将轮到您" : $"前方还有 {ahead} 人";

    async Task PollAsync()
    {
        if (_busy || _canceled || !IsHandleCreated) return;
        _busy = true;
        try
        {
            // Prefer claim when offered / idle may exist; 202 means still queued.
            using var claimResp = await _http.PostAsJsonAsync("api/v1/sessions/claim", new
            {
                client_instance_id = _clientInstanceId
            });
            var claimJson = await claimResp.Content.ReadAsStringAsync();
            if (claimResp.IsSuccessStatusCode && (int)claimResp.StatusCode != 202)
            {
                var acquired = JsonSerializer.Deserialize<AcquirePayload>(claimJson, _json);
                if (acquired?.RdpTicket != null && !string.IsNullOrEmpty(acquired.RdpTicket.Host))
                {
                    Result = acquired;
                    _poll.Stop();
                    _pulse.Stop();
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
            }

            using var meResp = await _http.GetAsync("api/v1/queue/me");
            if ((int)meResp.StatusCode == 404)
            {
                // Queue gone — maybe session already created.
                using var cur = await _http.GetAsync("api/v1/sessions/current");
                if (cur.IsSuccessStatusCode)
                {
                    var curJson = await cur.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(curJson);
                    if (doc.RootElement.TryGetProperty("session", out var sess) &&
                        sess.ValueKind == JsonValueKind.Object &&
                        sess.TryGetProperty("id", out var sid))
                    {
                        var sessionId = sid.GetString();
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            using var tktResp = await _http.PostAsync($"api/v1/sessions/{sessionId}/ticket", null);
                            var tktJson = await tktResp.Content.ReadAsStringAsync();
                            if (tktResp.IsSuccessStatusCode)
                            {
                                using var tdoc = JsonDocument.Parse(tktJson);
                                var ticketEl = tdoc.RootElement.GetProperty("rdp_ticket");
                                Result = new AcquirePayload
                                {
                                    Status = "allocated",
                                    Session = new SessionPayload { Id = sessionId },
                                    RdpTicket = new TicketPayload
                                    {
                                        TicketId = ticketEl.TryGetProperty("ticket_id", out var ti) ? ti.GetString() ?? "" : "",
                                        Host = ticketEl.GetProperty("host").GetString() ?? "",
                                        Port = ticketEl.TryGetProperty("port", out var p) ? p.GetInt32() : 3389,
                                        Username = ticketEl.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
                                        Password = ticketEl.TryGetProperty("password", out var pw) ? pw.GetString() ?? "" : ""
                                    }
                                };
                                _poll.Stop();
                                _pulse.Stop();
                                DialogResult = DialogResult.OK;
                                Close();
                                return;
                            }
                        }
                    }
                }
                _statusLabel.Text = "排队已结束，请重试连接";
                return;
            }

            if (!meResp.IsSuccessStatusCode) return;
            var q = await meResp.Content.ReadFromJsonAsync<QueueMePayload>(_json);
            if (q == null) return;
            _aheadLabel.Text = FormatAhead(q.Ahead);
            _detailLabel.Text = q.Status == "offered"
                ? "已轮到您，正在领取资源…"
                : $"位置第 {q.Position} 位 · 状态 {q.Status}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "刷新失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    async Task CancelAsync()
    {
        if (_canceled) return;
        _canceled = true;
        _poll.Stop();
        _pulse.Stop();
        _statusLabel.Text = "正在取消排队…";
        try
        {
            await _http.DeleteAsync("api/v1/queue/me");
        }
        catch { /* ignore */ }
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _pulse.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Headless queue wait for RDP_POOL_NO_UI=1.</summary>
static class QueueWaitConsole
{
    public static async Task<AcquirePayload?> WaitAsync(
        HttpClient http,
        JsonSerializerOptions json,
        string clientInstanceId,
        int initialAhead,
        CancellationToken ct = default)
    {
        Console.WriteLine($"组内排队中 ahead={initialAhead}；轮询 queue/me / claim（Ctrl+C 取消需进程级）。");
        while (!ct.IsCancellationRequested)
        {
            using var claimResp = await http.PostAsJsonAsync("api/v1/sessions/claim", new
            {
                client_instance_id = clientInstanceId
            }, ct);
            var claimJson = await claimResp.Content.ReadAsStringAsync(ct);
            if (claimResp.IsSuccessStatusCode && (int)claimResp.StatusCode != 202)
            {
                var acquired = JsonSerializer.Deserialize<AcquirePayload>(claimJson, json);
                if (acquired?.RdpTicket != null && !string.IsNullOrEmpty(acquired.RdpTicket.Host))
                    return acquired;
            }

            using var meResp = await http.GetAsync("api/v1/queue/me", ct);
            if ((int)meResp.StatusCode == 404)
                return null;
            if (meResp.IsSuccessStatusCode)
            {
                var q = await meResp.Content.ReadFromJsonAsync<QueueMePayload>(json, ct);
                if (q != null)
                    Console.WriteLine($"  queue status={q.Status} position={q.Position} ahead={q.Ahead}");
            }
            await Task.Delay(2000, ct);
        }
        return null;
    }
}

sealed class QueueMePayload
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("group_id")] public string GroupId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("ahead")] public int Ahead { get; set; }
}

sealed class AcquirePayload
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("session")] public SessionPayload? Session { get; set; }
    [JsonPropertyName("rdp_ticket")] public TicketPayload? RdpTicket { get; set; }
    [JsonPropertyName("queue")] public QueueMePayload? Queue { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

sealed class SessionPayload
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

sealed class TicketPayload
{
    [JsonPropertyName("ticket_id")] public string TicketId { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}
