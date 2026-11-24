using System.Runtime.InteropServices;

namespace MKNote.App.Services;

/// <summary>
/// Windows 系统字体枚举服务：通过 GDI EnumFontFamiliesExW 获取本机已安装的所有字体族，供界面字体与文档编辑器字体分别配置。
/// </summary>
public static class SystemFontService
{
    private const byte DEFAULT_CHARSET = 1;

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

    private static IReadOnlyList<string>? _cachedFonts;

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
                if (!string.IsNullOrEmpty(name) && !name.StartsWith('@'))
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

        // Prioritize popular CJK & English reading fonts at top, then alphabetical
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
