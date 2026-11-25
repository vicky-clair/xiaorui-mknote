namespace MKNote.Storage;

/// <summary>
/// SQLCipher 加密数据库与本地历史版本库的表结构 DDL 定义。
/// </summary>
internal static class SchemaSql
{
    public const string MainSchema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS categories (
            id          TEXT PRIMARY KEY,
            parent_id   TEXT NULL REFERENCES categories(id) ON DELETE SET NULL,
            name        TEXT NOT NULL,
            icon        TEXT NULL,
            color       TEXT NULL,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            is_deleted  INTEGER NOT NULL DEFAULT 0,
            created_at  INTEGER NOT NULL,
            updated_at  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS notes (
            id                TEXT PRIMARY KEY,
            category_id       TEXT NULL REFERENCES categories(id) ON DELETE SET NULL,
            title             TEXT NOT NULL DEFAULT '',
            title_pinyin      TEXT NOT NULL DEFAULT '',
            doc_format        TEXT NOT NULL DEFAULT 'rich_html',
            file_rel_path     TEXT NOT NULL DEFAULT '',
            content_blob      BLOB NOT NULL DEFAULT X'',
            content_summary   TEXT NOT NULL DEFAULT '',
            content_size      INTEGER NOT NULL DEFAULT 0,
            word_count        INTEGER NOT NULL DEFAULT 0,
            content_sha256    TEXT NOT NULL DEFAULT '',
            background_color  TEXT NULL,
            background_style  TEXT NOT NULL DEFAULT 'solid',
            frontmatter_json  TEXT NULL,
            source_url        TEXT NULL,
            is_pinned         INTEGER NOT NULL DEFAULT 0,
            is_starred        INTEGER NOT NULL DEFAULT 0,
            is_encrypted      INTEGER NOT NULL DEFAULT 0,
            password_hint     TEXT NULL,
            crypto_meta_json  TEXT NULL,
            is_sticky         INTEGER NOT NULL DEFAULT 0,
            is_deleted        INTEGER NOT NULL DEFAULT 0,
            deleted_at        INTEGER NULL,
            created_at        INTEGER NOT NULL,
            updated_at        INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tags (
            id    TEXT PRIMARY KEY,
            name  TEXT NOT NULL UNIQUE,
            color TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS note_tags (
            note_id TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            tag_id  TEXT NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
            PRIMARY KEY (note_id, tag_id)
        );

        -- Deduplicated AES-256-GCM encrypted binary blobs for ALL screenshots, manual images, and attachments
        CREATE TABLE IF NOT EXISTS media_blobs (
            sha256      TEXT PRIMARY KEY,
            mime_type   TEXT NOT NULL,
            file_size   INTEGER NOT NULL,
            cipher_blob BLOB NOT NULL,
            created_at  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS attachments (
            id               TEXT PRIMARY KEY,
            note_id          TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            file_name        TEXT NOT NULL,
            mime_type        TEXT NOT NULL,
            file_size        INTEGER NOT NULL,
            sha256           TEXT NOT NULL,
            storage_rel_path TEXT NOT NULL DEFAULT '',
            is_inline        INTEGER NOT NULL DEFAULT 0,
            created_at       INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS note_links (
            id              TEXT PRIMARY KEY,
            source_note_id  TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            target_note_id  TEXT NULL REFERENCES notes(id) ON DELETE SET NULL,
            target_title    TEXT NOT NULL,
            target_heading  TEXT NULL,
            alias           TEXT NULL,
            link_type       TEXT NOT NULL DEFAULT 'wikilink',
            context_snippet TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- Global encrypted media references (e.g. global background image) protected from attachment GC
        CREATE TABLE IF NOT EXISTS global_assets (
            key        TEXT PRIMARY KEY,
            sha256     TEXT NOT NULL,
            file_name  TEXT NOT NULL,
            mime_type  TEXT NOT NULL,
            updated_at INTEGER NOT NULL
        );

        -- 同步删除墓碑表：仅记录彻底删除的笔记 ID 与时间戳（不含任何正文或标题），用于双向同步防止已删笔记复活
        CREATE TABLE IF NOT EXISTS sync_tombstones (
            entity_type TEXT NOT NULL,
            entity_id   TEXT NOT NULL,
            deleted_at  INTEGER NOT NULL,
            PRIMARY KEY (entity_type, entity_id)
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
            note_id UNINDEXED,
            title,
            tags_text,
            plain_content,
            tokenize = 'trigram'
        );

        CREATE INDEX IF NOT EXISTS idx_notes_cat_upd ON notes(category_id, is_deleted, is_pinned DESC, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_notes_format  ON notes(doc_format, is_deleted);
        CREATE INDEX IF NOT EXISTS idx_links_source  ON note_links(source_note_id);
        CREATE INDEX IF NOT EXISTS idx_links_target  ON note_links(target_note_id);
        CREATE INDEX IF NOT EXISTS idx_attach_note   ON attachments(note_id);
        CREATE INDEX IF NOT EXISTS idx_attach_sha    ON attachments(sha256);
        """;

    public const string RevisionsSchema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS note_revisions (
            id             TEXT PRIMARY KEY,
            note_id        TEXT NOT NULL,
            doc_format     TEXT NOT NULL,
            title          TEXT NOT NULL,
            content_blob   BLOB NOT NULL,
            content_sha256 TEXT NOT NULL,
            reason         TEXT NOT NULL,
            created_at     INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_rev_note_time ON note_revisions(note_id, created_at DESC);
        """;
}
