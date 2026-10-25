using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
var groups = groupsResp?.Groups ?? new List<GroupItem>();
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
    Console.WriteLine(RedactSecrets(acquireJson));
    return 3;
}

if (!acquireResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine(RedactSecrets(acquireJson));
    return 4;
}

var acquire = JsonSerializer.Deserialize<AcquireResponse>(acquireJson, jsonOpts);
var ticket = acquire?.RdpTicket;
var session = acquire?.Session;
if (ticket == null || string.IsNullOrEmpty(ticket.Host))
{
    Console.Error.WriteLine("分配成功但无票据: " + RedactSecrets(acquireJson));
    return 5;
}

var hasPass = !string.IsNullOrEmpty(ticket.Password);
Console.WriteLine($"已分配 session={session?.Id} -> {ticket.Host}:{ticket.Port} user={ticket.Username} has_password={hasPass}");

string? rdpPath = null;
var cmdTargets = new List<string>();
var cleanupDone = false;

void CleanupLocalCreds()
{
    if (cleanupDone) return;
    cleanupDone = true;
    foreach (var t in cmdTargets)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/delete:{t}",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(5000);
        }
        catch { /* ignore */ }
    }
    if (rdpPath != null)
    {
        try { File.Delete(rdpPath); } catch { /* ignore */ }
    }
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    CleanupLocalCreds();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupLocalCreds();

try
{
    if (cfg.SkipMstsc)
    {
        Console.WriteLine("SkipMstsc=true，不启动远程桌面（仅验证 API）。");
    }
    else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        Console.WriteLine("非 Windows：请手动用 RDP 客户端连接上述 host（本工具仅在 Windows 调 mstsc）。");
    }
    else if (!hasPass)
    {
        Console.WriteLine("票据无密码（服务端未配置资源凭据）。将用 mstsc /v 打开，需手动输入密码。");
        Process.Start(new ProcessStartInfo
        {
            FileName = "mstsc",
            Arguments = $"/v:{ticket.Host}:{ticket.Port}",
            UseShellExecute = true
        });
    }
    else
    {
        var localUser = RdpLaunch.FormatLocalRdpUser(ticket.Host, ticket.Username);
        RdpLaunch.PrepareTrustedServer(ticket.Host, localUser);

        // .rdp（password 51 DPAPI）+ cmdkey；ActiveX ClearTextPassword 为主路径，避免 CredentialUIBroker
        rdpPath = Path.Combine(Path.GetTempPath(), $"rdp-pool-{session?.Id ?? Guid.NewGuid().ToString("N")}.rdp");
        await File.WriteAllTextAsync(rdpPath, RdpLaunch.BuildRdpFile(ticket), Encoding.ASCII);

        var users = new[] { ticket.Username, localUser }.Distinct(StringComparer.OrdinalIgnoreCase);
        cmdTargets.Add($"TERMSRV/{ticket.Host}");
        if (ticket.Port > 0 && ticket.Port != 3389)
            cmdTargets.Add($"TERMSRV/{ticket.Host}:{ticket.Port}");
        foreach (var target in cmdTargets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var u in users)
                RdpLaunch.RunCmdkeyAdd(target, u, ticket.Password);
        }
        Console.WriteLine("已预存 cmdkey + .rdp password51（退出时清理）");

        var port = ticket.Port > 0 ? ticket.Port : 3389;
        var holdSec = cfg.ConnectHoldSec;
        Console.WriteLine(holdSec > 0
            ? $"尝试 ActiveX 直连（Connected 后保持 {holdSec}s）..."
            : "尝试 ActiveX 直连（控制面账密，不经 mstsc 凭据框）...");
        var axOk = false;
        var axDetail = "";
        var axThread = new Thread(() =>
        {
            axOk = RdpPoolClient.RdpAxSession.TryConnect(
                ticket.Host, port, ticket.Username, ticket.Password, holdSec, out axDetail);
        });
        axThread.SetApartmentState(ApartmentState.STA);
        axThread.Start();
        axThread.Join();

        if (axOk)
        {
            Console.WriteLine("ActiveX 会话已结束: " + axDetail);
        }
        else
        {
            Console.WriteLine("ActiveX 不可用/未连通，回退 mstsc +.rdp: " + axDetail);
            var mstsc = Process.Start(new ProcessStartInfo
            {
                FileName = "mstsc",
                Arguments = "\"" + rdpPath + "\"",
                UseShellExecute = true
            });
            _ = Task.Run(() => RdpLaunch.TryAutoFillSecurityWarning(ticket.Password, TimeSpan.FromSeconds(20)));
            if (holdSec > 0)
            {
                Console.WriteLine($"mstsc 已启动，等待 {holdSec}s 后释放（冒烟）...");
                Thread.Sleep(holdSec * 1000);
                try { if (mstsc is { HasExited: false }) mstsc.CloseMainWindow(); } catch { /* ignore */ }
            }
            else
            {
                Console.WriteLine("已启动 mstsc。按 Enter 释放会话并清理本地凭据...");
                Console.ReadLine();
            }
            try { mstsc?.Refresh(); } catch { /* ignore */ }
        }
    }
}
finally
{
    CleanupLocalCreds();
}

