using System.Runtime.InteropServices;
using System.Text;

namespace ClassroomBreakLock.Interop;

/// <summary>
/// Win32 原生互操作：置顶压盖、屏蔽系统热键、捕获保护。
/// 这是"高优先级锁屏"的技术核心。
/// </summary>
internal static class NativeMethods
{
    // ---------------- 窗口 Z 序 / 置顶 ----------------

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;      // 不出现在 Alt+Tab 列表
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ---------------- 捕获保护（防截屏/防录屏/防投屏） ----------------

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Win10 2004+

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    // ---------------- 屏蔽系统热键（低级键盘钩子） ----------------

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int VK_TAB = 0x09;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_F4 = 0x73;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;   // Alt
    public const int VK_SHIFT = 0x10;
    public const int VK_DELETE = 0x2E;
    public const int VK_LBUTTON = 0x01;

    /// <summary>某个虚拟键当前是否按下（用于拖动轮询，绕开 WPF 的鼠标捕获）。</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>鼠标左键当前是否按下。</summary>
    public static bool IsLeftButtonDown() => IsKeyDown(VK_LBUTTON);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // ---------------- 光标 / 屏幕 ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>取窗口的物理像素矩形。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    public static extern bool ShowCursor(bool bShow);

    // ---------------- 电源 / 会话 ----------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    public const uint EWX_LOGOFF = 0x00000000;
    public const uint EWX_SHUTDOWN = 0x00000001;
    public const uint EWX_REBOOT = 0x00000002;
    public const uint EWX_FORCE = 0x00000004;

    // ---------------- 窗口可见性 / 虚拟桌面 ----------------

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;

    /// <summary>
    /// 查询窗口是否被 DWM 遮蔽（"cloaked"）。
    ///
    /// 切换虚拟桌面后，不在当前桌面上的窗口会被 DWM 标记为 cloaked：
    /// 此时 IsWindowVisible() 仍返回 true，但窗口实际不可见。
    /// 只靠 WPF 的 Visibility 判断不出这种情况，所以必须查这个属性。
    ///
    /// 另外，应用挂起时也会被 cloaked，所以调用方要结合自身状态判断。
    /// </summary>
    public static bool IsWindowCloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            // DWMWA_CLOAKED = 14
            var result = DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int));
            return result == 0 && cloaked != 0;
        }
        catch
        {
            // dwmapi 不可用（极老系统）时按"未遮蔽"处理
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---------------- 单实例互斥 ----------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateMutex(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll")]
    public static extern uint GetLastError();

    public const uint ERROR_ALREADY_EXISTS = 183;

    /// <summary>把一个窗口钉到最顶层。</summary>
    public static void PinTopMost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// 直接把窗口移到指定的**物理像素**位置，同时设置尺寸。
    ///
    /// 为什么不用 WPF 的 Window.Left/Top：
    ///   混合 DPI 多屏下，WPF 的 Left/Top 属于"逻辑坐标"，
    ///   它在跨屏时的换算规则复杂且受窗口当前所在屏的 DPI 影响，
    ///   实测会出现"代码算对了，窗口却落在别处"的情况。
    ///   直接用 SetWindowPos 给物理坐标最可靠，所见即所得。
    /// </summary>
    public static bool MoveToPhysical(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOZORDER);
    }

    /// <summary>
    /// 只移动、不改尺寸（带 SWP_NOSIZE）。
    ///
    /// 拖动时每帧都调这个：省略尺寸参数可以避免窗口重排，
    /// 明显比连尺寸一起设更流畅。
    /// </summary>
    public static bool MoveOnlyPhysical(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOZORDER | SWP_NOSENDCHANGING);
    }

    // ============================================================
    //  【已废弃】原生模态拖动 —— 保留此说明避免以后有人再走这条路
    //
    //  曾用 SendMessage(WM_NCLBUTTONDOWN, HTCAPTION) 让系统接管窗口拖动，
    //  想借此获得"硬件搬移"的顺滑。实测结果相反：
    //
    //    · 系统移动循环会跑一个**嵌套消息泵**，与 WPF 的 Dispatcher 抢消息，
    //      表现为拖动一顿一顿、甚至要使劲拖才动；
    //    · 而且它会吞掉鼠标抬起，导致 Button 的 Click 不触发
    //      （"单击无法锁定下课"就是这么来的）。
    //
    //  现在改用自绘轮询（见 FloatingButtonWindow.StartDragPolling）：
    //  用 DispatcherTimer 每 16ms 读光标 → SetWindowPos，不阻塞消息泵。
    //
    //  结论：**不要**再用 WM_NCLBUTTONDOWN / HTCAPTION 拖这个窗口。
    // ============================================================

    /// <summary>判断当前前台窗口是不是我们自己。</summary>
    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;
}
