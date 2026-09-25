using System.IO;
using System.Text;

namespace ClassroomBreakLock.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 极简文件日志：按天分文件，带简单的保留期清理。
/// 审计用途——谁在什么时候用什么方式解锁了，都要留痕。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _dir = "";
    private static int _retentionDays = 90;
    private static bool _enabled = true;

    public static string CurrentFile =>
        Path.Combine(_dir, $"breaklock-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Configure(string? directory, int retentionDays, bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _retentionDays = retentionDays <= 0 ? 90 : retentionDays;
            _dir = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ClassroomBreakLock", "logs")
                : directory;
            try { Directory.CreateDirectory(_dir); } catch { /* 日志目录建不了也不能让主程序挂 */ }
        }
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    /// <summary>
    /// Debug 级别当前是否会被输出。
    /// 用于在调用处提前判断，避免拼装那些高频但默认不写的诊断字符串。
    /// </summary>
    public static bool IsDebugEnabled => _enabled && _debugEnabled;

    /// <summary>是否输出 Debug 级日志。默认关闭，排查定位问题时可在配置里打开。</summary>
    private static bool _debugEnabled;

    /// <summary>设置是否输出 Debug 级日志。</summary>
    public static void SetDebugEnabled(bool on) => _debugEnabled = on;

    /// <summary>专门记认证事件，方便事后审计。</summary>
    public static void Audit(string method, bool success, string detail = "")
    {
        var who = Environment.UserName;
        var host = Environment.MachineName;
        Write(success ? LogLevel.Info : LogLevel.Warn,
            $"[AUDIT] 认证={method} 结果={(success ? "通过" : "拒绝")} " +
            $"用户={who} 主机={host} {detail}".TrimEnd());
    }

    private static void Write(LogLevel level, string message)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}";
        lock (Gate)
        {
            try
            {
                if (!string.IsNullOrEmpty(_dir))
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* 写日志失败静默，绝不影响主流程 */ }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>清理超过保留期的日志文件。</summary>
    public static void Cleanup()
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir)) return;
                var cutoff = DateTime.Now.AddDays(-_retentionDays);
                foreach (var f in Directory.GetFiles(_dir, "breaklock-*.log"))
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
