using System.IO;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;

namespace BreakLockVerify;

/// <summary>
/// 认证配置链路的回归测试。
///
/// 背景：曾出现「已登记 U 盘，但下次进设置仍提示未配置任何认证方式」的 bug。
/// 根因是登记 U 盘只改了内存里的集合、没落盘，而 AvailableMethods() 读的是配置对象。
/// 这里把整条链路（登记 → 持久化 → 读回 → 判定）钉死，防止回归。
/// </summary>
public static class AuthConfigChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== 认证方式判定（AvailableMethods）===");

        // 全新配置：应该判定为"零认证"
        var empty = AppConfig.CreateDefault();
        var auth0 = new AuthService(empty);
        var (u0, p0, t0, e0) = auth0.AvailableMethods();
        Check("空配置 → 四种方式都不可用（自检应拦截上锁）",
            !u0 && !p0 && !t0 && !e0, $"usb={u0} pwd={p0} totp={t0} emg={e0}");

        // 加一把启用的 U 盘钥匙 → 应判定为可用
        var cfgUsb = AppConfig.CreateDefault();
        cfgUsb.Auth.UsbKeys.Add(new UsbKey
        {
            Label = "demo1",
            VolumeSerial = "C6E5-5764",
            KeyFileName = "breaklock.key",
            KeyFileHash = "21ab310be2d4b957965931d189c0d30ec723656",
            Enabled = true
        });
        var (u1, p1, _, _) = new AuthService(cfgUsb).AvailableMethods();
        Check("已登记 1 把启用的 U 盘 → U 盘方式可用", u1, $"usb={u1}");
        Check("U 盘可用时其它方式仍为否（不误判）", !p1, $"pwd={p1}");

        // 钥匙被停用 → 不算可用
        var cfgDisabled = AppConfig.CreateDefault();
        cfgDisabled.Auth.UsbKeys.Add(new UsbKey
        {
            Label = "停用的", VolumeSerial = "X", KeyFileHash = "Y", Enabled = false
        });
        var (u2, _, _, _) = new AuthService(cfgDisabled).AvailableMethods();
        Check("钥匙 Enabled=false → 不计入可用方式", !u2, $"usb={u2}");

        Console.WriteLine();
        Console.WriteLine("=== 持久化往返（这是出过 bug 的地方）===");

        // 模拟"登记 U 盘 → 立刻落盘 → 重启读回"
        var dir = Path.Combine(Path.GetTempPath(), "bll_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfgPath = Path.Combine(dir, "config.json");

        try
        {
            var cfg = AppConfig.CreateDefault();
            cfg.ConfigPath = cfgPath;
            cfg.Auth.UsbKeys.Add(new UsbKey
            {
                Label = "班主任钥匙",
                VolumeSerial = "C6E5-5764",
                KeyFileName = "breaklock.key",
                KeyFileHash = "abc123",
                Enabled = true
            });
            ConfigStore.Save(cfg, cfgPath);
            Check("配置已写入磁盘", File.Exists(cfgPath));

            // 重新加载，模拟"关掉设置再打开"
            var reloaded = ConfigStore.Load(cfgPath);
            Check("读回后 U 盘钥匙数量为 1", reloaded.Auth.UsbKeys.Count == 1,
                $"实际 {reloaded.Auth.UsbKeys.Count}");

            var key = reloaded.Auth.UsbKeys.FirstOrDefault();
            Check("读回后 Label 正确", key?.Label == "班主任钥匙", key?.Label ?? "null");
            Check("读回后 VolumeSerial 正确", key?.VolumeSerial == "C6E5-5764", key?.VolumeSerial ?? "null");
            Check("读回后 KeyFileHash 正确", key?.KeyFileHash == "abc123", key?.KeyFileHash ?? "null");
            Check("读回后 Enabled 为 true", key?.Enabled == true);

            // 关键断言：重新加载后 AvailableMethods 必须认得这把钥匙
            var (u3, _, _, _) = new AuthService(reloaded).AvailableMethods();
            Check("★ 重新加载后 AvailableMethods 认为 U 盘可用（这就是之前的 bug）",
                u3, $"usb={u3}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("=== 密码 / TOTP / 应急码 的配置判定 ===");

        var cfgPwd = AppConfig.CreateDefault();
        var authPwd = new AuthService(cfgPwd);
        authPwd.SetPassword("test1234");
        var (_, p2, _, _) = authPwd.AvailableMethods();
        Check("设置密码后 → 密码方式可用", p2, $"pwd={p2}");
        Check("密码未存明文（Hash 非空且不等于原文）",
            cfgPwd.Auth.Password.Hash.Length > 0 && cfgPwd.Auth.Password.Hash != "test1234");
        Check("设置密码后仍能验证通过", authPwd.TryPassword("test1234").Success);
        Check("错误密码被拒绝", !authPwd.TryPassword("wrong").Success);

        var cfgTotp = AppConfig.CreateDefault();
        cfgTotp.Auth.Totp.Secret = Totp.GenerateSecret();
        cfgTotp.Auth.Totp.Enabled = true;
        var (_, _, t1, _) = new AuthService(cfgTotp).AvailableMethods();
        Check("配置 TOTP 密钥并启用 → 动态码方式可用", t1, $"totp={t1}");

        var cfgEmg = AppConfig.CreateDefault();
        var authEmg = new AuthService(cfgEmg);
        var code = authEmg.RegenerateEmergencyCode();
        var (_, _, _, e1) = authEmg.AvailableMethods();
        Check("生成应急码 → 应急码方式可用", e1, $"emg={e1}");
        Check("应急码可验证通过", authEmg.TryEmergency(code).Success);
        Check("应急码一次性：用过后失效", !authEmg.TryEmergency(code).Success);

        Console.WriteLine();
        Console.WriteLine("=== 零认证时的拦截判定（自检核心）===");
        // 用与 AppHost.HasAnyAuthMethod 相同的逻辑验证
        static bool HasAnyAuth(AppConfig c)
        {
            var (u, p, t, e) = new AuthService(c).AvailableMethods();
            return u || p || t || e;
        }
        Check("空配置 → 判定为无认证（应拦截上锁）", !HasAnyAuth(AppConfig.CreateDefault()));
        var oneKey = AppConfig.CreateDefault();
        oneKey.Auth.UsbKeys.Add(new UsbKey { Label = "k", VolumeSerial = "S", KeyFileHash = "H", Enabled = true });
        Check("仅一把 U 盘 → 判定为有认证（应允许上锁）", HasAnyAuth(oneKey));

        return failures;
    }
}
