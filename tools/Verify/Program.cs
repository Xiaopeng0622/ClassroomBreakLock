using ClassroomBreakLock.Auth;

namespace BreakLockVerify;

/// <summary>密码 / TOTP 相关校验。</summary>
public static class CryptoChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== TOTP RFC 6238 标准测试向量（SHA1, 8位）===");
        // RFC 6238 附录 B：密钥为 ASCII "12345678901234567890" 的 Base32
        const string rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

        var vectors = new (long UnixTime, string Expected)[]
        {
            (59L,          "94287082"),
            (1111111109L,  "07081804"),
            (1111111111L,  "14050471"),
            (1234567890L,  "89005924"),
            (2000000000L,  "69279037"),
            (20000000000L, "65353130"),
        };

        foreach (var (unix, expected) in vectors)
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            var actual = Totp.Compute(rfcSecret, t, 30, 8);
            Check($"T={unix} 期望={expected} 实际={actual}", actual == expected);
        }

        Console.WriteLine();
        Console.WriteLine("=== Base32 编解码往返一致性 ===");
        for (int i = 0; i < 5; i++)
        {
            var raw = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
            var enc = Totp.Base32Encode(raw);
            var dec = Totp.Base32Decode(enc);
            Check($"20字节往返 #{i + 1}", raw.SequenceEqual(dec), enc);
        }

        Console.WriteLine();
        Console.WriteLine("=== TOTP 校验与漂移容忍 ===");

        // ⚠️ 这一段刻意用**固定的基准时刻**而不是 DateTime.UtcNow。
        //    用"当前真实时间"会让断言随运行时刻漂移：如果恰好跨过 30 秒步长边界，
        //    "31 秒前的码"可能与当前码落在同一格，导致偶发假失败（实际踩到过）。
        var secret = Totp.GenerateSecret();
        var now = new DateTime(2026, 3, 15, 10, 30, 15, DateTimeKind.Utc);  // 固定在步长中段

        var code = Totp.Compute(secret, now);
        Check($"当前码 {code} 可通过", Totp.Verify(secret, code, now));

        // 构造一个确定不同于当前码的 6 位数字，避免"随机码恰好等于 000000"造成的偶发假失败
        var wrong = code == "000000" ? "111111" : "000000";
        Check($"错误码 {wrong} 被拒绝", !Totp.Verify(secret, wrong, now));
        Check("空码被拒绝", !Totp.Verify(secret, "", now));
        Check("非数字被拒绝", !Totp.Verify(secret, "abcdef", now));
        Check("位数不足被拒绝", !Totp.Verify(secret, "123", now));

        // 基准时刻在第 15 秒（步长中段），所以 31 秒前必然落在上一步长
        var prevCode = Totp.Compute(secret, now.AddSeconds(-31));
        Check($"漂移 1 步容忍（上一步码 {prevCode}）",
            prevCode != code && Totp.Verify(secret, prevCode, now));

        // 95 秒前 = 至少 3 个步长之前，超出 driftSteps=1 的容忍范围
        var oldCode = Totp.Compute(secret, now.AddSeconds(-95));
        Check("漂移 3 步应被拒绝", !Totp.Verify(secret, oldCode, now));

        var remain = Totp.SecondsRemaining(now);
        Check($"剩余秒数在 1..30 之间（={remain}）", remain is >= 1 and <= 30);

        // 真实时钟也要能正常工作（只验证不抛异常，不断言具体值）
        var realNow = DateTime.UtcNow;
        var realCode = Totp.Compute(secret, realNow);
        Check("真实时钟下也能正常生成/校验",
            Totp.Verify(secret, realCode, realNow) && realCode.Length == 6);

        Console.WriteLine();
        Console.WriteLine("=== PBKDF2 密码哈希 ===");
        var (salt, hash) = HashUtil.CreateHash("MyP@ssw0rd", 120_000);
        Check("正确密码验证通过", HashUtil.Verify("MyP@ssw0rd", salt, hash, 120_000));
        Check("错误密码被拒绝", !HashUtil.Verify("wrong", salt, hash, 120_000));
        Check("大小写敏感", !HashUtil.Verify("myp@ssw0rd", salt, hash, 120_000));
        var (salt2, hash2) = HashUtil.CreateHash("MyP@ssw0rd", 120_000);
        Check("相同密码两次哈希不同（盐随机）", salt != salt2 && hash != hash2);

        Console.WriteLine();
        Console.WriteLine("=== 应急码生成 ===");
        var emg = HashUtil.GenerateReadableCode();
        Check($"格式 XXXX-XXXX-XXXX（={emg}）", emg.Length == 14 && emg.Count(c => c == '-') == 2);
        Check("不含易混字符 I/O/0/1", !emg.Any(c => c is 'I' or 'O' or '0' or '1'));

        return failures;
    }
}
