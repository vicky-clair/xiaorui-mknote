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

    private static string DecodeHtmlPayload(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // Heuristic: UTF-16LE HTML has high ratio of 0x00 in odd byte positions (e.g. '<\0i\0m\0g\0')
        int checkLen = Math.Min(bytes.Length, 128);
        int oddZeros = 0;
        for (int i = 1; i < checkLen; i += 2)
            if (bytes[i] == 0) oddZeros++;
        if (checkLen >= 8 && oddZeros >= checkLen / 5)
            return Encoding.Unicode.GetString(bytes);

        return DecodeTextSmart(bytes);
    }

    private static string DecodeTextSmart(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // Try strict UTF-8 first
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Fall back to Windows GB18030 / GBK (code page 54936 / 936)
            if (OperatingSystem.IsWindows())
                return DecodeWindowsCodePage(bytes, 54936);
            return Encoding.UTF8.GetString(bytes);
        }
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
