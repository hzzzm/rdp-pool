using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpPool.Config;

public static class ClientConfigStore
{
    static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static ClientSettings Load(string path, bool migratePlaintextToDisk = true)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("缺少配置文件。请先运行 RdpPoolSetup.exe。", path);

        var file = JsonSerializer.Deserialize<ClientConfigFile>(File.ReadAllText(path), ReadOpts)
                   ?? throw new InvalidOperationException("配置文件无效");

        var settings = new ClientSettings
        {
            ApiBaseUrl = string.IsNullOrWhiteSpace(file.ApiBaseUrl)
                ? "http://127.0.0.1:8081"
                : file.ApiBaseUrl.Trim(),
            PreferredGroupId = file.PreferredGroupId ?? "",
            HeartbeatIntervalSec = file.HeartbeatIntervalSec,
            ClientInstanceId = file.ClientInstanceId ?? "",
            SkipMstsc = file.SkipMstsc,
            AutoRelease = file.AutoRelease,
            ConnectHoldSec = file.ConnectHoldSec,
            DisplayResolution = LocalDisplay.Normalize(file.DisplayResolution)
        };

        var hadPlainLogin = !string.IsNullOrEmpty(file.Login);
        var hadPlainPassword = !string.IsNullOrEmpty(file.Password);

        if (!string.IsNullOrWhiteSpace(file.LoginProtected))
        {
            if (!SecretProtector.TryUnprotectFromBase64(file.LoginProtected, out var login, out var err))
                throw new InvalidOperationException(
                    "无法解密 LoginProtected（须由同一 Windows 用户用本机 Setup 保存）: " + err);
            settings.Login = login;
        }
        else if (hadPlainLogin)
        {
            settings.Login = file.Login!;
            settings.MigratedFromPlaintext = true;
        }

        if (!string.IsNullOrWhiteSpace(file.PasswordProtected))
        {
            if (!SecretProtector.TryUnprotectFromBase64(file.PasswordProtected, out var password, out var err))
                throw new InvalidOperationException(
                    "无法解密 PasswordProtected（须由同一 Windows 用户用本机 Setup 保存）: " + err);
            settings.Password = password;
        }
        else if (hadPlainPassword)
        {
            settings.Password = file.Password!;
            settings.MigratedFromPlaintext = true;
        }

        if (migratePlaintextToDisk && settings.MigratedFromPlaintext)
        {
            try
            {
                Save(path, settings);
            }
            catch
            {
                // 只读目录时仍允许本次用明文运行
            }
        }

        return settings;
    }

    /// <summary>写入配置：账密仅以 DPAPI Base64 落盘，清除明文 Login/Password。</summary>
    public static void Save(string path, ClientSettings settings)
    {
        var file = new ClientConfigFile
        {
            ApiBaseUrl = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
                ? "http://127.0.0.1:8081"
                : settings.ApiBaseUrl.Trim(),
            Login = null,
            Password = null,
            LoginProtected = SecretProtector.ProtectToBase64(settings.Login ?? ""),
            PasswordProtected = SecretProtector.ProtectToBase64(settings.Password ?? ""),
            PreferredGroupId = settings.PreferredGroupId ?? "",
            HeartbeatIntervalSec = settings.HeartbeatIntervalSec <= 0 ? 15 : settings.HeartbeatIntervalSec,
            ClientInstanceId = settings.ClientInstanceId ?? "",
            SkipMstsc = settings.SkipMstsc,
            AutoRelease = settings.AutoRelease,
            ConnectHoldSec = settings.ConnectHoldSec,
            DisplayResolution = LocalDisplay.Normalize(settings.DisplayResolution)
        };

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, WriteOpts));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    public static ClientSettings? TryLoadExisting(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return Load(path, migratePlaintextToDisk: false);
        }
        catch
        {
            // Setup 预填时若密文不可解，返回仅含明文/URL 的尽力结果
            try
            {
                var file = JsonSerializer.Deserialize<ClientConfigFile>(File.ReadAllText(path), ReadOpts);
                if (file == null) return null;
                return new ClientSettings
                {
                    ApiBaseUrl = file.ApiBaseUrl,
                    Login = file.Login ?? "",
                    Password = "",
                    PreferredGroupId = file.PreferredGroupId ?? "",
                    HeartbeatIntervalSec = file.HeartbeatIntervalSec,
                    ClientInstanceId = file.ClientInstanceId ?? "",
                    SkipMstsc = file.SkipMstsc,
                    AutoRelease = file.AutoRelease,
                    ConnectHoldSec = file.ConnectHoldSec,
                    DisplayResolution = LocalDisplay.Normalize(file.DisplayResolution)
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 仅更新本地显示分辨率，保留 LoginProtected/PasswordProtected 等字段原样写入。
    /// 不校验登录、不重加密账密。
    /// </summary>
    public static void PatchDisplayResolution(string path, string displayResolution)
    {
        var normalized = LocalDisplay.Normalize(displayResolution);
        ClientConfigFile file;
        if (File.Exists(path))
        {
            file = JsonSerializer.Deserialize<ClientConfigFile>(File.ReadAllText(path), ReadOpts)
                   ?? new ClientConfigFile();
        }
        else
        {
            file = new ClientConfigFile();
        }

        file.DisplayResolution = normalized;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, WriteOpts));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    /// <summary>读取当前存盘的显示分辨率（文件不存在或无效时返回默认全屏）。</summary>
    public static string ReadDisplayResolution(string path)
    {
        if (!File.Exists(path))
            return LocalDisplay.Default;
        try
        {
            var file = JsonSerializer.Deserialize<ClientConfigFile>(File.ReadAllText(path), ReadOpts);
            return LocalDisplay.Normalize(file?.DisplayResolution);
        }
        catch
        {
            return LocalDisplay.Default;
        }
    }
}
