using System.Windows;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Interop;

namespace BreakLockVerify;

/// <summary>
/// 悬浮按钮的几何逻辑校验：靠边吸附判定、工作区夹取、显示器选择。
///
/// 注意：这里只测纯计算部分（不依赖真实多屏环境）。
/// MonitorService.Enumerate() 依赖真实显示器，跑在有屏的机器上才有意义，
/// 所以这里用构造出来的 MonitorInfo 直接测判定函数。
/// </summary>
public static class FloatingButtonChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== 靠边吸附判定 ===");

        // 一块 1920x1080 的屏，工作区上下留 40px 任务栏
        var mon = new MonitorInfo(
            Index: 0,
            DeviceName: "TEST",
            IsPrimary: true,
            Bounds: new Rect(0, 0, 1920, 1080),
            WorkArea: new Rect(0, 0, 1920, 1040),
            DpiScale: 1.0);

        const int threshold = 24;
        const double btnW = 92;   // 窗口宽（按钮 84 + 上下各 4）
        const double btnH = 92;

        // 贴右边
        var (left1, snap1) = MonitorService.EvaluateSnap(mon, btnW, btnH, new Point(1900, 500), threshold);
        Check("右边 20px 内 → 吸附", snap1 && !left1, $"snap={snap1} left={left1}");

        // 贴左边
        var (left2, snap2) = MonitorService.EvaluateSnap(mon, btnW, btnH, new Point(10, 500), threshold);
        Check("左边 10px 内 → 吸附到左边", snap2 && left2, $"snap={snap2} left={left2}");

        // 屏幕中央，不该吸附
        var (_, snap3) = MonitorService.EvaluateSnap(mon, btnW, btnH, new Point(900, 500), threshold);
        Check("屏幕中央 → 不吸附", !snap3);

        // 刚好在阈值边界
        var (_, snap4) = MonitorService.EvaluateSnap(
            mon, btnW, btnH, new Point(mon.WorkArea.Right - btnW - threshold, 500), threshold);
        Check($"右边恰好 {threshold}px → 吸附（含边界）", snap4);

        // 超出阈值 1px
        var (_, snap5) = MonitorService.EvaluateSnap(
            mon, btnW, btnH, new Point(mon.WorkArea.Right - btnW - threshold - 1, 500), threshold);
        Check($"右边 {threshold + 1}px → 不吸附", !snap5);

        Console.WriteLine();
        Console.WriteLine("=== 工作区夹取 ===");

        var c1 = MonitorService.ClampToWorkArea(mon, btnW, btnH, new Point(5000, 5000));
        Check("远超右下角 → 夹回右下", Math.Abs(c1.X - (1920 - btnW)) < 0.01 && Math.Abs(c1.Y - (1040 - btnH)) < 0.01,
            $"({c1.X:0},{c1.Y:0})");

        var c2 = MonitorService.ClampToWorkArea(mon, btnW, btnH, new Point(-500, -500));
        Check("远超左上角 → 夹回左上", Math.Abs(c2.X) < 0.01 && Math.Abs(c2.Y) < 0.01, $"({c2.X:0},{c2.Y:0})");

        var c3 = MonitorService.ClampToWorkArea(mon, btnW, btnH, new Point(800, 400));
        Check("正常范围内 → 原样返回", Math.Abs(c3.X - 800) < 0.01 && Math.Abs(c3.Y - 400) < 0.01);

        Console.WriteLine();
        Console.WriteLine("=== 负坐标显示器（副屏在主屏左侧）===");

        // 副屏在主屏左边：坐标是负的。这是最容易出 bug 的情况。
        var leftMon = new MonitorInfo(
            Index: 1, DeviceName: "LEFT", IsPrimary: false,
            Bounds: new Rect(-1920, 0, 1920, 1080),
            WorkArea: new Rect(-1920, 0, 1920, 1040),
            DpiScale: 1.0);

        var hit = MonitorService.HitTest(new List<MonitorInfo> { mon, leftMon }, new Point(-900, 500));
        Check("落在副屏上的点 → 命中副屏", hit?.DeviceName == "LEFT", hit?.DeviceName ?? "null");

        var hit2 = MonitorService.HitTest(new List<MonitorInfo> { mon, leftMon }, new Point(900, 500));
        Check("落在主屏上的点 → 命中主屏", hit2?.DeviceName == "TEST", hit2?.DeviceName ?? "null");

        var clampLeft = MonitorService.ClampToWorkArea(leftMon, btnW, btnH, new Point(-3000, 5000));
        Check("副屏上夹取使用副屏坐标（不是从 0 开始）",
            Math.Abs(clampLeft.X - (-1920)) < 0.01 && Math.Abs(clampLeft.Y - (1040 - btnH)) < 0.01,
            $"({clampLeft.X:0},{clampLeft.Y:0})");

        var (leftEdge, snapLeft) = MonitorService.EvaluateSnap(
            leftMon, btnW, btnH, new Point(-1915, 500), threshold);
        Check("副屏左边吸附 → 判定为 left", snapLeft && leftEdge, $"snap={snapLeft} left={leftEdge}");

        Console.WriteLine();
        Console.WriteLine("=== 显示器选择（Resolve）===");

        var cfgAuto = AppConfig.CreateDefault();
        cfgAuto.FloatingButton.TargetScreenIndex = -1;
        cfgAuto.FloatingButton.LastScreenDeviceName = "LEFT";
        var picked = MonitorService.Resolve(cfgAuto, new List<MonitorInfo> { mon, leftMon });
        Check("自动模式 + 记住了副屏 → 选副屏", picked.DeviceName == "LEFT", picked.DeviceName);

        cfgAuto.FloatingButton.LastScreenDeviceName = "";
        var pickedPrimary = MonitorService.Resolve(cfgAuto, new List<MonitorInfo> { mon, leftMon });
        Check("自动模式 + 无记录 → 回主屏", pickedPrimary.IsPrimary, pickedPrimary.DeviceName);

        var cfgManual = AppConfig.CreateDefault();
        cfgManual.FloatingButton.TargetScreenIndex = 1;
        var pickedManual = MonitorService.Resolve(cfgManual, new List<MonitorInfo> { mon, leftMon });
        Check("手动指定屏 1 → 选副屏", pickedManual.DeviceName == "LEFT", pickedManual.DeviceName);

        cfgManual.FloatingButton.TargetScreenIndex = 99;   // 越界
        var pickedFallback = MonitorService.Resolve(cfgManual, new List<MonitorInfo> { mon, leftMon });
        Check("手动指定越界 → 回落主屏", pickedFallback.IsPrimary, pickedFallback.DeviceName);

        Console.WriteLine();
        Console.WriteLine("=== 配置默认值 ===");
        var def = new FloatingButtonConfig();
        Check("默认启用靠边吸附", def.SnapToEdge);
        Check("默认吸附后缩起", def.CollapseWhenSnapped);
        Check("默认自动选屏（-1）", def.TargetScreenIndex == -1);
        Check("默认缩起直径小于展开直径", def.CollapsedSize < def.Size,
            $"{def.CollapsedSize} < {def.Size}");
        Check("默认淡出比例在合法区间", def.FadeToRatio is >= 0.1 and <= 1.0, def.FadeToRatio.ToString("0.00"));
        Check("默认未吸附", !def.IsSnapped);

        var cloned = def.Clone();
        cloned.SnappedEdge = "left";
        cloned.IsCollapsed = true;
        Check("Clone 是深拷贝（改副本不影响原对象）",
            !def.IsSnapped && def.SnappedEdge == "" && !def.IsCollapsed);

        // ────────────────────────────────────────────────────────────
        // 自由位置（拖动）相关 —— 出过"拖了没用"的 bug
        // ────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== 自由摆放位置（拖动）===");

        var freeCfg = AppConfig.CreateDefault();
        Check("默认没有自由位置（首次启动用右下角边距）", !freeCfg.FloatingButton.HasFreePosition);

        freeCfg.FloatingButton.HasFreePosition = true;
        freeCfg.FloatingButton.FreeX = 400;
        freeCfg.FloatingButton.FreeY = 250;

        // 深拷贝必须带上自由位置，否则"改设置再保存"会把拖动结果丢掉
        var freeClone = freeCfg.Clone();
        Check("Clone 保留 HasFreePosition", freeClone.FloatingButton.HasFreePosition);
        Check("Clone 保留 FreeX/FreeY",
            freeClone.FloatingButton.FreeX == 400 && freeClone.FloatingButton.FreeY == 250,
            $"({freeClone.FloatingButton.FreeX},{freeClone.FloatingButton.FreeY})");

        // 自由坐标 + 工作区 → 绝对坐标（模拟 PlaceAtStoredFreePosition）
        var workMon = new MonitorInfo(
            Index: 0, DeviceName: "M", IsPrimary: true,
            Bounds: new Rect(0, 0, 1920, 1080),
            WorkArea: new Rect(0, 0, 1920, 1040),
            DpiScale: 1.0);
        var absPos = new Point(workMon.WorkArea.Left + 400, workMon.WorkArea.Top + 250);
        Check("自由坐标换算为绝对坐标正确", Math.Abs(absPos.X - 400) < 0.01 && Math.Abs(absPos.Y - 250) < 0.01);

        // 副屏（负坐标）上的自由位置也要正确换算
        var negMon = new MonitorInfo(
            Index: 1, DeviceName: "L", IsPrimary: false,
            Bounds: new Rect(-1920, 0, 1920, 1080),
            WorkArea: new Rect(-1920, 0, 1920, 1040),
            DpiScale: 1.0);
        var negAbs = new Point(negMon.WorkArea.Left + 400, negMon.WorkArea.Top + 250);
        Check("副屏上的自由坐标换算正确（负基准）",
            Math.Abs(negAbs.X - (-1520)) < 0.01, $"x={negAbs.X}");

        // 自由位置超出工作区时会被夹回来
        var clampedFree = MonitorService.ClampToWorkArea(workMon, 92, 92,
            new Point(workMon.WorkArea.Left + 9999, workMon.WorkArea.Top + 9999));
        Check("超界的自由位置被夹回工作区",
            Math.Abs(clampedFree.X - (1920 - 92)) < 0.01 && Math.Abs(clampedFree.Y - (1040 - 92)) < 0.01,
            $"({clampedFree.X:0},{clampedFree.Y:0})");

        Console.WriteLine();
        Console.WriteLine("=== 显示器设置变化检测 ===");
        // 复现 bug 场景：同一个 AppConfig 实例被就地修改后回传，
        // 此时"新旧对象对比"永远为 false。必须依赖记录的上次值。
        var sameInstance = AppConfig.CreateDefault();
        var appliedIndex = sameInstance.FloatingButton.TargetScreenIndex;   // 模拟 _appliedScreenIndex
        Check("同一实例未修改 → 不触发显示器刷新",
            sameInstance.FloatingButton.TargetScreenIndex == appliedIndex);

        sameInstance.FloatingButton.TargetScreenIndex = 1;   // 就地改成"屏 2"
        Check("★ 同一实例被就地修改 → 能检测到变化（这就是之前不生效的原因）",
            sameInstance.FloatingButton.TargetScreenIndex != appliedIndex,
            $"{appliedIndex} → {sameInstance.FloatingButton.TargetScreenIndex}");

        // Resolve 要能按新的 index 选到对应的屏
        var twoMons = new List<MonitorInfo> { workMon, negMon };
        var picked2 = MonitorService.Resolve(sameInstance, twoMons);
        Check("改 index 后 Resolve 选到屏 2", picked2.DeviceName == "L", picked2.DeviceName);

        return failures;
    }
}
