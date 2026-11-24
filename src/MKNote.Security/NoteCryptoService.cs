using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MKNote.Security;

/// <summary>
/// 单篇加密笔记的加密算法元数据信封。
/// </summary>
public sealed record EncryptedEnvelope(
    string Algorithm,
    string Kdf,
    int Iterations,
    string SaltBase64,
    string NonceBase64);

/// <summary>
/// 本地优先加密安全引擎：
/// - 单篇笔记独立密码加密：AES-256-GCM + PBKDF2-HMAC-SHA512（310,000 次迭代）
/// - 本地应用锁主密码哈希与常量时间比对验证
/// </summary>
public static class NoteCryptoService
{
    private static readonly byte[] MagicHeader = "MKN2"u8.ToArray();
    private const int KeySizeBytes = 32;   // 256 位 AES 密钥
    private const int SaltSizeBytes = 16;  // 128 位随机盐值
    private const int NonceSizeBytes = 12; // 96 位 GCM 随机数
    private const int TagSizeBytes = 16;   // 128 位 GCM 认证标签
    private const int Pbkdf2Iterations = 310_000;

    /// <summary>
    /// 使用用户设置的独立密码对单篇笔记正文进行 AES-256-GCM 认证加密。
    /// </summary>
    public static (byte[] CipherFileBytes, string CryptoMetaJson) EncryptNote(string plainText, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var plainBytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        try
        {
            var ciphertext = new byte[plainBytes.Length];
            var tag = new byte[TagSizeBytes];

            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Encrypt(nonce, plainBytes, ciphertext, tag, MagicHeader);

            // 二进制信封布局：[4B "MKN2"][16B salt][12B nonce][16B tag][密文正文]
            var result = new byte[MagicHeader.Length + SaltSizeBytes + NonceSizeBytes + TagSizeBytes + ciphertext.Length];
            var span = result.AsSpan();
            MagicHeader.CopyTo(span);
            salt.CopyTo(span[4..20]);
            nonce.CopyTo(span[20..32]);
            tag.CopyTo(span[32..48]);
            ciphertext.CopyTo(span[48..]);

            var meta = new EncryptedEnvelope(
                "AES-256-GCM",
                "PBKDF2-SHA512",
                Pbkdf2Iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce));

            return (result, JsonSerializer.Serialize(meta));
        }
        finally
        {
            // 立即清零敏感内存，防止密钥与明文残留
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// 使用密码解密单篇加密笔记的 AES-256-GCM 密文块，密码错误或密文被篡改时抛出异常。
    /// </summary>
    public static string DecryptNote(byte[] cipherFileBytes, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (cipherFileBytes.Length < 48 || !cipherFileBytes.AsSpan(0, 4).SequenceEqual(MagicHeader))
            throw new CryptographicException("无效的加密笔记文件格式。");

        var span = cipherFileBytes.AsSpan();
        var salt = span[4..20];
        var nonce = span[20..32];
        var tag = span[32..48];
        var ciphertext = span[48..];

        var key = DeriveKey(password, salt, Pbkdf2Iterations);
        var plainBytes = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plainBytes, MagicHeader);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("密码错误或密文已损坏。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// 生成本地应用锁主密码的加盐 PBKDF2-SHA512 哈希值。
    /// </summary>
    public static (string HashBase64, string SaltBase64) HashAppLockPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = DeriveKey(password, salt, Pbkdf2Iterations);
        try
        {
            return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>
    /// 使用常量时间比对校验本地应用锁密码，防止时序侧信道攻击。
    /// </summary>
    public static bool VerifyAppLockPassword(string password, string? storedHashBase64, string? storedSaltBase64)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHashBase64) || string.IsNullOrEmpty(storedSaltBase64))
            return false;

        var salt = Convert.FromBase64String(storedSaltBase64);
        var expected = Convert.FromBase64String(storedHashBase64);
        var actual = DeriveKey(password, salt, Pbkdf2Iterations);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeySizeBytes);
}
