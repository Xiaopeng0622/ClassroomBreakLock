using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Interop;
using ClassroomBreakLock.Logging;
using ClassroomBreakLock.Scheduling;
using ClassroomBreakLock.Sync;
using ClassroomBreakLock.Views;

namespace ClassroomBreakLock;

/// <summary>
/// 主程序：把调度、锁屏、悬浮按钮、看门狗串起来的编排层。
///
/// 生命周期：
///   启动 -> 读配置 -> 建调度引擎 -> 每秒tick判定
///     判定=Lock   -> 显示锁屏窗口（置顶+钩子）
///     判定=Unlock -> 隐藏锁屏窗口，显示悬浮按钮
///   认证通过 / 点"下课" -> 解锁，并按解锁时长临时压制调度
/// </summary>
public sealed class AppHost
{
    private AppConfig _cfg = null!;
    private ScheduleEngine _engine = null!;
    private AuthService _auth = null!;
    private TopMostLock? _lock;
    private LockWindow? _lockWindow;
    private FloatingButtonWindow? _floatWindow;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _tickTimer;
    private Mutex? _singleInstance;
    private bool _locked;
    private DateTime? _manualUnlockUntil;
    private bool _diagHook;
    private bool _diagCapture;

    /// <summary>ClassIsland 状态同步接收端（本地 HTTP）。</summary>
    private ClassIslandBridge? _bridge;

    /// <summary>看门狗进程（子进程守护）。</summary>
    private Process? _watchdog;

    /// <summary>托盘图标：提供手动锁定/设置/退出的入口。</summary>
    private System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>全局紧急解锁热键 id。</summary>
    private const int HOTKEY_PANIC_ID = 0xC1A5;

    public void Start()
    {
        // 单实例：防止重复启动互相抢置顶
        _singleInstance = new Mutex(true, @"Global\ClassroomBreakLock_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            AppDialog.Info(null, "课间锁已经在运行了。", "教室多媒体课间锁");
            Application.Current.Shutdown();
            return;
        }

        _cfg = ConfigStore.Load();
        Log.Configure(_cfg.Logging.Directory, _cfg.Logging.RetentionDays, _cfg.Logging.Enabled);
        Log.SetDebugEnabled(_cfg.Logging.DebugVerbose);
        Log.Cleanup();
        Log.Info($"===== 课间锁启动 v0.1.0 教室={_cfg.ClassroomName} " +
                 $"管理员={IsAdministrator()} 配置={_cfg.ConfigPath} =====");

        _engine = new ScheduleEngine(_cfg);
        _auth = new AuthService(_cfg);

        // 托盘图标（手动锁定 / 设置 / 退出）
        SetupTray();

        // 悬浮按钮窗口
        _floatWindow = new FloatingButtonWindow(_cfg);
        _floatWindow.ClassDismissed += OnClassDismissed;
        _floatWindow.SettingsRequested += () => OpenSettingsRequireAuth();
        // 位置/缩起状态变化就落盘，重启后才能回到老师放的那块屏
        _floatWindow.LayoutChanged += OnFloatLayoutChanged;
        // 第三层确认的判定源：现在是否正处于上课时段
        _floatWindow.DuringClassProbe = () => _engine?.GetCurrentPeriod(DateTime.Now)?.Name;
        _floatWindow.Show();
        _floatWindow.ApplyConfig(_cfg);

        // 热键必须在悬浮窗 Show() 拿到 HWND 之后再注册
        SetupPanicHotkey();

        // 调度心跳：1 秒一次
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => Tick();
        _tickTimer.Start();

        StartWatchdog();
        StartSyncBridge();

        // 期望退出时先杀掉看门狗，避免它把程序又拉起来
        Application.Current.Exit += (_, _) => KillWatchdog();

        // 首次判定
        Tick();

        var mode = _cfg.TestMode
            ? "测试模式（不自动上锁，可用托盘/热键手动触发锁屏）"
            : "正式模式（按作息表自动调度）";
        Log.Info($"初始化完成，开始调度｜{mode}");

        // 启动自检：零认证方式时给出明确预警（后续所有上锁都会被拦截）
        if (!HasAnyAuthMethod())
        {
            Log.Warn("自检：尚未配置任何认证方式 —— 在配置完成前，所有上锁操作都会被阻止");
            _floatWindow?.ShowBubble("未配置认证方式，锁屏已暂停\n请先到设置里配置一种", 8);
        }
        else
        {
            Log.Info("自检：认证方式已就绪，锁屏功能正常");
        }

        WarmUpSettingsWindow();
        if (_cfg.TestMode)
        {
            _floatWindow?.ShowBubble("测试模式：不会自动锁屏\n双击图标可进设置", 10);
        }
    }

