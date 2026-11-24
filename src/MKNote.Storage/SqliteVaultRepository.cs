using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MKNote.Core.Interfaces;
using MKNote.Core.Markdown;
using MKNote.Core.Models;
using MKNote.Core.Templates;
using MKNote.Security;

namespace MKNote.Storage;

/// <summary>
/// 100% 本地加密知识库存储引擎（SQLCipher AES-256 全库加密 + 每块数据独立 AES-256-GCM 加密 + 加密防崩溃预写日志）。
/// 所有笔记正文、屏幕截图、手动插入图片、全局背景图、附件、偏好配置与历史版本均以密文保存，磁盘零明文残留。
/// </summary>
public sealed class SqliteVaultRepository : IVaultRepository
{
    private static readonly byte[] PlainSqliteHeader = "SQLite format 3\0"u8.ToArray();

    // Per-note async mutex prevents concurrent save races on large multi-content documents
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _noteLocks = new(StringComparer.Ordinal);
    // In-memory decrypted text cache (NoteId -> (ContentSha256, DecryptedText)) for instant exact symbol search
    private readonly ConcurrentDictionary<string, (string Sha, string Text)> _searchContentCache = new(StringComparer.Ordinal);

    static SqliteVaultRepository()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public string VaultRootPath { get; }
    public bool IsPortableMode { get; }
    public bool IsDatabaseEncrypted => true;

    private VaultKeyManager _keyManager = null!;

    private string MainDbPath => Path.Combine(VaultRootPath, "vault.db");
    private string RevisionsDbPath => Path.Combine(VaultRootPath, "revisions.db");
    private string BackupsDir => Path.Combine(VaultRootPath, "backups");
    private string RecoveryDir => Path.Combine(VaultRootPath, "recovery");

    private string MainConnString => new SqliteConnectionStringBuilder
    {
        DataSource = MainDbPath,
        Password = _keyManager.SqlCipherPasswordHex,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    private string RevisionsConnString => new SqliteConnectionStringBuilder
    {
        DataSource = RevisionsDbPath,
        Password = _keyManager.SqlCipherPasswordHex,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public SqliteVaultRepository(string? customVaultPath = null)
    {
        var baseDir = AppContext.BaseDirectory;
        var portableFlag = Path.Combine(baseDir, "portable.flag");
        var portableData = Path.Combine(baseDir, "Data");

        if (!string.IsNullOrWhiteSpace(customVaultPath))
        {
            VaultRootPath = customVaultPath;
            IsPortableMode = false;
        }
        else if (File.Exists(portableFlag) || Directory.Exists(portableData))
        {
            VaultRootPath = portableData;
            IsPortableMode = true;
        }
        else
        {
            VaultRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MKNoteNext", "Vault");
            IsPortableMode = false;
        }
    }

    private async Task<SqliteConnection> OpenMainConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(MainConnString);
        await conn.OpenAsync(ct);
        await ExecuteNonQueryAsync(conn, "PRAGMA busy_timeout = 8000; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -16384;", ct);
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(VaultRootPath);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(RecoveryDir);

        _keyManager = VaultKeyManager.LoadOrCreate(VaultRootPath, IsPortableMode);

        // If an earlier unencrypted dev vault exists, retire it cleanly so SQLCipher initializes with zero plaintext
        MigrateOutAnyPlaintextDevFiles();

        await using (var conn = await OpenMainConnectionAsync(ct))
        {
            await ExecuteNonQueryAsync(conn, SchemaSql.MainSchema, ct);
        }

        await using (var revConn = new SqliteConnection(RevisionsConnString))
        {
            await revConn.OpenAsync(ct);
            await ExecuteNonQueryAsync(revConn, "PRAGMA busy_timeout = 8000;", ct);
            await ExecuteNonQueryAsync(revConn, SchemaSql.RevisionsSchema, ct);
        }

        // Only seed default notebooks ONCE on true first-ever launch (so deleting default folders stays deleted!)
        bool alreadySeeded;
        await using (var conn = await OpenMainConnectionAsync(ct))
        {
            await using var checkCmd = new SqliteCommand(
                "SELECT EXISTS(SELECT 1 FROM settings WHERE key = 'vault_seeded') OR EXISTS(SELECT 1 FROM categories) OR EXISTS(SELECT 1 FROM notes)",
                conn);
            alreadySeeded = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct)) != 0;
        }

