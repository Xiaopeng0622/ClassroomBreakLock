using System.Windows;
using System.Windows.Interop;
using ClassroomBreakLock.Config;

namespace ClassroomBreakLock.Interop;

/// <summary>
/// 把窗口变成"钉死"的锁屏层：
///   1. TOPMOST 置顶 + 定时重申（防止其它程序抢 Z 序）
///   2. 屏蔽 Alt+Tab / Win / Alt+F4 / Ctrl+Esc 等系统热键
///   3. 抢回前台焦点（一旦被别的窗口抢走就夺回来）
///   4. 可选：从截屏/录屏/投屏中排除
/// </summary>
internal sealed class TopMostLock : IDisposable
{
    private readonly Window _window;
    private readonly System.Windows.Threading.DispatcherTimer _reassertTimer;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;   // 必须保活，否则被 GC 回收后钩子失效
    private IntPtr _hookId = IntPtr.Zero;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _disposed;
    private bool _paused;
    private readonly List<IntPtr> _keepAbove = new();

    /// <summary>
    /// 声明“要盖在锁屏层之上”的窗口（悬浮下课按钮）。
    /// 每次重申 Z 序时，先把自己钉到最顶，再把这些窗口钉到更顶，保证它们始终可点。
    /// </summary>
    public void KeepAbove(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && !_keepAbove.Contains(hwnd))
        {
            _keepAbove.Add(hwnd);
        }
    }

    /// <summary>钩子是否安装成功。</summary>
    public bool HookInstalled => _hookId != IntPtr.Zero;

    /// <summary>置顶是否生效（由最近一次 SetWindowPos 结果决定）。</summary>
    public bool TopMostApplied { get; private set; }

    /// <summary>捕获保护是否生效。</summary>
    public bool CaptureProtectionApplied { get; private set; }

    /// <summary>被屏蔽热键的拦截计数，用于自检。</summary>
    public int BlockedKeyCount { get; private set; }

    public TopMostLock(Window window)
    {
        _window = window;
        // 200ms 重申一次：绝大多数"抢置顶"的程序都撑不过这个节奏
        _reassertTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _reassertTimer.Tick += (_, _) => Reassert();
    }

    public void Engage(AppConfig config)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;

        // 1) 扩展样式：不进 Alt+Tab 列表（比钩子更彻底，即使钩子失效也不会被切走看到）
        var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);

        // 2) 置顶
        NativeMethods.PinTopMost(_hwnd);
        TopMostApplied = true;

        // 3) 捕获保护
        if (config.Security.ExcludeFromCapture)
            ApplyCaptureProtection(true);

        // 4) 键盘钩子
        if (config.Security.BlockSystemHotkeys)
            InstallHook();

        // 打开设置期间要保持暂停状态，别把前台窗口抢回来
        if (!_paused)
        {
            Resume();
        }
    }

    /// <summary>暂停 200ms 的 Z 序重申。打开设置等其它窗口期间调用，避免和前台窗口互相抢焦点造成卡顿。</summary>
    public void Pause()
    {
        _paused = true;
        _reassertTimer.Stop();
    }

    /// <summary>恢复 Z 序重申。</summary>
    public void Resume()
    {
        _paused = false;
        if (!_disposed)
        {
            _reassertTimer.Start();
        }
    }

    public void ApplyCaptureProtection(bool enable)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ok = NativeMethods.SetWindowDisplayAffinity(_hwnd,
            enable ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        if (!ok && enable)
        {
            // Win10 2004 以下不支持 EXCLUDEFROMCAPTURE，退回 MONITOR
            ok = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_MONITOR);
        }
        CaptureProtectionApplied = ok && enable;
    }

    private void Reassert()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;

        NativeMethods.PinTopMost(_hwnd);

        // 悬浮按钮等“必须留在最上面”的窗口，要在锁屏层之后再钉一次
        foreach (IntPtr hwnd in _keepAbove)
        {
            NativeMethods.PinTopMost(hwnd);
        }

        // 如果前台被别的窗口抢走，抢回来
        if (!IsKeptWindowForeground() && !NativeMethods.IsForeground(_hwnd))
        {
            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.PinTopMost(_hwnd);
        }
    }

    private bool IsKeptWindowForeground()
    {
        foreach (IntPtr hwnd in _keepAbove)
        {
            if (NativeMethods.IsForeground(hwnd))
            {
                return true;   // 前台是自己的悬浮按钮，别去抢焦点，否则点不下去
            }
        }

        return false;
    }

    private void InstallHook()
    {
        if (_hookId != IntPtr.Zero) return;
        _hookProc = HookCallback;
        var module = NativeMethods.GetModuleHandle(null);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, module, 0);
    }

    public void RemoveHook()
    {
        if (_hookId == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = System.Runtime.InteropServices.Marshal
                .PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();
            bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

            if (isDown && ShouldBlock(info.vkCode))
            {
                BlockedKeyCount++;
                return new IntPtr(1); // 吞掉，系统收不到
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(uint vk)
    {
        // Win 键（左右）
        if (vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN) return true;

        bool altDown = IsDown(NativeMethods.VK_MENU);
        bool ctrlDown = IsDown(NativeMethods.VK_CONTROL);
        bool shiftDown = IsDown(NativeMethods.VK_SHIFT);

        // Alt+Tab / Alt+Esc / Alt+F4
        if (altDown && vk is NativeMethods.VK_TAB or NativeMethods.VK_ESCAPE or NativeMethods.VK_F4)
            return true;

        // Ctrl+Esc（开始菜单）
        if (ctrlDown && vk == NativeMethods.VK_ESCAPE) return true;

        // Ctrl+Shift+Esc（任务管理器）
        if (ctrlDown && shiftDown && vk == NativeMethods.VK_ESCAPE) return true;

        // Ctrl+Alt+Del 无法通过钩子屏蔽（系统级 SAS），需靠策略，见 README

        return false;
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reassertTimer.Stop();
        RemoveHook();
    }
}