    // ---------------- ClassIsland 状态同步 ----------------

    private void StartSyncBridge()
    {
        StopSyncBridge();

        if (!_cfg.Sync.Enabled)
        {
            Log.Info("ClassIsland 同步未启用");
            return;
        }

        try
        {
            _bridge = new Sync.ClassIslandBridge(_cfg.Sync.Port, _cfg.Sync.Token);
            _bridge.EventReceived += OnClassIslandEvent;
            _bridge.Start();

            Log.Info(_bridge.IsRunning
                ? $"ClassIsland 同步已就绪：{_bridge.Prefix}class/start 、 /class/end"
                : $"ClassIsland 同步启动失败：{_bridge.LastError}");
        }
        catch (Exception ex)
        {
            Log.Warn($"ClassIsland 同步启动异常：{ex.Message}");
            _bridge = null;
        }
    }

    private void StopSyncBridge()
    {
        if (_bridge is null)
        {
            return;
        }

        try
        {
            _bridge.EventReceived -= OnClassIslandEvent;
            _bridge.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _bridge = null;
        }
    }

    /// <summary>收到 ClassIsland 推来的上课/下课事件。</summary>
    private void OnClassIslandEvent(ClassIslandEvent evt)
    {
        // HTTP 线程回调，后面要碰 UI，必须切回主线程
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_cfg.TestMode)
                {
                    _floatWindow?.ShowBubble($"ClassIsland：{evt.Describe()}（测试模式不动作）", 4);
                    return;
                }

                if (evt.IsClassStart)
                {
                    if (_cfg.Sync.LockOnClassStart && !_locked)
                    {
                        EngageLock(new ScheduleVerdict(
                            LockDecision.Lock,
                            $"ClassIsland：上课{(string.IsNullOrWhiteSpace(evt.Subject) ? "" : "（" + evt.Subject + "）")}",
                            null, null, null, null), "ClassIsland");
                    }
                }
                else
                {
                    if (_cfg.Sync.UnlockOnClassEnd && _locked)
                    {
                        ReleaseLock("ClassIsland：下课事件");
                    }
                }

