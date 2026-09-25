using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Interop;

/// <summary>一块显示器的信息。</summary>
/// <param name="Bounds">物理像素下的完整区域。</param>
/// <param name="WorkArea">物理像素下的工作区（已扣掉任务栏）。</param>
/// <param name="DpiScale">该屏自身的 DPI 缩放（1.0 = 100%）。</param>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    Rect Bounds,
    Rect WorkArea,
    double DpiScale)
{
    public string Display =>
        $"{(IsPrimary ? "主屏" : $"屏 {Index + 1}")} · {DeviceName}" +
        $" · {Bounds.Width:0}×{Bounds.Height:0}" +
        (Math.Abs(DpiScale - 1.0) > 0.01 ? $" @{DpiScale * 100:0}%" : "");

    public override string ToString() => Display;
}

/// <summary>
/// 多显示器枚举与定位。
///
/// 【坐标空间约定 —— 这是踩过坑的地方，务必先读懂】
///
/// Windows 有两套坐标：
///   1. **物理像素**：EnumDisplayMonitors / GetMonitorInfo / GetCursorPos 用的。
///      多屏拼接也在这套坐标里（副屏在主屏右边就是 x=1920 起）。
///   2. **WPF 的逻辑单位**：Window.Left / Top 用的。
///
/// 关键点：WPF 的逻辑坐标**不是**"物理像素 ÷ 该屏自己的 DPI"，
/// 而是以**主屏 DPI 为基准**的虚拟坐标空间（除非进程声明了 PerMonitorV2 且窗口已在目标屏上）。
///
/// 这个区别在混合 DPI 多屏下会直接导致窗口跑到屏幕外：
///   副屏物理 x=1920、缩放 250%、主屏 100%
///     错误算法 1920 / 2.5 = 768   ← 窗口跑到主屏中间甚至屏外
///     正确结果 1920                ← 因为基准是主屏的 100%
///
/// 之前的实现就是每块屏各除自己的 scale，于是 250% 的副屏坐标被压缩到 40%，
/// 悬浮按钮就跑到看不见的地方去了。
///
/// 所以这里的约定是：
///   - MonitorInfo 里的 Bounds / WorkArea **一律保存物理像素**，不做缩放
///   - 只在真正给 Window.Left/Top 赋值时，用 MonitorService.ToWpfUnits() 换算
/// </summary>
public static class MonitorService
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>枚举所有显示器。Index 顺序与 Windows 显示设置一致（主屏 0）。</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var raw = new List<(string Device, bool Primary, RECT Monitor, RECT Work, double Scale)>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc,
                ref RECT lprcMonitor, IntPtr data) =>
            {
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(hMonitor, ref mi)) return true;

                var scale = 1.0;
                try
                {
                    if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                        scale = dpiX / 96.0;
                }
                catch
                {
                    // shcore 在 Win8.1 以下不存在，退回 100%
                }

                raw.Add((
                    DeviceNameOf(hMonitor),
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    mi.rcMonitor,
                    mi.rcWork,
                    scale));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举显示器失败：{ex.Message}");
        }

        // 兜底：至少要有一块屏，否则整个定位逻辑没依据
        if (raw.Count == 0)
        {
            var wa = SystemParameters.WorkArea;
            raw.Add(("PRIMARY", true,
                new RECT { Left = (int)wa.Left, Top = (int)wa.Top, Right = (int)wa.Right, Bottom = (int)wa.Bottom },
                new RECT { Left = (int)wa.Left, Top = (int)wa.Top, Right = (int)wa.Right, Bottom = (int)wa.Bottom },
                1.0));
        }

        // 主屏排第一，其余按屏幕坐标从左到右、从上到下
        var ordered = raw
            .OrderByDescending(r => r.Primary)
            .ThenBy(r => r.Monitor.Left)
            .ThenBy(r => r.Monitor.Top)
            .ToList();

        var result = new List<MonitorInfo>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            // 坐标保持物理像素，不在这里做 DPI 换算（见类注释的长说明）
            result.Add(new MonitorInfo(
                i,
                r.Device,
                r.Primary,
                ToRect(r.Monitor),
                ToRect(r.Work),
                r.Scale));
        }
        return result;
    }

    /// <summary>RECT → Rect（原样转换，坐标空间不变）。</summary>
    private static Rect ToRect(RECT r)
        => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>
    /// 主屏的 DPI 缩放（带缓存）。
    ///
    /// ⚠️ 不要在高频路径里直接调 enumerate —— GetPrimaryScale 内部会枚举所有显示器，
    /// 每帧调用会造成明显卡顿（拖动时实测卡顿的主因）。
    /// 这里缓存结果，显示器配置变化时由 InvalidateCache() 失效。
    /// </summary>
    public static double GetPrimaryScale()
    {
        if (_cachedPrimaryScale > 0) return _cachedPrimaryScale;

        try
        {
            foreach (var m in Enumerate())
            {
                if (m.IsPrimary)
                {
                    _cachedPrimaryScale = m.DpiScale <= 0 ? 1.0 : m.DpiScale;
                    return _cachedPrimaryScale;
                }
            }
        }
        catch { }

        _cachedPrimaryScale = 1.0;
        return _cachedPrimaryScale;
    }

    private static double _cachedPrimaryScale;

    /// <summary>显示器配置变化后调用，让缓存的缩放值失效。</summary>
    public static void InvalidateCache()
    {
        _cachedPrimaryScale = 0;
        _cachedMonitors = null;
    }

    private static List<MonitorInfo>? _cachedMonitors;

    /// <summary>
    /// 带缓存的显示器枚举。高频调用方（拖动、tick）应使用这个，
    /// 只有在收到 DisplaySettingsChanged 后才需要重新枚举。
    /// </summary>
    public static List<MonitorInfo> EnumerateCached()
        => _cachedMonitors ??= Enumerate();

    /// <summary>
    /// 物理像素 → WPF 逻辑单位（按主屏 DPI 基准）。
    /// 给 Window.Left/Top 赋值前必须过这一步。
    /// </summary>
    public static double ToWpfUnits(double physicalPixels, double primaryScale = 0)
    {
        if (primaryScale <= 0) primaryScale = GetPrimaryScale();
        if (primaryScale <= 0) primaryScale = 1.0;
        return physicalPixels / primaryScale;
    }

    /// <summary>WPF 逻辑单位 → 物理像素（按主屏 DPI 基准）。</summary>
    public static double ToPhysicalPixels(double wpfUnits, double primaryScale = 0)
    {
        if (primaryScale <= 0) primaryScale = GetPrimaryScale();
        if (primaryScale <= 0) primaryScale = 1.0;
        return wpfUnits * primaryScale;
    }

    private static string DeviceNameOf(IntPtr hMonitor)
    {
        try
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                // MONITORINFOEX 才有 szDevice，这里用简单序号兜底
                return $"MONITOR{hMonitor.ToInt64():X}";
            }
        }
        catch { }
        return "UNKNOWN";
    }

    /// <summary>
    /// 按配置挑出目标显示器。
    ///   TargetScreenIndex >= 0 → 强制用那一块（越界则回落）
    ///   TargetScreenIndex == -1 → 自动：优先用 LastScreenDeviceName 匹配的屏，否则主屏
    /// </summary>
    public static MonitorInfo Resolve(AppConfig cfg, List<MonitorInfo>? monitors = null)
    {
        monitors ??= Enumerate();
        var fb = cfg.FloatingButton;

        // 手动指定
        if (fb.TargetScreenIndex >= 0)
        {
            if (fb.TargetScreenIndex < monitors.Count)
                return monitors[fb.TargetScreenIndex];

            Log.Warn($"指定的显示器序号 {fb.TargetScreenIndex} 超出范围（共 {monitors.Count} 块），" +
                     (fb.FallbackToPrimary ? "回落到主屏" : "回落到主屏（无其它选择）"));
            return monitors.First(m => m.IsPrimary);
        }

        // 自动：按上次记录的位置找
        if (!string.IsNullOrEmpty(fb.LastScreenDeviceName))
        {
            var hit = monitors.FirstOrDefault(m => m.DeviceName == fb.LastScreenDeviceName);
            if (hit is not null) return hit;
        }

        // 主屏优先，否则第一块
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>找出一个点落在哪块屏上（用于拖动后判断按钮去了哪块屏）。</summary>
    public static MonitorInfo? HitTest(List<MonitorInfo> monitors, Point point)
    {
        // 用按钮中心点判断更稳：跨屏拖动时边缘容易落在缝隙里
        return monitors.FirstOrDefault(m => m.Bounds.Contains(point))
            ?? monitors.OrderBy(m => DistanceTo(m.Bounds, point)).FirstOrDefault();
    }

    private static double DistanceTo(Rect r, Point p)
    {
        var dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
        var dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>把窗口约束到指定屏的工作区内，并返回吸附后的位置。</summary>
    public static Point ClampToWorkArea(MonitorInfo monitor, double width, double height, Point desired)
    {
        var wa = monitor.WorkArea;
        var x = Math.Clamp(desired.X, wa.Left, Math.Max(wa.Left, wa.Right - width));
        var y = Math.Clamp(desired.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - height));
        return new Point(x, y);
    }

    /// <summary>
    /// 判断是否该吸附，以及吸附到哪一边。
    /// 返回 (左边?, 是否触发吸附)。不触发时 left 无意义。
    /// </summary>
    public static (bool SnapLeft, bool ShouldSnap) EvaluateSnap(
        MonitorInfo monitor, double buttonWidth, double buttonHeight,
        Point position, int threshold)
    {
        var wa = monitor.WorkArea;
        var leftDist = position.X - wa.Left;
        var rightDist = wa.Right - (position.X + buttonWidth);

        var snapLeft = leftDist <= threshold && leftDist <= rightDist;
        var snapRight = rightDist <= threshold && rightDist < leftDist;

        if (snapLeft || snapRight) return (snapLeft, true);
        return (false, false);
    }
}
