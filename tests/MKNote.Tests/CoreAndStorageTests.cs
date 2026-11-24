using System.IO.Compression;
using System.Text;
using MKNote.Core.Interfaces;
using MKNote.Core.Localization;
using MKNote.Core.Markdown;
using MKNote.Core.Models;
using MKNote.Core.Templates;
using MKNote.Security;
using MKNote.Storage;
using MKNote.Sync;
using Xunit;

namespace MKNote.Tests;

/// <summary>
/// 核心领域模型、本地加密存储、全文符号检索、端到端加密同步及多格式导入导出的综合单元测试。
/// 所有测试均完全自包含，不依赖外部样本文件或网络服务。
/// </summary>
public sealed class CoreAndStorageTests : IDisposable
{
    private readonly string _tempVault = Path.Combine(Path.GetTempPath(), "mknote_test_" + Guid.NewGuid().ToString("N"));
    private readonly string _tempVaultB = Path.Combine(Path.GetTempPath(), "mknote_testB_" + Guid.NewGuid().ToString("N"));
    private readonly string _tempSyncDir = Path.Combine(Path.GetTempPath(), "mknote_sync_" + Guid.NewGuid().ToString("N"));

    /// <summary>测试结束后清理临时知识库与同步目录。</summary>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _tempVault, _tempVaultB, _tempSyncDir })
        {
            if (Directory.Exists(d))
            {
                try { Directory.Delete(d, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Crypto_Aes256Gcm_RoundTrip_And_WrongPasswordFails()
    {
        var secret = "这是一篇高度机密的本地笔记\n支持 UTF-8 与 Markdown [[双链]]";
        var (cipher, meta) = NoteCryptoService.EncryptNote(secret, "MyStrongP@ss123");

        Assert.Contains("AES-256-GCM", meta);
        Assert.Equal(secret, NoteCryptoService.DecryptNote(cipher, "MyStrongP@ss123"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            NoteCryptoService.DecryptNote(cipher, "WrongPassword"));

        var (hash, salt) = NoteCryptoService.HashAppLockPassword("123456");
        Assert.True(NoteCryptoService.VerifyAppLockPassword("123456", hash, salt));
        Assert.False(NoteCryptoService.VerifyAppLockPassword("000000", hash, salt));
    }

    [Fact]
    public void MarkdownAnalyzer_ExtractsFrontmatter_WikiLinks_Tags_Headings_And_RewritesLinks()
    {
        var md = """
            ---
            tags:
              - 架构/核心
            bg_color: "#EDF7EE"
            ---
            # 一级标题
            参见 [[目标笔记#小节|别名]] 与 ![[附图.png]]，还有 #效率/笔记
            ```
            [[代码块内不应解析]]
            ```
            ## 二级标题
            """;

        var res = MarkdownAnalyzer.Analyze("n1", md);
        Assert.Equal(2, res.Headings.Count);
        Assert.Equal(2, res.Links.Count);
        Assert.Equal("目标笔记", res.Links[0].TargetTitle);
        Assert.Equal("小节", res.Links[0].TargetHeading);
        Assert.Equal("别名", res.Links[0].Alias);
        Assert.Equal("embed", res.Links[1].LinkType);
        Assert.Contains("架构/核心", res.Tags);
        Assert.Contains("效率/笔记", res.Tags);

        var rewritten = MarkdownAnalyzer.RewriteWikiLinks(md, "目标笔记", "新名称");
        Assert.Contains("[[新名称#小节|别名]]", rewritten);
    }

    [Fact]
    public void WordStyleWordCount_MatchesMicrosoftWordFormula_ForChineseEnglishJapanese()
    {
        var text = "你好世界 Hello World\n日本語テスト 2026";
        var stats = MarkdownAnalyzer.ComputeWordCountStatsFromPlain(text);
        // CJK 字符：你好世界(4) + 日本語テスト(6) = 10；西文/数字词：Hello(1) + World(1) + 2026(1) = 3
        Assert.Equal(10, stats.CjkChars);
        Assert.Equal(3, stats.LatinWords);
        Assert.Equal(13, stats.WordStyleTotal);
        Assert.Equal(2, stats.Paragraphs);
        Assert.Equal(2, stats.Lines);
        Assert.True(stats.CharsWithSpaces > stats.CharsNoSpaces);
    }

    [Fact]
    public void ShortcutGuard_StrictlyBlocksFixedAppHotkeys()
    {
        Assert.True(ShortcutGuard.IsReservedAppShortcut("Ctrl+C"));
        Assert.True(ShortcutGuard.IsReservedAppShortcut("ctrl+v"));
        Assert.True(ShortcutGuard.IsReservedAppShortcut("<C-s>"));
        Assert.True(ShortcutGuard.IsReservedAppShortcut("Ctrl+Shift+A"));
        Assert.True(ShortcutGuard.IsReservedAppShortcut("Ctrl+K"));
        Assert.True(ShortcutGuard.IsReservedAppShortcut("Ctrl+E"));
        Assert.True(ShortcutGuard.IsReservedAppShortcut("Esc"));
        Assert.NotNull(ShortcutGuard.ValidateCustomShortcut("Ctrl+S", "markdown"));
        Assert.NotNull(ShortcutGuard.ValidateCustomShortcut("b", "markdown")); // 缺少修饰键
        Assert.Null(ShortcutGuard.ValidateCustomShortcut("Ctrl+Alt+B", "markdown"));
        Assert.Null(ShortcutGuard.ValidateCustomShortcut("jj", "vim"));
    }

    [Fact]
    public void Localization_SupportsChineseEnglishJapanese()
    {
        Assert.Equal("全部笔记", Strings.T("nav_all", "zh-CN"));
        Assert.Equal("All Notes", Strings.T("nav_all", "en-US"));
        Assert.Equal("すべてのノート", Strings.T("nav_all", "ja-JP"));
    }

    [Fact]
    public void BackgroundPreset_WcagContrast_ComputesLightAndDarkCorrectly()
    {
        Assert.True(BackgroundPreset.IsLightBackground("#FFFFFF"));
        Assert.True(BackgroundPreset.IsLightBackground("#EDF7EE"));
        Assert.False(BackgroundPreset.IsLightBackground("#1E1E1E"));
        Assert.False(BackgroundPreset.IsLightBackground("#1B2A20"));
    }

    [Fact]
    public void BuiltinTemplates_ContainAllTemplates_AndExpandDate()
    {
        var all = BuiltinTemplates.GetAll();
        Assert.True(all.Count >= 10);
        var expanded = BuiltinTemplates.ExpandVariables("会议记录 [%date%] {{date}}", new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal("会议记录 [2026-11-24] 2026-11-24", expanded);
    }

    [Fact]
    public async Task SqliteVault_CategoryDelete_DefaultAndCustom_CopyMove_ExactSymbolSearch_CrashRecovery()
    {
        var repo = new SqliteVaultRepository(_tempVault);
        await repo.InitializeAsync();

        // 1. 删除全部默认文件夹，并验证重新初始化后不会重新生成
        var defaultCats = await repo.GetCategoriesAsync();
        Assert.True(defaultCats.Count >= 3);
        foreach (var dc in defaultCats)
            await repo.DeleteCategoryAsync(dc.Id);
        Assert.Empty(await repo.GetCategoriesAsync());

        var repo2 = new SqliteVaultRepository(_tempVault);
        await repo2.InitializeAsync();
        Assert.Empty(await repo2.GetCategoriesAsync());

        // 2. 创建自定义文件夹、复制与移动笔记、为富文本设置标签
        var catA = await repo2.CreateCategoryAsync("文件夹A");
        var catB = await repo2.CreateCategoryAsync("文件夹B");
        var longPrefix = new string('甲', 300); // 超过 140 字符摘要长度，验证深正文搜索
        var richNote = await repo2.CreateNoteAsync(
            "符号测试笔记",
            DocumentFormat.RichHtml,
            catA.Id,
            $"<p>{longPrefix}</p><p>包含特殊符号 C# -> [[双链]] $100 == :: 结尾标记</p>");

        await repo2.UpdateNoteTagsAsync(richNote.Id, ["重要", "开发/CSharp"]);
        var loadedRich = await repo2.GetNoteByIdAsync(richNote.Id);
        Assert.Contains("重要", loadedRich!.Tags);
        Assert.Contains("开发/CSharp", loadedRich.Tags);

        var copied = await repo2.CopyNoteToCategoryAsync(richNote.Id, catB.Id);
        Assert.Equal(catB.Id, copied.CategoryId);
        Assert.NotEqual(richNote.Id, copied.Id);
        Assert.Equal(await repo2.ReadNoteContentAsync(richNote), await repo2.ReadNoteContentAsync(copied));

        await repo2.MoveNoteToCategoryAsync(richNote.Id, catB.Id);
        Assert.Equal(catB.Id, (await repo2.GetNoteByIdAsync(richNote.Id))!.CategoryId);

        await repo2.DeleteCategoryAsync(catA.Id);
        Assert.DoesNotContain(await repo2.GetCategoriesAsync(), c => c.Id == catA.Id);

        // 3. 精准符号与全库深正文检索
        foreach (var symbolQuery in new[] { "C#", "->", "[[双链]]", "$100", "==", "::", "结尾标记" })
        {
            var res = await repo2.QueryNotesAsync(new NoteQueryFilter(SearchQuery: symbolQuery));
            Assert.Contains(res, n => n.Id == richNote.Id);
            var ftsHits = await repo2.FullTextSearchAsync(symbolQuery);
            Assert.Contains(ftsHits, h => h.NoteId == richNote.Id);
        }

        // 4. 防崩溃加密恢复日志：密文落盘、异常重启后自动恢复正文
        await repo2.SaveCrashRecoveryEntryAsync(richNote.Id, richNote.Title, richNote.DocFormat, "<p>崩溃前最后1秒输入的内容CRASH_SAFE</p>");
        var recFile = Path.Combine(_tempVault, "recovery", $"{richNote.Id}.mkr");
        Assert.True(File.Exists(recFile));
        Assert.DoesNotContain("CRASH_SAFE", Encoding.Latin1.GetString(await File.ReadAllBytesAsync(recFile)));

        var recovered = await repo2.RecoverPendingDirtyNotesAsync();
        Assert.Single(recovered);
        Assert.Contains("CRASH_SAFE", await repo2.ReadNoteContentAsync((await repo2.GetNoteByIdAsync(richNote.Id))!));
        Assert.False(File.Exists(recFile));

        // 5. 全局文档背景图不受普通笔记附件垃圾回收影响
        byte[] bgImg = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
        var bgSha = await repo2.SaveGlobalMediaBlobAsync("wall.png", "image/png", bgImg);
        await repo2.PermanentlyDeleteNoteAsync(copied.Id);
        var fetchedBg = await repo2.ReadAttachmentBytesAsync(bgSha);
        Assert.NotNull(fetchedBg);
        Assert.Equal(bgImg, fetchedBg.Value.Data);

        // 6. 事务级批量软删除、批量恢复与批量永久删除验证
        var batchIds = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var n = await repo2.CreateNoteAsync($"批量测试_{i}", DocumentFormat.Markdown, catB.Id, $"正文_{i}");
            batchIds.Add(n.Id);
        }

        Assert.Equal(6, await repo2.SoftDeleteNotesBatchAsync(batchIds));
        Assert.Equal(6, (await repo2.QueryNotesAsync(new NoteQueryFilter(OnlyTrash: true))).Count);

        // 反选模拟：先批量恢复前 2 篇，再批量永久删除后 4 篇
        Assert.Equal(2, await repo2.RestoreNotesBatchAsync(batchIds.Take(2)));
        Assert.Equal(4, await repo2.PermanentlyDeleteNotesBatchAsync(batchIds.Skip(2)));
        Assert.Empty(await repo2.QueryNotesAsync(new NoteQueryFilter(OnlyTrash: true)));
        Assert.NotNull(await repo2.GetNoteByIdAsync(batchIds[0]));
        Assert.Null(await repo2.GetNoteByIdAsync(batchIds[5]));
        // 全局背景图依然完好无损
        Assert.NotNull(await repo2.ReadAttachmentBytesAsync(bgSha));
    }

    [Fact]
    public async Task EndToEndEncryptedSync_RoundTrip_ZeroPlaintextOnTarget_And_WrongPassphraseRejected()
    {
        var repoA = new SqliteVaultRepository(_tempVault);
        await repoA.InitializeAsync();
        var noteA = await repoA.CreateNoteAsync("群晖同步测试", DocumentFormat.Markdown, null, "机密NAS内容_SYNC_SECRET_01");
        byte[] imgBytes = [0x89, 0x50, 0x4E, 0x47, 9, 8, 7, 6];
        var attA = await repoA.SaveAttachmentAsync(noteA.Id, "pic.png", "image/png", imgBytes, isInline: true);

        var syncCfg = new SyncSettings
        {
            Provider = "LocalFolder",
            EndpointUrl = _tempSyncDir,
            EnableE2EE = true
        };
        var syncSvc = new EncryptedSyncService();

        // 设备 A 推送加密笔记与加密附件
        var reportA = await syncSvc.SynchronizeAsync(repoA, syncCfg, "", "StrongE2eeMasterPass!");
        Assert.True(reportA.UploadedNotes >= 1);
        Assert.True(reportA.UploadedBlobs >= 1);

        // 检查同步目标上的原始密文：严禁出现任何明文
        var noteSyncFile = Path.Combine(_tempSyncDir, "notes", $"{noteA.Id}.mke");
        Assert.True(File.Exists(noteSyncFile));
        var rawSyncedText = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(noteSyncFile));
        Assert.DoesNotContain("SYNC_SECRET_01", rawSyncedText);

        // 错误口令必须解密失败
        var repoB = new SqliteVaultRepository(_tempVaultB);
        await repoB.InitializeAsync();
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() =>
            syncSvc.SynchronizeAsync(repoB, syncCfg, "", "WrongPassphrase"));

        // 正确口令完整拉取笔记正文与加密图片
        var reportB = await syncSvc.SynchronizeAsync(repoB, syncCfg, "", "StrongE2eeMasterPass!");
        Assert.True(reportB.DownloadedNotes >= 1);
        Assert.True(reportB.DownloadedBlobs >= 1);

        var syncedNoteOnB = await repoB.GetNoteByIdAsync(noteA.Id);
        Assert.NotNull(syncedNoteOnB);
        Assert.Equal("机密NAS内容_SYNC_SECRET_01", await repoB.ReadNoteContentAsync(syncedNoteOnB!));
        var syncedBlobOnB = await repoB.ReadAttachmentBytesAsync(attA.Sha256);
        Assert.NotNull(syncedBlobOnB);
        Assert.Equal(imgBytes, syncedBlobOnB.Value.Data);
    }

    [Fact]
    public async Task EncryptedVault_StoresNotesAndImagesStrictlyEncrypted_ZeroPlaintextOnDisk()
    {
        var repo = new SqliteVaultRepository(_tempVault);
        await repo.InitializeAsync();
        var cats = await repo.GetCategoriesAsync();

        var uniqueSecretMarker = "TOP_SECRET_MARKER_987654321_XYZ";
        var note = await repo.CreateNoteAsync(
            "机密截图文档",
            DocumentFormat.Markdown,
            cats[0].Id,
            $"正文包含敏感词 {uniqueSecretMarker}");

        byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF, 0x42, 0x99];
        var att = await repo.SaveAttachmentAsync(note.Id, "screenshot_001.png", "image/png", fakePng, isInline: true);

        Assert.False(Directory.Exists(Path.Combine(_tempVault, "notes")));
        Assert.False(Directory.Exists(Path.Combine(_tempVault, "attachments")));
        Assert.False(File.Exists(Path.Combine(_tempVault, "config.json")));

        var loaded = await repo.ReadAttachmentBytesAsync(att.Sha256);
        Assert.NotNull(loaded);
        Assert.Equal(fakePng, loaded.Value.Data);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var rawDbBytes = await File.ReadAllBytesAsync(Path.Combine(_tempVault, "vault.db"));
        Assert.False(rawDbBytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8));

        var rawLatin = Encoding.Latin1.GetString(rawDbBytes);
        Assert.DoesNotContain(uniqueSecretMarker, rawLatin);
        Assert.DoesNotContain("screenshot_001.png", rawLatin);
    }

    [Fact]
    public async Task MknoteArchive_Import_RestoresMetadataHtmlAndEmbeddedImage()
    {
        var repo = new SqliteVaultRepository(_tempVault);
        await repo.InitializeAsync();
        var migrator = new MKNote.Migration.VaultMigrationService(repo);

        // 构造一个自包含的合法 .mknote (SFDS + MKEN + ZLIB) 归档测试样本
        byte[] png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var mknotePath = Path.Combine(_tempVault, "synthetic_sample.mknote");
        BuildSyntheticMknoteFile(mknotePath, "归档测试文档", "项目归档", "标签A", png1x1);

        var summary = await migrator.ImportMknoteFileAsync(mknotePath);
        Assert.Equal(1, summary.NotesImported);
        Assert.Equal(1, summary.AttachmentsImported);
        Assert.Equal(0, summary.SkippedCount);

        var cats = await repo.GetCategoriesAsync();
        var targetCat = cats.FirstOrDefault(c => c.Name == "项目归档");
        Assert.NotNull(targetCat);

        var notes = await repo.QueryNotesAsync(new NoteQueryFilter(CategoryId: targetCat!.Id));
        var imported = Assert.Single(notes);
        Assert.Equal("归档测试文档", imported.Title);
        Assert.Contains("标签A", imported.Tags);

        var html = await repo.ReadNoteContentAsync(imported);
        Assert.Contains("本地归档正文内容", html);
        var atts = await repo.GetAttachmentsAsync(imported.Id);
        var att = Assert.Single(atts);
        Assert.Contains($"https://vault.mknote.local/attachments/{att.Sha256}", html);

        var media = await repo.ReadAttachmentBytesAsync(att.Sha256);
        Assert.NotNull(media);
        Assert.StartsWith("image/", media.Value.MimeType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(png1x1, media.Value.Data);
    }

    [Fact]
    public async Task HtmlFolder_And_MarkdownFolder_ImportExport_WorkEndToEnd()
    {
        var repo = new SqliteVaultRepository(_tempVault);
        await repo.InitializeAsync();
        var migrator = new MKNote.Migration.VaultMigrationService(repo);

        byte[] png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        // 验证 HTML 文件夹导入（含相对路径图片）
        var htmlDir = Path.Combine(_tempVault, "html_export_sample");
        var filesSubDir = Path.Combine(htmlDir, "note_assets");
        Directory.CreateDirectory(filesSubDir);
        await File.WriteAllBytesAsync(Path.Combine(filesSubDir, "pic.png"), png1x1);
        await File.WriteAllTextAsync(Path.Combine(htmlDir, "note1.html"),
            "<html><head><title>HTML批量导入测试</title></head><body><p>完整正文内容</p><img src=\"note_assets/pic.png\" /></body></html>");

        var htmlRes = await migrator.ImportHtmlFolderAsync(htmlDir);
        Assert.Equal(1, htmlRes.NotesImported);
        Assert.Equal(1, htmlRes.AttachmentsImported);
        var htmlNote = await repo.GetNoteByTitleAsync("HTML批量导入测试");
        var htmlBody = await repo.ReadNoteContentAsync(htmlNote!);
        Assert.Contains("完整正文内容", htmlBody);
        Assert.Contains("https://vault.mknote.local/attachments/", htmlBody);

        // 验证 Markdown 文件夹导出与重新导入
        var mdExportDir = Path.Combine(_tempVault, "md_export");
        var exportedCount = await migrator.ExportToMarkdownFolderAsync(mdExportDir);
        Assert.True(exportedCount >= 1);

        var repoImport = new SqliteVaultRepository(_tempVaultB);
        await repoImport.InitializeAsync();
        var migratorImport = new MKNote.Migration.VaultMigrationService(repoImport);
        var mdImportRes = await migratorImport.ImportMarkdownFolderAsync(mdExportDir);
        Assert.True(mdImportRes.NotesImported >= 1);
    }

    /// <summary>生成用于单元测试的标准 .mknote 容器二进制文件（.SFDS + MKEN + ZLIB）。</summary>
    private static void BuildSyntheticMknoteFile(string outputPath, string title, string category, string tag, byte[] imageBytes)
    {
        const string resGuid = "11111111-2222-3333-4444-555555555555";
        var ini = $"""
            [note]
            name={title}
            category={category}
            tags={tag}

            [attach1]
            id={resGuid}
            name=sample.png
            rid={resGuid}
            file=att1.db
            """;
        var html = $"<p>本地归档正文内容</p><p><img src=\"https://example.invalid/{resGuid}\" /></p>";

        static byte[] WrapMken(byte[] raw)
        {
            using var ms = new MemoryStream();
            ms.Write("MKEN"u8);
            ms.Write(new byte[0x7d - 4]);
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        var entries = new (string Name, byte[] Payload)[]
        {
            ("note.ini", Encoding.UTF8.GetBytes(ini)),
            ("note", WrapMken(Encoding.Unicode.GetBytes(html))),
            ("att1.db", WrapMken(imageBytes))
        };

        using var fs = File.Create(outputPath);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        // .SFDS 头部占位（总长 0x40 字节，偏移 0x22 处回填目录表偏移）
        bw.Write(".SFDS"u8);
        bw.Write(new byte[0x40 - 5]);

        var ranges = new List<(string Name, int Start, int End)>();
        foreach (var entry in entries)
        {
            var start = (int)fs.Position;
            bw.Write(entry.Payload);
            var end = (int)fs.Position;
            ranges.Add((entry.Name, start, end));
        }

        // 目录区（Directory）
        var dirOffset = (int)fs.Position;
        static void WriteTaggedInt32(BinaryWriter w, int v) { w.Write((byte)0x04); w.Write(v); }
        foreach (var (name, start, end) in ranges)
        {
            bw.Write("SFDS"u8);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            bw.Write((byte)0x06);
            bw.Write((byte)nameBytes.Length);
            bw.Write(nameBytes);
            WriteTaggedInt32(bw, end - start); // size
            WriteTaggedInt32(bw, 0);           // unk1
            WriteTaggedInt32(bw, start);       // startOff
            WriteTaggedInt32(bw, end);         // endOff
            WriteTaggedInt32(bw, 0);           // unk2
            WriteTaggedInt32(bw, 0);           // unk3
            bw.Write(new byte[16]);            // 16B MD5
            WriteTaggedInt32(bw, 0);           // tail1
            WriteTaggedInt32(bw, 0);           // tail2
        }

        // 回填偏移 0x22 处的目录表偏移
        fs.Seek(0x22, SeekOrigin.Begin);
        bw.Write(dirOffset);
    }
}
