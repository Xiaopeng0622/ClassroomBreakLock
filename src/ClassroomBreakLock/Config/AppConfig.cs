using System.Text.Json.Serialization;

namespace ClassroomBreakLock.Config;

/// <summary>置顶与防绕过行为。</summary>
public sealed class SecurityConfig
{
    /// <summary>置顶压盖所有窗口。</summary>
    public bool TopMost { get; set; } = true;

    /// <summary>屏蔽 Win / Alt+Tab / Alt+F4 / Ctrl+Esc 等系统热键。</summary>
    public bool BlockSystemHotkeys { get; set; } = true;

    /// <summary>从截屏/录屏/投屏中排除本窗口。</summary>
    public bool ExcludeFromCapture { get; set; }

    /// <summary>防退出看门狗：检测到进程被结束/主窗口被关闭时自动重启自己。</summary>
    public bool WatchdogEnabled { get; set; } = true;

    /// <summary>看门狗巡检间隔（秒）。</summary>
    public int WatchdogIntervalSeconds { get; set; } = 5;

    /// <summary>禁止任务管理器等工具（需要管理员权限，模板阶段仅记录不强制）。</summary>
    public bool HardenSystem { get; set; } = false;

    public SecurityConfig Clone() => (SecurityConfig)MemberwiseClone();
}

/// <summary>右下角悬浮"下课"按钮。</summary>
public sealed class FloatingButtonConfig
{
    public bool Enabled { get; set; } = true;

    public string Text { get; set; } = "下课";

    /// <summary>距离屏幕右边距（像素）。</summary>
    public int MarginRight { get; set; } = 24;

    /// <summary>距离屏幕下边距（像素）。</summary>
    public int MarginBottom { get; set; } = 96;

    /// <summary>直径（像素）。</summary>
    public int Size { get; set; } = 84;

    /// <summary>不透明度 0.1~1.0。</summary>
    public double Opacity { get; set; } = 0.92;

    /// <summary>是否允许拖动。</summary>
    public bool Draggable { get; set; } = true;

    /// <summary>鼠标静止多少秒后自动淡出（0 = 不淡出）。</summary>
    public int FadeAfterIdleSeconds { get; set; } = 8;

    /// <summary>淡出后保留的不透明度比例（0.1~1.0，相对 Opacity）。</summary>
    public double FadeToRatio { get; set; } = 0.35;

    // ---------------- 多显示器 ----------------

    /// <summary>
    /// 目标显示器：-1 = 自动（记住上次所在屏），>=0 = 强制指定第 N 块屏。
    /// 屏幕序号按 Windows 显示设置的顺序（0 为主屏，向下排列）。
    /// </summary>
    public int TargetScreenIndex { get; set; } = -1;

    /// <summary>上次所在显示器的设备名（自动模式下用于恢复位置）。</summary>
    public string LastScreenDeviceName { get; set; } = "";

    /// <summary>用户是否把按钮拖到过自定义位置（而非默认右下角/吸附位）。</summary>
    public bool HasFreePosition { get; set; }

    /// <summary>自由摆放时的横坐标（相对目标屏工作区左上角）。</summary>
    public int FreeX { get; set; }

    /// <summary>自由摆放时的纵坐标（相对目标屏工作区左上角）。</summary>
    public int FreeY { get; set; }

    /// <summary>屏幕被拔掉/变更时是否回落到主屏（false 则可能定位失败）。</summary>
    public bool FallbackToPrimary { get; set; } = true;

    // ---------------- 靠边吸附 ----------------

    /// <summary>是否启用靠边吸附。</summary>
    public bool SnapToEdge { get; set; } = true;

    /// <summary>吸附触发距离（像素）：拖到距屏幕边缘这么近就吸附。</summary>
    public int SnapThreshold { get; set; } = 24;

    /// <summary>吸附后是否自动缩成小图标。</summary>
    public bool CollapseWhenSnapped { get; set; } = true;

    /// <summary>缩起后的直径（像素）。</summary>
    public int CollapsedSize { get; set; } = 28;

