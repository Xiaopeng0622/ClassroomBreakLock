// 拖动诊断工具：定位悬浮按钮窗口 → 模拟鼠标拖动 → 输出结果
// 用法：powershell -File tools\drag_probe.ps1
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class DragProbe
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    public class WinInfo { public long H; public int X, Y, W, Ht; public string Cls; }

    public static List<WinInfo> List(uint want)
    {
        var res = new List<WinInfo>();
        EnumWindows((h, l) =>
        {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == want)
            {
                var sb = new StringBuilder(256); GetClassName(h, sb, 256);
                if (sb.ToString().Contains("HwndWrapper[ClassroomBreakLock") && IsWindowVisible(h))
                {
                    RECT r; GetWindowRect(h, out r);
                    res.Add(new WinInfo { H = h.ToInt64(), X = r.L, Y = r.T, W = r.R - r.L, Ht = r.B - r.T,
                                          Cls = sb.ToString().Substring(0, Math.Min(38, sb.Length)) });
                }
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    /// <summary>平滑移动光标（分步），产生真实的 WM_MOUSEMOVE 序列</summary>
    public static void GlideTo(int fromX, int fromY, int toX, int toY, int steps, int delayMs)
    {
        for (int i = 1; i <= steps; i++)
        {
            int x = fromX + (toX - fromX) * i / steps;
            int y = fromY + (toY - fromY) * i / steps;
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(delayMs);
        }
    }

    /// <summary>
    /// 模拟一次真实拖动：从别处平滑滑入 → 按下 → 分步拖动 → 松开。
    /// 注意不能直接 SetCursorPos 瞬移到目标再按下：瞬移不产生移动轨迹，
    /// 有些窗口不会因此收到完整的鼠标消息序列。
    /// </summary>
    public static void SimulateDrag(int fromX, int fromY, int toX, int toY, int steps)
    {
        // 1) 先滑入目标附近，让窗口收到 MouseEnter / WM_MOUSEMOVE
        GlideTo(fromX, fromY, fromX, fromY, 1, 50);
        System.Threading.Thread.Sleep(150);

        // 2) 按下
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);

        // 3) 分步拖动
        for (int i = 1; i <= steps; i++)
        {
            int x = fromX + (toX - fromX) * i / steps;
            int y = fromY + (toY - fromY) * i / steps;
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(50);
        }

        System.Threading.Thread.Sleep(150);

        // 4) 松开
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(250);
    }

    /// <summary>完整流程：滑入 → 按下 → 拖动 → 松开</summary>
    public static void FullDrag(int approachX, int approachY,
                                int targetX, int targetY,
                                int toX, int toY, int steps)
    {
        // 从别处滑到按钮上（产生真实的进入轨迹）
        GlideTo(approachX, approachY, targetX, targetY, 12, 25);
        System.Threading.Thread.Sleep(200);

        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);

        for (int i = 1; i <= steps; i++)
        {
            int x = targetX + (toX - targetX) * i / steps;
            int y = targetY + (toY - targetY) * i / steps;
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(50);
        }

        System.Threading.Thread.Sleep(200);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(300);
    }

    /// <summary>光标当前落在哪个窗口上（用于确认按钮是否真的可点中）</summary>
    public static long WindowUnderPoint(int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        return WindowFromPoint(p).ToInt64();
    }
}