        if (!alreadySeeded)
        {
            var inbox = await CreateCategoryAsync("默认笔记本", null, ct);
            var knowledge = await CreateCategoryAsync("知识库 (Markdown)", null, ct);
            await CreateCategoryAsync("工作记录", null, ct);

            await CreateNoteAsync(
                "欢迎使用 MKNote Next",
                DocumentFormat.Markdown,
                knowledge.Id,
                """
                ---
                tags:
                  - 欢迎
                  - 指南/入门
                bg_color: "eye_green"
                bg_style: "dot"
                ---
                # 欢迎使用 MKNote Next（全库加密本地知识库）

                > [!tip] SQLCipher AES-256 全库加密 · 防崩溃保护 · 零明文落盘
                > 你的所有**笔记正文、截图、手动插入的图片、全局背景图、附件与版本历史**均经双重加密保存在本地加密数据库 (`vault.db`) 内，任何外部工具都无法直接读取明文。

                ## ✨ 核心特性一览

                1. **应用内截屏 & 加密插图**：点击顶部 **「📷 截图」** 或 **「🖼️ 插图」**，图片自动加密存入数据库并直接插入当前文档。
                2. **双文档内核**：自由创建 **富文本笔记** 或 **双向链接 Markdown**（支持行内预览、分屏实时预览、阅读模式）。
                3. **全局 Vim 模式 & 快捷键指南**：点击右上角 `⋯` 可查看并自定义 Vim 快捷键与 Markdown 快捷键。
                4. **群晖 NAS / WebDAV 端到端加密同步**：采用 AES-256-GCM 端到端加密，数据离开本机前即完成加密。
                5. **双向链接与知识图谱**：点击 [[Markdown 语法与双链示例]] 体验双链与图谱。

                ```mermaid
                flowchart LR
                    A["📷 截图 / 🖼️ 插图 / 📝 笔记"] --> B["AES-256-GCM 块加密"]
                    B --> C["SQLCipher AES-256 加密数据库"]
                    C --> D["防崩溃日志 + E2EE 群晖 NAS 同步"]
                ```

                ## ✅ 快速上手清单
                - [x] 启动即进入全加密本地工作区（无需注册登录）
                - [ ] 右键任意笔记本文件夹，尝试重命名或删除文件夹
                - [ ] 点击顶部「复制到 / 移动到」轻松转移笔记分类
                - [ ] 点击底部字数统计查看 Microsoft Word 风格完整统计
                """,
                backgroundColor: "eye_green",
                backgroundStyle: BackgroundStyle.Dot,
                tags: ["欢迎", "指南/入门"],
                ct: ct);

            await CreateNoteAsync(
                "Markdown 语法与双链示例",
                DocumentFormat.Markdown,
                knowledge.Id,
                """
                ---
                tags:
                  - Markdown
                  - 指南/双链
                bg_color: "parchment"
                bg_style: "lined"
                ---
                # Markdown 语法与双链示例

                本笔记演示流畅的双向链接 Markdown 写作体验，并反向链接到 [[欢迎使用 MKNote Next]]。

                > [!note] 实时预览模式
                > 顶部支持 **行内预览 (Live Preview)**、**分屏实时预览** 与 **阅读模式** 无缝切换。

                ## 数学公式与高亮
                - 支持 ==荧光笔高亮== 与多级标签 #知识管理/双向链接
                - 行内公式：$E = mc^2$

                $$
                \text{Security} = \text{SQLCipher(AES-256)} + \text{BLOB(AES-256-GCM)}
                $$
                """,
                backgroundColor: "parchment",
                backgroundStyle: BackgroundStyle.Lined,
                tags: ["Markdown", "指南/双链"],
                ct: ct);

            await CreateNoteAsync(
                "经典富文本笔记示例",
                DocumentFormat.RichHtml,
                inbox.Id,
                """
                <h2>经典富文本模式（全加密数据库存储）</h2>
                <p>富文本笔记中的所有排版、<mark style="background-color:#FFF59D">多色荧光笔高亮</mark>、表格、截图与手动插入的图片均全部加密保存在本地数据库中，硬盘上不会留下任何明文文件。</p>
                <ul data-type="taskList">
                  <li data-type="taskItem" data-checked="true"><p>内置 10 套常用笔记模板与多种纸张画布背景色</p></li>
                  <li data-type="taskItem" data-checked="false"><p>支持高效文本处理工具（行排序、去重、清除行尾空格、大小写转换、查找替换）</p></li>
                </ul>
                """,
                backgroundColor: "sky_blue",
                backgroundStyle: BackgroundStyle.Solid,
                tags: ["富文本", "示例"],
                ct: ct);

            await using var markConn = await OpenMainConnectionAsync(ct);
            await ExecuteNonQueryAsync(markConn, "INSERT OR REPLACE INTO settings(key, value) VALUES('vault_seeded', '1')", ct);
        }
    }

    private void MigrateOutAnyPlaintextDevFiles()
    {
        bool IsUnencryptedSqlite(string path)
        {
            if (!File.Exists(path)) return false;
            var header = new byte[16];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(header, 0, 16) < 16) return false;
            return header.AsSpan().SequenceEqual(PlainSqliteHeader);
        }

        if (IsUnencryptedSqlite(MainDbPath))
        {
            foreach (var f in new[] { MainDbPath, MainDbPath + "-wal", MainDbPath + "-shm", RevisionsDbPath, RevisionsDbPath + "-wal", RevisionsDbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }

        foreach (var dirName in new[] { "notes", "attachments" })
        {
            var dir = Path.Combine(VaultRootPath, dirName);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        var oldPlainConfig = Path.Combine(VaultRootPath, "config.json");
        if (File.Exists(oldPlainConfig))
        {
            try { File.Delete(oldPlainConfig); } catch { }
        }
    }

    public async Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var list = new List<CategoryItem>();
        await using var conn = await OpenMainConnectionAsync(ct);

        const string sql = """
            SELECT c.id, c.parent_id, c.name, c.icon, c.color, c.sort_order, c.is_deleted, c.created_at, c.updated_at,
                   (SELECT COUNT(*) FROM notes n WHERE n.category_id = c.id AND n.is_deleted = 0) AS note_count
            FROM categories c
            WHERE c.is_deleted = 0
            ORDER BY c.sort_order ASC, c.created_at ASC
            """;

        await using var cmd = new SqliteCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CategoryItem
            {
                Id = r.GetString(0),
                ParentId = r.IsDBNull(1) ? null : r.GetString(1),
                Name = r.GetString(2),
                Icon = r.IsDBNull(3) ? null : r.GetString(3),
                Color = r.IsDBNull(4) ? null : r.GetString(4),
                SortOrder = r.GetInt32(5),
                IsDeleted = r.GetInt32(6) != 0,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
                NoteCount = r.GetInt32(9)
            });
        }
        return list;
    }

    public async Task<CategoryItem> CreateCategoryAsync(string name, string? parentId = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new CategoryItem { Name = name.Trim(), ParentId = parentId, CreatedAt = now, UpdatedAt = now };

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand(
            "INSERT INTO categories(id, parent_id, name, sort_order, is_deleted, created_at, updated_at) VALUES($id, $pid, $name, 0, 0, $now, $now)",
            conn);
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$pid", (object?)item.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", item.Name);
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
        return item;
    }

    public async Task RenameCategoryAsync(string categoryId, string newName, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand(
            "UPDATE categories SET name = $name, updated_at = $now WHERE id = $id", conn);
        cmd.Parameters.AddWithValue("$name", newName.Trim());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", categoryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Deletes ANY category (default or user-created) and its sub-categories safely.
    /// Notes inside the deleted folder are preserved as uncategorized under "All Notes" (zero data loss).
    /// </summary>
    public async Task DeleteCategoryAsync(string categoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        var idsToDelete = await CollectDescendantCategoryIdsAsync(conn, categoryId, ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var id in idsToDelete)
        {
            await using (var moveCmd = new SqliteCommand("UPDATE notes SET category_id = NULL, updated_at = $now WHERE category_id = $id", conn, (SqliteTransaction)tx))
            {
                moveCmd.Parameters.AddWithValue("$now", now);
                moveCmd.Parameters.AddWithValue("$id", id);
                await moveCmd.ExecuteNonQueryAsync(ct);
            }
            await using (var delCmd = new SqliteCommand("UPDATE categories SET is_deleted = 1, updated_at = $now WHERE id = $id", conn, (SqliteTransaction)tx))
            {
                delCmd.Parameters.AddWithValue("$now", now);
                delCmd.Parameters.AddWithValue("$id", id);
                await delCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<NoteItem>> QueryNotesAsync(NoteQueryFilter filter, CancellationToken ct = default)
    {
        var results = new List<NoteItem>();
        await using var conn = await OpenMainConnectionAsync(ct);

        var where = new List<string>();
        await using var cmd = new SqliteCommand { Connection = conn };

        where.Add(filter.OnlyTrash ? "n.is_deleted = 1" : "n.is_deleted = 0");

        if (filter.OnlyStarred)
            where.Add("n.is_starred = 1");

        if (filter.FormatFilter.HasValue)
        {
            where.Add("n.doc_format = $fmt");
            cmd.Parameters.AddWithValue("$fmt", FormatToString(filter.FormatFilter.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.CategoryId))
        {
            if (filter.IncludeChildCategories)
            {
                var catIds = await CollectDescendantCategoryIdsAsync(conn, filter.CategoryId, ct);
                var paramNames = new List<string>();
                for (int i = 0; i < catIds.Count; i++)
                {
                    var p = $"$cid{i}";
                    paramNames.Add(p);
                    cmd.Parameters.AddWithValue(p, catIds[i]);
                }
                where.Add($"n.category_id IN ({string.Join(",", paramNames)})");
            }
            else
            {
                where.Add("n.category_id = $cid");
                cmd.Parameters.AddWithValue("$cid", filter.CategoryId);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.TagName))
        {
            where.Add("EXISTS (SELECT 1 FROM note_tags nt JOIN tags t ON t.id = nt.tag_id WHERE nt.note_id = n.id AND (t.name = $tag OR t.name LIKE $tagPrefix))");
            cmd.Parameters.AddWithValue("$tag", filter.TagName);
            cmd.Parameters.AddWithValue("$tagPrefix", filter.TagName + "/%");
        }

        bool hasSearch = !string.IsNullOrWhiteSpace(filter.SearchQuery);
        string? searchQuery = hasSearch ? filter.SearchQuery!.Trim() : null;

        // When searching, load content_blob too so we guarantee 100% exact symbol & full-body matching across ALL documents
        cmd.CommandText = hasSearch
            ? $"""
               SELECT n.id, n.category_id, n.title, n.title_pinyin, n.doc_format, n.file_rel_path,
                      n.content_summary, n.content_size, n.word_count, n.content_sha256,
                      n.background_color, n.background_style, n.frontmatter_json, n.source_url,
                      n.is_pinned, n.is_starred, n.is_encrypted, n.password_hint, n.crypto_meta_json,
                      n.is_sticky, n.is_deleted, n.deleted_at, n.created_at, n.updated_at, n.content_blob
               FROM notes n
               WHERE {string.Join(" AND ", where)}
               ORDER BY n.is_pinned DESC, n.updated_at DESC
               """
            : $"""
               SELECT n.id, n.category_id, n.title, n.title_pinyin, n.doc_format, n.file_rel_path,
                      n.content_summary, n.content_size, n.word_count, n.content_sha256,
                      n.background_color, n.background_style, n.frontmatter_json, n.source_url,
                      n.is_pinned, n.is_starred, n.is_encrypted, n.password_hint, n.crypto_meta_json,
                      n.is_sticky, n.is_deleted, n.deleted_at, n.created_at, n.updated_at
               FROM notes n
               WHERE {string.Join(" AND ", where)}
               ORDER BY n.is_pinned DESC, n.updated_at DESC
               """;

        var candidates = new List<(NoteItem Note, byte[]? Blob)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var note = ReadNoteRow(r);
            byte[]? blob = hasSearch && !r.IsDBNull(24) ? (byte[])r.GetValue(24) : null;
            candidates.Add((note, blob));
        }

        // Batch-load all tags for candidates
        var allNoteTags = await LoadAllNoteTagsMapAsync(conn, ct);
        foreach (var (note, _) in candidates)
        {
            if (allNoteTags.TryGetValue(note.Id, out var t))
                note.Tags = t;
        }

        if (!hasSearch)
        {
            return candidates.Select(c => c.Note).ToList();
        }

        // Parallel exact-match search across ALL non-encrypted note bodies (including symbols like C#, [[, ->, $, ==, ::)
        var matched = new ConcurrentBag<(int Order, NoteItem Note)>();
        var indexed = candidates.Select((c, idx) => (Index: idx, c.Note, c.Blob)).ToList();

        await Parallel.ForEachAsync(indexed, ct, (item, _) =>
        {
            if (MatchesExactQuery(item.Note, item.Blob, searchQuery!))
            {
                matched.Add((item.Index, item.Note));
            }
            return ValueTask.CompletedTask;
        });

        return matched.OrderBy(m => m.Order).Select(m => m.Note).ToList();
    }

    private bool MatchesExactQuery(NoteItem note, byte[]? contentBlob, string query)
    {
        if (note.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (note.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (note.IsEncrypted)
            return false; // Never leak encrypted note bodies in search

        var decrypted = GetOrDecryptForSearch(note, contentBlob);
        if (decrypted.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (note.DocFormat == DocumentFormat.RichHtml)
        {
            var plain = MarkdownAnalyzer.HtmlToLinePreservingText(decrypted);
            if (plain.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string GetOrDecryptForSearch(NoteItem note, byte[]? contentBlob)
    {
        if (contentBlob is null || contentBlob.Length == 0) return string.Empty;

        if (_searchContentCache.TryGetValue(note.Id, out var cached) &&
            string.Equals(cached.Sha, note.ContentSha256, StringComparison.Ordinal))
        {
            return cached.Text;
        }

        try
        {
            var text = _keyManager.DecryptString(contentBlob);
            _searchContentCache[note.Id] = (note.ContentSha256, text);
            return text;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<NoteItem?> GetNoteByIdAsync(string noteId, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            SELECT id, category_id, title, title_pinyin, doc_format, file_rel_path,
                   content_summary, content_size, word_count, content_sha256,
                   background_color, background_style, frontmatter_json, source_url,
                   is_pinned, is_starred, is_encrypted, password_hint, crypto_meta_json,
                   is_sticky, is_deleted, deleted_at, created_at, updated_at
            FROM notes WHERE id = $id
            """, conn);
        cmd.Parameters.AddWithValue("$id", noteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var note = ReadNoteRow(r);
        note.Tags = await LoadNoteTagsAsync(conn, note.Id, ct);
        return note;
    }

    public async Task<NoteItem?> GetNoteByTitleAsync(string title, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            SELECT id, category_id, title, title_pinyin, doc_format, file_rel_path,
                   content_summary, content_size, word_count, content_sha256,
                   background_color, background_style, frontmatter_json, source_url,
                   is_pinned, is_starred, is_encrypted, password_hint, crypto_meta_json,
                   is_sticky, is_deleted, deleted_at, created_at, updated_at
            FROM notes WHERE is_deleted = 0 AND lower(title) = lower($title) LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("$title", title.Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var note = ReadNoteRow(r);
        note.Tags = await LoadNoteTagsAsync(conn, note.Id, ct);
        return note;
    }

    public async Task<NoteItem> CreateNoteAsync(
        string title,
        DocumentFormat format,
        string? categoryId = null,
        string initialContent = "",
        string? backgroundColor = null,
        BackgroundStyle backgroundStyle = BackgroundStyle.Solid,
        IEnumerable<string>? tags = null,
        bool isSticky = false,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "无标题笔记" : title.Trim();

        var note = new NoteItem
        {
            Id = id,
            CategoryId = categoryId,
            Title = safeTitle,
            DocFormat = format,
            FileRelPath = "db://notes/" + id,
            BackgroundColor = backgroundColor,
            BackgroundStyle = backgroundStyle,
            IsSticky = isSticky,
            CreatedAt = now,
            UpdatedAt = now,
            Tags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? []
        };

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            INSERT INTO notes(
                id, category_id, title, title_pinyin, doc_format, file_rel_path, content_blob,
                content_summary, content_size, word_count, content_sha256,
                background_color, background_style, is_pinned, is_starred,
                is_encrypted, is_sticky, is_deleted, created_at, updated_at)
            VALUES(
                $id, $cid, $title, $py, $fmt, $path, $blob,
                '', 0, 0, '',
                $bg, $bgs, 0, 0,
                0, $sticky, 0, $now, $now)
            """, conn);
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$cid", (object?)note.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$py", note.Title.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$fmt", FormatToString(note.DocFormat));
        cmd.Parameters.AddWithValue("$path", note.FileRelPath);
        cmd.Parameters.AddWithValue("$blob", Array.Empty<byte>());
        cmd.Parameters.AddWithValue("$bg", (object?)note.BackgroundColor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bgs", note.BackgroundStyle.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$sticky", isSticky ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);

        await SaveNoteContentAsync(note, initialContent, createRevision: true, revisionReason: "created", ct: ct);
        return note;
    }

    public async Task<NoteItem> CopyNoteToCategoryAsync(string noteId, string? targetCategoryId, CancellationToken ct = default)
    {
        var source = await GetNoteByIdAsync(noteId, ct) ?? throw new KeyNotFoundException("源笔记不存在。");
        if (source.IsEncrypted)
            throw new InvalidOperationException("请先解密该笔记后再复制到其他笔记本。");

        var content = await ReadNoteContentAsync(source, ct: ct);
        var attachments = await GetAttachmentsAsync(noteId, ct);

        var copyTitle = string.Equals(source.CategoryId, targetCategoryId, StringComparison.Ordinal)
            ? $"{source.Title} (副本)"
            : source.Title;

        var copy = await CreateNoteAsync(
            copyTitle,
            source.DocFormat,
            targetCategoryId,
            content,
            source.BackgroundColor,
            source.BackgroundStyle,
            source.Tags,
            source.IsSticky,
            ct);

        if (attachments.Count > 0)
        {
            await using var conn = await OpenMainConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var att in attachments)
            {
                await using var cmd = new SqliteCommand("""
                    INSERT INTO attachments(id, note_id, file_name, mime_type, file_size, sha256, storage_rel_path, is_inline, created_at)
                    VALUES($id, $nid, $fn, $mime, $sz, $sha, $path, $inl, $now)
                    """, conn, (SqliteTransaction)tx);
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$nid", copy.Id);
                cmd.Parameters.AddWithValue("$fn", att.FileName);
                cmd.Parameters.AddWithValue("$mime", att.MimeType);
                cmd.Parameters.AddWithValue("$sz", att.FileSize);
                cmd.Parameters.AddWithValue("$sha", att.Sha256);
                cmd.Parameters.AddWithValue("$path", att.StorageRelPath);
                cmd.Parameters.AddWithValue("$inl", att.IsInline ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        return copy;
    }

    public async Task MoveNoteToCategoryAsync(string noteId, string? targetCategoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand(
            "UPDATE notes SET category_id = $cid, updated_at = $now WHERE id = $id", conn);
        cmd.Parameters.AddWithValue("$cid", (object?)targetCategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", noteId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateNoteTagsAsync(string noteId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        var note = await GetNoteByIdAsync(noteId, ct);
        if (note is null) return;

        var cleanTags = tags
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        note.Tags = cleanTags;

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SyncNoteTagsAsync(conn, (SqliteTransaction)tx, noteId, cleanTags, ct);
        await using (var upd = new SqliteCommand("UPDATE notes SET updated_at = $now WHERE id = $id", conn, (SqliteTransaction)tx))
        {
            upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            upd.Parameters.AddWithValue("$id", noteId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<string> ReadNoteContentAsync(NoteItem note, string? unlockPassword = null, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("SELECT content_blob FROM notes WHERE id = $id", conn);
        cmd.Parameters.AddWithValue("$id", note.Id);
        var blob = (byte[]?)await cmd.ExecuteScalarAsync(ct);
        if (blob is null || blob.Length == 0)
            return string.Empty;

        if (note.IsEncrypted)
        {
            if (string.IsNullOrEmpty(unlockPassword))
                throw new UnauthorizedAccessException("该笔记已加密，需要输入密码解锁。");
            return NoteCryptoService.DecryptNote(blob, unlockPassword);
        }

        var text = _keyManager.DecryptString(blob);
        _searchContentCache[note.Id] = (note.ContentSha256, text);
        return text;
    }

    public async Task SaveNoteContentAsync(
        NoteItem note,
        string content,
        string? unlockPassword = null,
        bool createRevision = false,
        string revisionReason = "auto_save",
        CancellationToken ct = default)
    {
        // Serialize concurrent writes to the same note so rapid typing + large multi-image docs never lose data
        var gate = _noteLocks.GetOrAdd(note.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            content ??= string.Empty;
            var contentBytes = Encoding.UTF8.GetBytes(content);
            var hashBytes = SHA256.HashData(contentBytes);
            var sha = Convert.ToHexStringLower(hashBytes);
            var hashChanged = !string.Equals(note.ContentSha256, sha, StringComparison.OrdinalIgnoreCase);

            byte[] encryptedContentBlob;
            if (note.IsEncrypted)
            {
                if (string.IsNullOrEmpty(unlockPassword))
                    throw new UnauthorizedAccessException("保存加密笔记需要提供解锁密码。");

                var (cipherBytes, metaJson) = NoteCryptoService.EncryptNote(content, unlockPassword);
                encryptedContentBlob = cipherBytes;
                note.CryptoMetaJson = metaJson;
                note.ContentSummary = string.Empty;
                note.WordCount = 0;
                note.FrontmatterJson = null;
                _searchContentCache.TryRemove(note.Id, out _);
            }
            else
            {
                encryptedContentBlob = _keyManager.EncryptBlob(contentBytes);
            }

            note.ContentSize = encryptedContentBlob.Length;
            note.ContentSha256 = sha;
            note.UpdatedAt = DateTimeOffset.UtcNow;

            IReadOnlyList<NoteLinkItem> extractedLinks = [];
            string plainText = string.Empty;

            if (!note.IsEncrypted)
            {
                if (note.DocFormat == DocumentFormat.Markdown)
                {
                    var analysis = MarkdownAnalyzer.Analyze(note.Id, content);
                    plainText = analysis.PlainText;
                    note.WordCount = analysis.WordCount;
                    note.ContentSummary = MarkdownAnalyzer.BuildSummary(plainText);
                    note.FrontmatterJson = analysis.FrontmatterJson;
                    extractedLinks = analysis.Links;

                    var mergedTags = new HashSet<string>(note.Tags, StringComparer.OrdinalIgnoreCase);
                    foreach (var t in analysis.Tags) mergedTags.Add(t);
                    note.Tags = [.. mergedTags];
                }
                else
                {
                    plainText = MarkdownAnalyzer.HtmlToPlainText(content);
                    note.WordCount = MarkdownAnalyzer.ComputeWordCountStats(content, DocumentFormat.RichHtml).WordStyleTotal;
                    note.ContentSummary = MarkdownAnalyzer.BuildSummary(plainText);
                }
                _searchContentCache[note.Id] = (sha, content);
            }

            await using var conn = await OpenMainConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var cmd = new SqliteCommand("""
                UPDATE notes SET
                    title = $title,
                    title_pinyin = $py,
                    category_id = $cid,
                    content_blob = $blob,
                    content_summary = $sum,
                    content_size = $sz,
                    word_count = $wc,
                    content_sha256 = $sha,
                    background_color = $bg,
                    background_style = $bgs,
                    frontmatter_json = $fm,
                    crypto_meta_json = $cm,
                    is_pinned = $pin,
                    is_starred = $star,
                    is_sticky = $sticky,
                    updated_at = $now
                WHERE id = $id
                """, conn, (SqliteTransaction)tx))
            {
                cmd.Parameters.AddWithValue("$title", note.Title);
                cmd.Parameters.AddWithValue("$py", note.Title.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$cid", (object?)note.CategoryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$blob", encryptedContentBlob);
                cmd.Parameters.AddWithValue("$sum", note.ContentSummary);
                cmd.Parameters.AddWithValue("$sz", note.ContentSize);
                cmd.Parameters.AddWithValue("$wc", note.WordCount);
                cmd.Parameters.AddWithValue("$sha", note.ContentSha256);
                cmd.Parameters.AddWithValue("$bg", (object?)note.BackgroundColor ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bgs", note.BackgroundStyle.ToString().ToLowerInvariant());
                cmd.Parameters.AddWithValue("$fm", (object?)note.FrontmatterJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cm", (object?)note.CryptoMetaJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pin", note.IsPinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$star", note.IsStarred ? 1 : 0);
                cmd.Parameters.AddWithValue("$sticky", note.IsSticky ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", note.UpdatedAt.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$id", note.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await SyncNoteTagsAsync(conn, (SqliteTransaction)tx, note.Id, note.Tags, ct);
            await SyncNoteLinksAsync(conn, (SqliteTransaction)tx, note.Id, extractedLinks, ct);
            await UpdateFtsIndexAsync(conn, (SqliteTransaction)tx, note, plainText, ct);

            await tx.CommitAsync(ct);

            // Once safely committed to SQLCipher WAL, clear crash-recovery dirty draft
            await ClearCrashRecoveryEntryAsync(note.Id, ct);

            if (createRevision && hashChanged && !note.IsEncrypted)
            {
                await SaveRevisionAsync(note, contentBytes, revisionReason, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateNoteMetadataAsync(NoteItem note, CancellationToken ct = default)
    {
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new SqliteCommand("""
            UPDATE notes SET
                category_id = $cid,
                title = $title,
                title_pinyin = $py,
                background_color = $bg,
                background_style = $bgs,
                is_pinned = $pin,
                is_starred = $star,
                is_sticky = $sticky,
                updated_at = $now
            WHERE id = $id
            """, conn, (SqliteTransaction)tx))
        {
            cmd.Parameters.AddWithValue("$cid", (object?)note.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$py", note.Title.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$bg", (object?)note.BackgroundColor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bgs", note.BackgroundStyle.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$pin", note.IsPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$star", note.IsStarred ? 1 : 0);
            cmd.Parameters.AddWithValue("$sticky", note.IsSticky ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", note.UpdatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", note.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await SyncNoteTagsAsync(conn, (SqliteTransaction)tx, note.Id, note.Tags, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<int> RenameNoteWithLinkUpdateAsync(string noteId, string newTitle, CancellationToken ct = default)
    {
        var note = await GetNoteByIdAsync(noteId, ct) ?? throw new KeyNotFoundException("笔记不存在。");
        var oldTitle = note.Title;
        var cleanNewTitle = newTitle.Trim();
        if (string.IsNullOrEmpty(cleanNewTitle) || oldTitle == cleanNewTitle) return 0;

        note.Title = cleanNewTitle;
        await UpdateNoteMetadataAsync(note, ct);

        var backlinks = await GetBacklinksAsync(noteId, oldTitle, ct);
        var updatedSourceIds = new HashSet<string>();

        foreach (var link in backlinks)
        {
            if (!updatedSourceIds.Add(link.SourceNoteId)) continue;
            var sourceNote = await GetNoteByIdAsync(link.SourceNoteId, ct);
            if (sourceNote is null || sourceNote.IsEncrypted || sourceNote.DocFormat != DocumentFormat.Markdown)
                continue;

            var content = await ReadNoteContentAsync(sourceNote, ct: ct);
            var rewritten = MarkdownAnalyzer.RewriteWikiLinks(content, oldTitle, cleanNewTitle);
            if (rewritten != content)
            {
                await SaveNoteContentAsync(sourceNote, rewritten, createRevision: true, revisionReason: "wikilink_rename", ct: ct);
            }
        }

        return updatedSourceIds.Count;
    }

    public async Task SetNoteEncryptionAsync(string noteId, string plainContent, string password, string? hint, CancellationToken ct = default)
    {
        _ = await GetNoteByIdAsync(noteId, ct) ?? throw new KeyNotFoundException("笔记不存在。");
        var (cipherBytes, metaJson) = NoteCryptoService.EncryptNote(plainContent, password);
        _searchContentCache.TryRemove(noteId, out _);

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new SqliteCommand("""
            UPDATE notes SET
                is_encrypted = 1,
                password_hint = $hint,
                crypto_meta_json = $meta,
                content_blob = $blob,
                content_summary = '',
                word_count = 0,
                updated_at = $now
            WHERE id = $id
            """, conn, (SqliteTransaction)tx))
        {
            cmd.Parameters.AddWithValue("$hint", (object?)hint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$meta", metaJson);
            cmd.Parameters.AddWithValue("$blob", cipherBytes);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", noteId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var delFts = new SqliteCommand("DELETE FROM notes_fts WHERE note_id = $id", conn, (SqliteTransaction)tx))
        {
            delFts.Parameters.AddWithValue("$id", noteId);
            await delFts.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task RemoveNoteEncryptionAsync(string noteId, string currentPassword, CancellationToken ct = default)
    {
        var note = await GetNoteByIdAsync(noteId, ct) ?? throw new KeyNotFoundException("笔记不存在。");
        if (!note.IsEncrypted) return;

        var plain = await ReadNoteContentAsync(note, currentPassword, ct);
        note.IsEncrypted = false;
        note.PasswordHint = null;
        note.CryptoMetaJson = null;

        await using (var conn = await OpenMainConnectionAsync(ct))
        {
            await using var cmd = new SqliteCommand(
                "UPDATE notes SET is_encrypted = 0, password_hint = NULL, crypto_meta_json = NULL WHERE id = $id", conn);
            cmd.Parameters.AddWithValue("$id", note.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await SaveNoteContentAsync(note, plain, createRevision: true, revisionReason: "decrypted", ct: ct);
    }

    public async Task SoftDeleteNoteAsync(string noteId, CancellationToken ct = default)
    {
        _searchContentCache.TryRemove(noteId, out _);
        await ClearCrashRecoveryEntryAsync(noteId, ct);

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("UPDATE notes SET is_deleted = 1, deleted_at = $now, updated_at = $now WHERE id = $id", conn);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", noteId);
        await cmd.ExecuteNonQueryAsync(ct);

        await using var fts = new SqliteCommand("DELETE FROM notes_fts WHERE note_id = $id", conn);
        fts.Parameters.AddWithValue("$id", noteId);
        await fts.ExecuteNonQueryAsync(ct);
    }

    public async Task RestoreNoteAsync(string noteId, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("UPDATE notes SET is_deleted = 0, deleted_at = NULL, updated_at = $now WHERE id = $id", conn);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", noteId);
        await cmd.ExecuteNonQueryAsync(ct);

        var note = await GetNoteByIdAsync(noteId, ct);
        if (note is not null && !note.IsEncrypted)
        {
            var content = await ReadNoteContentAsync(note, ct: ct);
            await SaveNoteContentAsync(note, content, ct: ct);
        }
    }

    public async Task PermanentlyDeleteNoteAsync(string noteId, CancellationToken ct = default)
    {
        _searchContentCache.TryRemove(noteId, out _);
        await ClearCrashRecoveryEntryAsync(noteId, ct);

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            DELETE FROM notes WHERE id = $id;
            DELETE FROM notes_fts WHERE note_id = $id;
            DELETE FROM media_blobs
             WHERE sha256 NOT IN (SELECT DISTINCT sha256 FROM attachments)
               AND sha256 NOT IN (SELECT DISTINCT sha256 FROM global_assets);
            """, conn);
        cmd.Parameters.AddWithValue("$id", noteId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EmptyTrashAsync(CancellationToken ct = default)
    {
        var trash = await QueryNotesAsync(new NoteQueryFilter(OnlyTrash: true), ct);
        foreach (var n in trash)
            await PermanentlyDeleteNoteAsync(n.Id, ct);
    }

    public async Task<IReadOnlyList<TagItem>> GetTagsAsync(CancellationToken ct = default)
    {
        var list = new List<TagItem>();
        await using var conn = await OpenMainConnectionAsync(ct);
        const string sql = """
            SELECT t.id, t.name, t.color, COUNT(n.id) AS cnt
            FROM tags t
            LEFT JOIN note_tags nt ON nt.tag_id = t.id
            LEFT JOIN notes n ON n.id = nt.note_id AND n.is_deleted = 0
            GROUP BY t.id
            HAVING cnt > 0
            ORDER BY t.name ASC
            """;
        await using var cmd = new SqliteCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new TagItem
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Color = r.IsDBNull(2) ? null : r.GetString(2),
                NoteCount = r.GetInt32(3)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<NoteLinkItem>> GetBacklinksAsync(string noteId, string noteTitle, CancellationToken ct = default)
    {
        var list = new List<NoteLinkItem>();
        await using var conn = await OpenMainConnectionAsync(ct);
        const string sql = """
            SELECT l.id, l.source_note_id, l.target_note_id, l.target_title, l.target_heading, l.alias, l.link_type, l.context_snippet, n.title
            FROM note_links l
            JOIN notes n ON n.id = l.source_note_id AND n.is_deleted = 0
            WHERE l.target_note_id = $id OR lower(l.target_title) = lower($title)
            ORDER BY n.updated_at DESC
            """;
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.Parameters.AddWithValue("$title", noteTitle);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NoteLinkItem
            {
                Id = r.GetString(0),
                SourceNoteId = r.GetString(1),
                TargetNoteId = r.IsDBNull(2) ? null : r.GetString(2),
                TargetTitle = r.GetString(3),
                TargetHeading = r.IsDBNull(4) ? null : r.GetString(4),
                Alias = r.IsDBNull(5) ? null : r.GetString(5),
                LinkType = r.GetString(6),
                ContextSnippet = r.GetString(7),
                SourceNoteTitle = r.GetString(8)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<NoteLinkItem>> GetOutgoingLinksAsync(string noteId, CancellationToken ct = default)
    {
        var list = new List<NoteLinkItem>();
        await using var conn = await OpenMainConnectionAsync(ct);
        const string sql = """
            SELECT id, source_note_id, target_note_id, target_title, target_heading, alias, link_type, context_snippet
            FROM note_links WHERE source_note_id = $id
            """;
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$id", noteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NoteLinkItem
            {
                Id = r.GetString(0),
                SourceNoteId = r.GetString(1),
                TargetNoteId = r.IsDBNull(2) ? null : r.GetString(2),
                TargetTitle = r.GetString(3),
                TargetHeading = r.IsDBNull(4) ? null : r.GetString(4),
                Alias = r.IsDBNull(5) ? null : r.GetString(5),
                LinkType = r.GetString(6),
                ContextSnippet = r.GetString(7)
            });
        }
        return list;
    }

    public async Task<GraphData> BuildGraphAsync(string? centerNoteId = null, int depth = 2, CancellationToken ct = default)
    {
        var notes = await QueryNotesAsync(new NoteQueryFilter(), ct);
        var byId = notes.ToDictionary(n => n.Id);
        var byTitle = new Dictionary<string, NoteItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes) byTitle.TryAdd(n.Title, n);

        var edges = new List<GraphEdge>();
        var unresolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await OpenMainConnectionAsync(ct);
        await using (var cmd = new SqliteCommand("SELECT source_note_id, target_note_id, target_title, link_type FROM note_links", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var src = r.GetString(0);
                if (!byId.ContainsKey(src)) continue;
                var tgtId = r.IsDBNull(1) ? null : r.GetString(1);
                var tgtTitle = r.GetString(2);
                var kind = r.GetString(3);

                if (tgtId is null && byTitle.TryGetValue(tgtTitle, out var resolved))
                    tgtId = resolved.Id;

                if (tgtId is null)
                {
                    var ghostId = "unresolved:" + tgtTitle.ToLowerInvariant();
                    unresolved.TryAdd(ghostId, tgtTitle);
                    edges.Add(new GraphEdge(src, ghostId, kind));
                }
                else if (byId.ContainsKey(tgtId))
                {
                    edges.Add(new GraphEdge(src, tgtId, kind));
                }
            }
        }

        HashSet<string>? allowed = null;
        if (!string.IsNullOrWhiteSpace(centerNoteId) && byId.ContainsKey(centerNoteId))
        {
            allowed = [centerNoteId];
            var frontier = new Queue<(string Id, int D)>();
            frontier.Enqueue((centerNoteId, 0));
            while (frontier.Count > 0)
            {
                var (curr, d) = frontier.Dequeue();
                if (d >= depth) continue;
                foreach (var e in edges)
                {
                    if (e.Source == curr && allowed.Add(e.Target)) frontier.Enqueue((e.Target, d + 1));
                    if (e.Target == curr && allowed.Add(e.Source)) frontier.Enqueue((e.Source, d + 1));
                }
            }
            edges = edges.Where(e => allowed.Contains(e.Source) && allowed.Contains(e.Target)).ToList();
        }

        var degree = new Dictionary<string, int>();
        foreach (var e in edges)
        {
            degree[e.Source] = degree.GetValueOrDefault(e.Source) + 1;
            degree[e.Target] = degree.GetValueOrDefault(e.Target) + 1;
        }

        var nodes = new List<GraphNode>();
        foreach (var n in notes)
        {
            if (allowed is not null && !allowed.Contains(n.Id)) continue;
            nodes.Add(new GraphNode(n.Id, n.Title, "note", n.CategoryId ?? "root", degree.GetValueOrDefault(n.Id), FormatToString(n.DocFormat)));
        }
        foreach (var (gid, title) in unresolved)
        {
            if (allowed is not null && !allowed.Contains(gid)) continue;
            nodes.Add(new GraphNode(gid, title, "unresolved", "unresolved", degree.GetValueOrDefault(gid), "markdown"));
        }

        return new GraphData(nodes, edges);
    }

    /// <summary>
    /// Full-vault search across ALL non-encrypted notes with exact literal & symbol matching
    /// (supports 1-char CJK, punctuation/operators like C#, [[, ->, $, ==, ::, and full body length).
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> FullTextSearchAsync(string query, int limit = 60, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var clean = query.Trim();

        var candidates = new List<(NoteItem Note, byte[]? Blob)>();
        await using (var conn = await OpenMainConnectionAsync(ct))
        {
            const string sql = """
                SELECT id, category_id, title, title_pinyin, doc_format, file_rel_path,
                       content_summary, content_size, word_count, content_sha256,
                       background_color, background_style, frontmatter_json, source_url,
                       is_pinned, is_starred, is_encrypted, password_hint, crypto_meta_json,
                       is_sticky, is_deleted, deleted_at, created_at, updated_at, content_blob
                FROM notes
                WHERE is_deleted = 0
                ORDER BY updated_at DESC
                """;
            await using var cmd = new SqliteCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var note = ReadNoteRow(r);
                byte[]? blob = r.IsDBNull(24) ? null : (byte[])r.GetValue(24);
                candidates.Add((note, blob));
            }
            var tagMap = await LoadAllNoteTagsMapAsync(conn, ct);
            foreach (var (n, _) in candidates)
                if (tagMap.TryGetValue(n.Id, out var t)) n.Tags = t;
        }

        var hits = new ConcurrentBag<SearchHit>();
        await Parallel.ForEachAsync(candidates, ct, (item, _) =>
        {
            var note = item.Note;
            double score = 0;
            string? snippet = null;

            if (note.Title.Contains(clean, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
                snippet = note.ContentSummary;
            }
            if (note.Tags.Any(t => t.Contains(clean, StringComparison.OrdinalIgnoreCase)))
            {
                score += 50;
                snippet ??= string.Join(" ", note.Tags.Select(t => "#" + t));
            }
            if (!note.IsEncrypted)
            {
                var body = GetOrDecryptForSearch(note, item.Blob);
                var searchTarget = note.DocFormat == DocumentFormat.RichHtml
                    ? MarkdownAnalyzer.HtmlToLinePreservingText(body)
                    : body;

                var idx = searchTarget.IndexOf(clean, StringComparison.OrdinalIgnoreCase);
                if (idx < 0 && note.DocFormat == DocumentFormat.RichHtml)
                {
                    idx = body.IndexOf(clean, StringComparison.OrdinalIgnoreCase);
                    searchTarget = body;
                }
                if (idx >= 0)
                {
                    score += 30;
                    var start = Math.Max(0, idx - 32);
                    var len = Math.Min(searchTarget.Length - start, clean.Length + 72);
                    snippet = searchTarget.Substring(start, len).Replace('\r', ' ').Replace('\n', ' ').Trim();
                }
            }

            if (score > 0)
            {
                hits.Add(new SearchHit(note.Id, note.Title, note.DocFormat, snippet ?? "", note.UpdatedAt, -score));
            }
            return ValueTask.CompletedTask;
        });

        return hits
            .OrderBy(h => h.Rank)
            .ThenByDescending(h => h.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<AttachmentItem>> GetAttachmentsAsync(string noteId, CancellationToken ct = default)
    {
        var list = new List<AttachmentItem>();
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand(
            "SELECT id, note_id, file_name, mime_type, file_size, sha256, storage_rel_path, is_inline, created_at FROM attachments WHERE note_id = $nid ORDER BY created_at DESC",
            conn);
        cmd.Parameters.AddWithValue("$nid", noteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AttachmentItem
            {
                Id = r.GetString(0),
                NoteId = r.GetString(1),
                FileName = r.GetString(2),
                MimeType = r.GetString(3),
                FileSize = r.GetInt64(4),
                Sha256 = r.GetString(5),
                StorageRelPath = r.GetString(6),
                IsInline = r.GetInt32(7) != 0,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8))
            });
        }
        return list;
    }

    public async Task<AttachmentItem> SaveAttachmentAsync(string noteId, string fileName, string mimeType, byte[] data, bool isInline, CancellationToken ct = default)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var mime = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
        var encryptedBlob = _keyManager.EncryptBlob(data);
        var now = DateTimeOffset.UtcNow;

        var item = new AttachmentItem
        {
            NoteId = noteId,
            FileName = Path.GetFileName(fileName),
            MimeType = mime,
            FileSize = data.Length,
            Sha256 = sha,
            StorageRelPath = "db://media_blobs/" + sha,
            IsInline = isInline,
            CreatedAt = now
        };

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var blobCmd = new SqliteCommand("""
            INSERT OR IGNORE INTO media_blobs(sha256, mime_type, file_size, cipher_blob, created_at)
            VALUES($sha, $mime, $sz, $blob, $now)
            """, conn, (SqliteTransaction)tx))
        {
            blobCmd.Parameters.AddWithValue("$sha", sha);
            blobCmd.Parameters.AddWithValue("$mime", mime);
            blobCmd.Parameters.AddWithValue("$sz", data.Length);
            blobCmd.Parameters.AddWithValue("$blob", encryptedBlob);
            blobCmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await blobCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new SqliteCommand("""
            INSERT INTO attachments(id, note_id, file_name, mime_type, file_size, sha256, storage_rel_path, is_inline, created_at)
            VALUES($id, $nid, $fn, $mime, $sz, $sha, $path, $inl, $now)
            """, conn, (SqliteTransaction)tx))
        {
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$nid", item.NoteId);
            cmd.Parameters.AddWithValue("$fn", item.FileName);
            cmd.Parameters.AddWithValue("$mime", item.MimeType);
            cmd.Parameters.AddWithValue("$sz", item.FileSize);
            cmd.Parameters.AddWithValue("$sha", item.Sha256);
            cmd.Parameters.AddWithValue("$path", item.StorageRelPath);
            cmd.Parameters.AddWithValue("$inl", isInline ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return item;
    }

    public async Task<string> SaveGlobalMediaBlobAsync(string fileName, string mimeType, byte[] data, CancellationToken ct = default)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var mime = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
        var encryptedBlob = _keyManager.EncryptBlob(data);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var blobCmd = new SqliteCommand("""
            INSERT OR IGNORE INTO media_blobs(sha256, mime_type, file_size, cipher_blob, created_at)
            VALUES($sha, $mime, $sz, $blob, $now)
            """, conn, (SqliteTransaction)tx))
        {
            blobCmd.Parameters.AddWithValue("$sha", sha);
            blobCmd.Parameters.AddWithValue("$mime", mime);
            blobCmd.Parameters.AddWithValue("$sz", data.Length);
            blobCmd.Parameters.AddWithValue("$blob", encryptedBlob);
            blobCmd.Parameters.AddWithValue("$now", now);
            await blobCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var gaCmd = new SqliteCommand("""
            INSERT OR REPLACE INTO global_assets(key, sha256, file_name, mime_type, updated_at)
            VALUES('global_bg', $sha, $fn, $mime, $now)
            """, conn, (SqliteTransaction)tx))
        {
            gaCmd.Parameters.AddWithValue("$sha", sha);
            gaCmd.Parameters.AddWithValue("$fn", Path.GetFileName(fileName));
            gaCmd.Parameters.AddWithValue("$mime", mime);
            gaCmd.Parameters.AddWithValue("$now", now);
            await gaCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return sha;
    }

    public async Task<(byte[] Data, string MimeType, string FileName)?> ReadAttachmentBytesAsync(string sha256OrAttachmentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha256OrAttachmentId)) return null;
        var clean = sha256OrAttachmentId.Replace("db://media_blobs/", "").Trim();

        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            SELECT m.cipher_blob, m.mime_type,
                   IFNULL((SELECT a.file_name FROM attachments a WHERE a.sha256 = m.sha256 LIMIT 1),
                   IFNULL((SELECT g.file_name FROM global_assets g WHERE g.sha256 = m.sha256 LIMIT 1), m.sha256))
            FROM media_blobs m
            WHERE m.sha256 = $key
               OR m.sha256 = (SELECT a2.sha256 FROM attachments a2 WHERE a2.id = $key LIMIT 1)
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("$key", clean);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var cipherBlob = (byte[])r.GetValue(0);
        var mime = r.GetString(1);
        var name = r.GetString(2);
        var plain = _keyManager.DecryptBlob(cipherBlob);
        return (plain, mime, name);
    }

    public async Task DeleteAttachmentAsync(string attachmentId, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            DELETE FROM attachments WHERE id = $id;
            DELETE FROM media_blobs
             WHERE sha256 NOT IN (SELECT DISTINCT sha256 FROM attachments)
               AND sha256 NOT IN (SELECT DISTINCT sha256 FROM global_assets);
            """, conn);
        cmd.Parameters.AddWithValue("$id", attachmentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string ResolveAttachmentAbsolutePath(string sha256OrRelPath) => string.Empty;

    // ──────────────────────── 加密防崩溃预写恢复日志 ────────────────────────

    public async Task SaveCrashRecoveryEntryAsync(string noteId, string title, DocumentFormat format, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteId)) return;
        Directory.CreateDirectory(RecoveryDir);

        var rec = new CrashRecoveryRecord(noteId, title ?? "", format, content ?? "", DateTimeOffset.UtcNow);
        var json = JsonSerializer.SerializeToUtf8Bytes(rec);
        var encrypted = _keyManager.EncryptBlob(json);

        var targetPath = Path.Combine(RecoveryDir, $"{noteId}.mkr");
        var tmpPath = targetPath + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, encrypted, ct);
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    public Task ClearCrashRecoveryEntryAsync(string noteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteId)) return Task.CompletedTask;
        var targetPath = Path.Combine(RecoveryDir, $"{noteId}.mkr");
        try
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
        }
        catch { }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CrashRecoveryRecord>> RecoverPendingDirtyNotesAsync(CancellationToken ct = default)
    {
        var recovered = new List<CrashRecoveryRecord>();
        if (!Directory.Exists(RecoveryDir)) return recovered;

        foreach (var file in Directory.GetFiles(RecoveryDir, "*.mkr"))
        {
            try
            {
                var enc = await File.ReadAllBytesAsync(file, ct);
                var plain = _keyManager.DecryptBlob(enc);
                var rec = JsonSerializer.Deserialize<CrashRecoveryRecord>(plain);
                if (rec is null || string.IsNullOrWhiteSpace(rec.NoteId))
                {
                    File.Delete(file);
                    continue;
                }

                var note = await GetNoteByIdAsync(rec.NoteId, ct);
                if (note is not null && !note.IsEncrypted && !note.IsDeleted)
                {
                    var currentContent = await ReadNoteContentAsync(note, ct: ct);
                    if (!string.Equals(currentContent, rec.Content, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(rec.Title))
                            note.Title = rec.Title;
                        await SaveNoteContentAsync(note, rec.Content, createRevision: true, revisionReason: "crash_recovery", ct: ct);
                        recovered.Add(rec);
                    }
                }

                if (File.Exists(file)) File.Delete(file);
            }
            catch
            {
                // Corrupt recovery entry should never crash startup
                try { File.Delete(file); } catch { }
            }
        }
        return recovered;
    }

    // ──────────────────────── E2EE Sync Support & Secret Protection ────────────────────────

    public async Task<IReadOnlyList<NoteItem>> GetAllNotesForSyncAsync(CancellationToken ct = default)
    {
        var list = new List<NoteItem>();
        await using var conn = await OpenMainConnectionAsync(ct);
        const string sql = """
            SELECT id, category_id, title, title_pinyin, doc_format, file_rel_path,
                   content_summary, content_size, word_count, content_sha256,
                   background_color, background_style, frontmatter_json, source_url,
                   is_pinned, is_starred, is_encrypted, password_hint, crypto_meta_json,
                   is_sticky, is_deleted, deleted_at, created_at, updated_at
            FROM notes
            ORDER BY updated_at DESC
            """;
        await using var cmd = new SqliteCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadNoteRow(r));

        var tagMap = await LoadAllNoteTagsMapAsync(conn, ct);
        foreach (var n in list)
            if (tagMap.TryGetValue(n.Id, out var t)) n.Tags = t;
        return list;
    }

    public async Task<IReadOnlyList<CategoryItem>> GetAllCategoriesForSyncAsync(CancellationToken ct = default)
    {
        var list = new List<CategoryItem>();
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand(
            "SELECT id, parent_id, name, icon, color, sort_order, is_deleted, created_at, updated_at FROM categories", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CategoryItem
            {
                Id = r.GetString(0),
                ParentId = r.IsDBNull(1) ? null : r.GetString(1),
                Name = r.GetString(2),
                Icon = r.IsDBNull(3) ? null : r.GetString(3),
                Color = r.IsDBNull(4) ? null : r.GetString(4),
                SortOrder = r.GetInt32(5),
                IsDeleted = r.GetInt32(6) != 0,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8))
            });
        }
        return list;
    }

    public async Task UpsertSyncedCategoryAsync(CategoryItem cat, CancellationToken ct = default)
    {
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            INSERT INTO categories(id, parent_id, name, icon, color, sort_order, is_deleted, created_at, updated_at)
            VALUES($id, $pid, $name, $icon, $color, $sort, $del, $crt, $upd)
            ON CONFLICT(id) DO UPDATE SET
                parent_id = excluded.parent_id,
                name = excluded.name,
                icon = excluded.icon,
                color = excluded.color,
                sort_order = excluded.sort_order,
                is_deleted = excluded.is_deleted,
                updated_at = excluded.updated_at
            """, conn);
        cmd.Parameters.AddWithValue("$id", cat.Id);
        cmd.Parameters.AddWithValue("$pid", (object?)cat.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", cat.Name);
        cmd.Parameters.AddWithValue("$icon", (object?)cat.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$color", (object?)cat.Color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sort", cat.SortOrder);
        cmd.Parameters.AddWithValue("$del", cat.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$crt", cat.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$upd", cat.UpdatedAt.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertSyncedNoteAsync(NoteItem note, string content, CancellationToken ct = default)
    {
        var existing = await GetNoteByIdAsync(note.Id, ct);
        if (existing is null)
        {
            await using var conn = await OpenMainConnectionAsync(ct);
            await using var cmd = new SqliteCommand("""
                INSERT INTO notes(
                    id, category_id, title, title_pinyin, doc_format, file_rel_path, content_blob,
                    content_summary, content_size, word_count, content_sha256,
                    background_color, background_style, is_pinned, is_starred,
                    is_encrypted, is_sticky, is_deleted, deleted_at, created_at, updated_at)
                VALUES(
                    $id, $cid, $title, $py, $fmt, $path, $blob,
                    '', 0, 0, '',
                    $bg, $bgs, $pin, $star,
                    0, $sticky, $del, $delAt, $crt, $upd)
                """, conn);
            cmd.Parameters.AddWithValue("$id", note.Id);
            cmd.Parameters.AddWithValue("$cid", (object?)note.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$py", note.Title.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$fmt", FormatToString(note.DocFormat));
            cmd.Parameters.AddWithValue("$path", "db://notes/" + note.Id);
            cmd.Parameters.AddWithValue("$blob", Array.Empty<byte>());
            cmd.Parameters.AddWithValue("$bg", (object?)note.BackgroundColor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bgs", note.BackgroundStyle.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$pin", note.IsPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$star", note.IsStarred ? 1 : 0);
            cmd.Parameters.AddWithValue("$sticky", note.IsSticky ? 1 : 0);
            cmd.Parameters.AddWithValue("$del", note.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$delAt", note.DeletedAt.HasValue ? note.DeletedAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$crt", note.CreatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$upd", note.UpdatedAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var conn = await OpenMainConnectionAsync(ct);
            await using var cmd = new SqliteCommand("UPDATE notes SET is_deleted = $del, deleted_at = $delAt WHERE id = $id", conn);
            cmd.Parameters.AddWithValue("$del", note.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$delAt", note.DeletedAt.HasValue ? note.DeletedAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", note.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!note.IsEncrypted)
        {
            await SaveNoteContentAsync(note, content, createRevision: true, revisionReason: "sync_pull", ct: ct);
            // Preserve exact remote UpdatedAt timestamp to avoid ping-pong re-uploads
            await using var conn = await OpenMainConnectionAsync(ct);
            await using var tsCmd = new SqliteCommand("UPDATE notes SET updated_at = $upd WHERE id = $id", conn);
            tsCmd.Parameters.AddWithValue("$upd", note.UpdatedAt.ToUnixTimeMilliseconds());
            tsCmd.Parameters.AddWithValue("$id", note.Id);
            await tsCmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<(string Sha256, string MimeType, byte[] Data)>> ExportAllMediaBlobsAsync(CancellationToken ct = default)
    {
        var result = new List<(string, string, byte[])>();
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("SELECT sha256, mime_type, cipher_blob FROM media_blobs", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sha = r.GetString(0);
            var mime = r.GetString(1);
            var cipher = (byte[])r.GetValue(2);
            var plain = _keyManager.DecryptBlob(cipher);
            result.Add((sha, mime, plain));
        }
        return result;
    }

    public async Task ImportMediaBlobAsync(string sha256, string mimeType, byte[] data, CancellationToken ct = default)
    {
        var encrypted = _keyManager.EncryptBlob(data);
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand("""
            INSERT OR IGNORE INTO media_blobs(sha256, mime_type, file_size, cipher_blob, created_at)
            VALUES($sha, $mime, $sz, $blob, $now)
            """, conn);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$mime", string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
        cmd.Parameters.AddWithValue("$sz", data.Length);
        cmd.Parameters.AddWithValue("$blob", encrypted);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string ProtectSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        return Convert.ToBase64String(_keyManager.EncryptString(plainText));
    }

    public string UnprotectSecret(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return string.Empty;
        try
        {
            return _keyManager.DecryptString(Convert.FromBase64String(protectedBase64));
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<NoteRevisionItem>> GetRevisionsAsync(string noteId, CancellationToken ct = default)
    {
        var list = new List<NoteRevisionItem>();
        await using var conn = new SqliteConnection(RevisionsConnString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(
            "SELECT id, note_id, doc_format, title, content_sha256, reason, created_at FROM note_revisions WHERE note_id = $nid ORDER BY created_at DESC LIMIT 50",
            conn);
        cmd.Parameters.AddWithValue("$nid", noteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NoteRevisionItem
            {
                Id = r.GetString(0),
                NoteId = r.GetString(1),
                DocFormat = ParseFormat(r.GetString(2)),
                Title = r.GetString(3),
                ContentSha256 = r.GetString(4),
                Reason = r.GetString(5),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6))
            });
        }
        return list;
    }

    public async Task<string> ReadRevisionContentAsync(string revisionId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(RevisionsConnString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand("SELECT content_blob FROM note_revisions WHERE id = $id", conn);
        cmd.Parameters.AddWithValue("$id", revisionId);
        var encBlob = (byte[]?)await cmd.ExecuteScalarAsync(ct);
        if (encBlob is null) return string.Empty;

        var compressed = _keyManager.DecryptBlob(encBlob);
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public Task<IReadOnlyList<NoteTemplateItem>> GetTemplatesAsync(CancellationToken ct = default) =>
        Task.FromResult(BuiltinTemplates.GetAll());

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenMainConnectionAsync(ct);
            await using var cmd = new SqliteCommand("SELECT value FROM settings WHERE key = 'app_settings'", conn);
            var json = (string?)await cmd.ExecuteScalarAsync(ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.VaultPath = VaultRootPath;
                settings.Sync ??= new SyncSettings();
                return settings;
            }
        }
        catch { }

        return new AppSettings { VaultPath = VaultRootPath };
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings);
        await using var conn = await OpenMainConnectionAsync(ct);
        await using var cmd = new SqliteCommand(
            "INSERT INTO settings(key, value) VALUES('app_settings', $val) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            conn);
        cmd.Parameters.AddWithValue("$val", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string> CreateLocalBackupAsync(string? targetDir = null, CancellationToken ct = default)
    {
        var dir = string.IsNullOrWhiteSpace(targetDir) ? BackupsDir : targetDir;
        Directory.CreateDirectory(dir);
        var backupFile = Path.Combine(dir, $"MKNoteEncryptedBackup_{DateTime.Now:yyyyMMdd_HHmmss}.mkbackup");
        var tempDir = Path.Combine(Path.GetTempPath(), "mknote_bak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dstConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(tempDir, "vault.db"),
                Password = _keyManager.SqlCipherPasswordHex,
                Pooling = false
            }.ToString();
            await using (var src = await OpenMainConnectionAsync(ct))
            await using (var dst = new SqliteConnection(dstConnStr))
            {
                await dst.OpenAsync(ct);
                src.BackupDatabase(dst);
                SqliteConnection.ClearPool(dst);
            }

            var keyFile = Path.Combine(VaultRootPath, "vault.key");
            if (File.Exists(keyFile))
                File.Copy(keyFile, Path.Combine(tempDir, "vault.key"), true);

            ZipFile.CreateFromDirectory(tempDir, backupFile, CompressionLevel.Optimal, includeBaseDirectory: false);
            return backupFile;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private async Task SaveRevisionAsync(NoteItem note, byte[] rawBytes, string reason, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await using (var brotli = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            await brotli.WriteAsync(rawBytes, ct);
        var encCompressed = _keyManager.EncryptBlob(ms.ToArray());

        await using var conn = new SqliteConnection(RevisionsConnString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand("""
            INSERT INTO note_revisions(id, note_id, doc_format, title, content_blob, content_sha256, reason, created_at)
            VALUES($id, $nid, $fmt, $title, $blob, $sha, $reason, $now)
            """, conn);
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$nid", note.Id);
        cmd.Parameters.AddWithValue("$fmt", FormatToString(note.DocFormat));
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$blob", encCompressed);
        cmd.Parameters.AddWithValue("$sha", note.ContentSha256);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SyncNoteTagsAsync(SqliteConnection conn, SqliteTransaction tx, string noteId, IEnumerable<string> tags, CancellationToken ct)
    {
        await using (var del = new SqliteCommand("DELETE FROM note_tags WHERE note_id = $nid", conn, tx))
        {
            del.Parameters.AddWithValue("$nid", noteId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var rawTag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = rawTag.Trim().TrimStart('#');
            if (string.IsNullOrEmpty(tag)) continue;

            string? tagId;
            await using (var find = new SqliteCommand("SELECT id FROM tags WHERE name = $name", conn, tx))
            {
                find.Parameters.AddWithValue("$name", tag);
                tagId = (string?)await find.ExecuteScalarAsync(ct);
            }

            if (tagId is null)
            {
                tagId = Guid.NewGuid().ToString("N");
                await using var ins = new SqliteCommand("INSERT INTO tags(id, name) VALUES($id, $name)", conn, tx);
                ins.Parameters.AddWithValue("$id", tagId);
                ins.Parameters.AddWithValue("$name", tag);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await using var link = new SqliteCommand("INSERT OR IGNORE INTO note_tags(note_id, tag_id) VALUES($nid, $tid)", conn, tx);
            link.Parameters.AddWithValue("$nid", noteId);
            link.Parameters.AddWithValue("$tid", tagId);
            await link.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SyncNoteLinksAsync(SqliteConnection conn, SqliteTransaction tx, string noteId, IReadOnlyList<NoteLinkItem> links, CancellationToken ct)
    {
        await using (var del = new SqliteCommand("DELETE FROM note_links WHERE source_note_id = $nid", conn, tx))
        {
            del.Parameters.AddWithValue("$nid", noteId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var l in links)
        {
            string? targetId;
            await using (var find = new SqliteCommand("SELECT id FROM notes WHERE is_deleted = 0 AND lower(title) = lower($t) LIMIT 1", conn, tx))
            {
                find.Parameters.AddWithValue("$t", l.TargetTitle);
                targetId = (string?)await find.ExecuteScalarAsync(ct);
            }

            await using var ins = new SqliteCommand("""
                INSERT INTO note_links(id, source_note_id, target_note_id, target_title, target_heading, alias, link_type, context_snippet)
                VALUES($id, $src, $tgt, $title, $head, $alias, $type, $snip)
                """, conn, tx);
            ins.Parameters.AddWithValue("$id", l.Id);
            ins.Parameters.AddWithValue("$src", noteId);
            ins.Parameters.AddWithValue("$tgt", (object?)targetId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$title", l.TargetTitle);
            ins.Parameters.AddWithValue("$head", (object?)l.TargetHeading ?? DBNull.Value);
            ins.Parameters.AddWithValue("$alias", (object?)l.Alias ?? DBNull.Value);
            ins.Parameters.AddWithValue("$type", l.LinkType);
            ins.Parameters.AddWithValue("$snip", l.ContextSnippet);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpdateFtsIndexAsync(SqliteConnection conn, SqliteTransaction tx, NoteItem note, string plainText, CancellationToken ct)
    {
        await using (var del = new SqliteCommand("DELETE FROM notes_fts WHERE note_id = $id", conn, tx))
        {
            del.Parameters.AddWithValue("$id", note.Id);
            await del.ExecuteNonQueryAsync(ct);
        }

        if (note.IsEncrypted || note.IsDeleted) return;

        await using var ins = new SqliteCommand(
            "INSERT INTO notes_fts(note_id, title, tags_text, plain_content) VALUES($id, $title, $tags, $body)",
            conn, tx);
        ins.Parameters.AddWithValue("$id", note.Id);
        ins.Parameters.AddWithValue("$title", note.Title);
        ins.Parameters.AddWithValue("$tags", string.Join(" ", note.Tags));
        ins.Parameters.AddWithValue("$body", plainText);
        await ins.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> LoadNoteTagsAsync(SqliteConnection conn, string noteId, CancellationToken ct)
    {
        var tags = new List<string>();
        await using var cmd = new SqliteCommand(
            "SELECT t.name FROM tags t JOIN note_tags nt ON nt.tag_id = t.id WHERE nt.note_id = $nid ORDER BY t.name", conn);
        cmd.Parameters.AddWithValue("$nid", noteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) tags.Add(r.GetString(0));
        return tags;
    }

    private static async Task<Dictionary<string, List<string>>> LoadAllNoteTagsMapAsync(SqliteConnection conn, CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var cmd = new SqliteCommand(
            "SELECT nt.note_id, t.name FROM note_tags nt JOIN tags t ON t.id = nt.tag_id ORDER BY t.name", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var nid = r.GetString(0);
            var tname = r.GetString(1);
            if (!map.TryGetValue(nid, out var list))
            {
                list = [];
                map[nid] = list;
            }
            list.Add(tname);
        }
        return map;
    }

    private static async Task<List<string>> CollectDescendantCategoryIdsAsync(SqliteConnection conn, string rootId, CancellationToken ct)
    {
        var all = new List<(string Id, string? ParentId)>();
        await using (var cmd = new SqliteCommand("SELECT id, parent_id FROM categories WHERE is_deleted = 0", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                all.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }

        var result = new List<string> { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            foreach (var (id, pid) in all)
            {
                if (pid == curr && !result.Contains(id))
                {
                    result.Add(id);
                    queue.Enqueue(id);
                }
            }
        }
        return result;
    }

    private static NoteItem ReadNoteRow(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        CategoryId = r.IsDBNull(1) ? null : r.GetString(1),
        Title = r.GetString(2),
        TitlePinyin = r.GetString(3),
        DocFormat = ParseFormat(r.GetString(4)),
        FileRelPath = r.GetString(5),
        ContentSummary = r.GetString(6),
        ContentSize = r.GetInt64(7),
        WordCount = r.GetInt32(8),
        ContentSha256 = r.GetString(9),
        BackgroundColor = r.IsDBNull(10) ? null : r.GetString(10),
        BackgroundStyle = Enum.TryParse<BackgroundStyle>(r.GetString(11), true, out var bs) ? bs : BackgroundStyle.Solid,
        FrontmatterJson = r.IsDBNull(12) ? null : r.GetString(12),
        SourceUrl = r.IsDBNull(13) ? null : r.GetString(13),
        IsPinned = r.GetInt32(14) != 0,
        IsStarred = r.GetInt32(15) != 0,
        IsEncrypted = r.GetInt32(16) != 0,
        PasswordHint = r.IsDBNull(17) ? null : r.GetString(17),
        CryptoMetaJson = r.IsDBNull(18) ? null : r.GetString(18),
        IsSticky = r.GetInt32(19) != 0,
        IsDeleted = r.GetInt32(20) != 0,
        DeletedAt = r.IsDBNull(21) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(21)),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(22)),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(23))
    };

    private static string FormatToString(DocumentFormat f) => f == DocumentFormat.Markdown ? "markdown" : "rich_html";
    private static DocumentFormat ParseFormat(string s) => s.Equals("markdown", StringComparison.OrdinalIgnoreCase) ? DocumentFormat.Markdown : DocumentFormat.RichHtml;

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
