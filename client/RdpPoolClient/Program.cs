using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var cfgPath = args.Length > 0 ? args[0] : "appsettings.json";
if (!File.Exists(cfgPath))
{
    Console.Error.WriteLine($"Missing {cfgPath}. Copy appsettings.example.json and fill locally.");
    return 1;
}

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var cfg = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(cfgPath), jsonOpts)
          ?? throw new InvalidOperationException("bad config");
using var http = new HttpClient { BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/") };

Console.WriteLine("Logging in...");
var loginResp = await http.PostAsJsonAsync("api/v1/auth/login", new { login = cfg.Login, password = cfg.Password });
if (!loginResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Login failed: {(int)loginResp.StatusCode}");
    return 1;
}
var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(jsonOpts);
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

var groupsResp = await http.GetFromJsonAsync<GroupsResponse>("api/v1/groups/available", jsonOpts);
var groups = groupsResp?.Groups ?? [];
if (groups.Count == 0)
{
    Console.Error.WriteLine("当前账号没有可用的资源组，请联系管理员授权。");
    return 2;
}

string groupId;
if (groups.Count == 1)
{
    groupId = groups[0].Id;
    Console.WriteLine($"Auto group: {groups[0].Name}");
}
else
{
    Console.WriteLine("Available groups:");
    for (var i = 0; i < groups.Count; i++)
        Console.WriteLine($"  [{i}] {groups[i].Name} idle={groups[i].IdleCount}");
    groupId = string.IsNullOrEmpty(cfg.PreferredGroupId) ? groups[0].Id : cfg.PreferredGroupId;
}

var acquireResp = await http.PostAsJsonAsync("api/v1/sessions/acquire", new
{
    group_id = groupId,
    client_instance_id = string.IsNullOrEmpty(cfg.ClientInstanceId) ? Guid.NewGuid().ToString("N") : cfg.ClientInstanceId
});
var acquireJson = await acquireResp.Content.ReadAsStringAsync();
Console.WriteLine($"Acquire {(int)acquireResp.StatusCode}: {acquireJson}");

if (acquireResp.IsSuccessStatusCode && (int)acquireResp.StatusCode == 200)
{
    Console.WriteLine("Next: write .rdp from ticket and start mstsc; then heartbeat loop.");
}
else if ((int)acquireResp.StatusCode == 202)
{
    Console.WriteLine("Queued — poll GET api/v1/queue/me until offered, then claim.");
}

return 0;

sealed class AppConfig
{
    public string ApiBaseUrl { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string PreferredGroupId { get; set; } = "";
    public int HeartbeatIntervalSec { get; set; } = 15;
    public string ClientInstanceId { get; set; } = "";
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
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("idle_count")]
    public int IdleCount { get; set; }
    [JsonPropertyName("busy_count")]
    public int BusyCount { get; set; }
}
