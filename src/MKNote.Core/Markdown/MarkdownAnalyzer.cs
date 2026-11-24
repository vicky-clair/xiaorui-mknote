using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MKNote.Core.Models;

namespace MKNote.Core.Markdown;

public sealed record MarkdownAnalysisResult(
    IReadOnlyList<HeadingItem> Headings,
    IReadOnlyList<NoteLinkItem> Links,
    IReadOnlyList<string> Tags,
    Dictionary<string, object?> Frontmatter,
    string? FrontmatterJson,
    string PlainText,
    int WordCount);

/// <summary>
/// Markdown 静态语法分析器：解析 YAML Frontmatter、[[双向链接]]、![[嵌入]]、多级 #标签、
/// 文档标题大纲、Microsoft Word 标准字数统计，并支持笔记重命名时的双链自动重写。
/// </summary>
public static partial class MarkdownAnalyzer
{
    [GeneratedRegex(@"^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)", RegexOptions.Compiled)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"(!)?\[\[([^\]\|#\r\n]+)(?:#([^\]\|\r\n]+))?(?:\|([^\]\r\n]+))?\]\]", RegexOptions.Compiled)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"(?:^|[\s\(（])#([\p{L}\p{N}_][\p{L}\p{N}_/\-]*)", RegexOptions.Compiled)]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Compiled)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"</(?:p|div|h[1-6]|li|tr|blockquote|pre)>|<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockBreakRegex();

    public static MarkdownAnalysisResult Analyze(string sourceNoteId, string markdown)
    {
        markdown ??= string.Empty;
        var frontmatter = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = markdown;

        var fmMatch = FrontmatterRegex().Match(markdown);
        if (fmMatch.Success)
        {
            ParseSimpleYaml(fmMatch.Groups[1].Value, frontmatter, tags);
            body = markdown[fmMatch.Length..];
        }

        // Remove fenced code blocks before scanning links/tags/headings
        var codeSpans = new List<(int Start, int End)>();
        foreach (Match cm in FencedCodeRegex().Matches(body))
            codeSpans.Add((cm.Index, cm.Index + cm.Length));

        bool InsideCode(int index) => codeSpans.Any(s => index >= s.Start && index < s.End);

        // Headings
        var headings = new List<HeadingItem>();
        var lines = body.Split('\n');
        var inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (trimmed.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var hm = HeadingRegex().Match(trimmed);
            if (hm.Success)
            {
                headings.Add(new HeadingItem(hm.Groups[1].Value.Length, hm.Groups[2].Value.Trim(), i + 1));
            }
        }

        // Wikilinks & Embeds
        var links = new List<NoteLinkItem>();
        foreach (Match m in WikiLinkRegex().Matches(body))
        {
            if (InsideCode(m.Index)) continue;

            var isEmbed = m.Groups[1].Success;
            var targetTitle = m.Groups[2].Value.Trim();
            var heading = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
            var alias = m.Groups[4].Success ? m.Groups[4].Value.Trim() : null;

            var snippetStart = Math.Max(0, m.Index - 35);
            var snippetLen = Math.Min(body.Length - snippetStart, m.Length + 70);
            var snippet = body.Substring(snippetStart, snippetLen).Replace('\r', ' ').Replace('\n', ' ').Trim();

            links.Add(new NoteLinkItem
            {
                SourceNoteId = sourceNoteId,
                TargetTitle = targetTitle,
                TargetHeading = heading,
                Alias = alias,
                LinkType = isEmbed ? "embed" : "wikilink",
                ContextSnippet = snippet
            });
        }

        // Inline #tags
        foreach (Match tm in InlineTagRegex().Matches(body))
        {
            if (InsideCode(tm.Index)) continue;
            var tag = tm.Groups[1].Value.TrimEnd('/');
            if (tag.Length > 0 && !int.TryParse(tag, out _))
                tags.Add(tag);
        }

        var plainText = ToPlainText(body);
        var stats = ComputeWordCountStats(markdown, DocumentFormat.Markdown);
        var fmJson = frontmatter.Count > 0 ? JsonSerializer.Serialize(frontmatter) : null;

        return new MarkdownAnalysisResult(headings, links, [.. tags], frontmatter, fmJson, plainText, stats.WordStyleTotal);
    }

    public static string RewriteWikiLinks(string markdown, string oldTitle, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(markdown) ||
            string.IsNullOrWhiteSpace(oldTitle) ||
            string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            return markdown;

        return WikiLinkRegex().Replace(markdown, m =>
        {
            var target = m.Groups[2].Value.Trim();
            if (!target.Equals(oldTitle, StringComparison.OrdinalIgnoreCase))
                return m.Value;

            var prefix = m.Groups[1].Success ? "!" : "";
            var heading = m.Groups[3].Success ? "#" + m.Groups[3].Value : "";
            var alias = m.Groups[4].Success ? "|" + m.Groups[4].Value : "";
            return $"{prefix}[[{newTitle}{heading}{alias}]]";
        });
    }

    public static string UpsertFrontmatterBackground(string markdown, string? bgColor, BackgroundStyle bgStyle)
    {
        markdown ??= string.Empty;
        var fmMatch = FrontmatterRegex().Match(markdown);
        var styleStr = bgStyle.ToString().ToLowerInvariant();

        if (!fmMatch.Success)
        {
            if (string.IsNullOrWhiteSpace(bgColor) && bgStyle == BackgroundStyle.Solid)
                return markdown;

            var sb = new StringBuilder();
            sb.AppendLine("---");
            if (!string.IsNullOrWhiteSpace(bgColor))
                sb.AppendLine($"bg_color: \"{bgColor}\"");
            if (bgStyle != BackgroundStyle.Solid)
                sb.AppendLine($"bg_style: \"{styleStr}\"");
            sb.AppendLine("---");
            sb.Append(markdown);
            return sb.ToString();
        }

        var yamlLines = fmMatch.Groups[1].Value
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !l.TrimStart().StartsWith("bg_color:", StringComparison.OrdinalIgnoreCase) &&
                        !l.TrimStart().StartsWith("bg_style:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(bgColor))
            yamlLines.Add($"bg_color: \"{bgColor}\"");
        if (bgStyle != BackgroundStyle.Solid)
            yamlLines.Add($"bg_style: \"{styleStr}\"");

        var rest = markdown[fmMatch.Length..];
        if (yamlLines.All(string.IsNullOrWhiteSpace))
            return rest;

        return "---\n" + string.Join("\n", yamlLines) + "\n---\n" + rest;
    }

    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var text = FrontmatterRegex().Replace(markdown, "");
        text = HtmlTagRegex().Replace(text, " ");
        text = WikiLinkRegex().Replace(text, m => m.Groups[4].Success ? m.Groups[4].Value : m.Groups[2].Value);
        text = Regex.Replace(text, @"[#*`~>_\[\]\(\)\|]+", " ");
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    public static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var noScript = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        var text = HtmlTagRegex().Replace(noScript, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string HtmlToLinePreservingText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var noScript = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        var withBreaks = HtmlBlockBreakRegex().Replace(noScript, "\n");
        var stripped = HtmlTagRegex().Replace(withBreaks, "");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped).Replace('\u00A0', ' ');
        return decoded.Replace("\r\n", "\n").TrimEnd('\n');
    }

    public static string BuildSummary(string plainText, int maxLength = 140)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;
        return plainText.Length <= maxLength ? plainText : plainText[..maxLength] + "…";
    }

    public static int CountWords(string plainText) => ComputeWordCountStatsFromPlain(plainText).WordStyleTotal;

    /// <summary>
    /// Computes Microsoft Word-style statistics:
    /// WordStyleTotal = CJK characters (Han/Kana/Hangul + CJK punctuation) + Latin/numeric words,
    /// plus exact CJK chars, Latin words, chars (no spaces), chars (with spaces), paragraphs, and lines.
    /// </summary>
    public static WordCountStats ComputeWordCountStats(string content, DocumentFormat format)
    {
        if (string.IsNullOrEmpty(content))
            return new WordCountStats(0, 0, 0, 0, 0, 0, 0);

        string text;
        if (format == DocumentFormat.RichHtml)
        {
            text = HtmlToLinePreservingText(content);
        }
        else
        {
            text = FrontmatterRegex().Replace(content, "").Replace("\r\n", "\n");
        }

        return ComputeWordCountStatsFromPlain(text);
    }

    public static WordCountStats ComputeWordCountStatsFromPlain(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new WordCountStats(0, 0, 0, 0, 0, 0, 0);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int cjkChars = 0;
        int latinWords = 0;
        int charsNoSpaces = 0;
        int charsWithSpaces = 0;
        bool inLatinWord = false;

        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                inLatinWord = false;
                continue;
            }

            charsWithSpaces++;
            if (!char.IsWhiteSpace(ch))
                charsNoSpaces++;

            if (IsCjkWordChar(ch))
            {
                cjkChars++;
                inLatinWord = false;
            }
            else if (IsLatinWordChar(ch))
            {
                if (!inLatinWord)
                {
                    latinWords++;
                    inLatinWord = true;
                }
            }
            else if (ch is '\'' or '’' or '-' && inLatinWord)
            {
                // Keep contractions/hyphenated English words as single Word-style word
            }
            else
            {
                inLatinWord = false;
            }
        }

        var linesArr = normalized.Split('\n');
        int lines = string.IsNullOrWhiteSpace(normalized) ? 0 : linesArr.Length;
        int paragraphs = linesArr.Count(l => !string.IsNullOrWhiteSpace(l));

        return new WordCountStats(
            WordStyleTotal: cjkChars + latinWords,
            CjkChars: cjkChars,
            LatinWords: latinWords,
            CharsNoSpaces: charsNoSpaces,
            CharsWithSpaces: charsWithSpaces,
            Paragraphs: paragraphs,
            Lines: lines);
    }

    private static bool IsCjkWordChar(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) || // CJK Unified Ideographs
        (ch >= 0x3400 && ch <= 0x4DBF) || // CJK Extension A
        (ch >= 0xF900 && ch <= 0xFAFF) || // CJK Compatibility Ideographs
        (ch >= 0x3040 && ch <= 0x309F) || // Hiragana
        (ch >= 0x30A0 && ch <= 0x30FF) || // Katakana
        (ch >= 0xAC00 && ch <= 0xD7AF);   // Hangul Syllables

    private static bool IsLatinWordChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= 'a' && ch <= 'z') ||
        (ch >= '0' && ch <= '9') ||
        ch == '_' ||
        (ch >= 0x00C0 && ch <= 0x024F) || // Latin Extended
        (ch >= 0x0400 && ch <= 0x04FF);   // Cyrillic

    private static void ParseSimpleYaml(string yaml, Dictionary<string, object?> dict, HashSet<string> tags)
    {
        string? currentListKey = null;
        List<string>? currentList = null;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") && currentListKey is not null && currentList is not null)
            {
                var item = trimmed[2..].Trim().Trim('"', '\'');
                currentList.Add(item);
                if (currentListKey.Equals("tags", StringComparison.OrdinalIgnoreCase) && item.Length > 0)
                    tags.Add(item.TrimStart('#'));
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();
            currentListKey = null;
            currentList = null;

            if (string.IsNullOrEmpty(val))
            {
                currentListKey = key;
                currentList = [];
                dict[key] = currentList;
            }
            else if (val.StartsWith('[') && val.EndsWith(']'))
            {
                var items = val[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.Trim('"', '\''))
                    .ToList();
                dict[key] = items;
                if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var t in items)
                        if (!string.IsNullOrWhiteSpace(t))
                            tags.Add(t.TrimStart('#'));
                }
            }
            else
            {
                var cleanVal = val.Trim('"', '\'');
                dict[key] = cleanVal;
                if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var t in cleanVal.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                        tags.Add(t.TrimStart('#'));
                }
            }
        }
    }
}
