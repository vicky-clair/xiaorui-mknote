using MKNote.Core.Models;

namespace MKNote.Core.Interfaces;

/// <summary>笔记列表组合筛选条件。</summary>
public sealed record NoteQueryFilter(
    string? CategoryId = null,
    bool IncludeChildCategories = true,
    string? TagName = null,
    bool OnlyStarred = false,
    bool OnlyTrash = false,
    DocumentFormat? FormatFilter = null,
    string? SearchQuery = null);

/// <summary>
/// 本地加密知识库仓储统一接口：封装笔记本分类、笔记正文、事务级批量删除与恢复、
/// 标签双链与知识图谱、加密媒体池、防崩溃恢复日志、E2EE 同步及历史版本快照。
/// </summary>
public interface IVaultRepository
{
    string VaultRootPath { get; }
    bool IsPortableMode { get; }

    Task InitializeAsync(CancellationToken ct = default);

    // Categories
    Task<IReadOnlyList<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default);
    Task<CategoryItem> CreateCategoryAsync(string name, string? parentId = null, CancellationToken ct = default);
    Task RenameCategoryAsync(string categoryId, string newName, CancellationToken ct = default);
    Task DeleteCategoryAsync(string categoryId, CancellationToken ct = default);

    // Notes
    Task<IReadOnlyList<NoteItem>> QueryNotesAsync(NoteQueryFilter filter, CancellationToken ct = default);
    Task<NoteItem?> GetNoteByIdAsync(string noteId, CancellationToken ct = default);
    Task<NoteItem?> GetNoteByTitleAsync(string title, CancellationToken ct = default);
    Task<NoteItem> CreateNoteAsync(
        string title,
        DocumentFormat format,
        string? categoryId = null,
        string initialContent = "",
        string? backgroundColor = null,
        BackgroundStyle backgroundStyle = BackgroundStyle.Solid,
        IEnumerable<string>? tags = null,
        bool isSticky = false,
        CancellationToken ct = default);

    Task<NoteItem> CopyNoteToCategoryAsync(string noteId, string? targetCategoryId, CancellationToken ct = default);
    Task MoveNoteToCategoryAsync(string noteId, string? targetCategoryId, CancellationToken ct = default);
    Task UpdateNoteTagsAsync(string noteId, IEnumerable<string> tags, CancellationToken ct = default);

    Task<string> ReadNoteContentAsync(NoteItem note, string? unlockPassword = null, CancellationToken ct = default);
    Task SaveNoteContentAsync(NoteItem note, string content, string? unlockPassword = null, bool createRevision = false, string revisionReason = "auto_save", CancellationToken ct = default);
    Task UpdateNoteMetadataAsync(NoteItem note, CancellationToken ct = default);
    Task<int> RenameNoteWithLinkUpdateAsync(string noteId, string newTitle, CancellationToken ct = default);
    Task SetNoteEncryptionAsync(string noteId, string plainContent, string password, string? hint, CancellationToken ct = default);
    Task RemoveNoteEncryptionAsync(string noteId, string currentPassword, CancellationToken ct = default);
    Task SoftDeleteNoteAsync(string noteId, CancellationToken ct = default);
    Task<int> SoftDeleteNotesBatchAsync(IEnumerable<string> noteIds, CancellationToken ct = default);
    Task RestoreNoteAsync(string noteId, CancellationToken ct = default);
    Task<int> RestoreNotesBatchAsync(IEnumerable<string> noteIds, CancellationToken ct = default);
    Task PermanentlyDeleteNoteAsync(string noteId, CancellationToken ct = default);
    Task<int> PermanentlyDeleteNotesBatchAsync(IEnumerable<string> noteIds, CancellationToken ct = default);
    Task EmptyTrashAsync(CancellationToken ct = default);

    // Tags, Links, Graph, Search
    Task<IReadOnlyList<TagItem>> GetTagsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NoteLinkItem>> GetBacklinksAsync(string noteId, string noteTitle, CancellationToken ct = default);
    Task<IReadOnlyList<NoteLinkItem>> GetOutgoingLinksAsync(string noteId, CancellationToken ct = default);
    Task<GraphData> BuildGraphAsync(string? centerNoteId = null, int depth = 2, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHit>> FullTextSearchAsync(string query, int limit = 60, CancellationToken ct = default);

    // Attachments & Encrypted Media Blobs (stored inside encrypted DB, zero plaintext on disk)
    Task<IReadOnlyList<AttachmentItem>> GetAttachmentsAsync(string noteId, CancellationToken ct = default);
    Task<AttachmentItem> SaveAttachmentAsync(string noteId, string fileName, string mimeType, byte[] data, bool isInline, CancellationToken ct = default);
    Task<string> SaveGlobalMediaBlobAsync(string fileName, string mimeType, byte[] data, CancellationToken ct = default);
    Task<(byte[] Data, string MimeType, string FileName)?> ReadAttachmentBytesAsync(string sha256OrAttachmentId, CancellationToken ct = default);
    Task DeleteAttachmentAsync(string attachmentId, CancellationToken ct = default);
    string ResolveAttachmentAbsolutePath(string sha256OrRelPath);
    bool IsDatabaseEncrypted { get; }

    // 加密防崩溃预写恢复日志（未提交前以 AES-256-GCM 暂存）
    Task SaveCrashRecoveryEntryAsync(string noteId, string title, DocumentFormat format, string content, CancellationToken ct = default);
    Task ClearCrashRecoveryEntryAsync(string noteId, CancellationToken ct = default);
    Task<IReadOnlyList<CrashRecoveryRecord>> RecoverPendingDirtyNotesAsync(CancellationToken ct = default);

    // E2EE Sync support & Secret protection
    Task<IReadOnlyList<NoteItem>> GetAllNotesForSyncAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CategoryItem>> GetAllCategoriesForSyncAsync(CancellationToken ct = default);
    Task UpsertSyncedCategoryAsync(CategoryItem category, CancellationToken ct = default);
    Task UpsertSyncedNoteAsync(NoteItem note, string content, CancellationToken ct = default);
    Task<IReadOnlyList<(string Sha256, string MimeType, byte[] Data)>> ExportAllMediaBlobsAsync(CancellationToken ct = default);
    Task ImportMediaBlobAsync(string sha256, string mimeType, byte[] data, CancellationToken ct = default);
    string ProtectSecret(string plainText);
    string UnprotectSecret(string protectedBase64);

    // Revisions & Templates & Settings
    Task<IReadOnlyList<NoteRevisionItem>> GetRevisionsAsync(string noteId, CancellationToken ct = default);
    Task<string> ReadRevisionContentAsync(string revisionId, CancellationToken ct = default);
    Task<IReadOnlyList<NoteTemplateItem>> GetTemplatesAsync(CancellationToken ct = default);
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task<string> CreateLocalBackupAsync(string? targetDir = null, CancellationToken ct = default);
}