if (session != null && !string.IsNullOrEmpty(session.Id) && cfg.AutoRelease)
{
    var rel = await http.PostAsync($"api/v1/sessions/{session.Id}/release", null);
    Console.WriteLine($"Release {(int)rel.StatusCode}");
}

return 0;

static string RedactSecrets(string json) =>
    Regex.Replace(json, "\"password\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"", "\"password\":\"***\"", RegexOptions.IgnoreCase);

static class RdpLaunch
{
    public static string FormatLocalRdpUser(string host, string username)
    {
        if (string.IsNullOrEmpty(username)) return username;
        if (username.Contains('\\') || username.Contains('@')) return username;
        // 本机账户：用 host\user，避免 mstsc 套用客户端域去弹「用其他账户」
        return host + "\\" + username;
    }

    public static void PrepareTrustedServer(string host, string username)
    {
        try
        {
            // 降低「未知远程连接」安全警告打断概率（当前用户）；用 reg.exe 避免额外 NuGet
            RunHidden("reg", "add \"HKCU\\Software\\Microsoft\\Terminal Server Client\" /v AuthenticationLevelOverride /t REG_DWORD /d 0 /f");
            var serverKey = "HKCU\\Software\\Microsoft\\Terminal Server Client\\Servers\\" + host;
            RunHidden("reg", "add \"" + serverKey + "\" /f");
            if (!string.IsNullOrEmpty(username))
                RunHidden("reg", "add \"" + serverKey + "\" /v UsernameHint /t REG_SZ /d \"" + username.Replace("\"", "") + "\" /f");
        }
        catch
        {
            // 无注册表权限时仍依赖 .rdp / cmdkey
        }
    }

    static void RunHidden(string fileName, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        p?.WaitForExit(10000);
    }

