using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MKNote.Security;

/// <summary>
/// 本地知识库主密钥文件的保护模式。
/// </summary>
public enum VaultKeyProtectionMode
{
    /// <summary>Windows DPAPI 当前用户绑定保护（标准安装默认，文件头 MKVK）。</summary>
    Dpapi,
    /// <summary>免口令便携信封保护（支持 U 盘跨机直开，防磁盘明文扫描，文件头 MKVP）。</summary>
    PortableEnvelope,
    /// <summary>强口令派生便携信封保护（PBKDF2-SHA512 310,000 次 + AES-256-GCM，文件头 MKP2）。</summary>
    PortablePassphrase
}

/// <summary>
/// 当便携库已启用 MKP2 强口令保护但启动时未提供主口令时抛出。
/// </summary>
public sealed class PortableVaultPassphraseRequiredException : CryptographicException
{
    public PortableVaultPassphraseRequiredException()
        : base("该便携知识库已启用强口令保护 (MKP2)，请输入便携库主口令以解锁。")
    {
    }
}

/// <summary>
/// 本地知识库 256 位主密钥（VMK）管理器。
/// 负责通过 Windows DPAPI（MKVK）、免口令便携信封（MKVP）或强口令派生信封（MKP2）保护密钥文件，
/// 并为 SQLCipher 整库及每个二进制正文/图片 BLOB 提供 AES-256-GCM 独立加密，确保磁盘零明文残留。
/// </summary>
public sealed class VaultKeyManager
{
    private static readonly byte[] DpapiEntropy = "MKNoteNext.LocalVault.DPAPI.v1::9f4e2a7c"u8.ToArray();
    // 便携模式既有免口令信封常量：用于 100% 向下兼容已有 MKVP 便携库，严禁改动以免破坏老数据；
    // 高敏感便携库可无损原地升级为 MKP2（用户主口令 PBKDF2-SHA512 派生）。
    private static readonly byte[] PortableEnvelopeKey = SHA256.HashData("MKNoteNext.PortableVault.KeyEnvelope.v1::7c3a9d5e"u8.ToArray());
    private static readonly byte[] BlobMagic = "MKB1"u8.ToArray();
    private static readonly byte[] KeyFileDpapiMagic = "MKVK"u8.ToArray();
    private static readonly byte[] KeyFilePortableMagic = "MKVP"u8.ToArray();
    private static readonly byte[] KeyFilePortablePassphraseMagic = "MKP2"u8.ToArray();
    private const int PortableKdfIterations = 310_000;
    private const int SaltSizeBytes = 16;

    private readonly byte[] _masterKey;

    private VaultKeyManager(byte[] masterKey, VaultKeyProtectionMode protectionMode)
    {
        _masterKey = masterKey;
        ProtectionMode = protectionMode;
    }

    public VaultKeyProtectionMode ProtectionMode { get; private set; }

    public string SqlCipherPasswordHex => "x'" + Convert.ToHexStringLower(_masterKey) + "'";