    /// <summary>吸附到哪一边：left / right。</summary>
    public string SnappedEdge { get; set; } = "";

    /// <summary>当前是否处于缩起状态（持久化，重启后保持）。</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>悬停展开的延迟毫秒数（0 = 立刻展开）。</summary>
    public int ExpandDelayMs { get; set; } = 120;

    public bool IsSnapped => !string.IsNullOrEmpty(SnappedEdge);

    public FloatingButtonConfig Clone() => (FloatingButtonConfig)MemberwiseClone();
}

/// <summary>与 ClassIsland 的状态同步（本地 HTTP 接收）。</summary>
public sealed class SyncConfig
{
    /// <summary>是否开启本地接收端口。</summary>
    public bool Enabled { get; set; }

    /// <summary>监听端口（只绑 localhost）。</summary>
    public int Port { get; set; } = 8731;

    /// <summary>可选的令牌，留空则不校验。</summary>
    public string Token { get; set; } = "";

    /// <summary>收到「上课」事件时立即上锁。</summary>
    public bool LockOnClassStart { get; set; } = true;

    /// <summary>收到「下课」事件时立即解锁。</summary>
    public bool UnlockOnClassEnd { get; set; } = true;

    public SyncConfig Clone() => (SyncConfig)MemberwiseClone();
}

/// <summary>
/// 锁屏界面的个性化外观。
///
/// 设计取舍：背景图会被**复制到程序数据目录**（assets 子目录），
/// 而不是只记一个路径——原图被删/被移动后锁屏不该变成一片黑。
/// </summary>
public sealed class AppearanceConfig
{
    /// <summary>是否启用自定义背景图。关掉就用内置的深色渐变。</summary>
    public bool UseBackgroundImage { get; set; }

    /// <summary>背景图文件名（相对程序数据目录的 assets 文件夹，只存文件名不存全路径）。</summary>
    public string BackgroundImageFile { get; set; } = "";

    /// <summary>
    /// 遮罩暗度 0.0~0.9。
    /// 在背景图上盖一层半透明黑，保证浅色图片上白字依然可读。0 = 不遮。
    /// </summary>
    public double OverlayOpacity { get; set; } = 0.45;

    /// <summary>背景模糊半径 0~60（像素）。0 = 不模糊。</summary>
    public double BlurRadius { get; set; } = 0;

    /// <summary>背景整体不透明度 0.1~1.0。调低可让内置深色底透出来。</summary>
    public double BackgroundOpacity { get; set; } = 1.0;

    /// <summary>图片填充方式：uniform（适应）/ uniformToFill（填充）/ fill（拉伸）。</summary>
    public string Stretch { get; set; } = "uniformToFill";

    public AppearanceConfig Clone() => (AppearanceConfig)MemberwiseClone();

    /// <summary>是否已有可用的背景图文件。</summary>
    public bool HasImage => UseBackgroundImage &&
                            !string.IsNullOrWhiteSpace(BackgroundImageFile);
}

/// <summary>提醒与提示音。</summary>
public sealed class AlertConfig
{
    /// <summary>锁屏前多少秒给出倒计时提示（0 = 不提示）。</summary>
    public int PreLockWarningSeconds { get; set; } = 15;

    /// <summary>是否在锁屏/解锁时发出提示音。</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>锁屏页显示的大标题。</summary>
    public string LockTitle { get; set; } = "课间休息，多媒体已锁定";

    /// <summary>锁屏页副标题/提示语。</summary>
    public string LockSubtitle { get; set; } = "请老师插入钥匙 U 盘，或输入密码解锁";

    public AlertConfig Clone() => (AlertConfig)MemberwiseClone();
}

/// <summary>日志审计。</summary>
public sealed class LoggingConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 输出 Debug 级详细日志（含悬浮按钮定位细节）。
    /// 排查"按钮位置不对/拖动异常"时打开，平时关掉避免日志膨胀。
    /// </summary>
    public bool DebugVerbose { get; set; }

    /// <summary>日志目录；留空则用 %ProgramData%\ClassroomBreakLock\logs。</summary>
    public string Directory { get; set; } = "";

    /// <summary>保留天数，超期自动清理。</summary>
    public int RetentionDays { get; set; } = 90;

    public LoggingConfig Clone() => (LoggingConfig)MemberwiseClone();
}

