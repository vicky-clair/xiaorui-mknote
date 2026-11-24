namespace MKNote.Core.Models;

/// <summary>
/// 文档存储与编辑格式枚举：支持经典富文本 HTML 与双向链接 Markdown 两种内核。
/// </summary>
public enum DocumentFormat
{
    RichHtml = 0,
    Markdown = 1
}

/// <summary>
/// 文档画布纸张纹理样式。
/// </summary>
public enum BackgroundStyle
{
    Solid = 0,
    Lined = 1,
    Grid = 2,
    Dot = 3,
    Paper = 4
}

/// <summary>
/// Markdown 文档视图模式：行内实时预览、左右分屏实时预览、沉浸阅读模式。
/// </summary>
public enum MarkdownViewMode
{
    LivePreview = 0,
    SplitPreview = 1,
    Reading = 2
}

/// <summary>
/// 双主题自适应文档背景色预设：浅色与深色主题各自拥有鲜明且护眼的专属配色与命名。
/// </summary>
public sealed record BackgroundPreset(
    string Id,
    string Name,
    string LightHex,
    string DarkName,
    string DarkHex,
    string Description)
{
    public static IReadOnlyList<BackgroundPreset> All { get; } =
    [
        new("default", "纯净象牙白", "#FFFFFF", "午夜纯黑", "#121418", "经典纯净底色，跟随浅色/深色主题自动切换"),
        new("eye_green", "护眼豆沙绿", "#E8F5EA", "暗夜墨松绿", "#132A22", "柔和护眼绿调，适合长时间阅读与写作"),
        new("parchment", "温暖羊皮纸", "#FAF1DC", "烘焙咖啡棕", "#2C2118", "暖调书卷质感，适合日记与读书笔记"),
        new("sky_blue", "清透天青蓝", "#E7F3FD", "深海星空蓝", "#12233A", "清爽深邃蓝调，适合技术文档与规划"),
        new("lilac", "柔雾丁香紫", "#F3EBFA", "星云暗夜紫", "#231936", "静谧紫韵氛围，适合创意与灵感记录"),
        new("sakura", "淡雅樱花粉", "#FDECEF", "酒红勃艮第", "#321924", "温暖雅致色调，适合随笔与摘录"),
        new("cool_gray", "极简云雾灰", "#EFF1F5", "石墨高级灰", "#23272E", "低干扰中性灰，专注文字内容本身"),
        new("sticky_yellow", "暖阳便签黄", "#FFF8D6", "复古黑板青", "#192F2B", "经典便签与黑板质感，醒目舒适")
    ];

    /// <summary>根据当前主题明暗返回对应展示名称。</summary>
    public string GetDisplayName(bool isDarkTheme) => isDarkTheme ? DarkName : Name;

    /// <summary>根据当前主题明暗返回对应十六进制背景色。</summary>
    public string GetHex(bool isDarkTheme) => isDarkTheme ? DarkHex : LightHex;

    public static string ResolveHex(string? colorValue, bool isDarkTheme)
    {
        if (string.IsNullOrWhiteSpace(colorValue) || colorValue == "default")
            return isDarkTheme ? "#121418" : "#FFFFFF";

        if (colorValue.StartsWith('#'))
            return colorValue;

        var preset = All.FirstOrDefault(p => p.Id.Equals(colorValue, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
            return isDarkTheme ? preset.DarkHex : preset.LightHex;

        return isDarkTheme ? "#121418" : "#FFFFFF";
    }

    /// <summary>
    /// 基于 WCAG 2.1 相对亮度算法计算背景色明暗（0.0 极暗 ~ 1.0 极亮），用于自动适配高对比度文字颜色。
    /// </summary>
    public static double GetRelativeLuminance(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return 1.0;

        var clean = hex.TrimStart('#');
        if (clean.Length == 8)
            clean = clean[2..]; // strip alpha ARGB
        if (clean.Length != 6)
            return 1.0;

        if (!byte.TryParse(clean[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(clean[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(clean[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return 1.0;

        static double Linearize(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    public static bool IsLightBackground(string hex) => GetRelativeLuminance(hex) > 0.42;
}

public sealed record AppSkinPreset(
    string Id,
    string Name,
    string AccentHex,
    string SidebarBgLight,
    string SidebarBgDark)
{
    public static IReadOnlyList<AppSkinPreset> All { get; } =
    [
        new("blue", "深海钴蓝", "#2B88D8", "#F3F6FA", "#1B2028"),
        new("green", "清风翠绿", "#4C9A2A", "#F2F7F2", "#1A231C"),
        new("teal", "青黛墨韵", "#008272", "#F1F7F6", "#182423"),
        new("pink", "樱花粉黛", "#D15B88", "#FAF3F6", "#261C21"),
        new("gray", "极简素灰", "#5C6470", "#F4F5F6", "#1F2022"),
        new("hand", "手绘暖棕", "#A46B39", "#F8F4EE", "#24201B"),
        new("black", "暗夜曜黑", "#4DA3FF", "#EAECEF", "#141517")
    ];
}
