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
/// 单篇、分类目录、多选或全库导出任务统计汇总。
/// </summary>
public sealed record ExportSummary(
    int ExportedNotes,
    int MarkdownCount,
    int HtmlCount,
    int ExportedAssets,
    int SkippedEncryptedCount,
    string OutputPath);

/// <summary>
/// 本地知识库多格式导入与导出引擎：
/// - Markdown 目录树批量导入与多级目录导出（自动按文档格式分别导出 .md 与独立可浏览 .html，并还原内嵌加密图片）
/// - 单篇笔记精准导出（Markdown 仅导出 .md，富文本导出为单文件自包含 .html）
/// - 指定笔记本分类单独导出（支持保留多级子分类目录树，自动区分 .md / .html）
/// - SFDS 单文件笔记包 (.mknote) 无损解析与图片加密入库
/// - 网页 HTML 文件夹 (.html + 关联本地图片目录) 批量导入
/// - 兼容结构化 SQLite 笔记库 (.db) 迁移
/// </summary>
public sealed partial class VaultMigrationService(IVaultRepository repo)
{
    [GeneratedRegex(@"https://vault\.mknote\.local/attachments/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VaultAttachmentUrlRegex();
    [GeneratedRegex(@"<body\b[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlBodyRegex();

    [GeneratedRegex(@"<title\b[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlTitleRegex();

    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*)([""'])([^""']+)\2", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlImgSrcRegex();

    [GeneratedRegex(@"<input\b([^>]*?\btype\s*=\s*[""']?checkbox[""']?[^>]*)/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CheckboxInputRegex();

    [GeneratedRegex(@"<(p|div|li)\b([^>]*)>\s*(?:<label\b[^>]*>\s*)?<input\b([^>]*?\btype\s*=\s*[""']?checkbox[""']?[^>]*)/?>\s*(?:</label>\s*)?([\s\S]*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockTodoItemRegex();

    [GeneratedRegex(@"</ul>\s*<ul\s+data-type=""taskList"">", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AdjacentTaskListsRegex();

    [GeneratedRegex(@"<ul\b(?![^>]*data-type\s*=\s*[""']taskList[""'])[^>]*>(\s*<li\s+data-type=""taskItem""[\s\S]*?)</ul>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PlainUlWrappingTaskItemsRegex();

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
    /// 导出单篇笔记：
    /// - Markdown 笔记严格导出为 .md 文件（若含加密图片则同级自动生成 assets/ 目录并重写相对路径）；
    /// - 富文本笔记严格导出为完整独立可浏览的 .html 文件（内嵌图片自动转为 Data URI 单文件自包含，发给他人或异地打开永不丢图）。
    /// </summary>
    public async Task<ExportSummary> ExportSingleNoteAsync(
        NoteItem note,
        string outputFilePath,
        string? unlockPassword = null,
        CancellationToken ct = default)
    {
        if (note.IsEncrypted && string.IsNullOrEmpty(unlockPassword))
            throw new InvalidOperationException("该笔记已单独加密，请先解锁后再导出。");

        bool isMd = note.DocFormat == DocumentFormat.Markdown;
        var expectedExt = isMd ? ".md" : ".html";
        if (!outputFilePath.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
            outputFilePath = Path.ChangeExtension(outputFilePath, expectedExt);

        var outDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var content = await repo.ReadNoteContentAsync(note, unlockPassword, ct);
        var exportedAssetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isMd)
        {
            var processedMd = await RewriteVaultAssetsAsync(
                content,
                outDir ?? Directory.GetCurrentDirectory(),
                embedImagesAsDataUri: false,
                exportedAssetSet,
                ct);
            await File.WriteAllTextAsync(outputFilePath, processedMd, Encoding.UTF8, ct);
            return new ExportSummary(1, 1, 0, exportedAssetSet.Count, 0, outputFilePath);
        }
        else
        {
            // 单文件富文本 HTML 导出：图片默认内联为 Data URI，形成真正零依赖的单个 .html 文件
            var processedBody = await RewriteVaultAssetsAsync(
                content,
                outDir ?? Directory.GetCurrentDirectory(),
                embedImagesAsDataUri: true,
                exportedAssetSet,
                ct);
            var fullHtml = BuildStandaloneHtmlDocument(note, processedBody);
            await File.WriteAllTextAsync(outputFilePath, fullHtml, Encoding.UTF8, ct);
            return new ExportSummary(1, 0, 1, exportedAssetSet.Count, 0, outputFilePath);
        }
    }

    /// <summary>
    /// 单独导出指定分类笔记本内的全部笔记（可选递归包含子分类并保留多级子目录树），
    /// 自动按每篇笔记的真实格式分别导出 .md 与 .html，并还原内嵌加密图片至同级 assets/ 目录。
    /// </summary>
    public async Task<ExportSummary> ExportCategoryAsync(
        string categoryId,
        string outputParentDir,
        bool includeSubcategories = true,
        IReadOnlyDictionary<string, string>? unlockedPasswords = null,
        CancellationToken ct = default)
    {
        var allCats = (await repo.GetCategoriesAsync(ct)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        if (!allCats.TryGetValue(categoryId, out var rootCat))
            throw new InvalidOperationException("未找到指定的笔记本分类。");

        var rootFolderName = EnsureNonEmptyName(Sanitize(rootCat.Name), "Notebook");
        var categoryRootDir = Path.Combine(outputParentDir, rootFolderName);
        Directory.CreateDirectory(categoryRootDir);

        // 递归构建当前分类及其子分类的目录路径映射（即使子分类下暂无笔记也保留目录层级）
        var catDirMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [rootCat.Id] = categoryRootDir
        };

        if (includeSubcategories)
        {
            var childrenLookup = allCats.Values
                .Where(c => !string.IsNullOrEmpty(c.ParentId))
                .GroupBy(c => c.ParentId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var queue = new Queue<string>();
            queue.Enqueue(rootCat.Id);
            while (queue.Count > 0)
            {
                var curId = queue.Dequeue();
                if (!childrenLookup.TryGetValue(curId, out var children)) continue;
                var parentDir = catDirMap[curId];
                foreach (var child in children)
                {
                    if (catDirMap.ContainsKey(child.Id)) continue;
                    var childDir = Path.Combine(parentDir, EnsureNonEmptyName(Sanitize(child.Name), "SubNotebook"));
                    Directory.CreateDirectory(childDir);
                    catDirMap[child.Id] = childDir;
                    queue.Enqueue(child.Id);
                }
            }
        }

        var notes = await repo.QueryNotesAsync(
            new NoteQueryFilter(CategoryId: categoryId, IncludeChildCategories: includeSubcategories),
            ct);

        return await ExportNotesCoreAsync(
            notes,
            n => n.CategoryId is not null && catDirMap.TryGetValue(n.CategoryId, out var d) ? d : categoryRootDir,
            categoryRootDir,
            unlockedPasswords,
            ct);
    }

    /// <summary>
    /// 批量导出多选笔记到指定文件夹，按各自格式分别导出 .md 与 .html。
    /// </summary>
    public async Task<ExportSummary> ExportNotesBatchAsync(
        IEnumerable<string> noteIds,
        string outputDir,
        IReadOnlyDictionary<string, string>? unlockedPasswords = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var notes = new List<NoteItem>();
        foreach (var id in noteIds.Distinct(StringComparer.Ordinal))
        {
            var note = await repo.GetNoteByIdAsync(id, ct);
            if (note is not null && !note.IsDeleted)
                notes.Add(note);
        }

        return await ExportNotesCoreAsync(notes, _ => outputDir, outputDir, unlockedPasswords, ct);
    }

    /// <summary>
    /// 导出整个知识库中所有未删除笔记，按完整多级笔记本目录树分别导出 .md 与 .html 文件及关联图片。
    /// </summary>
    public async Task<ExportSummary> ExportAllNotesAsync(
        string outputDir,
        IReadOnlyDictionary<string, string>? unlockedPasswords = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var allCats = (await repo.GetCategoriesAsync(ct)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var catDirMap = BuildCategoryDirectoryTree(allCats, outputDir);
        var uncategorizedDir = Path.Combine(outputDir, "未分类");
        var notes = await repo.QueryNotesAsync(new NoteQueryFilter(), ct);

        return await ExportNotesCoreAsync(
            notes,
            n => n.CategoryId is not null && catDirMap.TryGetValue(n.CategoryId, out var d) ? d : uncategorizedDir,
            outputDir,
            unlockedPasswords,
            ct);
    }

    /// <summary>
    /// 将未单独加密的笔记按笔记本多级目录结构导出为标准 Markdown (.md) 与 HTML (.html) 文件夹（兼容旧调用签名）。
    /// </summary>
    public async Task<int> ExportToMarkdownFolderAsync(string outputDir, CancellationToken ct = default)
    {
        var summary = await ExportAllNotesAsync(outputDir, unlockedPasswords: null, ct);
        return summary.ExportedNotes;
    }

    private async Task<ExportSummary> ExportNotesCoreAsync(
        IReadOnlyList<NoteItem> notes,
        Func<NoteItem, string> resolveTargetDir,
        string rootOutputPath,
        IReadOnlyDictionary<string, string>? unlockedPasswords,
        CancellationToken ct)
    {
        int mdCount = 0, htmlCount = 0, skippedEncrypted = 0, totalAssets = 0;
        var usedPathsPerDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var exportedAssetsPerDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            string? unlockPwd = null;
            if (note.IsEncrypted)
            {
                if (unlockedPasswords is null || !unlockedPasswords.TryGetValue(note.Id, out unlockPwd) || string.IsNullOrEmpty(unlockPwd))
                {
                    skippedEncrypted++;
                    continue;
                }
            }

            var targetDir = resolveTargetDir(note);
            Directory.CreateDirectory(targetDir);

            if (!usedPathsPerDir.TryGetValue(targetDir, out var usedNames))
            {
                usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedPathsPerDir[targetDir] = usedNames;
            }
            if (!exportedAssetsPerDir.TryGetValue(targetDir, out var dirAssets))
            {
                dirAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                exportedAssetsPerDir[targetDir] = dirAssets;
            }

            bool isMd = note.DocFormat == DocumentFormat.Markdown;
            var ext = isMd ? ".md" : ".html";
            var baseName = EnsureNonEmptyName(Sanitize(note.Title), "Untitled");
            var uniqueFileName = AllocateUniqueFileName(baseName, ext, usedNames);
            var outFile = Path.Combine(targetDir, uniqueFileName);

            var content = await repo.ReadNoteContentAsync(note, unlockPwd, ct);
            int beforeAssets = dirAssets.Count;
            var processed = await RewriteVaultAssetsAsync(
                content,
                targetDir,
                embedImagesAsDataUri: false,
                dirAssets,
                ct);
            totalAssets += dirAssets.Count - beforeAssets;

            if (isMd)
            {
                await File.WriteAllTextAsync(outFile, processed, Encoding.UTF8, ct);
                mdCount++;
            }
            else
            {
                var fullHtml = BuildStandaloneHtmlDocument(note, processed);
                await File.WriteAllTextAsync(outFile, fullHtml, Encoding.UTF8, ct);
                htmlCount++;
            }
        }

        return new ExportSummary(mdCount + htmlCount, mdCount, htmlCount, totalAssets, skippedEncrypted, rootOutputPath);
    }

    private static Dictionary<string, string> BuildCategoryDirectoryTree(
        IReadOnlyDictionary<string, CategoryItem> allCats,
        string rootDir)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        string ResolvePath(string catId, HashSet<string> visiting)
        {
            if (resolved.TryGetValue(catId, out var existing)) return existing;
            if (!allCats.TryGetValue(catId, out var cat)) return rootDir;
            var safeName = EnsureNonEmptyName(Sanitize(cat.Name), "Notebook");
            string dir;
            if (!string.IsNullOrEmpty(cat.ParentId) &&
                allCats.ContainsKey(cat.ParentId) &&
                visiting.Add(catId))
            {
                var parentDir = ResolvePath(cat.ParentId, visiting);
                dir = Path.Combine(parentDir, safeName);
            }
            else
            {
                dir = Path.Combine(rootDir, safeName);
            }
            resolved[catId] = dir;
            return dir;
        }

        foreach (var id in allCats.Keys)
            ResolvePath(id, []);

        return resolved;
    }

    private async Task<string> RewriteVaultAssetsAsync(
        string content,
        string targetDir,
        bool embedImagesAsDataUri,
        HashSet<string> exportedAssetTracker,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("vault.mknote.local/attachments/", StringComparison.OrdinalIgnoreCase))
            return content;

        var matches = VaultAttachmentUrlRegex().Matches(content);
        if (matches.Count == 0) return content;

        var urlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? assetsDir = null;

        foreach (Match m in matches)
        {
            var fullUrl = m.Value;
            if (urlMap.ContainsKey(fullUrl)) continue;

            var key = m.Groups[1].Value;
            var media = await repo.ReadAttachmentBytesAsync(key, ct);
            if (media is null) continue;

            var (data, mime, fileName) = media.Value;
            if (embedImagesAsDataUri && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                urlMap[fullUrl] = $"data:{mime};base64,{Convert.ToBase64String(data)}";
                exportedAssetTracker.Add($"inline:{key}");
                continue;
            }

            assetsDir ??= Path.Combine(targetDir, "assets");
            Directory.CreateDirectory(assetsDir);

            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = MimeToExt(mime);
            var safeBase = EnsureNonEmptyName(Sanitize(Path.GetFileNameWithoutExtension(fileName)), "asset");
            var shortHash = key.Length > 10 ? key[..10] : key;
            var diskFileName = $"{shortHash}_{safeBase}{ext}";
            var diskPath = Path.Combine(assetsDir, diskFileName);

            if (!File.Exists(diskPath))
                await File.WriteAllBytesAsync(diskPath, data, ct);

            exportedAssetTracker.Add(diskFileName);
            urlMap[fullUrl] = $"assets/{Uri.EscapeDataString(diskFileName)}";
        }

        if (urlMap.Count == 0) return content;
        return VaultAttachmentUrlRegex().Replace(content, m => urlMap.TryGetValue(m.Value, out var mapped) ? mapped : m.Value);
    }

    private static string BuildStandaloneHtmlDocument(NoteItem note, string bodyHtml)
    {
        var safeTitle = System.Net.WebUtility.HtmlEncode(note.Title);
        var bgColor = !string.IsNullOrWhiteSpace(note.BackgroundColor) && BackgroundPreset.IsLightBackground(note.BackgroundColor)
            ? note.BackgroundColor
            : "#FFFFFF";
        var tagsHtml = note.Tags.Count > 0
            ? $"<div class=\"meta-tags\">{string.Join(" ", note.Tags.Select(t => $"<span class=\"tag\">#{System.Net.WebUtility.HtmlEncode(t)}</span>"))}</div>"
            : string.Empty;

        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{{safeTitle}}</title>
              <style>
                :root { color-scheme: light; }
                body {
                  margin: 0;
                  padding: 32px 24px 64px;
                  background: {{bgColor}};
                  color: #1f2328;
                  font-family: "Microsoft YaHei UI", "Segoe UI", -apple-system, BlinkMacSystemFont, sans-serif;
                  font-size: 15px;
                  line-height: 1.75;
                }
                .doc-container { max-width: 960px; margin: 0 auto; }
                .doc-header { border-bottom: 1px solid rgba(0,0,0,0.08); padding-bottom: 12px; margin-bottom: 20px; }
                .doc-title { margin: 0 0 6px; font-size: 26px; font-weight: 600; line-height: 1.35; }
                .doc-meta { font-size: 12.5px; color: #656d76; }
                .meta-tags { margin-top: 8px; display: flex; flex-wrap: wrap; gap: 6px; }
                .tag { font-size: 12px; padding: 2px 8px; border-radius: 999px; background: rgba(9,105,218,0.08); color: #0969da; }
                img { max-width: 100%; height: auto; border-radius: 6px; }
                blockquote { margin: 12px 0; padding: 6px 14px; border-left: 4px solid #0969da; background: rgba(9,105,218,0.05); }
                pre { background: #f6f8fa; padding: 12px 14px; border-radius: 8px; overflow-x: auto; }
                code { font-family: "Cascadia Code", Consolas, monospace; font-size: 0.92em; background: rgba(175,184,193,0.2); padding: 1px 4px; border-radius: 4px; }
                pre code { background: none; padding: 0; }
                table { border-collapse: collapse; width: 100%; margin: 14px 0; }
                th, td { border: 1px solid #d0d7de; padding: 6px 10px; text-align: left; }
                th { background: rgba(0,0,0,0.04); font-weight: 600; }
                ul[data-type="taskList"] { list-style: none; padding-left: 4px; }
                li[data-type="taskItem"] { display: flex; align-items: flex-start; gap: 8px; margin: 4px 0; }
                li[data-type="taskItem"]::before { content: "☐"; font-size: 15px; line-height: 1.6; color: #57606a; }
                li[data-type="taskItem"][data-checked="true"]::before { content: "☑"; color: #1a7f37; }
                li[data-type="taskItem"][data-checked="true"] > p { text-decoration: line-through; opacity: 0.65; }
                li[data-type="taskItem"] > p { margin: 0; }
              </style>
            </head>
            <body>
              <article class="doc-container">
                <header class="doc-header">
                  <h1 class="doc-title">{{safeTitle}}</h1>
                  <div class="doc-meta">更新时间：{{note.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}}</div>
                  {{tagsHtml}}
                </header>
                <main class="doc-content">
            {{bodyHtml}}
                </main>
              </article>
            </body>
            </html>
            """;
    }

    private static string AllocateUniqueFileName(string baseName, string ext, HashSet<string> usedNames)
    {
        var candidate = baseName + ext;
        if (usedNames.Add(candidate)) return candidate;
        for (int i = 2; ; i++)
        {
            candidate = $"{baseName} ({i}){ext}";
            if (usedNames.Add(candidate)) return candidate;
        }
    }

    private static string EnsureNonEmptyName(string s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s;

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
        html = NormalizeImportedHtmlTodos(html);
        if (!string.IsNullOrWhiteSpace(html) &&
            !html.TrimStart().StartsWith("<p", StringComparison.OrdinalIgnoreCase) &&
            !html.TrimStart().StartsWith("<div", StringComparison.OrdinalIgnoreCase) &&
            !html.TrimStart().StartsWith("<ul", StringComparison.OrdinalIgnoreCase))
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

                body = NormalizeImportedHtmlTodos(body);
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

    /// <summary>
    /// 将导入 HTML 中的原生待办复选框（&lt;input type="checkbox"&gt;）规范化为富文本编辑器可交互渲染的标准 taskList/taskItem 结构，
    /// 避免待办完成/未完成图标在载入编辑器时被 Schema 过滤丢失。
    /// </summary>
    public static string NormalizeImportedHtmlTodos(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("checkbox", StringComparison.OrdinalIgnoreCase))
            return html;

        // 1. 将以 <input type="checkbox"> 开头的 <li> / <div> / <p> 转换为标准 TipTap taskItem
        var result = BlockTodoItemRegex().Replace(html, m =>
        {
            var tag = m.Groups[1].Value;
            var attrs = m.Groups[3].Value;
            var inner = m.Groups[4].Value.Trim();
            bool isChecked = IsCheckboxAttrsChecked(attrs);
            var checkedStr = isChecked ? "true" : "false";
            var contentHtml = inner.StartsWith("<p", StringComparison.OrdinalIgnoreCase) ? inner : $"<p>{inner}</p>";

            if (tag.Equals("li", StringComparison.OrdinalIgnoreCase))
                return $"<li data-type=\"taskItem\" data-checked=\"{checkedStr}\">{contentHtml}</li>";

            return $"<ul data-type=\"taskList\"><li data-type=\"taskItem\" data-checked=\"{checkedStr}\">{contentHtml}</li></ul>";
        });

        // 2. 为包裹 taskItem 的普通 <ul> 补上 data-type="taskList"
        result = PlainUlWrappingTaskItemsRegex().Replace(result, "<ul data-type=\"taskList\">$1</ul>");

        // 3. 合并相邻的 taskList 为同一个待办清单
        result = AdjacentTaskListsRegex().Replace(result, string.Empty);

        // 4. 兜底：行内或表格单元格内剩余的独立复选框转为可视勾选图标，确保永不丢失
        result = CheckboxInputRegex().Replace(result, m =>
            IsCheckboxAttrsChecked(m.Groups[1].Value) ? "☑ " : "☐ ");

        return result;
    }

    private static bool IsCheckboxAttrsChecked(string attrs)
    {
        if (string.IsNullOrWhiteSpace(attrs)) return false;
        if (Regex.IsMatch(attrs, @"\bchecked\s*=\s*[""']?(?:false|0)[""']?", RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(attrs, @"\bchecked(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s/>]+))?(?=\s|$|/)", RegexOptions.IgnoreCase);
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
