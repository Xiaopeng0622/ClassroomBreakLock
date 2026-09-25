using System;
using System.IO;
using ClassroomBreakLock.Config;

namespace BreakLockVerify;

/// <summary>
/// 锁屏个性化外观的配置校验。
///
/// 重点：外观参数越界不能把锁屏搞坏（比如遮罩 1.0 会让屏幕全黑，
/// 用户以为死机了；不透明度 0 会让背景完全看不见）。
/// </summary>
public static class AppearanceChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== 默认值 ===");
        var def = new AppearanceConfig();
        Check("默认不启用自定义背景", !def.UseBackgroundImage);
        Check("默认没有图片文件", !def.HasImage);
        Check($"默认遮罩在合法区间（{def.OverlayOpacity:0.00}）", def.OverlayOpacity is >= 0 and <= 0.9);
        Check("默认不模糊", def.BlurRadius == 0);
        Check("默认不透明度为 1.0", Math.Abs(def.BackgroundOpacity - 1.0) < 0.001);
        Check($"默认填充方式合法（{def.Stretch}）",
            def.Stretch is "uniform" or "uniformToFill" or "fill");

        Console.WriteLine();
        Console.WriteLine("=== HasImage 判定 ===");
        var a = new AppearanceConfig { UseBackgroundImage = true, BackgroundImageFile = "bg_abc.png" };
        Check("启用 + 有文件名 → HasImage = true", a.HasImage);
        a.UseBackgroundImage = false;
        Check("关闭开关 → HasImage = false（即使有文件名）", !a.HasImage);
        a.UseBackgroundImage = true;
        a.BackgroundImageFile = "";
        Check("文件名为空 → HasImage = false", !a.HasImage);
        a.BackgroundImageFile = "   ";
        Check("文件名纯空白 → HasImage = false", !a.HasImage);

        Console.WriteLine();
        Console.WriteLine("=== 配置越界时的夹取（Normalize）===");
        // 直接调用 ConfigStore 的规范化路径：写盘 → 读回
        var dir = Path.Combine(Path.GetTempPath(), "bll_app_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfgPath = Path.Combine(dir, "config.json");

        try
        {
            var bad = AppConfig.CreateDefault();
            bad.ConfigPath = cfgPath;
            bad.Appearance.UseBackgroundImage = true;
            bad.Appearance.BackgroundImageFile = "bg_x.png";
            bad.Appearance.OverlayOpacity = 5.0;        // 越界：会导致全黑
            bad.Appearance.BlurRadius = 999;            // 越界：模糊到卡顿
            bad.Appearance.BackgroundOpacity = -3.0;    // 越界：负数
            bad.Appearance.Stretch = "乱写的值";         // 非法枚举

            ConfigStore.Save(bad, cfgPath);
            var loaded = ConfigStore.Load(cfgPath);

            Check($"遮罩 5.0 被夹到 ≤0.9（实际 {loaded.Appearance.OverlayOpacity:0.00}）",
                loaded.Appearance.OverlayOpacity <= 0.9);
            Check($"模糊 999 被夹到 ≤60（实际 {loaded.Appearance.BlurRadius:0}）",
                loaded.Appearance.BlurRadius <= 60);
            Check($"不透明度 -3.0 被夹到 ≥0.1（实际 {loaded.Appearance.BackgroundOpacity:0.00}）",
                loaded.Appearance.BackgroundOpacity >= 0.1);
            Check($"非法填充方式被重置（实际 {loaded.Appearance.Stretch}）",
                loaded.Appearance.Stretch is "uniform" or "uniformToFill" or "fill");
            Check("背景图文件名保留", loaded.Appearance.BackgroundImageFile == "bg_x.png");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("=== Clone 深拷贝 ===");
        var orig = new AppearanceConfig { BlurRadius = 25, BackgroundOpacity = 0.5 };
        var copy = orig.Clone();
        copy.BlurRadius = 0;
        copy.BackgroundOpacity = 1.0;
        Check("Clone 是独立副本（改副本不影响原对象）",
            orig.BlurRadius == 25 && Math.Abs(orig.BackgroundOpacity - 0.5) < 0.001);
        Check("AppConfig.Clone 会带上 Appearance",
            AppConfig.CreateDefault().Clone().Appearance is not null);

        Console.WriteLine();
        Console.WriteLine("=== 图片缺失时优雅回退 ===");
        // 配置指向一个不存在的文件，ResolveImagePath 必须返回 null（而不是抛异常）
        var missing = new AppearanceConfig
        {
            UseBackgroundImage = true,
            BackgroundImageFile = "bg_不存在.png"
        };
        string? resolved = null;
        var threw = false;
        try { resolved = AppearanceAssets.ResolveImagePath(missing); }
        catch { threw = true; }
        Check("文件不存在时不抛异常", !threw);
        Check("文件不存在时返回 null（调用方回退内置背景）", resolved is null);

        Check("未启用时直接返回 null（不去碰磁盘）",
            AppearanceAssets.ResolveImagePath(new AppearanceConfig()) is null);

        return failures;
    }
}
