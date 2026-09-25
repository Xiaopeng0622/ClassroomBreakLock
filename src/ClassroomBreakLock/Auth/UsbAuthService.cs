using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Auth;

/// <summary>一个已插入的可移动驱动器。</summary>
public sealed record RemovableDrive(
    string RootPath,
    string VolumeLabel,
    string VolumeSerial,
    string FileSystem,
    long TotalBytes)
{
    public string Display => $"{VolumeLabel} ({RootPath.TrimEnd('\\')})";
}

public sealed record UsbAuthResult(bool Success, string Message, UsbKey? MatchedKey = null, RemovableDrive? Drive = null);

/// <summary>
/// U 盘认证：必须同时满足
///   1. 卷序列号(SN) 与已登记的钥匙一致  —— 认盘本身
///   2. 盘内密钥文件内容哈希一致          —— 认盘里的文件
/// 任一不符即拒绝。把文件拷到别的 U 盘、或换个 U 盘插进来都进不去。
/// </summary>
public static class UsbAuthService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();

    private const uint DRIVE_REMOVABLE = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetDriveType(string lpRootPathName);

    /// <summary>枚举当前插入的所有可移动盘。</summary>
    public static List<RemovableDrive> EnumerateRemovableDrives()
    {
        var list = new List<RemovableDrive>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Removable) continue;
                    if (!drive.IsReady) continue;
                    if (GetDriveType(drive.RootDirectory.FullName) != DRIVE_REMOVABLE) continue;

                    var serial = ReadVolumeSerial(drive.RootDirectory.FullName);
                    list.Add(new RemovableDrive(
                        drive.RootDirectory.FullName,
                        string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "可移动磁盘" : drive.VolumeLabel,
                        serial,
                        drive.DriveFormat,
                        drive.TotalSize));
                }
                catch
                {
                    // 单个盘读不到就跳过，不影响其它盘
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举可移动驱动器失败：{ex.Message}");
        }
        return list;
    }

    /// <summary>读取卷序列号。需要管理员权限才能拿到真实硬件 SN，模板阶段用卷序列号足够。</summary>
    public static string ReadVolumeSerial(string rootPath)
    {
        try
        {
            var root = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
            if (GetVolumeInformation(root, null, 0, out var serial, out _, out _,
                    null, 0))
            {
                return $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取卷序列号失败 {rootPath}：{ex.Message}");
        }
        return "";
    }

    /// <summary>读取盘内密钥文件内容的 SHA-256。</summary>
    public static string? TryReadKeyHash(RemovableDrive drive, string keyFileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyFileName)) return null;
            var path = Path.Combine(drive.RootPath, keyFileName);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return HashUtil.Sha256Hex(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取密钥文件失败 {drive.RootPath}{keyFileName}：{ex.Message}");
            return null;
        }
    }

    /// <summary>对当前插入的所有 U 盘做一次认证尝试。</summary>
    public static UsbAuthResult Authenticate(IEnumerable<UsbKey> registeredKeys)
    {
        var keys = registeredKeys.Where(k => k.Enabled).ToList();
        if (keys.Count == 0)
            return new UsbAuthResult(false, "尚未登记任何钥匙 U 盘");

        var drives = EnumerateRemovableDrives();
        if (drives.Count == 0)
            return new UsbAuthResult(false, "未检测到 U 盘，请插入钥匙盘");

        foreach (var drive in drives)
        {
            foreach (var key in keys)
            {
                // 第一道：卷序列号
                if (!string.Equals(drive.VolumeSerial, key.VolumeSerial, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 第二道：密钥文件内容
                var hash = TryReadKeyHash(drive, key.KeyFileName);
                if (hash is null)
                    return new UsbAuthResult(false,
                        $"识别到「{key.Label}」，但盘内缺少密钥文件 {key.KeyFileName}", key, drive);

                if (!string.Equals(hash, key.KeyFileHash, StringComparison.OrdinalIgnoreCase))
                    return new UsbAuthResult(false,
                        $"「{key.Label}」密钥文件内容不匹配，可能已被篡改", key, drive);

                return new UsbAuthResult(true, $"U 盘认证通过：{key.Label}", key, drive);
            }
        }

        var snList = string.Join(", ", drives.Select(d => $"{d.Display}={d.VolumeSerial}"));
        return new UsbAuthResult(false, $"插入了 {drives.Count} 个 U 盘，但都不是已登记的钥匙盘（{snList}）");
    }

    /// <summary>把当前插入的某个 U 盘登记为钥匙，并生成配套密钥文件。</summary>
    public static (bool Ok, string Message, UsbKey? Key) RegisterKey(
        RemovableDrive drive, string label, string keyFileName, string? customContent = null)
    {
        try
        {
            var content = customContent;
            if (string.IsNullOrWhiteSpace(content))
            {
                // 生成随机密钥内容
                content = $"ClassroomBreakLock-Key\n" +
                          $"serial={drive.VolumeSerial}\n" +
                          $"created={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"nonce={Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))}\n";
            }

            var path = Path.Combine(drive.RootPath, keyFileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));

            var key = new UsbKey
            {
                Label = label,
                VolumeSerial = drive.VolumeSerial,
                KeyFileName = keyFileName,
                KeyFileHash = HashUtil.Sha256Hex(Encoding.UTF8.GetBytes(content)),
                Enabled = true
            };
            Log.Info($"已登记钥匙 U 盘：{label} SN={drive.VolumeSerial} 文件名={keyFileName}");
            return (true, $"登记成功：{label}（SN {drive.VolumeSerial}）", key);
        }
        catch (Exception ex)
        {
            Log.Error($"登记钥匙 U 盘失败：{ex.Message}");
            return (false, $"登记失败：{ex.Message}", null);
        }
    }
}
