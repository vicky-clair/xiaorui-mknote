using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace MKNote.Migration;

public sealed record MknoteAttachmentData(
    string Id,
    string FileName,
    string RemoteId,
    string ContainerEntryName,
    string MimeType,
    byte[] Bytes);

public sealed record ParsedMknotePackage(
    string Title,
    string? CategoryName,
    IReadOnlyList<string> Tags,
    string HtmlContent,
    IReadOnlyList<MknoteAttachmentData> Attachments);

/// <summary>
/// SFDS 单文件笔记归档（.mknote）原生解析器：
/// - 文件头魔数为 ".SFDS"，偏移 0x22 处记录目录表偏移。
/// - 目录表按 "SFDS" 标记依次记录各成员（note、note.ini、*.db）。
/// - 内容体采用 "MKEN" 头部（0x7d 字节）+ ZLIB 压缩流存储。
/// - 自动识别 UTF-16LE / UTF-8 正文与 GB18030 / UTF-8 元数据配置。
/// </summary>
public static partial class MknotePackageReader
{
    private static readonly byte[] SfdsHeaderMagic = ".SFDS"u8.ToArray();
    private static readonly byte[] SfdsEntryMagic = "SFDS"u8.ToArray();
    private static readonly byte[] MkenMagic = "MKEN"u8.ToArray();

    private sealed record SfdsEntry(string Name, long Size, long StartOffset, long EndOffset);