/// <summary>全局设置根对象。整个应用的行为都由它决定。</summary>
public sealed class AppConfig
{
    /// <summary>配置架构版本，便于后续升级迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 测试模式：启动后不自动上锁，锁屏只能通过托盘菜单/快捷键手动触发。
    /// 用来解决"没有 U 盘就进不去设置"的先有鸡还是先有蛋问题。
    /// <b>正式部署前务必关掉。</b>
    /// 默认 true —— 首次运行保证你一定能进设置完成配置。
    /// </summary>
    public bool TestMode { get; set; } = true;

    /// <summary>本机教室名，显示在锁屏页与日志里。</summary>
    public string ClassroomName { get; set; } = "高一(3)班";

    /// <summary>总开关：false = 整个锁屏功能停用（维护模式）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>全局默认：课前提前多少分钟自动解锁。</summary>
    public int EarlyUnlockMinutes { get; set; } = 2;

    /// <summary>全局默认：下课后延迟多少秒自动上锁。</summary>
    public int LockDelaySeconds { get; set; } = 5;

    /// <summary>自动解锁只对"上课时段"生效；true 表示课间也自动解锁（一般不勾）。</summary>
    public bool AutoUnlockDuringBreak { get; set; } = false;

    public SecurityConfig Security { get; set; } = new();
    public FloatingButtonConfig FloatingButton { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();
    public AppearanceConfig Appearance { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();

    /// <summary>与 ClassIsland 的状态同步（本地 HTTP 接收上课/下课）。</summary>
    public SyncConfig Sync { get; set; } = new();

    /// <summary>索引 0=周日 ... 6=周六。</summary>
    public List<WeeklySchedule> Weekly { get; set; } = new();

    /// <summary>具体日期覆盖规则。</summary>
    public List<DateOverride> Overrides { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    /// <summary>生成一份开箱可用的默认配置：标准中学作息。</summary>
    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();

        // 周一~周五：同样的作息
        for (int day = 0; day < 7; day++)
        {
            var ws = new WeeklySchedule { Enabled = day is >= 1 and <= 5 };
            if (ws.Enabled)
            {
                ws.Periods = new List<ClassPeriod>
                {
                    new() { Name = "早读",   Start = "07:30", End = "07:55", LockDuring = false },
                    new() { Name = "第1节",  Start = "08:00", End = "08:45" },
                    new() { Name = "第2节",  Start = "08:55", End = "09:40" },
                    new() { Name = "第3节",  Start = "10:00", End = "10:45" },
                    new() { Name = "第4节",  Start = "10:55", End = "11:40" },
                    new() { Name = "午休",   Start = "11:40", End = "13:30", LockDuring = false },
                    new() { Name = "第5节",  Start = "13:30", End = "14:15" },
                    new() { Name = "第6节",  Start = "14:25", End = "15:10" },
                    new() { Name = "第7节",  Start = "15:30", End = "16:15" },
                    new() { Name = "第8节",  Start = "16:25", End = "17:10" },
                    new() { Name = "晚自习", Start = "19:00", End = "21:00" },
                };
            }
            cfg.Weekly.Add(ws);
        }

        return cfg;
    }

    public AppConfig Clone()
    {
        var c = (AppConfig)MemberwiseClone();
        c.Security = Security.Clone();
        c.FloatingButton = FloatingButton.Clone();
        c.Alerts = Alerts.Clone();
        c.Appearance = Appearance.Clone();
        c.Logging = Logging.Clone();
        c.Auth = Auth.Clone();
        c.Sync = Sync.Clone();
        c.Weekly = Weekly.Select(w => w.Clone()).ToList();
        c.Overrides = Overrides.Select(o => new DateOverride
        {
            Date = o.Date, Locked = o.Locked, Note = o.Note
        }).ToList();
        return c;
    }
}
