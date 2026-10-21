using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var cfgPath = args.Length > 0 ? args[0] : "appsettings.json";
if (!File.Exists(cfgPath))
{
    Console.Error.WriteLine($"缺少 {cfgPath}。请复制 appsettings.example.json 为 appsettings.json 并填写本地配置。");
    Console.Error.WriteLine("依赖：.NET 6 SDK（https://dotnet.microsoft.com/download）");
    return 1;
}

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var cfg = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(cfgPath), jsonOpts)
          ?? throw new InvalidOperationException("bad config");
using var http = new HttpClient { BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/") };

Console.WriteLine($"API: {cfg.ApiBaseUrl}");
Console.WriteLine("登录中...");
var loginResp = await http.PostAsJsonAsync("api/v1/auth/login", new { login = cfg.Login, password = cfg.Password });
if (!loginResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"登录失败: {(int)loginResp.StatusCode} {await loginResp.Content.ReadAsStringAsync()}");
    return 1;
}
var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(jsonOpts);
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
Console.WriteLine("登录成功");

var groupsResp = await http.GetFromJsonAsync<GroupsResponse>("api/v1/groups/available", jsonOpts);
var groups = groupsResp?.Groups ?? [];
if (groups.Count == 0)
{
    Console.Error.WriteLine("当前账号没有可用的资源组，请联系管理员授权（或先跑 seed）。");
    return 2;
}

string groupId;
if (groups.Count == 1)
{
    groupId = groups[0].Id;
    Console.WriteLine($"仅一组，自动选用: {groups[0].Name}");
}
else
{
    Console.WriteLine("可用组:");
    for (var i = 0; i < groups.Count; i++)
        Console.WriteLine($"  [{i}] {groups[i].Name} idle={groups[i].IdleCount} busy={groups[i].BusyCount}");
    if (!string.IsNullOrEmpty(cfg.PreferredGroupId) && groups.Any(g => g.Id == cfg.PreferredGroupId))
        groupId = cfg.PreferredGroupId;
    else
    {
        Console.Write("输入序号选择组 [0]: ");
        var line = Console.ReadLine();
        var idx = 0;
        _ = int.TryParse(line, out idx);
        if (idx < 0 || idx >= groups.Count) idx = 0;
        groupId = groups[idx].Id;
    }
}

var acquireResp = await http.PostAsJsonAsync("api/v1/sessions/acquire", new
{
    group_id = groupId,
    client_instance_id = string.IsNullOrEmpty(cfg.ClientInstanceId) ? Guid.NewGuid().ToString("N") : cfg.ClientInstanceId
});
var acquireJson = await acquireResp.Content.ReadAsStringAsync();
Console.WriteLine($"Acquire {(int)acquireResp.StatusCode}");

if ((int)acquireResp.StatusCode == 202)
{
    Console.WriteLine("组内排队中。请轮询 GET api/v1/queue/me，或稍后重试。");
    Console.WriteLine(acquireJson);
    return 3;
}

if (!acquireResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine(acquireJson);
    return 4;
}

var acquire = JsonSerializer.Deserialize<AcquireResponse>(acquireJson, jsonOpts);
var ticket = acquire?.RdpTicket;
var session = acquire?.Session;
if (ticket == null || string.IsNullOrEmpty(ticket.Host))
{
    Console.Error.WriteLine("分配成功但无票据: " + acquireJson);
    return 5;
}

Console.WriteLine($"已分配 session={session?.Id} -> {ticket.Host}:{ticket.Port} user={ticket.Username}");

if (cfg.SkipMstsc)
{
    Console.WriteLine("SkipMstsc=true，不启动远程桌面（仅验证 API）。");
}
else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.WriteLine("非 Windows：请手动用 RDP 客户端连接上述 host（本工具仅在 Windows 调 mstsc）。");
}
else if (string.IsNullOrEmpty(ticket.Password))
{
    Console.WriteLine("票据无密码（未 seed DEMO_RDP_PASSWORD）。将用 mstsc /v 打开，需手动输入密码。");
    Process.Start(new ProcessStartInfo
    {
        FileName = "mstsc",
        Arguments = $"/v:{ticket.Host}:{ticket.Port}",
        UseShellExecute = true
    });
}
else
{
    var rdpPath = Path.Combine(Path.GetTempPath(), $"rdp-pool-{session?.Id ?? Guid.NewGuid().ToString("N")}.rdp");
    await File.WriteAllTextAsync(rdpPath, BuildRdpFile(ticket), Encoding.Unicode);
    Console.WriteLine($"已写临时 .rdp: {rdpPath}");
    // cmdkey stores credentials for mstsc (local, cleared after)
    var target = $"TERMSRV/{ticket.Host}";
    Process.Start(new ProcessStartInfo
    {
        FileName = "cmdkey",
        Arguments = $"/generic:{target} /user:{ticket.Username} /pass:{ticket.Password}",
        UseShellExecute = false,
        CreateNoWindow = true
    })?.WaitForExit();
    var mstsc = Process.Start(new ProcessStartInfo
    {
        FileName = "mstsc",
        Arguments = $"\"{rdpPath}\"",
        UseShellExecute = true
    });
    Console.WriteLine("已启动 mstsc。按 Enter 释放会话并清理本地凭据...");
    Console.ReadLine();
    try { Process.Start(new ProcessStartInfo { FileName = "cmdkey", Arguments = $"/delete:{target}", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(); } catch { /* ignore */ }
    try { File.Delete(rdpPath); } catch { /* ignore */ }
}

if (session != null && !string.IsNullOrEmpty(session.Id) && cfg.AutoRelease)
{
    var rel = await http.PostAsync($"api/v1/sessions/{session.Id}/release", null);
    Console.WriteLine($"Release {(int)rel.StatusCode}");
}

return 0;

static string BuildRdpFile(Ticket ticket)
{
    // Password not embedded in .rdp (use cmdkey). Prompt for credentials if missing.
    return string.Join("\r\n",
        $"full address:s:{ticket.Host}:{ticket.Port}",
        "prompt for credentials:i:0",
        "authentication level:i:2",
        "negotiate security layer:i:1",
        "username:s:" + ticket.Username,
        "screen mode id:i:2",
        "");
}

sealed class AppConfig
{
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:8081";
    public string Login { get; set; } = "demo";
    public string Password { get; set; } = "";
    public string PreferredGroupId { get; set; } = "";
    public int HeartbeatIntervalSec { get; set; } = 15;
    public string ClientInstanceId { get; set; } = "";
    public bool SkipMstsc { get; set; }
    public bool AutoRelease { get; set; } = true;
}

sealed class LoginResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";
}

sealed class GroupsResponse
{
    [JsonPropertyName("groups")]
    public List<GroupItem> Groups { get; set; } = [];
}

sealed class GroupItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("idle_count")] public int IdleCount { get; set; }
    [JsonPropertyName("busy_count")] public int BusyCount { get; set; }
}

sealed class AcquireResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("session")] public SessionInfo? Session { get; set; }
    [JsonPropertyName("rdp_ticket")] public Ticket? RdpTicket { get; set; }
}

sealed class SessionInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

sealed class Ticket
{
    [JsonPropertyName("ticket_id")] public string TicketId { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}
