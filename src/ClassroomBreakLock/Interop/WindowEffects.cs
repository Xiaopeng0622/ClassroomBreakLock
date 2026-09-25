using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassroomBreakLock.Interop;

/// <summary>
/// Win11 视觉能力的薄封装：窗口圆角 + 标题栏深色。
/// 全部为"尽力而为"——系统不支持或调用失败时静默跳过，不影响功能。
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>强制 Win11 圆角（系统默认不圆角时也生效）。</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        int value = DwmwcpRound;
        TrySet(window, DwmwaWindowCornerPreference, value);
    }

    /// <summary>浅色界面下把标题栏文字变深，避免深色标题栏与浅色窗口割裂。</summary>
    public static void ApplyLightTitleBar(Window window)
    {
        int value = 0;   // FALSE = 浅色标题栏
        TrySet(window, DwmwaUseImmersiveDarkMode, value);
    }

    private static void TrySet(Window window, int attribute, int value)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // 老系统没有这些属性，忽略即可
        }
    }
}