    /// <summary>
    /// 检测指定知识库目录的 vault.key 是否已启用 MKP2 强口令保护（需在初始化前提示用户输入主口令）。
    /// </summary>
    public static bool RequiresPortablePassphrase(string vaultRootPath)
    {
        var keyFile = Path.Combine(vaultRootPath, "vault.key");
        if (!File.Exists(keyFile)) return false;
        try
        {
            using var fs = File.OpenRead(keyFile);
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) < 4) return false;
            return header.SequenceEqual(KeyFilePortablePassphraseMagic);
        }
        catch
        {
            return false;
        }
    }

    public static VaultKeyManager LoadOrCreate(string vaultRootPath, bool isPortableMode, string? portablePassphrase = null)
    {
        Directory.CreateDirectory(vaultRootPath);
        var keyFile = Path.Combine(vaultRootPath, "vault.key");

        if (File.Exists(keyFile))
        {
            var raw = File.ReadAllBytes(keyFile);
            if (raw.Length > 4 && raw.AsSpan(0, 4).SequenceEqual(KeyFileDpapiMagic))
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DPAPI 保护的密钥仅可在 Windows 上解锁。");
                var unwrapped = UnprotectDpapi(raw[4..]);
                return new VaultKeyManager(unwrapped, VaultKeyProtectionMode.Dpapi);
            }
            if (raw.Length > 4 && raw.AsSpan(0, 4).SequenceEqual(KeyFilePortableMagic))
            {
                var unwrapped = DecryptWithKey(raw[4..], PortableEnvelopeKey);
                return new VaultKeyManager(unwrapped, VaultKeyProtectionMode.PortableEnvelope);
            }
            if (raw.Length > 4 + SaltSizeBytes && raw.AsSpan(0, 4).SequenceEqual(KeyFilePortablePassphraseMagic))
            {
                if (string.IsNullOrEmpty(portablePassphrase))
                    throw new PortableVaultPassphraseRequiredException();

                var salt = raw.AsSpan(4, SaltSizeBytes);
                var derived = Rfc2898DeriveBytes.Pbkdf2(portablePassphrase, salt, PortableKdfIterations, HashAlgorithmName.SHA512, 32);
                try
                {
                    var unwrapped = DecryptWithKey(raw.AsSpan(4 + SaltSizeBytes), derived);
                    return new VaultKeyManager(unwrapped, VaultKeyProtectionMode.PortablePassphrase);
                }
                catch (CryptographicException)
                {
                    throw new CryptographicException("便携库主口令错误，无法解锁本地加密库。");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(derived);
                }
            }
            throw new CryptographicException("vault.key 文件格式无效或已损坏。");
        }

        // 首次初始化：生成随机 256 位知识库主密钥（VMK）
        var vmk = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped;
        VaultKeyProtectionMode mode;
        if (!isPortableMode && OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectDpapi(vmk);
            wrapped = [.. KeyFileDpapiMagic, .. protectedBytes];
            mode = VaultKeyProtectionMode.Dpapi;
        }
        else if (!string.IsNullOrEmpty(portablePassphrase))
        {
            wrapped = BuildPortablePassphraseKeyFile(vmk, portablePassphrase);
            mode = VaultKeyProtectionMode.PortablePassphrase;
        }
        else
        {
            var encBytes = EncryptWithKey(vmk, PortableEnvelopeKey);
            wrapped = [.. KeyFilePortableMagic, .. encBytes];
            mode = VaultKeyProtectionMode.PortableEnvelope;
        }

        WriteKeyFileAtomic(keyFile, wrapped);
        return new VaultKeyManager(vmk, mode);
    }

    /// <summary>
    /// 无损原地重封装：将当前 256 位主密钥以用户强口令（MKP2 · PBKDF2-SHA512 310,000 次 + AES-256-GCM）重新封装写入 vault.key，
    /// 不修改 vault.db 与 revisions.db 中的任何数据，确保零数据丢失。
    /// </summary>
    public void RewrapAsPortablePassphrase(string vaultRootPath, string newPassphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassphrase);
        var keyFile = Path.Combine(vaultRootPath, "vault.key");
        var wrapped = BuildPortablePassphraseKeyFile(_masterKey, newPassphrase);
        WriteKeyFileAtomic(keyFile, wrapped);
        ProtectionMode = VaultKeyProtectionMode.PortablePassphrase;
    }

    /// <summary>
    /// 无损原地重封装：将当前 256 位主密钥恢复为免口令便携信封（MKVP），不改动任何笔记数据。
    /// </summary>
    public void RewrapAsLegacyPortable(string vaultRootPath)
    {
        var keyFile = Path.Combine(vaultRootPath, "vault.key");
        var encBytes = EncryptWithKey(_masterKey, PortableEnvelopeKey);
        byte[] wrapped = [.. KeyFilePortableMagic, .. encBytes];
        WriteKeyFileAtomic(keyFile, wrapped);
        ProtectionMode = VaultKeyProtectionMode.PortableEnvelope;
    }

    /// <summary>
    /// 使用主密钥通过 AES-256-GCM 加密任意字节数据（笔记正文、截图 PNG、手动插入图片或附件），随后写入 SQLCipher 加密数据库。
    /// </summary>
    public byte[] EncryptBlob(ReadOnlySpan<byte> plainBytes) => EncryptWithKey(plainBytes, _masterKey);

    public byte[] DecryptBlob(ReadOnlySpan<byte> cipherBytes) => DecryptWithKey(cipherBytes, _masterKey);

    public byte[] EncryptString(string text) => EncryptBlob(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public string DecryptString(ReadOnlySpan<byte> cipherBytes)
    {
        if (cipherBytes.IsEmpty) return string.Empty;
        var plain = DecryptBlob(cipherBytes);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] BuildPortablePassphraseKeyFile(ReadOnlySpan<byte> vmk, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var derived = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, PortableKdfIterations, HashAlgorithmName.SHA512, 32);
        try
        {
            var encVmk = EncryptWithKey(vmk, derived);
            // 布局：[4B "MKP2"][16B salt][MKB1 AES-256-GCM 密文主密钥]
            var result = new byte[4 + SaltSizeBytes + encVmk.Length];
            KeyFilePortablePassphraseMagic.CopyTo(result);
            salt.CopyTo(result.AsSpan(4, SaltSizeBytes));
            encVmk.CopyTo(result.AsSpan(4 + SaltSizeBytes));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    private static void WriteKeyFileAtomic(string keyFile, byte[] data)
    {
        var tmp = keyFile + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, keyFile, overwrite: true);
    }

    private static byte[] EncryptWithKey(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, ciphertext, tag, BlobMagic);

        // 布局：[4B "MKB1"][12B nonce][16B tag][密文]
        var result = new byte[4 + 12 + 16 + ciphertext.Length];
        var span = result.AsSpan();
        BlobMagic.CopyTo(span);
        nonce.CopyTo(span[4..16]);
        tag.CopyTo(span[16..32]);
        ciphertext.CopyTo(span[32..]);
        return result;
    }

    private static byte[] DecryptWithKey(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> key)
    {
        if (blob.Length < 32 || !blob[..4].SequenceEqual(BlobMagic))
            throw new CryptographicException("加密数据块格式无效。");

        var nonce = blob[4..16];
        var tag = blob[16..32];
        var ciphertext = blob[32..];
        var plain = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plain, BlobMagic);
        return plain;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectDpapi(byte[] data) =>
        ProtectedData.Protect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectDpapi(byte[] data) =>
        ProtectedData.Unprotect(data, DpapiEntropy, DataProtectionScope.CurrentUser);
}