    public static ParsedMknotePackage ParseFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        return ParseBytes(raw, Path.GetFileNameWithoutExtension(path));
    }

    public static ParsedMknotePackage ParseBytes(byte[] raw, string fallbackTitle)
    {
        if (raw.Length < 0x40 || !raw.AsSpan(0, 5).SequenceEqual(SfdsHeaderMagic))
            throw new InvalidDataException("不是有效的 .mknote (SFDS) 单文件归档格式。");

        var entries = ReadSfdsDirectory(raw);
        var entryMap = entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        // 1. 解析 note.ini 元数据（支持 UTF-8 / GB18030）
        string title = string.IsNullOrWhiteSpace(fallbackTitle) ? "导入笔记" : fallbackTitle;
        string? category = null;
        var tags = new List<string>();
        var attachMeta = new List<(string Id, string Name, string Rid, string File)>();

        if (entryMap.TryGetValue("note.ini", out var iniEntry))
        {
            var iniBytes = SliceEntry(raw, iniEntry);
            var iniText = DecodeTextSmart(iniBytes);
            ParseNoteIni(iniText, ref title, ref category, tags, attachMeta);
        }

        // 2. Decompress main HTML body ("note")
        string htmlBody = string.Empty;
        if (entryMap.TryGetValue("note", out var noteEntry))
        {
            var rawNote = SliceEntry(raw, noteEntry);
            var decompressed = UnwrapMkenPayload(rawNote);
            htmlBody = DecodeHtmlPayload(decompressed);
        }

        // 3. Decompress all attachments referenced in note.ini (plus any extra *.db entries)
        var attachments = new List<MknoteAttachmentData>();
        var consumedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "note", "note.ini" };

        foreach (var meta in attachMeta)
        {
            if (string.IsNullOrWhiteSpace(meta.File) || !entryMap.TryGetValue(meta.File, out var attEntry))
                continue;
            consumedEntries.Add(meta.File);
            var payload = UnwrapMkenPayload(SliceEntry(raw, attEntry));
            var fileName = string.IsNullOrWhiteSpace(meta.Name) ? meta.File : meta.Name;
            var mime = DetectMimeType(payload, fileName);
            attachments.Add(new MknoteAttachmentData(meta.Id, fileName, meta.Rid, meta.File, mime, payload));
        }

        foreach (var e in entries)
        {
            if (consumedEntries.Contains(e.Name)) continue;
            try
            {
                var payload = UnwrapMkenPayload(SliceEntry(raw, e));
                var mime = DetectMimeType(payload, e.Name);
                var ext = MimeToExt(mime);
                var fname = Path.HasExtension(e.Name) && !e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    ? e.Name
                    : Path.GetFileNameWithoutExtension(e.Name) + ext;
                attachments.Add(new MknoteAttachmentData(Path.GetFileNameWithoutExtension(e.Name), fname, "", e.Name, mime, payload));
            }
            catch { }
        }

        return new ParsedMknotePackage(title, category, tags, htmlBody, attachments);
    }

    private static List<SfdsEntry> ReadSfdsDirectory(byte[] raw)
    {
        var list = new List<SfdsEntry>();
        int dirOffset = BitConverter.ToInt32(raw, 0x22);
        if (dirOffset <= 0 || dirOffset >= raw.Length)
            throw new InvalidDataException("SFDS 目录表偏移无效。");

        int p = dirOffset;
        while (p + 4 <= raw.Length && raw.AsSpan(p, 4).SequenceEqual(SfdsEntryMagic))
        {
            p += 4;
            string name = ReadTaggedString(raw, ref p);
            long size = ReadTaggedInt(raw, ref p);
            _ = ReadTaggedInt(raw, ref p); // unk1
            long startOff = ReadTaggedInt(raw, ref p);
            long endOff = ReadTaggedInt(raw, ref p);
            _ = ReadTaggedInt(raw, ref p); // unk2
            _ = ReadTaggedInt(raw, ref p); // unk3
            if (p + 16 <= raw.Length) p += 16; // 16-byte MD5 digest
            _ = ReadTaggedInt(raw, ref p); // tail1
            _ = ReadTaggedInt(raw, ref p); // tail2

            if (startOff >= 0 && endOff >= startOff && endOff <= raw.Length)
            {
                list.Add(new SfdsEntry(name, size, startOff, endOff));
            }
        }
        return list;
    }

    private static string ReadTaggedString(byte[] buf, ref int p)
    {
        if (p >= buf.Length) return string.Empty;
        byte tag = buf[p++];
        int len = tag switch
        {
            0x06 => buf[p++], // vaString (ShortString)
            0x0c or 0x14 => ReadInt32Advance(buf, ref p), // vaLString / vaUTF8String
            _ => throw new InvalidDataException($"未知的 SFDS 字符串标记 0x{tag:X2} @ 0x{p - 1:X}")
        };
        var str = Encoding.UTF8.GetString(buf, p, len);
        p += len;
        return str;
    }

    private static long ReadTaggedInt(byte[] buf, ref int p)
    {
        if (p >= buf.Length) return 0;
        byte tag = buf[p++];
        return tag switch
        {
            0x02 => buf[p++], // vaInt8
            0x03 => ReadUInt16Advance(buf, ref p), // vaInt16
            0x04 => ReadUInt32Advance(buf, ref p), // vaInt32
            0x13 => ReadInt64Advance(buf, ref p),  // vaInt64
            0x06 => SkipShortString(buf, ref p),
            _ => throw new InvalidDataException($"未知的 SFDS 整数标记 0x{tag:X2} @ 0x{p - 1:X}")
        };
    }

    private static int ReadInt32Advance(byte[] b, ref int p) { int v = BitConverter.ToInt32(b, p); p += 4; return v; }
    private static ushort ReadUInt16Advance(byte[] b, ref int p) { ushort v = BitConverter.ToUInt16(b, p); p += 2; return v; }
    private static uint ReadUInt32Advance(byte[] b, ref int p) { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; }
    private static long ReadInt64Advance(byte[] b, ref int p) { long v = BitConverter.ToInt64(b, p); p += 8; return v; }
    private static long SkipShortString(byte[] b, ref int p) { int len = b[p++]; p += len; return 0; }

    private static byte[] SliceEntry(byte[] raw, SfdsEntry entry)
    {
        int start = (int)entry.StartOffset;
        int len = (int)(entry.EndOffset - entry.StartOffset);
        var slice = new byte[len];
        Buffer.BlockCopy(raw, start, slice, 0, len);
        return slice;
    }

    private static byte[] UnwrapMkenPayload(byte[] entryBytes)
    {
        int mkenPos = IndexOfBytes(entryBytes, MkenMagic);
        if (mkenPos < 0)
            return entryBytes;

        // Standard MKEN header is 0x7d bytes followed by ZLIB stream (0x78 0x9C / 0x78 0xDA / 0x78 0x01)
        int payloadStart = mkenPos + 0x7d;
        if (payloadStart >= entryBytes.Length)
            return [];

        // Check for ZLIB header at payloadStart (or scan within a few bytes if header size differs)
        int zPos = -1;
        if (IsZlibHeader(entryBytes, payloadStart))
        {
            zPos = payloadStart;
        }
        else
        {
            for (int i = mkenPos + 16; i < Math.Min(entryBytes.Length - 2, mkenPos + 256); i++)
            {
                if (IsZlibHeader(entryBytes, i))
                {
                    zPos = i;
                    break;
                }
            }
        }

        if (zPos >= 0)
        {
            try
            {
                using var ms = new MemoryStream(entryBytes, zPos, entryBytes.Length - zPos);
                using var zs = new ZLibStream(ms, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zs.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                // Fall back to raw slice if not actually compressed
            }
        }

        return entryBytes[payloadStart..];
    }

    private static bool IsZlibHeader(byte[] data, int idx)
    {
        if (idx + 1 >= data.Length) return false;
        byte cmf = data[idx];
        byte flg = data[idx + 1];
        return cmf == 0x78 && ((cmf * 256 + flg) % 31 == 0);
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        var span = haystack.AsSpan();
        return span.IndexOf(needle);
    }

    /// <summary>
    /// 智能解码 .mknote 正文载荷（优先识别 BOM、全缓冲区 UTF-16LE/BE NUL 奇偶特征、严格 UTF-8、UTF-16LE vs GB18030 评分对比）。
    /// 彻底解决纯中文开头（前 128 字节无 ASCII 标签）的 BOM-less UTF-16LE 文档被误判为 GB18030 导致乱码的问题。
    /// </summary>
    public static string DecodeHtmlPayload(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // 1. 全量（最多扫描前 32KB）检测 UTF-16LE / UTF-16BE 的奇偶位 NUL 字节特征
        //    有效 UTF-8 / GB18030 文本不会包含配对的 0x00 字节；而 UTF-16LE 中只要出现任意英文、数字、空格或 <br> 标签，奇数位必有 0x00
        var utf16Hit = DetectUtf16ByNullParity(bytes);
        if (utf16Hit == 1) return Encoding.Unicode.GetString(bytes);
        if (utf16Hit == 2) return Encoding.BigEndianUnicode.GetString(bytes);

        // 2. 尝试严格 UTF-8（不允许非法字节，且不含 NUL）
        if (TryDecodeStrictUtf8(bytes, out var utf8Text))
            return utf8Text;

        // 3. 无 NUL 字节且非合法 UTF-8（例如全文纯中文且没有任何英文/空格/HTML标签的 UTF-16LE，或是 GB18030 文本）：
        //    对 UTF-16LE 与 GB18030 解码结果进行可读性打分对比
        var gbText = DecodeGb18030(bytes);
        if (bytes.Length >= 2 && (bytes.Length & 1) == 0)
        {
            var leText = Encoding.Unicode.GetString(bytes);
            if (ScoreDecodedText(leText) > ScoreDecodedText(gbText))
                return leText;
        }
        return gbText;
    }

    /// <summary>
    /// 通用文本文件智能解码（适用于 note.ini、.md、.html 等外部导入文件）：
    /// 支持 UTF-8/UTF-16 BOM、无 BOM UTF-16LE/BE、严格 UTF-8 与中文 GB18030/GBK 自动回退。
    /// </summary>
    public static string DecodeTextSmart(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var utf16Hit = DetectUtf16ByNullParity(bytes);
        if (utf16Hit == 1) return Encoding.Unicode.GetString(bytes);
        if (utf16Hit == 2) return Encoding.BigEndianUnicode.GetString(bytes);

        if (TryDecodeStrictUtf8(bytes, out var utf8Text))
            return utf8Text;

        return DecodeGb18030(bytes);
    }

    /// <summary>
    /// 检测无 BOM 缓冲区的 UTF-16 奇偶位 0x00 特征：返回 1 表示 UTF-16LE，2 表示 UTF-16BE，0 表示未命中。
    /// </summary>
    private static int DetectUtf16ByNullParity(byte[] bytes)
    {
        if (bytes.Length < 4 || (bytes.Length & 1) != 0)
            return 0;

        int scanLen = Math.Min(bytes.Length, 32768) & ~1;
        int oddAsciiZeros = 0;  // 形如 [ASCII, 0x00] -> UTF-16LE
        int evenAsciiZeros = 0; // 形如 [0x00, ASCII] -> UTF-16BE
        int oddAnyZeros = 0;
        int evenAnyZeros = 0;

        for (int i = 0; i < scanLen; i += 2)
        {
            byte lo = bytes[i];
            byte hi = bytes[i + 1];
            if (hi == 0 && lo != 0)
            {
                oddAnyZeros++;
                if (lo == '\t' || lo == '\r' || lo == '\n' || (lo >= 0x20 && lo <= 0x7E))
                    oddAsciiZeros++;
            }
            else if (lo == 0 && hi != 0)
            {
                evenAnyZeros++;
                if (hi == '\t' || hi == '\r' || hi == '\n' || (hi >= 0x20 && hi <= 0x7E))
                    evenAsciiZeros++;
            }
        }

        if (oddAsciiZeros >= 2 && oddAnyZeros > evenAnyZeros * 3)
            return 1;
        if (evenAsciiZeros >= 2 && evenAnyZeros > oddAnyZeros * 3)
            return 2;
        return 0;
    }

    private static bool TryDecodeStrictUtf8(byte[] bytes, out string decoded)
    {
        // 若字节流中包含 NUL 字符则不当作普通 UTF-8 文本
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            decoded = string.Empty;
            return false;
        }
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            decoded = strictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static string DecodeGb18030(byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
            return DecodeWindowsCodePage(bytes, 54936);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 评估候选解码文本的自然语言可读性（用于无 NUL 字节且非 UTF-8 场景下区分纯中文 UTF-16LE 与 GB18030）。
    /// </summary>
    private static double ScoreDecodedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return double.NegativeInfinity;
        int len = Math.Min(text.Length, 4096);
        double score = 0;

        for (int i = 0; i < len; i++)
        {
            char ch = text[i];
            if (char.IsSurrogate(ch) || ch == '\uFFFD')
            {
                score -= 20;
            }
            else if (ch < 0x20 && ch != '\r' && ch != '\n' && ch != '\t')
            {
                score -= 15;
            }
            else if (ch >= 0x4E00 && ch <= 0x9FA5)
            {
                // 常用基本汉字区间
                score += 2.0;
            }
            else if ((ch >= 0x3000 && ch <= 0x303F) || (ch >= 0xFF01 && ch <= 0xFF5E))
            {
                // 中文标点与全角字符
                score += 2.5;
            }
            else if (ch >= 0x20 && ch <= 0x7E)
            {
                // 常见可打印 ASCII（但连续乱码中常混入大量怪异符号如 @S?:g(u）
                score += char.IsLetterOrDigit(ch) || ch == ' ' ? 1.0 : 0.2;
            }
            else if (ch >= 0xE000 && ch <= 0xF8FF)
            {
                // 私用区字符通常代表错误解码
                score -= 12;
            }
            else
            {
                score -= 2.5;
            }
        }

        // 命中标准 HTML 标签额外加分
        if (text.Contains("<br", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<p", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<div", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<span", StringComparison.OrdinalIgnoreCase))
            score += 40;

        return score / len;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte* lpMultiByteStr, int cbMultiByte, char* lpWideCharStr, int cchWideChar);

    private static unsafe string DecodeWindowsCodePage(byte[] bytes, uint codePage)
    {
        if (bytes.Length == 0) return string.Empty;
        fixed (byte* pSrc = bytes)
        {
            int charCount = MultiByteToWideChar(codePage, 0, pSrc, bytes.Length, null, 0);
            if (charCount <= 0)
                charCount = MultiByteToWideChar(936, 0, pSrc, bytes.Length, null, 0);
            if (charCount <= 0)
                return Encoding.UTF8.GetString(bytes);

            var chars = new char[charCount];
            fixed (char* pDst = chars)
            {
                MultiByteToWideChar(codePage, 0, pSrc, bytes.Length, pDst, charCount);
            }
            return new string(chars);
        }
    }

    private static void ParseNoteIni(
        string iniText,
        ref string title,
        ref string? category,
        List<string> tags,
        List<(string Id, string Name, string Rid, string File)> attachMeta)
    {
        string currentSection = "";
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in iniText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!sections.ContainsKey(currentSection))
                    sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && !string.IsNullOrEmpty(currentSection))
            {
                sections[currentSection][line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        if (sections.TryGetValue("note", out var noteSec))
        {
            if (noteSec.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n))
                title = n;
            if (noteSec.TryGetValue("category", out var c) && !string.IsNullOrWhiteSpace(c))
                category = c;
            if (noteSec.TryGetValue("tags", out var t) && !string.IsNullOrWhiteSpace(t))
            {
                foreach (var tag in t.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tags.Add(tag);
            }
        }

        foreach (var (secName, kv) in sections)
        {
            if (!secName.StartsWith("attach", StringComparison.OrdinalIgnoreCase)) continue;
            kv.TryGetValue("id", out var id);
            kv.TryGetValue("name", out var name);
            kv.TryGetValue("rid", out var rid);
            kv.TryGetValue("file", out var file);
            if (!string.IsNullOrWhiteSpace(file))
                attachMeta.Add((id ?? "", name ?? file, rid ?? "", file));
        }
    }

    public static string DetectMimeType(byte[] data, string fileName)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
        if (data.Length >= 4 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return "image/gif";
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[8] == 0x57 && data[9] == 0x45) return "image/webp";
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";
        if (data.Length >= 4 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46) return "application/pdf";

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    private static string MimeToExt(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };
}
