namespace ClassroomBreakLock.Config;

/// <summary>U 盘钥匙：卷序列号(SN) + 盘内密钥文件内容，双重绑定同一把 U 盘。</summary>
public sealed class UsbKey
{
    /// <summary>显示名，例如 "班主任钥匙U盘"。</summary>
    public string Label { get; set; } = "未命名U盘";

    /// <summary>卷序列号，形如 "1A2B-3C4D"。这是 U 盘自身的硬件标识。</summary>
    public string VolumeSerial { get; set; } = "";

    /// <summary>密钥文件名（相对 U 盘根目录），例如 "breaklock.key"。</summary>
    public string KeyFileName { get; set; } = "breaklock.key";

    /// <summary>密钥文件内容的 SHA-256（十六进制小写）。内容不落地明文。</summary>
    public string KeyFileHash { get; set; } = "";

    /// <summary>是否启用这把钥匙。</summary>
    public bool Enabled { get; set; } = true;

    public bool HasBinding => !string.IsNullOrWhiteSpace(VolumeSerial) &&
                              !string.IsNullOrWhiteSpace(KeyFileHash);

    public UsbKey Clone() => new()
    {
        Label = Label,
        VolumeSerial = VolumeSerial,
        KeyFileName = KeyFileName,
        KeyFileHash = KeyFileHash,
        Enabled = Enabled
    };
}

/// <summary>密码认证。</summary>
public sealed class PasswordAuth
{
    public bool Enabled { get; set; } = true;

    /// <summary>PBKDF2 迭代次数。</summary>
    public int Iterations { get; set; } = 120_000;

    /// <summary>Base64 盐。</summary>
    public string Salt { get; set; } = "";

    /// <summary>Base64 派生密钥。绝不存明文密码。</summary>
    public string Hash { get; set; } = "";

    /// <summary>密码提示语，显示在锁屏上。</summary>
    public string Hint { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Hash);

    public PasswordAuth Clone() => new()
    {
        Enabled = Enabled,
        Iterations = Iterations,
        Salt = Salt,
        Hash = Hash,
        Hint = Hint
    };
}

/// <summary>TOTP 认证（RFC 6238，兼容 Google/Microsoft Authenticator）。</summary>
public sealed class TotpAuth
{
    public bool Enabled { get; set; }

    /// <summary>Base32 密钥（不带空格）。</summary>
    public string Secret { get; set; } = "";

    /// <summary>签发者名，显示在验证器 App 里。</summary>
    public string Issuer { get; set; } = "ClassroomBreakLock";

    /// <summary>账号名，显示在验证器 App 里。</summary>
    public string Account { get; set; } = "教室多媒体";

    /// <summary>时间步长（秒），标准为 30。</summary>
    public int Step { get; set; } = 30;

    /// <summary>生成位数，标准为 6。</summary>
    public int Digits { get; set; } = 6;

    /// <summary>允许的时间漂移窗口（前后各容忍多少个步长）。</summary>
    public int DriftSteps { get; set; } = 1;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Secret);

    public TotpAuth Clone() => new()
    {
        Enabled = Enabled,
        Secret = Secret,
        Issuer = Issuer,
        Account = Account,
        Step = Step,
        Digits = Digits,
        DriftSteps = DriftSteps
    };
}

/// <summary>应急/一次性主密码——忘带 U 盘又没手机时兜底。</summary>
public sealed class EmergencyAuth
{
    public bool Enabled { get; set; } = true;

    /// <summary>PBKDF2 盐（Base64）。</summary>
    public string Salt { get; set; } = "";

    /// <summary>PBKDF2 派生密钥（Base64）。</summary>
    public string Hash { get; set; } = "";

    public int Iterations { get; set; } = 200_000;

    /// <summary>每用一次就作废，强制管理员换新（防止长期泄漏）。</summary>
    public bool OneTimeUse { get; set; } = true;

    /// <summary>使用后作废的时间戳；非空且 OneTimeUse 为真时该应急码失效。</summary>
    public string? ConsumedAt { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Hash);

    public bool IsUsable => Enabled && IsConfigured &&
                            !(OneTimeUse && !string.IsNullOrEmpty(ConsumedAt));

    public EmergencyAuth Clone() => new()
    {
        Enabled = Enabled,
        Salt = Salt,
        Hash = Hash,
        Iterations = Iterations,
        OneTimeUse = OneTimeUse,
        ConsumedAt = ConsumedAt
    };
}

/// <summary>认证方式的集合与总开关。</summary>
public sealed class AuthConfig
{
    public List<UsbKey> UsbKeys { get; set; } = new();
    public PasswordAuth Password { get; set; } = new();
    public TotpAuth Totp { get; set; } = new();
    public EmergencyAuth Emergency { get; set; } = new();

    /// <summary>是否允许"下课按钮点击即解锁"（不需要任何凭据）。</summary>
    public bool AllowButtonUnlockWithoutAuth { get; set; } = true;

    /// <summary>任意一种认证通过后，解锁持续多少分钟；0 = 直到下个锁屏时段开始时重新锁。</summary>
    public int UnlockDurationMinutes { get; set; } = 0;

    public AuthConfig Clone() => new()
    {
        UsbKeys = UsbKeys.Select(k => k.Clone()).ToList(),
        Password = Password.Clone(),
        Totp = Totp.Clone(),
        Emergency = Emergency.Clone(),
        AllowButtonUnlockWithoutAuth = AllowButtonUnlockWithoutAuth,
        UnlockDurationMinutes = UnlockDurationMinutes
    };
}
