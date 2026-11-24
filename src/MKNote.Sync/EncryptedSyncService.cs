using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MKNote.Core.Interfaces;
using MKNote.Core.Models;

namespace MKNote.Sync;

public sealed record SyncReport(
    int UploadedNotes,
    int DownloadedNotes,
    int ConflictCopiesCreated,
    int UploadedBlobs,
    int DownloadedBlobs,
    int SyncedCategories,
    bool UsedE2EE,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// 端到端加密 (E2EE) 双向同步引擎。
/// 所有笔记正文、分类与媒体图片块在离开本机前均使用 AES-256-GCM + PBKDF2-SHA512 加密，
/// 支持群晖 NAS (WebDAV / SMB)、通用 WebDAV 与本地同步文件夹；冲突时自动保留副本防止数据丢失。
/// </summary>
public sealed class EncryptedSyncService
{
    private static readonly byte[] E2eeMagic = "MKSE"u8.ToArray(); // MKNote Sync E2EE v1
    private static readonly byte[] PlainMagic = "MKSP"u8.ToArray(); // Optional non-E2EE envelope (only if user disables E2EE)
    private const int Pbkdf2Iterations = 210_000;

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

    public async Task<SyncReport> SynchronizeAsync(
        IVaultRepository repo,
        SyncSettings settings,
        string plainPassword,
        string? e2eePassphrase,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (settings.EnableE2EE && string.IsNullOrWhiteSpace(e2eePassphrase))
            throw new InvalidOperationException("已开启端到端加密 (E2EE)，必须填写加密同步主密码。");

        using var transport = CreateTransport(settings, plainPassword);
        progress?.Report("正在连接同步目标...");
        await transport.EnsureDirectoryAsync("meta", ct);
        await transport.EnsureDirectoryAsync("notes", ct);
        await transport.EnsureDirectoryAsync("blobs", ct);

        int syncedCats = 0, upNotes = 0, downNotes = 0, conflicts = 0, upBlobs = 0, downBlobs = 0;
        var lastSync = settings.LastSyncTimeUtc ?? DateTimeOffset.MinValue;

        // 1. Sync Categories (LWW merge)
        progress?.Report("正在同步笔记本分类...");
        var localCats = (await repo.GetAllCategoriesForSyncAsync(ct)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var remoteCatsBytes = await transport.ReadFileAsync("meta/categories.mke", ct);
        var mergedCats = new Dictionary<string, CategoryItem>(localCats, StringComparer.Ordinal);

        if (remoteCatsBytes is not null)
        {
            var remoteJson = UnwrapPayload(remoteCatsBytes, settings.EnableE2EE, e2eePassphrase);
            var remoteCats = JsonSerializer.Deserialize<List<CategoryItem>>(remoteJson) ?? [];
            foreach (var rc in remoteCats)
            {
                if (!mergedCats.TryGetValue(rc.Id, out var lc) || rc.UpdatedAt > lc.UpdatedAt)
                {
                    mergedCats[rc.Id] = rc;
                    await repo.UpsertSyncedCategoryAsync(rc, ct);
                    syncedCats++;
                }
            }
        }
        var wrappedCats = WrapPayload(JsonSerializer.SerializeToUtf8Bytes(mergedCats.Values.ToList()), settings.EnableE2EE, e2eePassphrase);
        await transport.WriteFileAsync("meta/categories.mke", wrappedCats, ct);

        // 2. Sync Encrypted Media Blobs (deduplicated by SHA-256)
        progress?.Report("正在同步加密图片与附件...");
        var localBlobs = await repo.ExportAllMediaBlobsAsync(ct);
        var localBlobSet = localBlobs.Select(b => b.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteBlobFiles = (await transport.ListFilesAsync("blobs", ct))
            .Where(f => f.EndsWith(".mkb", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (sha, mime, data) in localBlobs)
        {
            var fname = $"{sha}.mkb";
            if (!remoteBlobFiles.Contains(fname))
            {
                var payload = new RemoteBlobPayload { Sha256 = sha, MimeType = mime, Data = data };
                var enc = WrapPayload(JsonSerializer.SerializeToUtf8Bytes(payload), settings.EnableE2EE, e2eePassphrase);
                await transport.WriteFileAsync($"blobs/{fname}", enc, ct);
                upBlobs++;
            }
        }

        foreach (var fname in remoteBlobFiles)
        {
            var sha = Path.GetFileNameWithoutExtension(fname);
            if (!localBlobSet.Contains(sha))
            {
                var raw = await transport.ReadFileAsync($"blobs/{fname}", ct);
                if (raw is not null)
                {
                    var plain = UnwrapPayload(raw, settings.EnableE2EE, e2eePassphrase);
                    var payload = JsonSerializer.Deserialize<RemoteBlobPayload>(plain);
                    if (payload is not null)
                    {
                        await repo.ImportMediaBlobAsync(payload.Sha256, payload.MimeType, payload.Data, ct);
                        downBlobs++;
                    }
                }
            }
        }

        // 3. 同步笔记正文（双向合并 + 冲突自动保留副本）
        progress?.Report("正在同步加密笔记正文...");
        var localNotes = (await repo.GetAllNotesForSyncAsync(ct))
            .Where(n => !n.IsEncrypted) // Per-note password-locked notes stay local unless unlocked
            .ToDictionary(n => n.Id, StringComparer.Ordinal);
        var remoteNoteFiles = (await transport.ListFilesAsync("notes", ct))
            .Where(f => f.EndsWith(".mke", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.Ordinal);

        // 3a. Pull remote notes & resolve differences
        foreach (var (noteId, fileName) in remoteNoteFiles)
        {
            var raw = await transport.ReadFileAsync($"notes/{fileName}", ct);
            if (raw is null) continue;
            var plain = UnwrapPayload(raw, settings.EnableE2EE, e2eePassphrase);
            var remotePayload = JsonSerializer.Deserialize<RemoteNotePayload>(plain);
            if (remotePayload is null) continue;

            if (!localNotes.TryGetValue(noteId, out var localNote))
            {
                await repo.UpsertSyncedNoteAsync(remotePayload.Note, remotePayload.Content, ct);
                downNotes++;
                continue;
            }

            var sameContent = string.Equals(localNote.ContentSha256, remotePayload.Note.ContentSha256, StringComparison.OrdinalIgnoreCase);
            var sameMeta = localNote.Title == remotePayload.Note.Title &&
                           localNote.CategoryId == remotePayload.Note.CategoryId &&
                           localNote.IsDeleted == remotePayload.Note.IsDeleted &&
                           localNote.IsPinned == remotePayload.Note.IsPinned &&
                           localNote.IsStarred == remotePayload.Note.IsStarred &&
                           localNote.BackgroundColor == remotePayload.Note.BackgroundColor;

            if (sameContent && sameMeta) continue;

            // Detect genuine two-sided conflict: both modified after last sync with different content
            bool localChangedSinceSync = localNote.UpdatedAt > lastSync;
            bool remoteChangedSinceSync = remotePayload.Note.UpdatedAt > lastSync;
            if (!sameContent && localChangedSinceSync && remoteChangedSinceSync && lastSync > DateTimeOffset.MinValue)
            {
                var localContent = await repo.ReadNoteContentAsync(localNote, ct: ct);
                var conflictTitle = $"{localNote.Title} (同步冲突副本 {DateTime.Now:yyyyMMdd-HHmm})";
                await repo.CreateNoteAsync(
                    conflictTitle,
                    localNote.DocFormat,
                    localNote.CategoryId,
                    localContent,
                    localNote.BackgroundColor,
                    localNote.BackgroundStyle,
                    localNote.Tags,
                    ct: ct);
                conflicts++;
            }

            if (remotePayload.Note.UpdatedAt > localNote.UpdatedAt)
            {
                await repo.UpsertSyncedNoteAsync(remotePayload.Note, remotePayload.Content, ct);
                downNotes++;
            }
            else if (localNote.UpdatedAt > remotePayload.Note.UpdatedAt)
            {
                var localContent = await repo.ReadNoteContentAsync(localNote, ct: ct);
                var upPayload = new RemoteNotePayload { Note = localNote, Content = localContent };
                var enc = WrapPayload(JsonSerializer.SerializeToUtf8Bytes(upPayload), settings.EnableE2EE, e2eePassphrase);
                await transport.WriteFileAsync($"notes/{noteId}.mke", enc, ct);
                upNotes++;
            }
        }

        // 3b. Push local-only notes
        foreach (var (noteId, localNote) in localNotes)
        {
            if (remoteNoteFiles.ContainsKey(noteId)) continue;
            var localContent = await repo.ReadNoteContentAsync(localNote, ct: ct);
            var upPayload = new RemoteNotePayload { Note = localNote, Content = localContent };
            var enc = WrapPayload(JsonSerializer.SerializeToUtf8Bytes(upPayload), settings.EnableE2EE, e2eePassphrase);
            await transport.WriteFileAsync($"notes/{noteId}.mke", enc, ct);
            upNotes++;
        }

        var now = DateTimeOffset.UtcNow;
        progress?.Report("同步完成！");
        return new SyncReport(upNotes, downNotes, conflicts, upBlobs, downBlobs, syncedCats, settings.EnableE2EE, now);
    }

    public static byte[] WrapPayload(ReadOnlySpan<byte> plainBytes, bool enableE2ee, string? passphrase)
    {
        if (!enableE2ee)
        {
            var outBytes = new byte[4 + plainBytes.Length];
            PlainMagic.CopyTo(outBytes);
            plainBytes.CopyTo(outBytes.AsSpan(4));
            return outBytes;
        }

        if (string.IsNullOrEmpty(passphrase))
            throw new CryptographicException("E2EE 同步加密密码不能为空。");

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
        try
        {
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plainBytes, cipher, tag, E2eeMagic);

            // [4B "MKSE"][16B salt][12B nonce][16B tag][ciphertext]
            var result = new byte[48 + cipher.Length];
            E2eeMagic.CopyTo(result);
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

    public static byte[] UnwrapPayload(ReadOnlySpan<byte> blob, bool expectE2ee, string? passphrase)
    {
        if (blob.Length >= 4 && blob[..4].SequenceEqual(PlainMagic))
            return blob[4..].ToArray();

        if (blob.Length < 48 || !blob[..4].SequenceEqual(E2eeMagic))
            throw new CryptographicException("远端同步文件格式无效或已损坏。");

        if (string.IsNullOrEmpty(passphrase))
            throw new CryptographicException("远端同步数据已采用端到端加密 (E2EE)，请先输入同步加密主密码。");

        var salt = blob[4..20];
        var nonce = blob[20..32];
        var tag = blob[32..48];
        var cipher = blob[48..];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
        try
        {
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, E2eeMagic);
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
