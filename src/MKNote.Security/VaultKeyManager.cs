using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MKNote.Security;

/// <summary>
/// 本地知识库 256 位主密钥（VMK）管理器。
/// 负责通过 Windows DPAPI（或便携模式密钥信封）保护密钥文件，并为 SQLCipher 整库及每个二进制正文/图片 BLOB 提供 AES-256-GCM 独立加密，确保磁盘零明文残留。
/// </summary>
public sealed class VaultKeyManager
{
    private static readonly byte[] DpapiEntropy = "MKNoteNext.LocalVault.DPAPI.v1::9f4e2a7c"u8.ToArray();
    private static readonly byte[] PortableEnvelopeKey = SHA256.HashData("MKNoteNext.PortableVault.KeyEnvelope.v1::7c3a9d5e"u8.ToArray());
    private static readonly byte[] BlobMagic = "MKB1"u8.ToArray();
    private static readonly byte[] KeyFileDpapiMagic = "MKVK"u8.ToArray();
    private static readonly byte[] KeyFilePortableMagic = "MKVP"u8.ToArray();

    private readonly byte[] _masterKey;

    private VaultKeyManager(byte[] masterKey)
    {
        _masterKey = masterKey;
    }

    public string SqlCipherPasswordHex => "x'" + Convert.ToHexStringLower(_masterKey) + "'";

    public static VaultKeyManager LoadOrCreate(string vaultRootPath, bool isPortableMode)
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
                return new VaultKeyManager(unwrapped);
            }
            if (raw.Length > 4 && raw.AsSpan(0, 4).SequenceEqual(KeyFilePortableMagic))
            {
                var unwrapped = DecryptWithKey(raw[4..], PortableEnvelopeKey);
                return new VaultKeyManager(unwrapped);
            }
            throw new CryptographicException("vault.key 文件格式无效或已损坏。");
        }

        // First-time initialization: generate random 256-bit Vault Master Key
        var vmk = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped;
        if (!isPortableMode && OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectDpapi(vmk);
            wrapped = [.. KeyFileDpapiMagic, .. protectedBytes];
        }
        else
        {
            var encBytes = EncryptWithKey(vmk, PortableEnvelopeKey);
            wrapped = [.. KeyFilePortableMagic, .. encBytes];
        }

        File.WriteAllBytes(keyFile, wrapped);
        return new VaultKeyManager(vmk);
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

    private static byte[] EncryptWithKey(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, ciphertext, tag, BlobMagic);

        // Format: [4B "MKB1"][12B nonce][16B tag][ciphertext]
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
