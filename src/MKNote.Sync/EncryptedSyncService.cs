using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MKNote.Core.Interfaces;
using MKNote.Core.Models;

namespace MKNote.Sync;

/// <summary>
/// 单次加密同步完成后的统计摘要。
/// </summary>
public sealed record SyncReport(
    int UploadedNotes,
    int DownloadedNotes,
    int ConflictCopiesCreated,
    int UploadedBlobs,
    int DownloadedBlobs,
    int SyncedCategories,
    bool UsedE2EE,
    DateTimeOffset CompletedAtUtc,
    double ElapsedSeconds = 0,
    int SkippedUnchangedNotes = 0,
    int DeletedRemoteNotes = 0,
    int DeletedLocalNotes = 0,
    string SyncMode = "Bidirectional");

/// <summary>
/// 同步进度明细，用于驱动界面精确进度条（当前项 / 总项数）与阶段文字提示。
/// </summary>
public sealed record SyncProgressInfo(
    string Message,
    int Completed = 0,
    int Total = 0,
    bool IsIndeterminate = true)
{
    public double Percent => Total > 0 ? Math.Clamp(Completed * 100.0 / Total, 0, 100) : 0;
}

/// <summary>
/// 端到端加密 (E2EE) 多策略同步引擎。
/// 支持四种清晰可控的同步/备份模式：
/// - Bidirectional（默认）：双向增量同步 + 墓碑联动真删（彻底删除后清理远端并同步到其他设备，绝不复活）+ 冲突自动保留副本。
/// - MergeKeepBoth：保留两者（只增不删，双端合并且冲突保留副本，不因删除操作清理远端文件）。
/// - LocalMaster：以本地为主（单向镜像备份到远端，本机覆盖远端并清理远端多余/已删文件，绝不向本地拉回旧文件）。
/// - RemoteMaster：以远端为主（从远端恢复覆盖本地同名笔记并同步远端删除，绝不修改远端数据）。
/// </summary>
public sealed class EncryptedSyncService
{
    public const string ModeBidirectional = "Bidirectional";
    public const string ModeMergeKeepBoth = "MergeKeepBoth";
    public const string ModeLocalMaster = "LocalMaster";
    public const string ModeRemoteMaster = "RemoteMaster";

    private static readonly byte[] E2eeV1Magic = "MKSE"u8.ToArray(); // v1：每文件独立 PBKDF2（向下兼容读取）
    private static readonly byte[] E2eeV2Magic = "MKS2"u8.ToArray(); // v2：会话级 PBKDF2 主密钥 + 每文件 HKDF-SHA256 独立 AES-256-GCM 密钥
    private static readonly byte[] PlainMagic = "MKSP"u8.ToArray();  // 非 E2EE 封包（仅在用户手动关闭 E2EE 时使用）
    private static readonly byte[] HkdfInfoV2 = "MKNoteSync-E2EE-v2"u8.ToArray();
    private const int Pbkdf2Iterations = 210_000;
    private const int MaxConcurrency = 4;
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(90);

    private sealed class SyncKdfMetadata
    {
        public int Version { get; set; } = 2;
        public string Kdf { get; set; } = "PBKDF2-SHA512";
        public int Iterations { get; set; } = Pbkdf2Iterations;
        public string SaltBase64 { get; set; } = string.Empty;
    }

