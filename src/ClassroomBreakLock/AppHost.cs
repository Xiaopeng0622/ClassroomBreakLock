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
    private AppConfig _cfg;
    private ScheduleEngine _engine;
    private AuthService _auth;
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

    /// <summary>测试模式下为 true：调度不自动上锁，只能手动触发。</summary>
    private bool _forceUnlockedByTestMode;   // 保留字段以免遗漏引用

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
        WarmUpSettingsWindow();
        if (_cfg.TestMode)
            _floatWindow.ShowBubble("测试模式：不会自动锁屏\n双击图标可进设置", 10);
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
                            null, null, null, null));
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
        EngageLock(_engine.Evaluate(DateTime.Now));
    }

    private void ForceUnlockWithConfirm()
    {
        if (!_locked) return;
        if (!AppDialog.Confirm(null,
                "确定要强制解除锁定吗？此操作会记入日志。",
                "强制解锁", "解锁", "取消", danger: true))
        {
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
            // 装了「允许点击悬浮下课按钮直接解锁」时，锁屏期间必须保留按钮（第二解锁入口）。
            // 之前这里无条件隐藏，而每秒 tick 都会重跑一次，
            // 所以双击永远来不及——看得到按钮也点不动。
            _floatWindow.SetSuppressed(!_cfg.Auth.AllowButtonUnlockWithoutAuth);
            return;
        }

        _floatWindow.SetSuppressed(false);

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

    private void EngageLock(ScheduleVerdict verdict)
    {
        if (_locked) return;
        _locked = true;

        Log.Info($"上锁：{verdict.Describe()}");

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

        // 悬浮“下课”按钮：
        //   开了「允许点击悬浮下课按钮直接解锁」-> 锁屏期间也留在最上层，作为第二解锁入口
        //   否则按原设计隐藏，避免给出一条无凭据的绕过通道
        if (_cfg.Auth.AllowButtonUnlockWithoutAuth && _floatWindow is not null)
        {
            _floatWindow.SetSuppressed(false);
            _lock.KeepAbove(new System.Windows.Interop.WindowInteropHelper(_floatWindow).Handle);
            Log.Info("锁屏期间保留悬浮“下课”按钮（已允许免凭据解锁）");
        }
        else
        {
            _floatWindow?.SetSuppressed(true);
        }

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

    private void OnClassDismissed()
    {
        if (_floatWindow is null) return;

        // 未锁屏时点“下课”：之前是直接 return，点什么都没反应。
        // 现在改为“记录本节课已下课”，并压制接下来一段时间的自动上锁。
        if (!_locked)
        {
            var minutes = _cfg.Auth.UnlockDurationMinutes > 0 ? _cfg.Auth.UnlockDurationMinutes : 10;
            _manualUnlockUntil = DateTime.Now.AddMinutes(minutes);
            Log.Info($"教师点击“下课”（未锁屏）：接下来 {minutes} 分钟内不自动上锁");
            _floatWindow.ShowBubble($"已记录下课：接下来 {minutes} 分钟内不会自动上锁", 5);
            return;
        }

        if (!_cfg.Auth.AllowButtonUnlockWithoutAuth)
        {
            _floatWindow.ShowBubble("已设置为需认证解锁，请使用锁屏上的认证方式");
            return;
        }

        Log.Audit("下课按钮", true, "教师点击悬浮按钮宣告下课");
        var minutes2 = _cfg.Auth.UnlockDurationMinutes;
        if (minutes2 > 0) _manualUnlockUntil = DateTime.Now.AddMinutes(minutes2);
        ReleaseLock("教师点击“下课”按钮");
    }

    private void OpenSettingsRequireAuth()
    {
        // 已经解锁状态下才允许进设置；锁屏状态下必须先在锁屏上通过认证
        if (_locked)
        {
            Log.Warn("锁屏状态下请求进入设置被拒绝，需先通过认证");
            AppDialog.Warn(null,
                "请先在锁屏界面通过任意一种认证方式解锁，然后再进入设置。",
                "需要认证");
            return;
        }

        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        // 预热过就直接复用，避免“点开设置卡半秒”
        _lock?.Pause();

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_cfg, _auth);
            _settingsWindow.ConfigSaved += OnConfigSaved;
            _settingsWindow.DiagnosticsRequested += PushDiagnostics;
            _settingsWindow.Closed += (_, _) =>
            {
                PushDiagnosticsTo(_settingsWindow);
                _settingsWindow = null;
                _lock?.Resume();
            };
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
        PushDiagnostics();

        Log.Info("打开设置界面");
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
                    _settingsWindow ??= new SettingsWindow(_cfg, _auth);
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
