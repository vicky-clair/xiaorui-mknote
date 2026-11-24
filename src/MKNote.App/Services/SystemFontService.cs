using System.Runtime.InteropServices;

namespace MKNote.App.Services;

/// <summary>
/// Windows 系统字体枚举服务：通过 GDI EnumFontFamiliesExW 获取本机已安装的正文字体族，
/// 自动过滤图标/符号类字体（如 Wingdings、Segoe MDL2 Assets、Segoe Fluent Icons），
/// 并构建带中/英/日安全回退链的字体族，防止界面图标或字符出现乱码方框。
/// </summary>
public static class SystemFontService
{
    private const byte DEFAULT_CHARSET = 1;
    private const byte SYMBOL_CHARSET = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    private delegate int EnumFontFamExProc(ref LOGFONTW lpelfe, nint lpntme, uint fontType, nint lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(nint hdc, ref LOGFONTW lpLogfont, EnumFontFamExProc lpProc, nint lParam, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    private static readonly string[] SymbolOrIconFontTokens =
    [
        "Wingdings", "Webdings", "Marlett", "Symbol", "MDL2", "Fluent Icons",
        "Segoe UI Emoji", "Segoe UI Historic", "HoloLens", "MT Extra",
        "Bookshelf Symbol", "MS Reference Specialty", "MS Outlook",
        "FontAwesome", "Font Awesome", "Material Icons", "Codicon", "Icons"
    ];

    private static IReadOnlyList<string>? _cachedFonts;

    /// <summary>判断给定字体名称是否属于图标/符号类字体（不应作为正文或界面文字字体）。</summary>
    public static bool IsSymbolOrIconFont(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return false;
        foreach (var token in SymbolOrIconFontTokens)
        {
            if (fontName.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 为 WinUI 界面生成带多语言回退链的字体族字符串，确保选用纯英文字体时中日韩文字仍可正常显示。
    /// </summary>
    public static string BuildSafeUiFontChain(string? fontFamilyName)
    {
        var primary = fontFamilyName?.Split(',')[0].Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(primary) || IsSymbolOrIconFont(primary))
            primary = "Microsoft YaHei UI";

        return $"{primary}, Microsoft YaHei UI, Segoe UI, Yu Gothic UI, Meiryo UI";
    }

    /// <summary>
    /// 为 WebView2 编辑器生成带多语言回退链的 CSS font-family 字符串。
    /// </summary>
    public static string BuildSafeEditorCssFontChain(string? fontFamilyName)
    {
        var primary = fontFamilyName?.Split(',')[0].Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(primary) || IsSymbolOrIconFont(primary))
            primary = "Microsoft YaHei UI";

        return $"\"{primary}\", \"Microsoft YaHei UI\", \"Segoe UI\", \"Yu Gothic UI\", \"Meiryo UI\", sans-serif";
    }

    public static IReadOnlyList<string> GetInstalledFontFamilies()
    {
        if (_cachedFonts is not null) return _cachedFonts;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdc = GetDC(0);
        try
        {
            var lf = new LOGFONTW { lfCharSet = DEFAULT_CHARSET, lfFaceName = string.Empty };
            EnumFontFamExProc callback = (ref LOGFONTW font, nint _, uint _, nint _) =>
            {
                var name = font.lfFaceName?.Trim();
                // 跳过竖排字体 (@开头)、符号字符集字体 (SYMBOL_CHARSET) 及各类图标字体
                if (!string.IsNullOrEmpty(name) &&
                    !name.StartsWith('@') &&
                    font.lfCharSet != SYMBOL_CHARSET &&
                    !IsSymbolOrIconFont(name))
                {
                    set.Add(name);
                }
                return 1;
            };
            EnumFontFamiliesExW(hdc, ref lf, callback, 0, 0);
        }
        finally
        {
            if (hdc != 0) ReleaseDC(0, hdc);
        }

        // 将常用中日韩与英文字体排在顶部，方便快速选择
        string[] preferred =
        [
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Yu Gothic UI", "Meiryo UI",
            "SimSun", "SimHei", "KaiTi", "FangSong", "DengXian",
            "Cascadia Code", "Consolas", "Arial", "Georgia", "Times New Roman"
        ];

        var ordered = new List<string>();
        foreach (var p in preferred)
        {
            if (set.Remove(p)) ordered.Add(p);
        }
        ordered.AddRange(set.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase));

        _cachedFonts = ordered.Count > 0 ? ordered : ["Microsoft YaHei UI", "Segoe UI", "Consolas"];
        return _cachedFonts;
    }
}