    private sealed class RemoteNoteManifestEntry
    {
        public string FileName { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public string ContentSha256 { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? CategoryId { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsPinned { get; set; }
        public bool IsStarred { get; set; }
        public string? BackgroundColor { get; set; }
        public BackgroundStyle BackgroundStyle { get; set; } = BackgroundStyle.Solid;

        public static RemoteNoteManifestEntry FromNote(NoteItem n, string fileName) => new()
        {
            FileName = fileName,
            UpdatedAt = n.UpdatedAt,
            ContentSha256 = n.ContentSha256,
            Title = n.Title,
            CategoryId = n.CategoryId,
            IsDeleted = n.IsDeleted,
            IsPinned = n.IsPinned,
            IsStarred = n.IsStarred,
            BackgroundColor = n.BackgroundColor,
            BackgroundStyle = n.BackgroundStyle
        };

        public bool Matches(NoteItem local) =>
            string.Equals(ContentSha256, local.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
            Title == local.Title &&
            CategoryId == local.CategoryId &&
            IsDeleted == local.IsDeleted &&
            IsPinned == local.IsPinned &&
            IsStarred == local.IsStarred &&
            BackgroundColor == local.BackgroundColor &&
            BackgroundStyle == local.BackgroundStyle;
    }

    private sealed class RemoteNotePayload
    {
        public NoteItem Note { get; set; } = new();
        public string Content { get; set; } = string.Empty;
    }

    private sealed class RemoteBlobPayload
    {
        public string Sha256 { get; set; } = string.Empty;
        public string MimeType { get; set; } = "application/octet-stream";
        public byte[] Data { get; set; } = [];
    }

    public static string NormalizeSyncMode(string? mode) => mode switch
    {
        ModeMergeKeepBoth => ModeMergeKeepBoth,
        ModeLocalMaster => ModeLocalMaster,
        ModeRemoteMaster => ModeRemoteMaster,
        _ => ModeBidirectional
    };

    public static ISyncTransport CreateTransport(SyncSettings settings, string plainPassword) =>
        settings.Provider switch
        {
            "SynologyWebDav" or "GenericWebDav" => new WebDavSyncTransport(
                settings.EndpointUrl,
                settings.RemotePath,
                settings.Username,
                plainPassword,
                settings.AllowSelfSignedLanCert),
            "SynologySmb" or "LocalFolder" => new FolderSyncTransport(settings.EndpointUrl),
            _ => throw new InvalidOperationException("请先选择同步方式（群晖 NAS WebDAV / 群晖 SMB / 通用 WebDAV / 本地同步目录）。")
        };

    public Task<SyncReport> SynchronizeAsync(
        IVaultRepository repo,
        SyncSettings settings,
        string plainPassword,
        string? e2eePassphrase,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        IProgress<SyncProgressInfo>? adapter = progress is null
            ? null
            : new Progress<SyncProgressInfo>(p => progress.Report(p.Message));
        return SynchronizeWithProgressAsync(repo, settings, plainPassword, e2eePassphrase, adapter, ct);
    }

    public async Task<SyncReport> SynchronizeWithProgressAsync(
        IVaultRepository repo,
        SyncSettings settings,
        string plainPassword,
        string? e2eePassphrase,
        IProgress<SyncProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        if (settings.EnableE2EE && string.IsNullOrWhiteSpace(e2eePassphrase))
            throw new InvalidOperationException("已开启端到端加密 (E2EE)，必须填写加密同步主密码。");

        var mode = NormalizeSyncMode(settings.SyncMode);
        bool allowWriteRemote = mode != ModeRemoteMaster;
        bool allowReadRemoteToLocal = mode != ModeLocalMaster;

        var sw = Stopwatch.StartNew();
        using var transport = CreateTransport(settings, plainPassword);

        void Report(string msg, int completed = 0, int total = 0, bool indeterminate = true) =>
            progress?.Report(new SyncProgressInfo(msg, completed, total, indeterminate));

        Report("正在连接同步目标...");
        if (allowWriteRemote)
        {
            await transport.EnsureDirectoryAsync("meta", ct).ConfigureAwait(false);
            await transport.EnsureDirectoryAsync("notes", ct).ConfigureAwait(false);
            await transport.EnsureDirectoryAsync("blobs", ct).ConfigureAwait(false);
        }

        // 0. 派生会话级 E2EE 主密钥（整个同步流程仅执行 1 次 PBKDF2）
        byte[]? sessionMasterKey = null;
        try
        {
            if (settings.EnableE2EE)
            {
                Report("正在派生端到端加密会话密钥...");
                sessionMasterKey = await LoadOrCreateSessionMasterKeyAsync(
                    transport,
                    e2eePassphrase!,
                    allowCreateRemote: allowWriteRemote,
                    ct).ConfigureAwait(false);
            }

            int syncedCats = 0, upNotes = 0, downNotes = 0, conflicts = 0, upBlobs = 0, downBlobs = 0;
            int skippedNotes = 0, deletedRemoteNotes = 0, deletedLocalNotes = 0;
            var lastSync = settings.LastSyncTimeUtc ?? DateTimeOffset.MinValue;
            using var repoLock = new SemaphoreSlim(1, 1);

            // 1. 同步笔记本分类
            Report("正在同步笔记本分类...");
            var localCats = (await repo.GetAllCategoriesForSyncAsync(ct).ConfigureAwait(false))
                .ToDictionary(c => c.Id, StringComparer.Ordinal);

            if (mode == ModeLocalMaster)
            {
                var wrappedCats = WrapPayload(
                    JsonSerializer.SerializeToUtf8Bytes(localCats.Values.ToList()),
                    settings.EnableE2EE,
                    e2eePassphrase,
                    sessionMasterKey);
                await transport.WriteFileAsync("meta/categories.mke", wrappedCats, ct).ConfigureAwait(false);
            }
            else
            {
                var remoteCatsBytes = await transport.ReadFileAsync("meta/categories.mke", ct).ConfigureAwait(false);
                var mergedCats = new Dictionary<string, CategoryItem>(localCats, StringComparer.Ordinal);
                bool needUploadCats = allowWriteRemote && remoteCatsBytes is null;

                if (remoteCatsBytes is not null)
                {
                    var remoteJson = UnwrapPayload(remoteCatsBytes, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                    var remoteCats = JsonSerializer.Deserialize<List<CategoryItem>>(remoteJson) ?? [];
                    var remoteMap = remoteCats.ToDictionary(c => c.Id, StringComparer.Ordinal);

                    foreach (var rc in remoteCats)
                    {
                        bool shouldApply = mode == ModeRemoteMaster
                            || !mergedCats.TryGetValue(rc.Id, out var lc)
                            || rc.UpdatedAt > lc.UpdatedAt;

                        if (shouldApply)
                        {
                            mergedCats[rc.Id] = rc;
                            await repo.UpsertSyncedCategoryAsync(rc, ct).ConfigureAwait(false);
                            syncedCats++;
                        }
                        else if (allowWriteRemote && mergedCats.TryGetValue(rc.Id, out var curLocal) && curLocal.UpdatedAt > rc.UpdatedAt)
                        {
                            needUploadCats = true;
                        }
                    }

                    if (allowWriteRemote && localCats.Keys.Any(id => !remoteMap.ContainsKey(id)))
                        needUploadCats = true;
                }

                if (needUploadCats)
                {
                    var wrappedCats = WrapPayload(
                        JsonSerializer.SerializeToUtf8Bytes(mergedCats.Values.ToList()),
                        settings.EnableE2EE,
                        e2eePassphrase,
                        sessionMasterKey);
                    await transport.WriteFileAsync("meta/categories.mke", wrappedCats, ct).ConfigureAwait(false);
                }
            }

            // 2. 同步加密图片与附件（按 SHA-256 去重 + 4 并发）
            Report("正在扫描图片与附件清单...");
            var localBlobs = await repo.ExportAllMediaBlobsAsync(ct).ConfigureAwait(false);
            var localBlobSet = localBlobs.Select(b => b.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remoteBlobFiles = (await transport.ListFilesAsync("blobs", ct).ConfigureAwait(false))
                .Where(f => f.EndsWith(".mkb", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (allowWriteRemote)
            {
                var blobsToUpload = localBlobs
                    .Where(b => !remoteBlobFiles.Contains($"{b.Sha256}.mkb"))
                    .ToList();

                if (blobsToUpload.Count > 0)
                {
                    int done = 0;
                    Report($"正在上传加密图片与附件 (0 / {blobsToUpload.Count})...", 0, blobsToUpload.Count, false);
                    await RunBoundedAsync(blobsToUpload, MaxConcurrency, async item =>
                    {
                        var fname = $"{item.Sha256}.mkb";
                        var payload = new RemoteBlobPayload { Sha256 = item.Sha256, MimeType = item.MimeType, Data = item.Data };
                        var enc = WrapPayload(
                            JsonSerializer.SerializeToUtf8Bytes(payload),
                            settings.EnableE2EE,
                            e2eePassphrase,
                            sessionMasterKey);
                        await transport.WriteFileAsync($"blobs/{fname}", enc, ct).ConfigureAwait(false);
                        var cur = Interlocked.Increment(ref done);
                        Interlocked.Increment(ref upBlobs);
                        Report($"正在上传加密图片与附件 ({cur} / {blobsToUpload.Count})...", cur, blobsToUpload.Count, false);
                    }, ct).ConfigureAwait(false);
                }
            }

            if (allowReadRemoteToLocal)
            {
                var blobsToDownload = remoteBlobFiles
                    .Where(fname => !localBlobSet.Contains(Path.GetFileNameWithoutExtension(fname)))
                    .ToList();

                if (blobsToDownload.Count > 0)
                {
                    int done = 0;
                    Report($"正在下载加密图片与附件 (0 / {blobsToDownload.Count})...", 0, blobsToDownload.Count, false);
                    await RunBoundedAsync(blobsToDownload, MaxConcurrency, async fname =>
                    {
                        var raw = await transport.ReadFileAsync($"blobs/{fname}", ct).ConfigureAwait(false);
                        if (raw is not null)
                        {
                            var plain = UnwrapPayload(raw, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                            var payload = JsonSerializer.Deserialize<RemoteBlobPayload>(plain);
                            if (payload is not null)
                            {
                                await repoLock.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    await repo.ImportMediaBlobAsync(payload.Sha256, payload.MimeType, payload.Data, ct).ConfigureAwait(false);
                                }
                                finally
                                {
                                    repoLock.Release();
                                }
                                Interlocked.Increment(ref downBlobs);
                            }
                        }
                        var cur = Interlocked.Increment(ref done);
                        Report($"正在下载加密图片与附件 ({cur} / {blobsToDownload.Count})...", cur, blobsToDownload.Count, false);
                    }, ct).ConfigureAwait(false);
                }
            }

            // 3. 读取全量本地笔记、远端文件清单、增量索引清单与删除墓碑
            Report("正在读取远端笔记索引与删除记录...");
            var allLocalNotesList = await repo.GetAllNotesForSyncAsync(ct).ConfigureAwait(false);
            var allLocalNoteIds = allLocalNotesList.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var localNotes = allLocalNotesList
                .Where(n => !n.IsEncrypted) // 单独加锁的私密笔记保持仅存本机，除非用户先解锁
                .ToDictionary(n => n.Id, StringComparer.Ordinal);

            var remoteNoteFiles = (await transport.ListFilesAsync("notes", ct).ConfigureAwait(false))
                .Where(f => f.EndsWith(".mke", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.Ordinal);

            var manifest = new ConcurrentDictionary<string, RemoteNoteManifestEntry>(StringComparer.Ordinal);
            bool manifestDirty = false;

            var manifestBytes = await transport.ReadFileAsync("meta/manifest.mke", ct).ConfigureAwait(false);
            if (manifestBytes is not null)
            {
                var manifestJson = UnwrapPayload(manifestBytes, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, RemoteNoteManifestEntry>>(manifestJson);
                if (parsed is not null)
                {
                    foreach (var kv in parsed)
                    {
                        if (remoteNoteFiles.ContainsKey(kv.Key))
                            manifest[kv.Key] = kv.Value;
                        else
                            manifestDirty = true;
                    }
                }
            }
            else if (allowWriteRemote && (remoteNoteFiles.Count > 0 || localNotes.Count > 0))
            {
                manifestDirty = true;
            }

            // 加载并合并删除墓碑（仅在 Bidirectional / LocalMaster / RemoteMaster 生效；MergeKeepBoth 忽略墓碑只增不删）
            await repo.PruneOldTombstonesAsync(TombstoneRetention, ct).ConfigureAwait(false);
            var localTombstones = await repo.GetNoteTombstonesAsync(ct).ConfigureAwait(false);
            var remoteTombstones = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var tombBytes = await transport.ReadFileAsync("meta/tombstones.mke", ct).ConfigureAwait(false);
            if (tombBytes is not null)
            {
                var tombJson = UnwrapPayload(tombBytes, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                var parsedTombs = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(tombJson);
                if (parsedTombs is not null)
                {
                    var cutoff = DateTimeOffset.UtcNow.Subtract(TombstoneRetention);
                    foreach (var (tid, ts) in parsedTombs)
                    {
                        if (ts >= cutoff)
                            remoteTombstones[tid] = ts;
                    }
                }
            }

            var mergedTombstones = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            bool tombstonesDirty = false;
            var retentionCutoff = DateTimeOffset.UtcNow.Subtract(TombstoneRetention);

            if (mode == ModeRemoteMaster)
            {
                foreach (var (tid, ts) in remoteTombstones)
                    if (ts >= retentionCutoff) mergedTombstones[tid] = ts;
            }
            else if (mode != ModeMergeKeepBoth)
            {
                foreach (var (tid, ts) in remoteTombstones)
                    if (ts >= retentionCutoff) mergedTombstones[tid] = ts;

                foreach (var (tid, ts) in localTombstones)
                {
                    if (ts < retentionCutoff) continue;
                    if (!mergedTombstones.TryGetValue(tid, out var existing) || ts > existing)
                    {
                        mergedTombstones[tid] = ts;
                        tombstonesDirty = true;
                    }
                }
            }

            // ==================== 分支 A：以本地为主（单向镜像备份到远端） ====================
            if (mode == ModeLocalMaster)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var remoteExtras = remoteNoteFiles
                    .Where(kv => !allLocalNoteIds.Contains(kv.Key))
                    .Select(kv => (NoteId: kv.Key, FileName: kv.Value))
                    .ToList();

                if (remoteExtras.Count > 0)
                {
                    int done = 0;
                    Report($"以本地为主：正在清理远端已删除笔记 (0 / {remoteExtras.Count})...", 0, remoteExtras.Count, false);
                    await RunBoundedAsync(remoteExtras, MaxConcurrency, async item =>
                    {
                        await transport.DeleteFileAsync($"notes/{item.FileName}", ct).ConfigureAwait(false);
                        manifest.TryRemove(item.NoteId, out _);
                        lock (mergedTombstones)
                        {
                            mergedTombstones[item.NoteId] = nowUtc;
                            tombstonesDirty = true;
                        }
                        manifestDirty = true;
                        Interlocked.Increment(ref deletedRemoteNotes);
                        var cur = Interlocked.Increment(ref done);
                        Report($"以本地为主：正在清理远端已删除笔记 ({cur} / {remoteExtras.Count})...", cur, remoteExtras.Count, false);
                    }, ct).ConfigureAwait(false);
                }

                var toPush = new List<NoteItem>();
                foreach (var (noteId, localNote) in localNotes)
                {
                    // 本机仍存在的笔记从墓碑中移除（防止被误删）
                    if (mergedTombstones.Remove(noteId))
                        tombstonesDirty = true;

                    if (remoteNoteFiles.ContainsKey(noteId) &&
                        manifest.TryGetValue(noteId, out var entry) &&
                        entry.Matches(localNote))
                    {
                        skippedNotes++;
                        continue;
                    }
                    toPush.Add(localNote);
                }

                if (toPush.Count > 0)
                {
                    int done = 0;
                    Report($"以本地为主：正在镜像上传笔记 (0 / {toPush.Count})...", 0, toPush.Count, false);
                    await RunBoundedAsync(toPush, MaxConcurrency, async localNote =>
                    {
                        string localContent;
                        await repoLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            localContent = await repo.ReadNoteContentAsync(localNote, ct: ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            repoLock.Release();
                        }
                        var upPayload = new RemoteNotePayload { Note = localNote, Content = localContent };
                        var enc = WrapPayload(
                            JsonSerializer.SerializeToUtf8Bytes(upPayload),
                            settings.EnableE2EE,
                            e2eePassphrase,
                            sessionMasterKey);
                        var fname = $"{localNote.Id}.mke";
                        await transport.WriteFileAsync($"notes/{fname}", enc, ct).ConfigureAwait(false);
                        manifest[localNote.Id] = RemoteNoteManifestEntry.FromNote(localNote, fname);
                        manifestDirty = true;
                        Interlocked.Increment(ref upNotes);
                        var cur = Interlocked.Increment(ref done);
                        Report($"以本地为主：正在镜像上传笔记 ({cur} / {toPush.Count})...", cur, toPush.Count, false);
                    }, ct).ConfigureAwait(false);
                }
            }
            // ==================== 分支 B：以远端为主（从远端恢复覆盖本地） ====================
            else if (mode == ModeRemoteMaster)
            {
                var toPull = new List<(string NoteId, string FileName)>();
                foreach (var (noteId, fileName) in remoteNoteFiles)
                {
                    if (manifest.TryGetValue(noteId, out var entry) &&
                        mergedTombstones.TryGetValue(noteId, out var delAt) &&
                        delAt >= entry.UpdatedAt)
                        continue;

                    if (manifest.TryGetValue(noteId, out var cached) &&
                        localNotes.TryGetValue(noteId, out var localNote) &&
                        cached.Matches(localNote))
                    {
                        skippedNotes++;
                        continue;
                    }
                    toPull.Add((noteId, fileName));
                }

                if (toPull.Count > 0)
                {
                    int done = 0;
                    Report($"以远端为主：正在恢复拉取笔记 (0 / {toPull.Count})...", 0, toPull.Count, false);
                    await RunBoundedAsync(toPull, MaxConcurrency, async item =>
                    {
                        var raw = await transport.ReadFileAsync($"notes/{item.FileName}", ct).ConfigureAwait(false);
                        if (raw is not null)
                        {
                            var plain = UnwrapPayload(raw, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                            var remotePayload = JsonSerializer.Deserialize<RemoteNotePayload>(plain);
                            if (remotePayload is not null)
                            {
                                await repoLock.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    await repo.UpsertSyncedNoteAsync(remotePayload.Note, remotePayload.Content, ct).ConfigureAwait(false);
                                }
                                finally
                                {
                                    repoLock.Release();
                                }
                                Interlocked.Increment(ref downNotes);
                            }
                        }
                        var cur = Interlocked.Increment(ref done);
                        Report($"以远端为主：正在恢复拉取笔记 ({cur} / {toPull.Count})...", cur, toPull.Count, false);
                    }, ct).ConfigureAwait(false);
                }

                // 同步应用远端墓碑到本地（清理远端已彻底删除的笔记）
                var localIdsToPurge = mergedTombstones
                    .Where(kv => localNotes.ContainsKey(kv.Key) && !remoteNoteFiles.ContainsKey(kv.Key))
                    .Select(kv => kv.Key)
                    .ToList();
                if (localIdsToPurge.Count > 0)
                {
                    deletedLocalNotes += await repo.PermanentlyDeleteNotesBatchAsync(localIdsToPurge, ct).ConfigureAwait(false);
                }
                if (mergedTombstones.Count > 0)
                {
                    await repo.UpsertNoteTombstonesAsync(mergedTombstones, ct).ConfigureAwait(false);
                }
            }
            // ==================== 分支 C：双向同步（Bidirectional / MergeKeepBoth） ====================
            else
            {
                // 若开启同步删除（Bidirectional），先按墓碑清理远端与本地已彻底删除的笔记
                if (mode == ModeBidirectional && mergedTombstones.Count > 0)
                {
                    var remoteToDelete = new List<(string NoteId, string FileName)>();
                    var localToDelete = new List<string>();
                    var resurrectedTombIds = new List<string>();

                    foreach (var (tombId, deletedAt) in mergedTombstones)
                    {
                        bool aliveWithNewerEdit = false;

                        if (remoteNoteFiles.TryGetValue(tombId, out var rFile))
                        {
                            DateTimeOffset remoteUpdatedAt;
                            if (manifest.TryGetValue(tombId, out var mEntry))
                            {
                                remoteUpdatedAt = mEntry.UpdatedAt;
                            }
                            else
                            {
                                var raw = await transport.ReadFileAsync($"notes/{rFile}", ct).ConfigureAwait(false);
                                if (raw is not null)
                                {
                                    var plain = UnwrapPayload(raw, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                                    var rp = JsonSerializer.Deserialize<RemoteNotePayload>(plain);
                                    remoteUpdatedAt = rp?.Note.UpdatedAt ?? DateTimeOffset.MinValue;
                                }
                                else
                                {
                                    remoteUpdatedAt = DateTimeOffset.MinValue;
                                }
                            }

                            if (deletedAt >= remoteUpdatedAt)
                                remoteToDelete.Add((tombId, rFile));
                            else
                                aliveWithNewerEdit = true;
                        }

                        if (!aliveWithNewerEdit && localNotes.TryGetValue(tombId, out var lNote))
                        {
                            if (deletedAt >= lNote.UpdatedAt)
                                localToDelete.Add(tombId);
                            else
                                aliveWithNewerEdit = true;
                        }

                        if (aliveWithNewerEdit)
                        {
                            resurrectedTombIds.Add(tombId);
                            remoteToDelete.RemoveAll(x => x.NoteId == tombId);
                            localToDelete.Remove(tombId);
                        }
                    }

                    foreach (var rid in resurrectedTombIds)
                    {
                        mergedTombstones.Remove(rid);
                        tombstonesDirty = true;
                    }

                    if (remoteToDelete.Count > 0)
                    {
                        int done = 0;
                        Report($"正在清理远端已删除笔记 (0 / {remoteToDelete.Count})...", 0, remoteToDelete.Count, false);
                        await RunBoundedAsync(remoteToDelete, MaxConcurrency, async item =>
                        {
                            await transport.DeleteFileAsync($"notes/{item.FileName}", ct).ConfigureAwait(false);
                            manifest.TryRemove(item.NoteId, out _);
                            manifestDirty = true;
                            Interlocked.Increment(ref deletedRemoteNotes);
                            var cur = Interlocked.Increment(ref done);
                            Report($"正在清理远端已删除笔记 ({cur} / {remoteToDelete.Count})...", cur, remoteToDelete.Count, false);
                        }, ct).ConfigureAwait(false);

                        foreach (var (nid, _) in remoteToDelete)
                            remoteNoteFiles.Remove(nid);
                    }

                    if (localToDelete.Count > 0)
                    {
                        deletedLocalNotes += await repo.PermanentlyDeleteNotesBatchAsync(localToDelete, ct).ConfigureAwait(false);
                        foreach (var nid in localToDelete)
                            localNotes.Remove(nid);
                    }
                }

                // 分类规划：哪些远端笔记可以秒跳过、哪些直接覆盖上传、哪些需要拉取比对
                var toPullAndReconcile = new List<(string NoteId, string FileName)>();
                var toPushDirect = new List<NoteItem>();

                foreach (var (noteId, fileName) in remoteNoteFiles)
                {
                    if (manifest.TryGetValue(noteId, out var entry) && localNotes.TryGetValue(noteId, out var localNote))
                    {
                        if (entry.Matches(localNote))
                        {
                            skippedNotes++;
                            continue;
                        }

                        bool remoteUnchangedSinceSync = lastSync > DateTimeOffset.MinValue && entry.UpdatedAt <= lastSync;
                        if (remoteUnchangedSinceSync && localNote.UpdatedAt > entry.UpdatedAt)
                        {
                            toPushDirect.Add(localNote);
                            continue;
                        }
                    }

                    toPullAndReconcile.Add((noteId, fileName));
                }

                foreach (var (noteId, localNote) in localNotes)
                {
                    if (!remoteNoteFiles.ContainsKey(noteId))
                        toPushDirect.Add(localNote);
                }

                // 3a. 并发拉取并比对变更/新增/未入索引的远端笔记
                if (toPullAndReconcile.Count > 0)
                {
                    int done = 0;
                    Report($"正在比对并同步远端笔记 (0 / {toPullAndReconcile.Count})...", 0, toPullAndReconcile.Count, false);
                    await RunBoundedAsync(toPullAndReconcile, MaxConcurrency, async item =>
                    {
                        var (noteId, fileName) = item;
                        var raw = await transport.ReadFileAsync($"notes/{fileName}", ct).ConfigureAwait(false);
                        if (raw is not null)
                        {
                            var plain = UnwrapPayload(raw, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                            var remotePayload = JsonSerializer.Deserialize<RemoteNotePayload>(plain);
                            if (remotePayload is not null)
                            {
                                if (!localNotes.TryGetValue(noteId, out var localNote))
                                {
                                    await repoLock.WaitAsync(ct).ConfigureAwait(false);
                                    try
                                    {
                                        await repo.UpsertSyncedNoteAsync(remotePayload.Note, remotePayload.Content, ct).ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        repoLock.Release();
                                    }
                                    manifest[noteId] = RemoteNoteManifestEntry.FromNote(remotePayload.Note, fileName);
                                    manifestDirty = true;
                                    Interlocked.Increment(ref downNotes);
                                }
                                else
                                {
                                    var remoteEntry = RemoteNoteManifestEntry.FromNote(remotePayload.Note, fileName);
                                    var sameContent = string.Equals(localNote.ContentSha256, remotePayload.Note.ContentSha256, StringComparison.OrdinalIgnoreCase);
                                    var sameMeta = remoteEntry.Matches(localNote);

                                    if (sameContent && sameMeta)
                                    {
                                        manifest[noteId] = remoteEntry;
                                        manifestDirty = true;
                                        Interlocked.Increment(ref skippedNotes);
                                    }
                                    else
                                    {
                                        bool localChangedSinceSync = localNote.UpdatedAt > lastSync;
                                        bool remoteChangedSinceSync = remotePayload.Note.UpdatedAt > lastSync;
                                        if (!sameContent && localChangedSinceSync && remoteChangedSinceSync && lastSync > DateTimeOffset.MinValue)
                                        {
                                            await repoLock.WaitAsync(ct).ConfigureAwait(false);
                                            try
                                            {
                                                var localContent = await repo.ReadNoteContentAsync(localNote, ct: ct).ConfigureAwait(false);
                                                var conflictTitle = $"{localNote.Title} (同步冲突副本 {DateTime.Now:yyyyMMdd-HHmm})";
                                                await repo.CreateNoteAsync(
                                                    conflictTitle,
                                                    localNote.DocFormat,
                                                    localNote.CategoryId,
                                                    localContent,
                                                    localNote.BackgroundColor,
                                                    localNote.BackgroundStyle,
                                                    localNote.Tags,
                                                    ct: ct).ConfigureAwait(false);
                                            }
                                            finally
                                            {
                                                repoLock.Release();
                                            }
                                            Interlocked.Increment(ref conflicts);
                                        }

                                        if (remotePayload.Note.UpdatedAt > localNote.UpdatedAt)
                                        {
                                            await repoLock.WaitAsync(ct).ConfigureAwait(false);
                                            try
                                            {
                                                await repo.UpsertSyncedNoteAsync(remotePayload.Note, remotePayload.Content, ct).ConfigureAwait(false);
                                            }
                                            finally
                                            {
                                                repoLock.Release();
                                            }
                                            manifest[noteId] = remoteEntry;
                                            manifestDirty = true;
                                            Interlocked.Increment(ref downNotes);
                                        }
                                        else if (localNote.UpdatedAt > remotePayload.Note.UpdatedAt)
                                        {
                                            string localContent;
                                            await repoLock.WaitAsync(ct).ConfigureAwait(false);
                                            try
                                            {
                                                localContent = await repo.ReadNoteContentAsync(localNote, ct: ct).ConfigureAwait(false);
                                            }
                                            finally
                                            {
                                                repoLock.Release();
                                            }
                                            var upPayload = new RemoteNotePayload { Note = localNote, Content = localContent };
                                            var enc = WrapPayload(
                                                JsonSerializer.SerializeToUtf8Bytes(upPayload),
                                                settings.EnableE2EE,
                                                e2eePassphrase,
                                                sessionMasterKey);
                                            await transport.WriteFileAsync($"notes/{noteId}.mke", enc, ct).ConfigureAwait(false);
                                            manifest[noteId] = RemoteNoteManifestEntry.FromNote(localNote, $"{noteId}.mke");
                                            manifestDirty = true;
                                            Interlocked.Increment(ref upNotes);
                                        }
                                        else
                                        {
                                            manifest[noteId] = remoteEntry;
                                            manifestDirty = true;
                                        }
                                    }
                                }
                            }
                        }

                        var cur = Interlocked.Increment(ref done);
                        Report($"正在比对并同步远端笔记 ({cur} / {toPullAndReconcile.Count})...", cur, toPullAndReconcile.Count, false);
                    }, ct).ConfigureAwait(false);
                }

                // 3b. 并发上传本机新增或仅本机修改的笔记
                if (toPushDirect.Count > 0)
                {
                    int done = 0;
                    Report($"正在上传加密笔记 (0 / {toPushDirect.Count})...", 0, toPushDirect.Count, false);
                    await RunBoundedAsync(toPushDirect, MaxConcurrency, async localNote =>
                    {
                        string localContent;
                        await repoLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            localContent = await repo.ReadNoteContentAsync(localNote, ct: ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            repoLock.Release();
                        }

                        var upPayload = new RemoteNotePayload { Note = localNote, Content = localContent };
                        var enc = WrapPayload(
                            JsonSerializer.SerializeToUtf8Bytes(upPayload),
                            settings.EnableE2EE,
                            e2eePassphrase,
                            sessionMasterKey);
                        var fname = $"{localNote.Id}.mke";
                        await transport.WriteFileAsync($"notes/{fname}", enc, ct).ConfigureAwait(false);
                        manifest[localNote.Id] = RemoteNoteManifestEntry.FromNote(localNote, fname);
                        manifestDirty = true;

                        var cur = Interlocked.Increment(ref done);
                        Interlocked.Increment(ref upNotes);
                        Report($"正在上传加密笔记 ({cur} / {toPushDirect.Count})...", cur, toPushDirect.Count, false);
                    }, ct).ConfigureAwait(false);
                }
            }

            // 4. 回写加密墓碑清单与增量索引清单（仅在允许写远端的模式）
            if (allowWriteRemote && mode != ModeMergeKeepBoth && (tombstonesDirty || (mergedTombstones.Count > 0 && tombBytes is null)))
            {
                await repo.UpsertNoteTombstonesAsync(mergedTombstones, ct).ConfigureAwait(false);
                var tombJsonBytes = JsonSerializer.SerializeToUtf8Bytes(mergedTombstones);
                var wrappedTombs = WrapPayload(tombJsonBytes, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                await transport.WriteFileAsync("meta/tombstones.mke", wrappedTombs, ct).ConfigureAwait(false);
            }

            if (allowWriteRemote && manifestDirty)
            {
                Report("正在保存同步索引清单...", 99, 100, false);
                var manifestJsonBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new Dictionary<string, RemoteNoteManifestEntry>(manifest, StringComparer.Ordinal));
                var wrappedManifest = WrapPayload(manifestJsonBytes, settings.EnableE2EE, e2eePassphrase, sessionMasterKey);
                await transport.WriteFileAsync("meta/manifest.mke", wrappedManifest, ct).ConfigureAwait(false);
            }

            sw.Stop();
            var now = DateTimeOffset.UtcNow;
            Report("同步完成！", 100, 100, false);
            return new SyncReport(
                upNotes,
                downNotes,
                conflicts,
                upBlobs,
                downBlobs,
                syncedCats,
                settings.EnableE2EE,
                now,
                Math.Round(sw.Elapsed.TotalSeconds, 1),
                skippedNotes,
                deletedRemoteNotes,
                deletedLocalNotes,
                mode);
        }
        finally
        {
            if (sessionMasterKey is not null)
                CryptographicOperations.ZeroMemory(sessionMasterKey);
        }
    }

    private static async Task<byte[]?> LoadOrCreateSessionMasterKeyAsync(
        ISyncTransport transport,
        string passphrase,
        bool allowCreateRemote,
        CancellationToken ct)
    {
        byte[]? salt = null;
        int iterations = Pbkdf2Iterations;

        var rawKdf = await transport.ReadFileAsync("meta/kdf.json", ct).ConfigureAwait(false);
        if (rawKdf is not null)
        {
            var meta = JsonSerializer.Deserialize<SyncKdfMetadata>(rawKdf);
            if (meta is not null && !string.IsNullOrWhiteSpace(meta.SaltBase64))
            {
                salt = Convert.FromBase64String(meta.SaltBase64);
                iterations = Math.Clamp(meta.Iterations, 100_000, 1_000_000);
            }
        }

        if (salt is null || salt.Length < 16)
        {
            if (!allowCreateRemote) return null;
            salt = RandomNumberGenerator.GetBytes(16);
            var meta = new SyncKdfMetadata
            {
                Version = 2,
                Kdf = "PBKDF2-SHA512",
                Iterations = Pbkdf2Iterations,
                SaltBase64 = Convert.ToBase64String(salt)
            };
            await transport.WriteFileAsync("meta/kdf.json", JsonSerializer.SerializeToUtf8Bytes(meta), ct).ConfigureAwait(false);
        }

        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA512, 32);
    }

    private static async Task RunBoundedAsync<T>(
        IReadOnlyList<T> items,
        int maxConcurrency,
        Func<T, Task> worker,
        CancellationToken ct)
    {
        if (items.Count == 0) return;
        using var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = new List<Task>(items.Count);

        foreach (var item in items)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await worker(item).ConfigureAwait(false);
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static byte[] WrapPayload(
        ReadOnlySpan<byte> plainBytes,
        bool enableE2ee,
        string? passphrase,
        ReadOnlySpan<byte> sessionMasterKey = default)
    {
        if (!enableE2ee)
        {
            var outBytes = new byte[4 + plainBytes.Length];
            PlainMagic.CopyTo(outBytes);
            plainBytes.CopyTo(outBytes.AsSpan(4));
            return outBytes;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key;
        byte[] magic;

        if (!sessionMasterKey.IsEmpty)
        {
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionMasterKey.ToArray(), 32, salt, HkdfInfoV2);
            magic = E2eeV2Magic;
        }
        else
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new CryptographicException("E2EE 同步加密密码不能为空。");
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
            magic = E2eeV1Magic;
        }

        try
        {
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plainBytes, cipher, tag, magic);

            var result = new byte[48 + cipher.Length];
            magic.CopyTo(result);
            salt.CopyTo(result.AsSpan(4, 16));
            nonce.CopyTo(result.AsSpan(20, 12));
            tag.CopyTo(result.AsSpan(32, 16));
            cipher.CopyTo(result.AsSpan(48));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] UnwrapPayload(
        ReadOnlySpan<byte> blob,
        bool expectE2ee,
        string? passphrase,
        ReadOnlySpan<byte> sessionMasterKey = default)
    {
        if (blob.Length >= 4 && blob[..4].SequenceEqual(PlainMagic))
            return blob[4..].ToArray();

        if (blob.Length < 48)
            throw new CryptographicException("远端同步文件格式无效或已损坏。");

        var magic = blob[..4];
        bool isV2 = magic.SequenceEqual(E2eeV2Magic);
        bool isV1 = magic.SequenceEqual(E2eeV1Magic);

        if (!isV1 && !isV2)
            throw new CryptographicException("远端同步文件格式无效或已损坏。");

        if (string.IsNullOrEmpty(passphrase) && sessionMasterKey.IsEmpty)
            throw new CryptographicException("远端同步数据已采用端到端加密 (E2EE)，请先输入同步加密主密码。");

        var salt = blob[4..20];
        var nonce = blob[20..32];
        var tag = blob[32..48];
        var cipher = blob[48..];

        byte[] key;
        if (isV2)
        {
            if (sessionMasterKey.IsEmpty)
                throw new CryptographicException("缺少会话主密钥，无法解密 v2 端到端加密数据。");
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionMasterKey.ToArray(), 32, salt.ToArray(), HkdfInfoV2);
        }
        else
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new CryptographicException("远端同步数据已采用端到端加密 (E2EE)，请先输入同步加密主密码。");
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
        }

        try
        {
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, magic);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("端到端加密 (E2EE) 主密码错误，无法解密远端同步数据。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
