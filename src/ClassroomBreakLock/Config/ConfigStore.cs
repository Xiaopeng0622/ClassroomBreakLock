using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassroomBreakLock.Config;

/// <summary>配置的读写与热重载。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null
    };

    /// <summary>默认配置位置：%ProgramData%\ClassroomBreakLock\config.json</summary>
    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ClassroomBreakLock",
            "config.json");

    /// <summary>调试用配置位置（工作目录下的 config.json 优先）。</summary>
    public static string PortableConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string ResolveConfigPath()
    {
        // 便携模式：exe 同目录有 config.json 就用它，方便模板测试不污染系统
        if (File.Exists(PortableConfigPath)) return PortableConfigPath;
        return DefaultConfigPath;
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= ResolveConfigPath();
        AppConfig cfg;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? AppConfig.CreateDefault();
            }
            else
            {
                cfg = AppConfig.CreateDefault();
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Warn($"读取配置失败，回退默认配置：{ex.Message}");
            cfg = AppConfig.CreateDefault();
        }

        Normalize(cfg);
        cfg.ConfigPath = path;
        return cfg;
    }

    public static void Save(AppConfig cfg, string? path = null)
    {
        path ??= string.IsNullOrEmpty(cfg.ConfigPath) ? ResolveConfigPath() : cfg.ConfigPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 原子写：先写临时文件再替换，避免掉电写坏配置
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOpts));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
            else File.Move(tmp, path);

            cfg.ConfigPath = path;
        }
        catch (Exception ex)
        {
            Logging.Log.Error($"保存配置失败：{ex.Message}");
            throw;
        }
    }

    /// <summary>补齐缺失字段，保证老配置升级后不崩。</summary>
    private static void Normalize(AppConfig cfg)
    {
        cfg.Security ??= new SecurityConfig();        cfg.FloatingButton ??= new FloatingButtonConfig();
        cfg.Alerts ??= new AlertConfig();
        cfg.Appearance ??= new AppearanceConfig();

        // 外观参数夹到合法区间，防止手改配置文件写出越界值
        cfg.Appearance.OverlayOpacity = Math.Clamp(cfg.Appearance.OverlayOpacity, 0.0, 0.9);
        cfg.Appearance.BlurRadius = Math.Clamp(cfg.Appearance.BlurRadius, 0, 60);
        cfg.Appearance.BackgroundOpacity = Math.Clamp(cfg.Appearance.BackgroundOpacity, 0.1, 1.0);
        if (cfg.Appearance.Stretch is not ("uniform" or "uniformToFill" or "fill"))
        {
            cfg.Appearance.Stretch = "uniformToFill";
        }

        cfg.Logging ??= new LoggingConfig();
        cfg.Auth ??= new AuthConfig();
        cfg.Auth.UsbKeys ??= new List<UsbKey>();
        cfg.Auth.Password ??= new PasswordAuth();
        cfg.Auth.Totp ??= new TotpAuth();
        cfg.Auth.Emergency ??= new EmergencyAuth();

        // 星期表补齐到 7 天
        cfg.Weekly ??= new List<WeeklySchedule>();
        while (cfg.Weekly.Count < 7)
            cfg.Weekly.Add(new WeeklySchedule { Enabled = false });

        cfg.Overrides ??= new List<DateOverride>();

        if (cfg.EarlyUnlockMinutes < 0) cfg.EarlyUnlockMinutes = 0;
        if (cfg.EarlyUnlockMinutes > 60) cfg.EarlyUnlockMinutes = 60;
        if (cfg.LockDelaySeconds < 0) cfg.LockDelaySeconds = 0;
        if (cfg.Security.WatchdogIntervalSeconds < 1) cfg.Security.WatchdogIntervalSeconds = 1;
        if (cfg.FloatingButton.Size < 40) cfg.FloatingButton.Size = 40;
    }
}
