using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BreakLockVerify;

/// <summary>
/// DWM 遮蔽检测的实机验证。
///
/// 虚拟桌面切换会让窗口被 DWM cloak，此时 IsWindowVisible 仍为 true，
/// 只有 DWMWA_CLOAKED 能反映真实可见性。这里直接对那个 P/Invoke 做真机测试，
/// 确认它在当前系统上可用（返回 0 且能读出值）。
/// </summary>
public static class WindowVisibilityChecks
{
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== DWM 遮蔽检测 API 可用性 ===");

        var desktop = GetDesktopWindow();
        Check("GetDesktopWindow 返回有效句柄", desktop != IntPtr.Zero,
            $"0x{desktop.ToInt64():X}");

        // 对桌面窗口查询 cloaked：API 应返回成功（0）
        var hr = DwmGetWindowAttribute(desktop, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int));
        Check("DwmGetWindowAttribute(DWMWA_CLOAKED) 调用成功", hr == 0, $"HRESULT=0x{hr:X8}");
        Check("桌面窗口未被遮蔽（应读到 0）", cloaked == 0, $"cloaked={cloaked}");

        // 无效句柄应返回失败，不能崩
        var hrBad = DwmGetWindowAttribute(IntPtr.Zero, 14, out _, sizeof(int));
        Check("无效句柄不崩溃（返回失败码）", hrBad != 0, $"HRESULT=0x{hrBad:X8}");

        Console.WriteLine();
        Console.WriteLine("=== 与课堂锁相同的判定路径 ===");
        // 走 ClassroomBreakLock.Interop.NativeMethods 里的实现
        Check("NativeMethods.IsWindowCloaked(桌面窗口) == false",
            !ClassroomBreakLock.Interop.NativeMethods.IsWindowCloaked(desktop));
        Check("NativeMethods.IsWindowCloaked(Zero) == false（防御）",
            !ClassroomBreakLock.Interop.NativeMethods.IsWindowCloaked(IntPtr.Zero));
        Check("NativeMethods.IsWindowVisible(桌面窗口) == true",
            ClassroomBreakLock.Interop.NativeMethods.IsWindowVisible(desktop));
        Check("IsWindow(无效句柄) == false", !IsWindow(new IntPtr(0x1234)));

        return failures;
    }
}
