using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MKNote.Core.Interfaces;
using MKNote.Core.Models;

namespace MKNote.Migration;

/// <summary>
/// 批量导入任务统计汇总。
/// </summary>
public sealed record ImportSummary(
    int CategoriesImported,
    int NotesImported,
    int SkippedCount,
    IReadOnlyList<string> Warnings,
    int AttachmentsImported = 0);

/// <summary>
/// 本地知识库多格式导入与导出引擎：
/// - Markdown 目录树批量导入与导出（保留多级文件夹结构）
/// - SFDS 单文件笔记包 (.mknote) 无损解析与图片加密入库
/// - 网页 HTML 文件夹 (.html + 关联本地图片目录) 批量导入
/// - 兼容结构化 SQLite 笔记库 (.db) 迁移
/// </summary>
public sealed partial class VaultMigrationService(IVaultRepository repo)
{
    [GeneratedRegex(@"<body\b[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlBodyRegex();

    [GeneratedRegex(@"<title\b[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlTitleRegex();

    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*)([""'])([^""']+)\2", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlImgSrcRegex();

    /// <summary>
    /// 递归导入本地 Markdown 文件夹，按原目录树自动建立多级笔记本分类。
    /// </summary>
    public async Task<ImportSummary> ImportMarkdownFolderAsync(string folderPath, string? targetParentCategoryId = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        int cats = 0, notes = 0, skipped = 0;
        var warnings = new List<string>();
        var dirToCat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rootCat = await repo.CreateCategoryAsync(Path.GetFileName(folderPath.TrimEnd('\\', '/')), targetParentCategoryId, ct);
        dirToCat[folderPath] = rootCat.Id;
        cats++;

        foreach (var dir in Directory.GetDirectories(folderPath, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue; // 忽略隐藏配置目录
            var parentDir = Path.GetDirectoryName(dir)!;
            var parentId = dirToCat.GetValueOrDefault(parentDir, rootCat.Id);
            var c = await repo.CreateCategoryAsync(name, parentId, ct);
            dirToCat[dir] = c.Id;
            cats++;
        }

        foreach (var mdFile in Directory.GetFiles(folderPath, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(folderPath, mdFile);
            if (rel.Split(Path.DirectorySeparatorChar).Any(seg => seg.StartsWith('.')))
            {
                skipped++;
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(mdFile, Encoding.UTF8, ct);
                var dir = Path.GetDirectoryName(mdFile)!;
                var catId = dirToCat.GetValueOrDefault(dir, rootCat.Id);
                var title = Path.GetFileNameWithoutExtension(mdFile);
                await repo.CreateNoteAsync(title, DocumentFormat.Markdown, catId, content, ct: ct);
                notes++;
            }
            catch (Exception ex)
            {
                skipped++;
                warnings.Add($"{rel}: {ex.Message}");
            }
        }

        return new ImportSummary(cats, notes, skipped, warnings);
    }

    /// <summary>
    /// 将未单独加密的笔记按笔记本目录结构导出为标准 Markdown / HTML 文件夹。
    /// </summary>
    public async Task<int> ExportToMarkdownFolderAsync(string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var categories = (await repo.GetCategoriesAsync(ct)).ToDictionary(c => c.Id);
        var notes = await repo.QueryNotesAsync(new NoteQueryFilter(), ct);
        int exported = 0;

        foreach (var note in notes)
        {
            if (note.IsEncrypted) continue;
            var subFolder = note.CategoryId is not null && categories.TryGetValue(note.CategoryId, out var cat)
                ? Sanitize(cat.Name)
                : "Uncategorized";
            var dir = Path.Combine(outputDir, subFolder);
            Directory.CreateDirectory(dir);

            var ext = note.DocFormat == DocumentFormat.Markdown ? ".md" : ".html";
            var content = await repo.ReadNoteContentAsync(note, ct: ct);
            var outFile = Path.Combine(dir, $"{Sanitize(note.Title)}{ext}");
            await File.WriteAllTextAsync(outFile, content, Encoding.UTF8, ct);
            exported++;
        }

        return exported;
    }

    /// <summary>
    /// 导入 SFDS 单文件归档笔记 (.mknote)：还原标题、分类、标签与正文，并将全部内嵌图片加密入库后重写引用地址。
    /// </summary>
    public async Task<ImportSummary> ImportMknoteFileAsync(string mknotePath, string? targetCategoryId = null, CancellationToken ct = default)
    {
        if (!File.Exists(mknotePath))
            throw new FileNotFoundException("未找到指定的 .mknote 归档文件", mknotePath);

        var parsed = MknotePackageReader.ParseFile(mknotePath);
        int catsCreated = 0;
        string? catId = targetCategoryId;

        if (catId is null && !string.IsNullOrWhiteSpace(parsed.CategoryName))
        {
            var existingCats = await repo.GetCategoriesAsync(ct);
            var match = existingCats.FirstOrDefault(c => c.Name.Equals(parsed.CategoryName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                catId = match.Id;
            }
            else
            {
                var createdCat = await repo.CreateCategoryAsync(parsed.CategoryName, null, ct);
                catId = createdCat.Id;
                catsCreated = 1;
            }
        }

        var note = await repo.CreateNoteAsync(
            parsed.Title,
            DocumentFormat.RichHtml,
            catId,
            initialContent: "",
            tags: parsed.Tags,
            ct: ct);

        string html = parsed.HtmlContent;
        var extraHtml = new StringBuilder();
        int attachCount = 0;

        foreach (var att in parsed.Attachments)
        {
            bool isImg = att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var saved = await repo.SaveAttachmentAsync(note.Id, att.FileName, att.MimeType, att.Bytes, isInline: isImg, ct: ct);
            attachCount++;

            var secureUrl = $"https://vault.mknote.local/attachments/{saved.Sha256}";
            bool replaced = false;

            // 使用通用正则将正文中包含远程资源 ID 或本地附件 ID 的 img src 重写为应用内加密地址
            if (!string.IsNullOrWhiteSpace(att.RemoteId))
            {
                var escapedRid = Regex.Escape(att.RemoteId);
                var updated = Regex.Replace(
                    html,
                    $@"([""'])[^""']*{escapedRid}[^""']*\1",
                    m => $"{m.Groups[1].Value}{secureUrl}{m.Groups[1].Value}",
                    RegexOptions.IgnoreCase);
                if (!string.Equals(updated, html, StringComparison.Ordinal))
                {
                    html = updated;
                    replaced = true;
                }
            }

            if (!replaced && !string.IsNullOrWhiteSpace(att.Id))
            {
                var escapedId = Regex.Escape(att.Id);
                var updated = Regex.Replace(
                    html,
                    $@"([""'])[a-z]+://[^""']*/{escapedId}\1",
                    m => $"{m.Groups[1].Value}{secureUrl}{m.Groups[1].Value}",
                    RegexOptions.IgnoreCase);
                if (!string.Equals(updated, html, StringComparison.Ordinal))
                {
                    html = updated;
                    replaced = true;
                }
            }

            if (!replaced)
            {
                if (isImg)
                    extraHtml.Append($"<p><img src=\"{secureUrl}\" alt=\"{System.Net.WebUtility.HtmlEncode(att.FileName)}\" /></p>");
                else
                    extraHtml.Append($"<p><a href=\"{secureUrl}\">📎 {System.Net.WebUtility.HtmlEncode(att.FileName)}</a></p>");
            }
        }

        if (extraHtml.Length > 0)
            html += extraHtml.ToString();
        if (!string.IsNullOrWhiteSpace(html) && !html.TrimStart().StartsWith("<p", StringComparison.OrdinalIgnoreCase) && !html.TrimStart().StartsWith("<div", StringComparison.OrdinalIgnoreCase))
            html = $"<p>{html}</p>";

        await repo.SaveNoteContentAsync(note, html, createRevision: true, revisionReason: "mknote_import", ct: ct);
        return new ImportSummary(catsCreated, 1, 0, [], AttachmentsImported: attachCount);
    }

    /// <summary>
    /// 批量导入本地 HTML 文件夹，并将文档引用的同级本地图片或 Data URI 图片全部加密存入数据库。
    /// </summary>
    public async Task<ImportSummary> ImportHtmlFolderAsync(string folderPath, string? targetParentCategoryId = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        int cats = 0, notes = 0, skipped = 0, attachments = 0;
        var warnings = new List<string>();
        var rootCat = await repo.CreateCategoryAsync(Path.GetFileName(folderPath.TrimEnd('\\', '/')), targetParentCategoryId, ct);
        cats++;

        var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories))
            .Where(f => !Path.GetFileName(f).Equals("index.html", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var htmlFile in htmlFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rawHtml = await File.ReadAllTextAsync(htmlFile, Encoding.UTF8, ct);
                var titleMatch = HtmlTitleRegex().Match(rawHtml);
                var title = titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value)
                    ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim())
                    : Path.GetFileNameWithoutExtension(htmlFile);

                var bodyMatch = HtmlBodyRegex().Match(rawHtml);
                var body = bodyMatch.Success ? bodyMatch.Groups[1].Value : rawHtml;
                var baseDir = Path.GetDirectoryName(htmlFile)!;

                var note = await repo.CreateNoteAsync(title, DocumentFormat.RichHtml, rootCat.Id, "", ct: ct);

                // 提取并加密所有本地相对路径与内联 Base64 图片
                var matches = HtmlImgSrcRegex().Matches(body);
                if (matches.Count > 0)
                {
                    var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Match m in matches)
                    {
                        var srcVal = m.Groups[3].Value.Trim();
                        if (replacements.ContainsKey(srcVal) ||
                            srcVal.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            srcVal.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (srcVal.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                        {
                            var comma = srcVal.IndexOf(',');
                            if (comma > 0)
                            {
                                var mimePart = srcVal[5..comma].Split(';')[0];
                                var imgBytes = Convert.FromBase64String(srcVal[(comma + 1)..]);
                                var att = await repo.SaveAttachmentAsync(note.Id, $"embedded{MimeToExt(mimePart)}", mimePart, imgBytes, isInline: true, ct: ct);
                                replacements[srcVal] = $"https://vault.mknote.local/attachments/{att.Sha256}";
                                attachments++;
                            }
                            continue;
                        }

                        var decodedRel = Uri.UnescapeDataString(srcVal).Replace('/', Path.DirectorySeparatorChar);
                        var candidate = Path.GetFullPath(Path.Combine(baseDir, decodedRel));
                        if (File.Exists(candidate))
                        {
                            var imgBytes = await File.ReadAllBytesAsync(candidate, ct);
                            var mime = MknotePackageReader.DetectMimeType(imgBytes, candidate);
                            var att = await repo.SaveAttachmentAsync(note.Id, Path.GetFileName(candidate), mime, imgBytes, isInline: true, ct: ct);
                            replacements[srcVal] = $"https://vault.mknote.local/attachments/{att.Sha256}";
                            attachments++;
                        }
                    }

                    body = HtmlImgSrcRegex().Replace(body, m =>
                    {
                        var srcVal = m.Groups[3].Value.Trim();
                        return replacements.TryGetValue(srcVal, out var secureUrl)
                            ? $"{m.Groups[1].Value}{m.Groups[2].Value}{secureUrl}{m.Groups[2].Value}"
                            : m.Value;
                    });
                }

                await repo.SaveNoteContentAsync(note, body, createRevision: true, revisionReason: "html_import", ct: ct);
                notes++;
            }
            catch (Exception ex)
            {
                skipped++;
                warnings.Add($"{Path.GetFileName(htmlFile)}: {ex.Message}");
            }
        }

        return new ImportSummary(cats, notes, skipped, warnings, attachments);
    }

    /// <summary>
    /// 导入包含 Categories / Notes 表的通用本地 SQLite 笔记库 (.db)。
    /// </summary>
    public async Task<ImportSummary> ImportLegacyDbAsync(string sqliteDbPath, CancellationToken ct = default)
    {
        if (!File.Exists(sqliteDbPath))
            throw new FileNotFoundException("未找到指定的本地 SQLite 笔记库文件", sqliteDbPath);

        int cats = 0, notes = 0, skipped = 0;
        var warnings = new List<string>();
        var catMap = new Dictionary<long, string>();

        await using var conn = new SqliteConnection($"Data Source={sqliteDbPath};Mode=ReadOnly");
        await conn.OpenAsync(ct);

        var rootCat = await repo.CreateCategoryAsync("SQLite 数据库导入", null, ct);
        cats++;

        await using (var cmd = new SqliteCommand("SELECT id, ParentID, Name FROM Categories WHERE IFNULL(IsDeleted, 0) = 0", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var oldId = r.GetInt64(0);
                var name = r.IsDBNull(2) ? "未命名分类" : r.GetString(2);
                var created = await repo.CreateCategoryAsync(name, rootCat.Id, ct);
                catMap[oldId] = created.Id;
                cats++;
            }
        }

        await using (var cmd = new SqliteCommand(
            "SELECT id, CategoryID, Name, Tags, ContentSummary, IsEncrypted FROM Notes WHERE IFNULL(IsDeleted, 0) = 0", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                try
                {
                    var oldCatId = r.IsDBNull(1) ? 0L : r.GetInt64(1);
                    var title = r.IsDBNull(2) ? "无标题" : r.GetString(2);
                    var tagsStr = r.IsDBNull(3) ? "" : r.GetString(3);
                    var summary = r.IsDBNull(4) ? "" : r.GetString(4);
                    var isEnc = !r.IsDBNull(5) && r.GetInt32(5) != 0;

                    var targetCat = catMap.GetValueOrDefault(oldCatId, rootCat.Id);
                    var tags = tagsStr.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
                    var body = isEnc
                        ? "<p><em>[该条目为加密笔记元数据记录]</em></p>"
                        : $"<p>{System.Net.WebUtility.HtmlEncode(summary)}</p>";

                    await repo.CreateNoteAsync(title, DocumentFormat.RichHtml, targetCat, body, tags: tags, ct: ct);
                    notes++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    warnings.Add(ex.Message);
                }
            }
        }

        return new ImportSummary(cats, notes, skipped, warnings);
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

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
