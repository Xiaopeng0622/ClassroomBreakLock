using System.Security.Cryptography;

namespace ClassroomBreakLock.Auth;

/// <summary>密码 / 应急码的 PBKDF2 加盐哈希工具。绝不保存明文。</summary>
public static class HashUtil
{
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string SaltBase64, string HashBase64) CreateHash(string plain, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password: System.Text.Encoding.UTF8.GetBytes(plain),
            salt: salt,
            iterations: iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySize);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    /// <summary>定时安全的比对，防时序侧信道。</summary>
    public static bool Verify(string plain, string saltBase64, string hashBase64, int iterations)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password: System.Text.Encoding.UTF8.GetBytes(plain),
                salt: salt,
                iterations: iterations,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>生成一个好念的随机密码（用于应急码），排除易混字符。</summary>
    public static string GenerateReadableCode(int groups = 3, int groupLen = 4)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 去掉 I O 0 1
        var chars = new char[groups * groupLen + groups - 1];
        int ci = 0;
        for (int g = 0; g < groups; g++)
        {
            if (g > 0) chars[ci++] = '-';
            for (int i = 0; i < groupLen; i++)
                chars[ci++] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(chars);
    }
}
