using System;
using System.IO;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Config;

/// <summary>
/// 自定义背景图的存取。
///
/// 为什么要把图片**复制**到程序数据目录而不是只记路径：
///   老师选完图之后很可能把原图删掉/挪走（U 盘拔了、桌面清理了），
///   那时锁屏就会变成一片空白。复制一份进 assets，就与原图解耦了。
/// </summary>
public static class AppearanceAssets
{
    /// <summary>背景图存放目录：%ProgramData%\ClassroomBreakLock\assets</summary>
    public static string AssetsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ClassroomBreakLock",
        "assets");

    /// <summary>内置背景（无自定义图时）用的深色渐变由 XAML 提供，这里只管自定义图。</summary>
    public static string? ResolveImagePath(AppearanceConfig cfg)
    {
        if (!cfg.HasImage) return null;

        var path = Path.Combine(AssetsDir, cfg.BackgroundImageFile);
        if (File.Exists(path)) return path;

        Log.Warn($"自定义背景图不存在：{path}（将回退到内置背景）");
        return null;
    }

    /// <summary>
    /// 把选中的图片导入到 assets 目录，返回存储后的文件名。
    /// 会带上内容哈希前缀，避免同名文件互相覆盖、也便于识别同一张图。
    /// </summary>
    public static (bool Ok, string Message, string FileName) Import(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return (false, "文件不存在", "");

            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"))
                return (false, $"不支持的图片格式：{ext}", "");

            Directory.CreateDirectory(AssetsDir);

            var bytes = File.ReadAllBytes(sourcePath);
            var hash = HashUtil.Sha256Hex(bytes).Substring(0, 12);
            var fileName = $"bg_{hash}{ext}";
            var destPath = Path.Combine(AssetsDir, fileName);

            // 同一张图重复导入就跳过写入
            if (!File.Exists(destPath))
            {
                File.WriteAllBytes(destPath, bytes);
                Log.Info($"已导入背景图：{fileName}（{bytes.Length / 1024} KB）");
            }

            CleanupOldBackgrounds(keep: fileName);

            return (true, $"已导入背景图（{bytes.Length / 1024} KB）", fileName);
        }
        catch (Exception ex)
        {
            Log.Error($"导入背景图失败：{ex.Message}");
            return (false, $"导入失败：{ex.Message}", "");
        }
    }

    /// <summary>删掉除 keep 之外的历史背景图，避免 assets 目录越积越大。</summary>
    private static void CleanupOldBackgrounds(string keep)
    {
        try
        {
            if (!Directory.Exists(AssetsDir)) return;
            foreach (var f in Directory.GetFiles(AssetsDir, "bg_*"))
            {
                if (Path.GetFileName(f) == keep) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理旧背景图失败：{ex.Message}");
        }
    }

    /// <summary>清除自定义背景（回到内置深色渐变）。</summary>
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(AssetsDir))
            {
                foreach (var f in Directory.GetFiles(AssetsDir, "bg_*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            Log.Info("已清除自定义背景图");
        }
        catch (Exception ex)
        {
            Log.Warn($"清除背景图失败：{ex.Message}");
        }
    }
}
