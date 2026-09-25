using System.Security.Cryptography;

namespace ClassroomBreakLock.Auth;

/// <summary>
/// TOTP 实现（RFC 6238 / HOTP RFC 4226）。
/// 可与 Google Authenticator、Microsoft Authenticator、1Password 等通用验证器互通。
/// </summary>
public static class Totp
{
    /// <summary>生成一个 Base32 密钥（默认 20 字节 = 160 bit，标准长度）。</summary>
    public static string GenerateSecret(int bytes = 20)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Base32Encode(raw);
    }

    /// <summary>计算指定时刻的验证码。</summary>
    public static string Compute(string base32Secret, DateTime utcNow, int step = 30, int digits = 6)
    {
        var key = Base32Decode(base32Secret);
        var counter = (long)(utcNow - DateTime.UnixEpoch).TotalSeconds / step;
        return Hotp(key, counter, digits);
    }

    /// <summary>校验用户输入的验证码，容忍一定的时间漂移。</summary>
    public static bool Verify(string base32Secret, string code, DateTime utcNow,
        int step = 30, int digits = 6, int driftSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = Normalize(code);
        if (code.Length != digits || !code.All(char.IsDigit)) return false;

        var key = Base32Decode(base32Secret);
        var counter = (long)(utcNow - DateTime.UnixEpoch).TotalSeconds / step;

        for (var offset = -driftSteps; offset <= driftSteps; offset++)
        {
            var expected = Hotp(key, counter + offset, digits);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected),
                    System.Text.Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    /// <summary>距离当前验证码失效还有多少秒——锁屏界面可以画个进度环。</summary>
    public static int SecondsRemaining(DateTime utcNow, int step = 30)
    {
        var elapsed = (long)(utcNow - DateTime.UnixEpoch).TotalSeconds % step;
        return (int)(step - elapsed);
    }

    /// <summary>生成 otpauth:// 链接，供验证器 App 扫码或手工录入。</summary>
    public static string BuildOtpAuthUri(string secret, string issuer, string account)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
           $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

    private static string Hotp(byte[] key, long counter, int digits)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, digits);
        return otp.ToString(new string('0', digits));
    }

    public static string Normalize(string code)
        => new(code.Where(char.IsDigit).ToArray());

    // ---------------- Base32 (RFC 4648) ----------------

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(byte[] data)
    {
        if (data.Length == 0) return "";
        var sb = new System.Text.StringBuilder((data.Length + 4) / 5 * 8);
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string input)
    {
        var s = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray())
                    .TrimEnd('=').ToUpperInvariant();
        if (s.Length == 0) return Array.Empty<byte>();

        var output = new List<byte>(s.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in s)
        {
            var idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Base32 密钥含非法字符 '{c}'");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
