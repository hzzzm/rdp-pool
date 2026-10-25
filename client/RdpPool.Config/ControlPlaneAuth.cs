using System.Net.Http.Json;
using System.Text.Json;

namespace RdpPool.Config;

public sealed class AuthVerifyResult
{
    public bool Ok { get; init; }
    public int StatusCode { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>控制面登录校验（Setup 保存前调用）。</summary>
public static class ControlPlaneAuth
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<AuthVerifyResult> VerifyLoginAsync(
        string apiBaseUrl, string login, string password, CancellationToken ct = default)
    {
        var baseUrl = (apiBaseUrl ?? "").Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new AuthVerifyResult
            {
                Ok = false,
                StatusCode = 0,
                Message = "服务器地址无效，须为 http:// 或 https:// URL。"
            };
        }

        try
        {
            using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await http.PostAsJsonAsync(
                "api/v1/auth/login",
                new { login, password },
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                // 轻量确认有 token，避免 200 但空壳
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (doc.RootElement.TryGetProperty("access_token", out var t) &&
                        t.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(t.GetString()))
                    {
                        return new AuthVerifyResult { Ok = true, StatusCode = (int)resp.StatusCode, Message = "登录成功" };
                    }
                }
                catch
                {
                    // 仍视为成功若 HTTP 2xx
                }

                return new AuthVerifyResult { Ok = true, StatusCode = (int)resp.StatusCode, Message = "登录成功" };
            }

            var detail = SummarizeErrorBody(body);
            var msg = (int)resp.StatusCode switch
            {
                401 or 403 => "用户名或密码错误，或账号无权限。",
                404 => "登录接口不存在，请检查服务器地址是否指向控制面（通常为 :8081，而非管理后台 :8080）。",
                >= 500 => "控制面服务器错误，请稍后重试或联系管理员。",
                _ => "登录失败。"
            };
            if (!string.IsNullOrWhiteSpace(detail))
                msg += "\n详情：" + detail;

            return new AuthVerifyResult
            {
                Ok = false,
                StatusCode = (int)resp.StatusCode,
                Message = msg
            };
        }
        catch (TaskCanceledException)
        {
            return new AuthVerifyResult
            {
                Ok = false,
                StatusCode = 0,
                Message = "连接控制面超时，请检查服务器地址与网络。"
            };
        }
        catch (HttpRequestException ex)
        {
            return new AuthVerifyResult
            {
                Ok = false,
                StatusCode = 0,
                Message = "无法连接控制面：\n" + (ex.Message ?? "网络错误")
            };
        }
        catch (Exception ex)
        {
            return new AuthVerifyResult
            {
                Ok = false,
                StatusCode = 0,
                Message = "校验登录时出错：\n" + ex.Message
            };
        }
    }

    static string SummarizeErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            foreach (var name in new[] { "message", "error", "detail", "msg" })
            {
                if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString() ?? "";
                    if (s.Length > 200) s = s[..200] + "…";
                    return s;
                }
            }
        }
        catch
        {
            // fall through
        }

        var flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
        if (flat.Length > 200) flat = flat[..200] + "…";
        return flat;
    }
}
