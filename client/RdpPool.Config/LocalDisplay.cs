namespace RdpPool.Config;

/// <summary>
/// 客户端本地显示分辨率（与 mstsc「显示」页类似）。不上传服务器。
/// 取值：fullscreen | match_local | {width}x{height}（如 1280x720）。
/// </summary>
public static class LocalDisplay
{
    public const string FullScreen = "fullscreen";
    public const string MatchLocal = "match_local";
    public const string Default = FullScreen;

    /// <summary>下拉选项：(存盘值, 界面标签)。</summary>
    public static readonly (string Value, string Label)[] Options =
    {
        (FullScreen, "全屏"),
        (MatchLocal, "与本地屏幕相同"),
        ("1920x1080", "1920 × 1080"),
        ("1680x1050", "1680 × 1050"),
        ("1600x900", "1600 × 900"),
        ("1440x900", "1440 × 900"),
        ("1366x768", "1366 × 768"),
        ("1280x1024", "1280 × 1024"),
        ("1280x800", "1280 × 800"),
        ("1280x720", "1280 × 720"),
        ("1024x768", "1024 × 768"),
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Default;
        var v = value.Trim().ToLowerInvariant();
        if (v is FullScreen or MatchLocal)
            return v;
        if (TryParseSize(v, out _, out _))
            return v;
        // 兼容 "1920×1080" / "1920 x 1080"
        var compact = v.Replace("×", "x").Replace(" ", "");
        if (TryParseSize(compact, out var w, out var h))
            return $"{w}x{h}";
        return Default;
    }

    public static string LabelFor(string? value)
    {
        var n = Normalize(value);
        foreach (var (val, label) in Options)
        {
            if (val == n) return label;
        }
        return n;
    }

    public static bool TryParseSize(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = value.Split('x', 'X', '×');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), out width) || !int.TryParse(parts[1].Trim(), out height))
            return false;
        return width >= 640 && height >= 480 && width <= 7680 && height <= 4320;
    }

    /// <summary>
    /// 解析为 RDP 应用参数。match_local / fullscreen 的宽高由调用方传入本机主屏尺寸。
    /// </summary>
    public static DisplayApply Resolve(string? value, int localWidth, int localHeight)
    {
        var n = Normalize(value);
        if (n == FullScreen)
        {
            return new DisplayApply(
                FullScreen: true,
                ScreenModeId: 2,
                Width: localWidth > 0 ? localWidth : 1920,
                Height: localHeight > 0 ? localHeight : 1080);
        }

        if (n == MatchLocal)
        {
            var w = localWidth > 0 ? localWidth : 1920;
            var h = localHeight > 0 ? localHeight : 1080;
            return new DisplayApply(FullScreen: false, ScreenModeId: 1, Width: w, Height: h);
        }

        if (TryParseSize(n, out var pw, out var ph))
            return new DisplayApply(FullScreen: false, ScreenModeId: 1, Width: pw, Height: ph);

        return new DisplayApply(FullScreen: true, ScreenModeId: 2,
            Width: localWidth > 0 ? localWidth : 1920,
            Height: localHeight > 0 ? localHeight : 1080);
    }
}

public readonly record struct DisplayApply(bool FullScreen, int ScreenModeId, int Width, int Height);