    public static void RunCmdkeyAdd(string target, string user, string password)
    {
        var passArg = password.Replace("\"", "\\\"");
        var psi = new ProcessStartInfo
        {
            FileName = "cmdkey",
            Arguments = "/generic:" + target + " /user:\"" + user + "\" /pass:\"" + passArg + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
    }

    public static string BuildRdpFile(Ticket ticket)
    {
        // username 用 host\user，配合 cmdkey TERMSRV 目标，减少「用其他账户」框
        var user = FormatLocalRdpUser(ticket.Host, ticket.Username);
        var lines = new List<string>
        {
            "full address:s:" + ticket.Host + ":" + ticket.Port,
            "prompt for credentials:i:0",
            "promptcredentialonce:i:0",
            // 0=连接且不因服务器身份告警打断（自签/IP 场景）；凭据来自 password 51 / cmdkey
            "authentication level:i:0",
            "enablecredsspsupport:i:1",
            "negotiate security layer:i:1",
            "username:s:" + user,
            "domain:s:",
            "screen mode id:i:2",
            "autoreconnection enabled:i:1",
        };
        if (!string.IsNullOrEmpty(ticket.Password))
        {
            var hex = ProtectPasswordForRdp(ticket.Password);
            if (!string.IsNullOrEmpty(hex))
                lines.Add("password 51:b:" + hex);
        }
        lines.Add("");
        return string.Join("\r\n", lines);
    }

    /// <summary>Windows mstsc 认可的 .rdp 密码字段：CryptProtectData → hex。</summary>
    static string ProtectPasswordForRdp(string password)
    {
        var plain = Encoding.Unicode.GetBytes(password);
        var blobIn = new DATA_BLOB();
        var blobOut = new DATA_BLOB();
        blobIn.cbData = plain.Length;
        blobIn.pbData = Marshal.AllocHGlobal(plain.Length);
        try
        {
            Marshal.Copy(plain, 0, blobIn.pbData, plain.Length);
            // szDataDescr 保持 null；与 mstsc「保存密码」行为一致
            if (!CryptProtectData(ref blobIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blobOut))
                return "";
            try
            {
                var enc = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, enc, 0, blobOut.cbData);
                return BitConverter.ToString(enc).Replace("-", "");
            }
            finally
            {
                if (blobOut.pbData != IntPtr.Zero)
                    LocalFree(blobOut.pbData);
            }
        }
        finally
        {
            if (blobIn.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(blobIn.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Win11/Win10 对未知发行者 .rdp 或身份校验会弹出「远程桌面连接安全警告」并要密码。
    /// 在短窗内检测该对话框，填入控制面密码并回车，避免用户再手输。
    /// </summary>
    public static void TryAutoFillSecurityWarning(string password, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(password)) return;
        var deadline = DateTime.UtcNow + timeout;
        string[] titles =
        {
            "远程桌面连接安全警告",
            "Remote Desktop Connection Security Warning",
            "Windows 安全中心",
            "Windows Security",
            "输入你的凭据"
        };
        while (DateTime.UtcNow < deadline)
        {
            foreach (var title in titles)
            {
                var hwnd = FindWindow(null, title);
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
                    continue;
                SetForegroundWindow(hwnd);
                Thread.Sleep(250);
                // 焦点通常在密码框；先 Ctrl+A 清掉再输入
                keybd_event(0x11, 0, 0, UIntPtr.Zero); // Ctrl
                keybd_event(0x41, 0, 0, UIntPtr.Zero); // A
                keybd_event(0x41, 0, 2, UIntPtr.Zero);
                keybd_event(0x11, 0, 2, UIntPtr.Zero);
                SendChars(password);
                Thread.Sleep(120);
                // 安全警告：连接(N)；凭据框：Enter
                if (title.Contains("安全警告", StringComparison.Ordinal) ||
                    title.Contains("Security Warning", StringComparison.OrdinalIgnoreCase))
                {
                    keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt
                    keybd_event(0x4E, 0, 0, UIntPtr.Zero); // N
                    keybd_event(0x4E, 0, 2, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                }
                else
                {
                    keybd_event(0x0D, 0, 0, UIntPtr.Zero); // Enter
                    keybd_event(0x0D, 0, 2, UIntPtr.Zero);
                }
                return;
            }
            Thread.Sleep(300);
        }
    }

    static void SendChars(string text)
    {
        foreach (var ch in text)
        {
            var scan = VkKeyScan(ch);
            var vk = (byte)(scan & 0xff);
            var shift = (scan & 0x100) != 0;
            if (shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
            if (shift) keybd_event(0x10, 0, 2, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
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
    /// <summary>ActiveX/mstsc 连通后保持秒数再退出（冒烟用）；0=交互等待。</summary>
    public int ConnectHoldSec { get; set; }
}

sealed class LoginResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";
}

sealed class GroupsResponse
{
    [JsonPropertyName("groups")]
    public List<GroupItem> Groups { get; set; } = new();
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
