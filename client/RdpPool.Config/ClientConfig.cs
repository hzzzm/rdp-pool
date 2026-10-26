namespace RdpPool.Config;

/// <summary>appsettings.json 磁盘形态。账密只应出现在 *Protected 字段（Base64 DPAPI）。</summary>
public sealed class ClientConfigFile
{
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:8081";

    /// <summary>旧版明文用户名；加载后应迁移为 LoginProtected 并清空。</summary>
    public string? Login { get; set; }

    /// <summary>旧版明文密码；加载后应迁移为 PasswordProtected 并清空。</summary>
    public string? Password { get; set; }

    /// <summary>DPAPI CurrentUser 保护后的用户名（Base64）。</summary>
    public string? LoginProtected { get; set; }

    /// <summary>DPAPI CurrentUser 保护后的密码（Base64）。</summary>
    public string? PasswordProtected { get; set; }

    public string PreferredGroupId { get; set; } = "";
    public int HeartbeatIntervalSec { get; set; } = 15;
    public string ClientInstanceId { get; set; } = "";
    public bool SkipMstsc { get; set; }
    public bool AutoRelease { get; set; } = true;

    /// <summary>ActiveX/mstsc 连通后保持秒数再退出（冒烟用）；0=交互等待。</summary>
    public int ConnectHoldSec { get; set; }

    /// <summary>
    /// 本地显示分辨率：fullscreen | match_local | 1280x720 等。明文非敏感；不上传服务器。
    /// </summary>
    public string? DisplayResolution { get; set; }
}

/// <summary>运行时已解密的配置。</summary>
public sealed class ClientSettings
{
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:8081";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string PreferredGroupId { get; set; } = "";
    public int HeartbeatIntervalSec { get; set; } = 15;
    public string ClientInstanceId { get; set; } = "";
    public bool SkipMstsc { get; set; }
    public bool AutoRelease { get; set; } = true;
    public int ConnectHoldSec { get; set; }

    /// <summary>本地显示：fullscreen | match_local | WxH。默认全屏（与旧版 .rdp screen mode id:2 一致）。</summary>
    public string DisplayResolution { get; set; } = "fullscreen";

    /// <summary>本次加载是否从明文 Login/Password 迁到了 Protected 字段。</summary>
    public bool MigratedFromPlaintext { get; set; }
}
