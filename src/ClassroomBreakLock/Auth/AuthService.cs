using ClassroomBreakLock.Config;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Auth;

public enum AuthMethod { None, UsbKey, Password, Totp, Emergency, Button, AutoSchedule }

public sealed record AuthOutcome(bool Success, AuthMethod Method, string Message)
{
    public static AuthOutcome Ok(AuthMethod m, string msg) => new(true, m, msg);
    public static AuthOutcome Fail(AuthMethod m, string msg) => new(false, m, msg);
}

/// <summary>
/// 统一认证门面：把三种（+应急码）认证方式收口成一个入口，
/// 并统一记录审计日志。UI 层只管调这里。
/// </summary>
public sealed class AuthService
{
    private AppConfig _cfg;

    public AuthService(AppConfig cfg) => _cfg = cfg;

    public void UpdateConfig(AppConfig cfg) => _cfg = cfg;

    /// <summary>当前配置下有哪些认证方式可用（用于锁屏界面决定显示哪些输入区）。</summary>
    public (bool Usb, bool Password, bool Totp, bool Emergency) AvailableMethods()
    {
        var a = _cfg.Auth;
        return (
            a.UsbKeys.Any(k => k.Enabled),
            a.Password.Enabled && a.Password.IsConfigured,
            a.Totp.Enabled && a.Totp.IsConfigured,
            a.Emergency.IsUsable
        );
    }

    public AuthOutcome TryUsb()
    {
        var r = UsbAuthService.Authenticate(_cfg.Auth.UsbKeys);
        Log.Audit("U盘", r.Success, r.Message);
        return new AuthOutcome(r.Success, AuthMethod.UsbKey, r.Message);
    }

    public AuthOutcome TryPassword(string input)
    {
        var p = _cfg.Auth.Password;
        if (!p.Enabled) return AuthOutcome.Fail(AuthMethod.Password, "密码认证未启用");
        if (!p.IsConfigured) return AuthOutcome.Fail(AuthMethod.Password, "尚未设置密码，请先到设置里配置");

        var ok = HashUtil.Verify(input, p.Salt, p.Hash, p.Iterations);
        var msg = ok ? "密码正确" : "密码错误";
        Log.Audit("密码", ok, msg);
        return new AuthOutcome(ok, AuthMethod.Password, msg);
    }

    public AuthOutcome TryTotp(string input)
    {
        var t = _cfg.Auth.Totp;
        if (!t.Enabled) return AuthOutcome.Fail(AuthMethod.Totp, "TOTP 认证未启用");
        if (!t.IsConfigured) return AuthOutcome.Fail(AuthMethod.Totp, "尚未配置 TOTP 密钥");

        bool ok;
        try
        {
            ok = Totp.Verify(t.Secret, input, DateTime.UtcNow, t.Step, t.Digits, t.DriftSteps);
        }
        catch (Exception ex)
        {
            Log.Audit("TOTP", false, $"密钥格式异常：{ex.Message}");
            return AuthOutcome.Fail(AuthMethod.Totp, "TOTP 密钥格式异常，请检查设置");
        }

        var msg = ok ? "动态码正确" : "动态码错误或已过期";
        Log.Audit("TOTP", ok, msg);
        return new AuthOutcome(ok, AuthMethod.Totp, msg);
    }

    public AuthOutcome TryEmergency(string input)
    {
        var e = _cfg.Auth.Emergency;
        if (!e.IsUsable)
            return AuthOutcome.Fail(AuthMethod.Emergency, "应急码不可用或已被使用过");

        var ok = HashUtil.Verify(input.Trim().ToUpperInvariant(), e.Salt, e.Hash, e.Iterations);
        if (ok)
        {
            if (e.OneTimeUse)
            {
                e.ConsumedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Log.Warn("应急码已使用并作废，请尽快在设置中生成新的应急码");
            }
            Log.Audit("应急码", true, "使用应急码解锁");
            return AuthOutcome.Ok(AuthMethod.Emergency, "应急码通过");
        }

        Log.Audit("应急码", false, "应急码错误");
        return AuthOutcome.Fail(AuthMethod.Emergency, "应急码错误");
    }

    /// <summary>设置/修改密码。</summary>
    public void SetPassword(string newPassword)
    {
        var (salt, hash) = HashUtil.CreateHash(newPassword, _cfg.Auth.Password.Iterations);
        _cfg.Auth.Password.Salt = salt;
        _cfg.Auth.Password.Hash = hash;
        _cfg.Auth.Password.Enabled = true;
        Log.Info("密码已更新");
    }

    /// <summary>生成新的应急码，返回明文供管理员抄写（只显示这一次）。</summary>
    public string RegenerateEmergencyCode()
    {
        var code = HashUtil.GenerateReadableCode();
        var (salt, hash) = HashUtil.CreateHash(code, _cfg.Auth.Emergency.Iterations);
        _cfg.Auth.Emergency.Salt = salt;
        _cfg.Auth.Emergency.Hash = hash;
        _cfg.Auth.Emergency.ConsumedAt = null;
        _cfg.Auth.Emergency.Enabled = true;
        Log.Info("已生成新的应急码");
        return code;
    }
}