                _floatWindow?.ShowBubble($"ClassIsland：{evt.Describe()}", 4);
                PushDiagnostics();
            }
            catch (Exception ex)
            {
                Log.Warn($"处理 ClassIsland 事件失败：{ex.Message}");
            }
        }));
    }

    // ---------------- 托盘与紧急热键 ----------------

    /// <summary>加载随程序打包的应用图标（Assets/app.ico）。失败时回退到系统图标，不影响运行。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (res?.Stream is not null)
            {
                // 优先挑选与系统托盘尺寸匹配的帧，小图标下更清晰
                var size = System.Windows.Forms.SystemInformation.SmallIconSize;
                return new System.Drawing.Icon(res.Stream, size);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"加载应用图标失败，回退系统图标：{ex.Message}");
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>托盘图标：即使锁屏层故障，老师也能从这里进设置或退出。</summary>
    private void SetupTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "教室多媒体课间锁",
                Visible = true
            };

            var menu = new System.Windows.Forms.ContextMenuStrip
            {
                // 换成 Fluent 风格渲染器（WinForms 默认那套灰框菜单太丑）
                Renderer = new Interop.FluentMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = false,
                DropShadowEnabled = true,
                BackColor = System.Drawing.Color.FromArgb(0xFB, 0xFB, 0xFB),
                ForeColor = System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x1B),
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F),
                Padding = new System.Windows.Forms.Padding(6)
            };

            var miLock = new System.Windows.Forms.ToolStripMenuItem("立即锁定");
            miLock.Click += (_, _) => ForceLock();
            menu.Items.Add(miLock);

            var miUnlock = new System.Windows.Forms.ToolStripMenuItem("立即解除锁定（需确认）");
            miUnlock.Click += (_, _) => ForceUnlockWithConfirm();
            menu.Items.Add(miUnlock);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var miSettings = new System.Windows.Forms.ToolStripMenuItem("打开设置");
            miSettings.Click += (_, _) => OpenSettingsRequireAuth();
            menu.Items.Add(miSettings);

            var miOpenLog = new System.Windows.Forms.ToolStripMenuItem("打开日志目录");
            miOpenLog.Click += (_, _) => OpenLogFolder();
            menu.Items.Add(miOpenLog);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var miExit = new System.Windows.Forms.ToolStripMenuItem("退出程序");
            miExit.Click += (_, _) =>
            {
                if (AppDialog.Confirm(null,
                        "确定要退出课间锁吗？退出后课间将不再自动锁屏。",
                        "确认退出", "退出", "取消", danger: true))
                {
                    Shutdown();
                }
            };
            menu.Items.Add(miExit);

            // 统一菜单项尺寸与危险色
            foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
            {
                item.Padding = new System.Windows.Forms.Padding(8, 5, 8, 5);
                item.Margin = System.Windows.Forms.Padding.Empty;
            }
            miExit.ForeColor = System.Drawing.Color.FromArgb(0xC4, 0x2B, 0x1C);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => OpenSettingsRequireAuth();

            Log.Info("托盘图标已就绪");
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘图标初始化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 注册全局紧急解锁热键 Ctrl+Alt+Shift+K。
    /// 用途：万一认证方式全配错了、或者界面卡住，还能救回来。
    /// 注意：只在"未锁屏"或"测试模式"下生效，正式锁屏时不允许热键绕过。
    /// </summary>
    private void SetupPanicHotkey()
    {
        try
        {
            if (_floatWindow is null) return;
            var helper = new System.Windows.Interop.WindowInteropHelper(_floatWindow);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn("注册紧急热键失败：悬浮窗句柄尚未创建");
                return;
            }

            var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (src is null)
            {
                Log.Warn("注册紧急热键失败：拿不到 HwndSource");
                return;
            }
            src.AddHook(WndProc);
            _hotkeyHwnd = hwnd;

            if (RegisterHotKey(hwnd, HOTKEY_PANIC_ID, MOD_CONTROL | MOD_ALT | MOD_SHIFT, VK_K))
                Log.Info("紧急解锁热键已注册：Ctrl+Alt+Shift+K");
            else
                Log.Warn($"注册紧急热键失败，Win32 错误码={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Warn($"注册紧急热键异常：{ex.Message}");
        }
    }

    private IntPtr _hotkeyHwnd = IntPtr.Zero;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_PANIC_ID)
        {
            handled = true;
            Log.Warn("紧急解锁热键被按下");

            // 正式锁屏状态下，热键只提示、不直接解锁，避免被学生利用
            if (_locked && !_cfg.TestMode)
            {
                AppDialog.Warn(null,
                    "锁屏状态下紧急热键不解锁，请使用 U 盘/密码/动态码。\n" +
                    "如确实无法解锁，请以管理员身份结束进程并删除配置。",
                    "紧急解锁");
                return IntPtr.Zero;
            }

            // 热键同样要过认证门——否则它就成了绕过设置保护的万能钥匙。
            // 测试模式下例外：那时本来就是调试用的，且锁屏不会自动触发。
            if (!_cfg.TestMode)
            {
                var hotkeyOwner = _floatWindow is { IsVisible: true } ? _floatWindow : null;
                if (!SettingsAuthGate.Require(hotkeyOwner, _cfg, _auth,
                        "紧急热键会解除课间锁定，请先验证身份"))
                {
                    Log.Audit("紧急热键", false, "认证未通过，未解锁");
                    return IntPtr.Zero;
                }
            }

            Log.Audit("紧急热键", true, "认证通过，解除锁定");
            ReleaseLock("管理员使用紧急热键");
            AppDialog.Info(null, "已解除锁定。", "紧急解锁");
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_K = 0x4B;

    private void ForceLock()
    {
        if (_locked) return;
        _manualUnlockUntil = null;
        EngageLock(_engine.Evaluate(DateTime.Now), "托盘立即锁定");
    }

    private void ForceUnlockWithConfirm()
    {
        if (!_locked) return;
        if (!AppDialog.Confirm(null,
                "确定要强制解除锁定吗？\n\n此操作需要验证身份，并会记入日志。",
                "强制解锁", "继续", "取消", danger: true))
        {
            return;
        }

        // 托盘菜单不是后门：强制解锁同样要过认证门
        var trayOwner = _floatWindow is { IsVisible: true } ? _floatWindow : null;
        if (!SettingsAuthGate.Require(trayOwner, _cfg, _auth,
                "强制解除锁定会立即开放多媒体使用，请先验证身份"))
        {
            Log.Audit("托盘强制解锁", false, "认证未通过，未解锁");
            return;
        }

        Log.Audit("托盘强制解锁", true, "管理员通过托盘菜单强制解锁");
        ReleaseLock("管理员通过托盘强制解锁");
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(_cfg.Logging.Directory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ClassroomBreakLock", "logs")
                : _cfg.Logging.Directory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"打开日志目录失败：{ex.Message}"); }
    }

    // ---------------- 调度心跳 ----------------

    private void Tick()
    {
        if (_cfg is null || _engine is null) return;

        try
        {
            var now = DateTime.Now;
            var verdict = _engine.Evaluate(now);
            var shouldLock = verdict.ShouldLock;

            // 测试模式：调度永不自动上锁，锁屏只能靠托盘/热键手动触发；
            // 手动锁上后也不会被调度自动解开，必须走认证或紧急热键。
            if (_cfg.TestMode)
            {
                shouldLock = _locked;
            }

            // 手动解锁的临时压制
            if (shouldLock && _manualUnlockUntil is not null)
            {
                if (now < _manualUnlockUntil.Value)
                {
                    shouldLock = false;
                }
                else
                {
                    _manualUnlockUntil = null;
                    Log.Info("手动解锁时长已到，恢复调度判定");
                }
            }

            if (shouldLock && !_locked) EngageLock(verdict);
            else if (!shouldLock && _locked) ReleaseLock("调度判定为解锁");

            UpdateFloatingButton(verdict, now);
        }
        catch (Exception ex)
        {
            Log.Error($"调度 tick 异常：{ex}");
        }
    }

    private void UpdateFloatingButton(ScheduleVerdict verdict, DateTime now)
    {
        if (_floatWindow is null) return;

        if (_locked)
        {
            // “下课”按钮现在是**上锁**入口，锁屏期间必须隐藏——
            // 否则它会浮在锁屏层上，变成一个多余的干扰元素。
            _floatWindow.SetSuppressed(true);
            return;
        }

        _floatWindow.SetSuppressed(false);

        // 虚拟桌面切换会让窗口被 DWM 遮蔽（Visibility 仍是 Visible 但看不见），
        // 系统不会自动把它带回新桌面，所以每次 tick 都确认一下。
        //
        // 例外：用户正在拖动时跳过。拖动期间 UI 线程要留给窗口移动，
        // 而 IsWindowCloaked 是一次跨进程 DWM 调用，会让拖动顿挫。
        if (!_floatWindow.IsDragging)
        {
            _floatWindow.EnsureVisibleIfNeeded();
        }

        // 副标题显示距离下一节课还有多久
        if (verdict.NextPeriod is not null)
        {
            var left = verdict.NextPeriod.StartTime - now.TimeOfDay;
            _floatWindow.UpdateSubText(left > TimeSpan.Zero
                ? $"距{verdict.NextPeriod.Name} {left.Minutes:D2}:{left.Seconds:D2}"
                : "");
        }
        else
        {
            _floatWindow.UpdateSubText("");
        }
    }

    // ---------------- 锁定 / 解锁 ----------------

    /// <summary>
    /// 自检：当前是否配置了至少一种可用的认证方式。
    /// 只要有一种能解开锁屏，允许锁屏就是安全的。
    /// </summary>
    private bool HasAnyAuthMethod()
    {
        try
        {
            var (usb, pwd, totp, emergency) = _auth.AvailableMethods();
            return usb || pwd || totp || emergency;
        }
        catch (Exception ex)
        {
            // 判定失败时按"不安全"处理 —— 宁可锁不上，也不要把人锁在外面
            Log.Error($"认证方式自检异常，按未配置处理：{ex.Message}");
            return false;
        }
    }

    /// <summary>已经就"无认证方式"提醒过几次（用于节流，别每秒弹一次）。</summary>
    private DateTime? _lastNoAuthWarnAt;

    /// <summary>用户已经选择过"暂不配置"，在配置变化前不再重复弹窗。</summary>
    private bool _noAuthWarnDismissed;

    /// <summary>
    /// 零认证方式时拦截上锁，并提示去配置。
    ///
    /// 节流策略：调度每秒都会调用 EngageLock，**日志和弹窗都必须节流**，
    /// 否则上课 45 分钟会刷出几千条重复日志。
    /// 用户主动的操作（托盘/下课按钮）每次都明确告知；
    /// 自动调度只在第一次、以及之后每次间隔超过阈值时记录。
    /// </summary>
    private void WarnNoAuthBlocksLock(string trigger)
    {
        var isManual = trigger is not "调度";

        if (isManual)
        {
            Log.Warn($"自检拦截上锁［{trigger}］：尚未配置任何认证方式，锁屏后将无法解锁");
            ShowNoAuthDialog(trigger);
            return;
        }

        // 自动调度：只在一个"拦截周期"开始时记一条，之后静默；
        // 用户已明确表示暂不配置、或刚提醒过，都不再重复。
        if (_noAuthWarnDismissed) return;

        var now = DateTime.Now;
        if (_lastNoAuthWarnAt is not null &&
            (now - _lastNoAuthWarnAt.Value).TotalMinutes < NoAuthWarnIntervalMinutes)
        {
            return;
        }

        _lastNoAuthWarnAt = now;
        Log.Warn($"自检拦截上锁［{trigger}］：尚未配置任何认证方式，锁屏后将无法解锁" +
                 $"（后续 {NoAuthWarnIntervalMinutes} 分钟内的重复拦截不再记录）");
        ShowNoAuthDialog(trigger);
    }

    /// <summary>同一类自检提醒的最小间隔（分钟）。</summary>
    private const int NoAuthWarnIntervalMinutes = 10;

    private void ShowNoAuthDialog(string trigger)
    {
        var where = trigger == "调度" ? "自动锁屏" : $"「{trigger}」";

        var goConfig = AppDialog.Confirm(
            null,
            $"检测到尚未配置任何身份认证方式（U 盘 / 密码 / 动态码 / 应急码）。\n\n" +
            $"为避免锁屏后无法解锁，已阻止本次{where}。\n\n" +
            "是否现在前往设置配置一种认证方式？",
            "暂不能锁屏",
            "去配置",
            "暂不配置",
            danger: true);

        if (goConfig)
        {
            _noAuthWarnDismissed = false;
            // 此时必然未锁屏，设置门会因"零认证"自动放行，不会把人卡住
            OpenSettingsRequireAuth("自检引导：请先配置一种认证方式");
        }
        else
        {
            _noAuthWarnDismissed = true;
            Log.Warn("用户选择暂不配置认证方式；在配置完成前，所有上锁操作都会被拦截");
            _floatWindow?.ShowBubble("未配置认证方式，已暂停锁屏", 5);
        }
    }

    private void EngageLock(ScheduleVerdict verdict, string trigger = "调度")
    {
        if (_locked) return;

        // ===== 自检：没有任何认证方式时，绝不锁屏 =====
        // 否则一旦锁上就再也解不开（连设置都进不去），这是最坏的结果。
        // 放在 EngageLock 里是因为它是所有上锁路径的唯一收口点，
        // 挡在这里才不会漏掉某个入口。
        if (!HasAnyAuthMethod())
        {
            _floatWindow?.SetSuppressed(false);
            WarnNoAuthBlocksLock(trigger);
            return;
        }

        _locked = true;

        // 注意别直接打印 verdict.Describe()——那描述的是"调度当时的判定"，
        // 手动上锁时调度可能正判定为"解锁"（比如课程已结束），
        // 于是日志会出现"上锁：解锁"这种自相矛盾的话。这里只取判定依据做补充说明。
        Log.Info($"上锁［{trigger}］依据：{verdict.Reason}");

        _lockWindow = new LockWindow(_cfg, _auth);
        _lockWindow.AttachEngine(_engine);
        _lockWindow.Unlocked += OnAuthenticated;
        _lockWindow.SettingsRequested += () => OpenSettingsRequireAuth();

        // 窗口必须先有句柄才能装钩子和置顶
        _lockWindow.Show();

        _lock = new TopMostLock(_lockWindow);
        _lock.Engage(_cfg);
        _diagHook = _lock.HookInstalled;
        _diagCapture = _lock.CaptureProtectionApplied;

        // 悬浮“下课”按钮在锁屏期间一律隐藏：
        // 它的语义是“手动上锁”，锁屏后再留着没有意义，
        // 也会在锁屏层上多出一个视觉干扰。
        _floatWindow?.SetSuppressed(true);

        // 锁屏/解锁不再放提示音：老师上课时会很突兀。
        // （Alerts.SoundEnabled 保留在配置里以便兼容，当前不再有任何播放点）

        Log.Info($"锁屏已生效：置顶={_lock.TopMostApplied} 钩子={_lock.HookInstalled} " +
                 $"捕获保护={_lock.CaptureProtectionApplied}");
    }

    private void ReleaseLock(string reason)
    {
        if (!_locked) return;
        _locked = false;

        Log.Info($"解锁：{reason}");

        _lock?.Dispose();
        _lock = null;

        if (_lockWindow is not null)
        {
            _lockWindow.StopTimers();
            _lockWindow.Unlocked -= OnAuthenticated;
            _lockWindow.Hide();
            _lockWindow.Close();
            _lockWindow = null;
        }

        _floatWindow?.SetSuppressed(false);
        // 解锁提示音已取消，保持安静
    }

    /// <summary>
    /// 悬浮按钮位置/缩起状态变了：写回配置。
    /// 拖动可能很频繁，所以做个轻量节流——只在停下来后写一次。
    /// </summary>
    private DispatcherTimer? _layoutSaveTimer;

    private void OnFloatLayoutChanged()
    {
        _layoutSaveTimer?.Stop();
        _layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _layoutSaveTimer.Tick += (_, _) =>
        {
            _layoutSaveTimer?.Stop();
            _layoutSaveTimer = null;
            try
            {
                ConfigStore.Save(_cfg);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存悬浮按钮位置失败：{ex.Message}");
            }
        };
        _layoutSaveTimer.Start();
    }

    // ---------------- 事件处理 ----------------

    private void OnAuthenticated(AuthMethod method)
    {
        Log.Info($"认证解锁成功，方式={method}");

        var minutes = _cfg.Auth.UnlockDurationMinutes;
        if (minutes > 0)
        {
            _manualUnlockUntil = DateTime.Now.AddMinutes(minutes);
            Log.Info($"本次解锁将在 {minutes} 分钟后重新交还调度");
        }

        ReleaseLock($"认证通过（{method}）");
    }

    /// <summary>
    /// 用户在悬浮按钮上确认了「下课」——立即上锁。
    ///
    /// 注意语义：这个按钮是**手动上锁**入口，不是解锁。
    /// 作息表仍然负责自动锁/自动解锁；这里只是让老师在下课离开时能主动锁上。
    /// </summary>
    private void OnClassDismissed()
    {
        if (_floatWindow is null) return;

        if (_locked)
        {
            // 已经锁着就不用再锁了（正常流程走不到这里，防御性处理）
            _floatWindow.ShowBubble("当前已经是锁定状态");
            return;
        }

        Log.Audit("下课按钮", true, "教师确认下课，手动上锁");

        // 清掉手动解锁的压制，否则刚锁上就会被"未到时间"逻辑挡回去
        _manualUnlockUntil = null;

        EngageLock(_engine.Evaluate(DateTime.Now), "下课按钮");
    }

    private void OpenSettingsRequireAuth(string? gateReason = null)
    {
        // 已经解锁状态下也要过认证门——否则学生只要等到下课就能直接翻设置改配置。
        // 锁屏状态下更严格：必须先解除锁定。
        if (_locked)
        {
            Log.Audit("设置门", false, "锁屏状态下请求进入设置，已拒绝");
            AppDialog.Warn(null,
                "请先在锁屏界面通过认证解除锁定，然后再进入设置。",
                "需要认证");
            return;
        }

        // 认证门：U 盘 / 密码 / 动态码 / 应急码，按配置显示可用的方式。
        // 注意：零认证时 Require 会直接放行（否则用户被永久锁在设置外），
        // 所以"自检引导配置"这条路径一定能进得去。
        //
        // ⚠️ 关键：弹认证门之前必须**暂停锁屏的置顶重申**。
        //    锁屏窗口是 Topmost，而且 TopMostLock 每 200ms 会把它重新提到最前，
        //    这会把认证弹窗压在下面 —— 表现为"弹窗看不见/点不到，卡在锁屏界面"。
        //    这里暂停，弹窗关闭后再恢复。
        bool gatePassed;
        _lock?.Pause();
        try
        {
            var gateOwner = _floatWindow is { IsVisible: true } ? _floatWindow : null;
            gatePassed = SettingsAuthGate.Require(gateOwner, _cfg, _auth,
                gateReason ?? "进入设置会修改锁屏规则，请先验证身份");
        }
        finally
        {
            // 无论通过与否都要恢复锁屏置顶，否则锁屏会失去保护
            _lock?.Resume();
        }

        if (!gatePassed)
        {
            Log.Audit("设置门", false, "认证未通过，设置未打开");
            return;
        }

        Log.Audit("设置门", true, "认证通过，打开设置");

        // 复用已打开的窗口
        if (_settingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        // ⚠️ 关键：字段里可能残留一个**已关闭**的窗口对象。
        //   WPF 的窗口一旦 Close 就不能再 Show（会抛 InvalidOperationException）。
        //   所以这里必须同时判断"是否存在"和"是否还能用"，不能只看 null。
        if (_settingsWindow is not null && !IsWindowReusable(_settingsWindow))
        {
            Log.Info("设置窗口已被关闭，重新创建");
            _settingsWindow = null;
        }

        // 预热过就直接复用，避免“点开设置卡半秒”
        _lock?.Pause();

        _settingsWindow ??= CreateSettingsWindow();

        _settingsWindow.Show();
        _settingsWindow.Activate();
        PushDiagnostics();

        Log.Info("打开设置界面");
    }

    /// <summary>
    /// 窗口是否还能复用。WPF 窗口关掉后不能再次 Show，
    /// 只能靠重新 new 一个。用 Dispatcher 是否已关闭来判断。
    /// </summary>
    private static bool IsWindowReusable(Window w)
    {
        try
        {
            return !w.Dispatcher.HasShutdownStarted && !w.Dispatcher.HasShutdownFinished;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 统一创建设置窗口并挂好事件。
    /// 所有创建路径都必须走这里——之前预热路径漏挂了 Closed，
    /// 导致关窗后字段残留一个死对象，再次打开设置就崩。
    /// </summary>
    private SettingsWindow CreateSettingsWindow()
    {
        var win = new SettingsWindow(_cfg, _auth);
        win.ConfigSaved += OnConfigSaved;
        win.DiagnosticsRequested += PushDiagnostics;
        win.Closed += (_, _) =>
        {
            PushDiagnosticsTo(win);
            // 只有还是当前实例时才清空，避免误清掉后来新建的窗口
            if (ReferenceEquals(_settingsWindow, win)) _settingsWindow = null;
            _lock?.Resume();
        };
        return win;
    }

    /// <summary>启动后在空闲时预热设置窗口：把 XAML/BAML/JIT 的开销提前付掉，首次打开不再卡。</summary>
    private void WarmUpSettingsWindow()
    {
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                try
                {
                    // 必须走 CreateSettingsWindow()，否则事件（尤其 Closed）挂不上，
                    // 关窗后 _settingsWindow 会残留死对象，下次打开直接崩。
                    _settingsWindow ??= CreateSettingsWindow();
                }
                catch (Exception ex)
                {
                    Log.Info($"预热设置窗口失败（不影响使用）：{ex.Message}");
                }
            }));
    }

    private void OnConfigSaved(AppConfig newCfg)
    {
        Log.Info("应用新配置");
        _cfg = newCfg;
        _engine = new ScheduleEngine(_cfg);
        _auth.UpdateConfig(_cfg);
        Log.Configure(_cfg.Logging.Directory, _cfg.Logging.RetentionDays, _cfg.Logging.Enabled);
        Log.SetDebugEnabled(_cfg.Logging.DebugVerbose);

        // 配置变了，之前"暂不配置认证方式"的决定要重新评估：
        // 配好了就该恢复正常锁屏；清空了则重新开始提醒。
        _noAuthWarnDismissed = false;
        _lastNoAuthWarnAt = null;

        var hasAuth = HasAnyAuthMethod();
        Log.Info(hasAuth
            ? "自检：已配置认证方式，锁屏功能可用"
            : "自检：仍未配置任何认证方式，上锁操作将继续被拦截");

        _floatWindow?.ApplyConfig(_cfg);
        _lockWindow?.UpdateConfig(_cfg);

        // 同步配置可能改了端口/开关，重建监听
        StartSyncBridge();

        // 热应用安全设置
        if (_lock is not null)
        {
            _lock.RemoveHook();
            if (_cfg.Security.BlockSystemHotkeys)
                _lock.Engage(_cfg);      // 重新装钩子
            else
                _lock.ApplyCaptureProtection(_cfg.Security.ExcludeFromCapture);
            _diagHook = _lock.HookInstalled;
            _diagCapture = _lock.CaptureProtectionApplied;
        }

        Tick();
    }

    /// <summary>把运行的实时状态回填到设置界面。</summary>
    private void PushDiagnostics() => PushDiagnosticsTo(_settingsWindow);

    private void PushDiagnosticsTo(SettingsWindow? win)
    {
        if (win is null)
        {
            return;
        }

        // 关键：解锁后 _lock 会被释放，三项自然为 false。
        // 这并不代表故障，所以要告诉界面窗“当前是否处于锁屏状态”，让它区分
        // “未生效”和“当前未锁屏、未启用”。
        win.UpdateDiagnostics(
            _lock?.TopMostApplied ?? false,
            _lock?.HookInstalled ?? false,
            _lock?.CaptureProtectionApplied ?? false,
            _lock?.BlockedKeyCount ?? 0,
            IsAdministrator(),
            _locked);
    }

    // ---------------- 看门狗 ----------------

    /// <summary>
    /// 看门狗：另起一个本进程实例带 --watchdog 参数，周期性检查主进程还在不在，
    /// 不在就把它拉起来。防止学生直接结束进程绕过锁屏。
    /// </summary>
    private void StartWatchdog()
    {
        if (!_cfg.Security.WatchdogEnabled) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Warn("看门狗启动失败：拿不到自身路径");
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                Arguments = $"--watchdog {Environment.ProcessId} {_cfg.Security.WatchdogIntervalSeconds}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            _watchdog = Process.Start(psi);
            Log.Info($"看门狗已启动 pid={_watchdog?.Id}");
        }
        catch (Exception ex)
        {
            Log.Warn($"看门狗启动失败：{ex.Message}");
        }
    }

    /// <summary>看门狗模式的入口（由 Program.Main 分流进来）。</summary>
    public static void RunWatchdog(int targetPid, int intervalSeconds)
    {
        try
        {
            var interval = Math.Clamp(intervalSeconds, 1, 3600);
            while (true)
            {
                Thread.Sleep(interval * 1000);
                Process target;
                try
                {
                    target = Process.GetProcessById(targetPid);
                }
                catch
                {
                    // 主进程没了，重新拉起
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                    }
                    return;
                }
                if (target.HasExited) return;
            }
        }
        catch
        {
            // 看门狗自身出错就安静退出，不影响主程序
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Shutdown()
    {
        Log.Info("课间锁退出");
        App.BeginShutdown();

        // 先杀看门狗，否则它会把我们重新拉起来
        KillWatchdog();

        _tickTimer?.Stop();
        StopSyncBridge();

        try
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
        catch { }

        try
        {
            if (_hotkeyHwnd != IntPtr.Zero)
                UnregisterHotKey(_hotkeyHwnd, HOTKEY_PANIC_ID);
        }
        catch { }

        _lock?.Dispose();
        _lockWindow?.StopTimers();
        _floatWindow?.StopTimer();

        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }

        Application.Current.Shutdown();
    }

    private void KillWatchdog()
    {
        try
        {
            if (_watchdog is { HasExited: false })
            {
                _watchdog.Kill();
                _watchdog.WaitForExit(2000);
            }
        }
        catch { }
        _watchdog = null;
    }
}

/// <summary>简单提示音。</summary>
// 提示音已全部取消：原先锁定/解锁会分别放 Hand / Asterisk 两个系统音，
// 老师反馈“弹窗提示时还有响”，其实响的是这两个。现在整个程序不主动发声。
