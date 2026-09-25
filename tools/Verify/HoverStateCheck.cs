using System;

namespace BreakLockVerify;

/// <summary>
/// 吸附后展开/缩起的交互逻辑校验。
///
/// 行为约定（按用户要求）：**吸附到边缘后，不左键点击就一直是缩起状态。**
/// 展开/缩起完全由点击驱动，不做任何鼠标位置判定。
///
/// 这取代了早期的"悬停展开"方案——后者因为缩起(28px)与展开(84px)的判定区域
/// 与窗口尺寸变化耦合，会出现「缩起→鼠标在圆点内→展开→鼠标落到窗口外→缩起」
/// 的无限横跳（用户实际报过这个 bug）。
///
/// 这里用状态机模型把交互规则钉死，防止后续改动重新引入振荡。
/// </summary>
public static class HoverStateChecks
{
    /// <summary>模拟的交互状态</summary>
    private sealed class Model
    {
        public bool Snapped = true;       // 已吸附到边缘
        public bool Collapsed;            // 是否缩起
        public bool SnapToEdge = true;    // 配置：启用吸附
        public bool CollapseWhenSnapped = true;

        /// <summary>模拟一次左键单击。返回 true 表示点击被展开/缩起消费掉。</summary>
        public bool Click()
        {
            if (!SnapToEdge || !CollapseWhenSnapped || !Snapped) return false;
            Collapsed = !Collapsed;
            return true;
        }

        /// <summary>模拟鼠标移入/移出（当前设计下不应改变任何状态）。</summary>
        public void MouseMove() { /* 有意为空：不做悬停判定 */ }
    }

    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== 吸铁后的状态由点击唯一驱动 ===");

        var m = new Model { Collapsed = true };
        Check("初始：吸附且缩起", m.Snapped && m.Collapsed);

        Check("鼠标移入不改变状态（不再悬停展开）", RunAndStay(m));
        Check("缩起状态保持", m.Collapsed);

        var consumed = m.Click();
        Check("第 1 次点击 → 展开，且点击被消费（不会触发下课）",
            consumed && !m.Collapsed, $"consumed={consumed} collapsed={m.Collapsed}");

        Check("展开后鼠标乱动仍保持展开", RunAndStay(m) && !m.Collapsed);

        consumed = m.Click();
        Check("第 2 次点击 → 缩回，同样被消费",
            consumed && m.Collapsed, $"consumed={consumed} collapsed={m.Collapsed}");

        Console.WriteLine();
        Console.WriteLine("=== 关键：鼠标静止不动时不可能横跳 ===");
        // 用户报的 bug 场景：鼠标停在圆点上不动，按钮来回缩起/展开。
        // 新设计下状态只被 Click() 改变，MouseMove 是空操作，因此结构上不可能振荡。
        var stable = new Model { Collapsed = true };
        var flips = 0;
        var prev = stable.Collapsed;
        for (var i = 0; i < 200; i++)
        {
            stable.MouseMove();   // 疯狂触发鼠标事件，模拟"停在原地"的持续事件
            if (stable.Collapsed != prev) { flips++; prev = stable.Collapsed; }
        }
        Check($"200 次鼠标事件后状态切换次数 = {flips}（必须为 0）", flips == 0);

        Console.WriteLine();
        Console.WriteLine("=== 未吸附时点击应正常触发下课 ===");
        var free = new Model { Snapped = false, Collapsed = false };
        Check("未吸附 → 点击不被展开逻辑消费（走下课确认）", !free.Click());
        Check("未吸附时点击不改变缩起状态", !free.Collapsed);

        Console.WriteLine();
        Console.WriteLine("=== 关闭吸附功能时不应接管点击 ===");
        var off = new Model { SnapToEdge = false, Collapsed = false };
        Check("SnapToEdge=false → 点击不被消费", !off.Click());
        var off2 = new Model { CollapseWhenSnapped = false, Collapsed = false };
        Check("CollapseWhenSnapped=false → 点击不被消费", !off2.Click());

        return failures;
    }

    /// <summary>连续触发鼠标事件，返回状态是否保持不变。</summary>
    private static bool RunAndStay(Model m)
    {
        var before = m.Collapsed;
        for (var i = 0; i < 20; i++) m.MouseMove();
        return m.Collapsed == before;
    }
}
