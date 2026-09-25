using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Config;

/// <summary>
/// 课表导入：Excel(.xlsx) / CSV / TXT / YAML（含 ClassIsland / CESE 导出格式）。
/// 只依赖 BCL——xlsx 用 System.IO.Compression + XDocument 直读，YAML 自带极简解析器。
/// </summary>
public static class ScheduleImporter
{
    /// <summary>导入结果：单日时段 + 可选的「整周」结果 + 被跳过的说明。</summary>
    public sealed class Result
    {
        /// <summary>不适合按天拆分的格式（Excel/CSV）解析出来的时段，用于填充"当前这一天"。</summary>
        public List<ClassPeriod> Periods { get; } = new();

        /// <summary>带星期信息的格式（ClassIsland/CESE）解析出来的整周作息，key = 0(周日)~6(周六)。</summary>
        public List<DayResult> Days { get; } = new();

        public List<string> Warnings { get; } = new();

        /// <summary>来源描述，用于提示语。</summary>
        public string Source { get; set; } = "";

        public bool HasWeekInfo => Days.Count > 0;

        public int TotalPeriodCount => HasWeekInfo
            ? Days.Sum(d => d.Periods.Count)
            : Periods.Count;
    }

    /// <summary>某一天的作息。</summary>
    public sealed class DayResult
    {
        public int DayIndex { get; set; }

        public string DayName { get; set; } = "";

        public List<ClassPeriod> Periods { get; } = new();
    }

    /// <summary>匹配 8:45 / 08:45 / 8：45 / 8.45 / 8时45 等写法。</summary>
    private static readonly Regex TimeRegex =
        new(@"(\d{1,2})\s*[:：.．时]\s*(\d{1,2})", RegexOptions.Compiled);

    public static Result ParseFile(string path)
    {
        string lower = path.ToLowerInvariant();
        string name = Path.GetFileName(path);

        if (lower.EndsWith(".xlsx") || lower.EndsWith(".xlsm"))
        {
            return ParseRows(ReadXlsxRows(path), name);
        }

        if (lower.EndsWith(".csv"))
        {
            return ParseRows(ReadDelimited(File.ReadAllText(path, DetectEncoding(path))), name);
        }

        if (lower.EndsWith(".yaml") || lower.EndsWith(".yml"))
        {
            return ParseYaml(File.ReadAllText(path, DetectEncoding(path)), name);
        }

        // .txt / 其它：一行一个时段
        return ParseRows(File.ReadAllLines(path, DetectEncoding(path)).Select(l => new[] { l }), name);
    }

    public static Result ParseText(string text)
        => LooksLikeYaml(text)
            ? ParseYaml(text, "粘贴内容")
            : ParseRows(ReadDelimited(text), "粘贴内容");

    /// <summary>
    /// 把任意支持的文件转成**纯文本**，用于交给 AI 兜底识别。
    /// Excel 会逐行展开成制表符分隔的文本，其余格式直接读原文。
    /// </summary>
    public static string ExtractRawText(string path)
    {
        string lower = path.ToLowerInvariant();

        try
        {
            if (lower.EndsWith(".xlsx") || lower.EndsWith(".xlsm"))
            {
                var rows = ReadXlsxRows(path);
                var sb = new StringBuilder();
                foreach (var row in rows)
                {
                    sb.AppendLine(string.Join("\t", row));
                }
                return sb.ToString();
            }

            return File.ReadAllText(path, DetectEncoding(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"提取课表原文失败：{ex.Message}");
            return "";
        }
    }

    private static bool LooksLikeYaml(string text)
        => text.Contains("schedules:", StringComparison.OrdinalIgnoreCase)
           || text.Contains("periods:", StringComparison.OrdinalIgnoreCase)
           || text.Contains("subjects:", StringComparison.OrdinalIgnoreCase)
           || text.Contains("classes:", StringComparison.OrdinalIgnoreCase);

    // ---------------- 解析入口 ----------------

    private static Result ParseYaml(string text, string source)
    {
        YamlNode root = ParseYamlTree(text);

        // 先试 ClassIsland / CESE 那种带星期的结构；不行再退回"平铺行"解析
        if (TryCollectWeekSchedule(root, source, out Result? cese))
        {
            return cese!;
        }

        Result generic = ParseRows(YamlToRows(root), source);
        generic.Source = source;
        return generic;
    }

    /// <summary>
    /// 识别并解析 ClassIsland / CESE 导出格式：
    ///   schedules:
    ///     - name: 周一
    ///       classes:
    ///         - subject: 语文
    ///           start_time: 06:50:00
    ///           end_time: 07:20:00
    ///       enable_day: 1
    ///       weeks: all
    /// 单双周（weeks: odd/even）会按时间段合并——课间锁只需要知道"该不该锁"。
    /// </summary>
    private static bool TryCollectWeekSchedule(YamlNode root, string source, out Result? result)
    {
        result = null;

        if (!root.Map.TryGetValue("schedules", out YamlNode? schedules) || !schedules.IsList)
        {
            return false;
        }

        var built = new Result { Source = source };
        var byDay = new Dictionary<int, DayResult>();

        foreach (YamlNode dayNode in schedules.List)
        {
            if (!dayNode.Map.TryGetValue("classes", out YamlNode? classes) || !classes.IsList)
            {
                continue;
            }

            int dayIndex = ResolveDayIndex(dayNode);
            if (dayIndex < 0)
            {
                built.Warnings.Add($"跳过「{Pick(dayNode, "name") ?? "未命名"}」：没找到可识别的 enable_day");
                continue;
            }

            if (!byDay.TryGetValue(dayIndex, out DayResult? day))
            {
                day = new DayResult { DayIndex = dayIndex, DayName = DayNameOf(dayIndex) };
                byDay[dayIndex] = day;
            }

            foreach (YamlNode cls in classes.List)
            {
                string subject = Pick(cls, "subject", "name", "科目", "名称") ?? "";
                string? start = Pick(cls, "start_time", "start", "startTime", "开始", "起");
                string? end = Pick(cls, "end_time", "end", "endTime", "结束", "止");

                if (start is null || end is null)
                {
                    continue;
                }

                string? s = NormalizeTime(start);
                string? e = NormalizeTime(end);

                if (s is null || e is null)
                {
                    built.Warnings.Add($"跳过一条「{subject}」：时间无法识别（{start} / {end}）");
                    continue;
                }

                if (TimeSpan.Parse(e) <= TimeSpan.Parse(s))
                {
                    continue;
                }

                // 单双周合并：同一时间段只保留一份
                if (day.Periods.Any(p => p.Start == s && p.End == e))
                {
                    continue;
                }

                day.Periods.Add(new ClassPeriod
                {
                    Name = string.IsNullOrWhiteSpace(subject) ? $"第{day.Periods.Count + 1}节" : subject,
                    Start = s,
                    End = e,
                    LockDuring = true
                });
            }
        }

        if (byDay.Count == 0)
        {
            return false;
        }

        foreach (DayResult day in byDay.Values.OrderBy(d => d.DayIndex))
        {
            day.Periods.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            MergeAdjacent(day.Periods);
            built.Days.Add(day);
        }

        result = built;
        return true;
    }

    /// <summary>
    /// 合并首尾相接、且科目相同的相邻时段。
    /// 单双周合并后经常出现「早读 07:20-07:50 + 早读 07:50-08:00」这种连排，
    /// 课间锁在两个时段之间本来就不会变状态，拆开只会让作息表变成一大串。
    /// </summary>
    private static void MergeAdjacent(List<ClassPeriod> periods)
    {
        for (int i = 0; i < periods.Count - 1; i++)
        {
            ClassPeriod a = periods[i];
            ClassPeriod b = periods[i + 1];

            if (a.LockDuring == b.LockDuring
                && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                && a.End == b.Start)
            {
                a.End = b.End;
                periods.RemoveAt(i + 1);
                i--;
            }
        }
    }

    private static int ResolveDayIndex(YamlNode dayNode)
    {
        string? enableDay = Pick(dayNode, "enable_day", "enableDay", "day", "weekday");
        if (int.TryParse(enableDay, out int d) && d is >= 1 and <= 7)
        {
            return d % 7;   // 1=周一 ... 7=周日
        }

        // 退回按名字猜：周一/星期二/...
        string? name = Pick(dayNode, "name");
        if (name is not null)
        {
            for (int i = 0; i < 7; i++)
            {
                if (name.Contains(DayNameOf(i), StringComparison.Ordinal)
                    || name.Contains(DayNameOf(i).Replace("周", "星期"), StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string DayNameOf(int dayIndex) => dayIndex switch
    {
        0 => "周日",
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        _ => "未知"
    };

    /// <summary>把 "06:50:00" / "6:50" / "6时50分" 统一成 "06:50"。</summary>
    private static string? NormalizeTime(string raw)
    {
        Match m = TimeRegex.Match(raw);
        return m.Success ? (TryTime(m, out string value) ? value : null) : null;
    }

    // ---------------- 平铺行解析（Excel / CSV / TXT / 简单 YAML） ----------------

    private static Result ParseRows(IEnumerable<string[]> rows, string source)
    {
        var result = new Result { Source = source };
        int lineNo = 0;

        foreach (string[] cells in rows)
        {
            lineNo++;
            string joined = string.Join(" ", cells).Trim();

            if (joined.Length == 0 || IsHeader(joined))
            {
                continue;
            }

            MatchCollection matches = TimeRegex.Matches(joined);
            if (matches.Count < 2)
            {
                result.Warnings.Add($"第 {lineNo} 行没找到两个时间：{Shorten(joined)}");
                continue;
            }

            if (!TryTime(matches[0], out string start) || !TryTime(matches[1], out string end))
            {
                result.Warnings.Add($"第 {lineNo} 行时间不合法：{Shorten(joined)}");
                continue;
            }

            var period = new ClassPeriod
            {
                Name = GuessName(cells, joined, result.Periods.Count + 1),
                Start = start,
                End = end,
                LockDuring = GuessLockDuring(cells),
                EarlyUnlockMinutesOverride = GuessEarlyUnlock(cells)
            };

            if (period.EndTime <= period.StartTime)
            {
                result.Warnings.Add($"第 {lineNo} 行结束时间不晚于开始时间：{Shorten(joined)}");
                continue;
            }

            result.Periods.Add(period);
        }

        result.Periods.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        return result;
    }

    private static bool IsHeader(string joined)
        => (joined.Contains("节次") && (joined.Contains("开始") || joined.Contains("结束")))
           || (joined.Contains("开始") && joined.Contains("结束"))
           || (joined.Contains("start", StringComparison.OrdinalIgnoreCase)
               && joined.Contains("end", StringComparison.OrdinalIgnoreCase));

    private static bool TryTime(Match m, out string value)
    {
        value = "";
        if (!int.TryParse(m.Groups[1].Value, out int h) || !int.TryParse(m.Groups[2].Value, out int min))
        {
            return false;
        }

        if (h is < 0 or > 23 || min is < 0 or > 59)
        {
            return false;
        }

        value = $"{h:00}:{min:00}";
        return true;
    }

    private static string GuessName(string[] cells, string joined, int ordinal)
    {
        foreach (string cell in cells)
        {
            string t = cell.Trim();
            if (t.Length == 0 || TimeRegex.IsMatch(t))
            {
                continue;
            }

            if (t.All(c => char.IsDigit(c) || c == '.' || c == '-'))
            {
                continue;
            }

            return t.Length > 20 ? t.Substring(0, 20) : t;
        }

        return $"第{ordinal}节";
    }

    private static bool GuessLockDuring(string[] cells)
    {
        if (cells.Length >= 4)
        {
            string flag = cells[3].Trim().ToLowerInvariant();
            if (flag is "否" or "no" or "n" or "0" or "×" or "x" or "-" or "不" or "false" or "不锁")
            {
                return false;
            }
        }

        return true;
    }

    private static int? GuessEarlyUnlock(string[] cells)
        => cells.Length >= 5 && int.TryParse(cells[4].Trim(), out int minutes) && minutes is >= 0 and <= 120
            ? minutes
            : null;

    // ---------------- 读文件 ----------------

    private static List<string[]> ReadDelimited(string text)
    {
        var rows = new List<string[]>();

        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Contains('\t') ? line.Split('\t') : line.Split(',');
            rows.Add(parts.Select(p => p.Trim().Trim('"')).ToArray());
        }

        return rows;
    }

    /// <summary>最小 xlsx 读取：sharedStrings + 第一个工作表。</summary>
    private static List<string[]> ReadXlsxRows(string path)
    {
        var rows = new List<string[]>();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        using ZipArchive zip = ZipFile.OpenRead(path);

        var shared = new List<string>();
        ZipArchiveEntry? ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry is not null)
        {
            using Stream s = ssEntry.Open();
            XDocument doc = XDocument.Load(s);
            foreach (XElement si in doc.Descendants(ns + "si"))
            {
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
            }
        }

        ZipArchiveEntry? sheet = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sheet is null)
        {
            return rows;
        }

        using Stream sheetStream = sheet.Open();
        XDocument sheetDoc = XDocument.Load(sheetStream);

        foreach (XElement row in sheetDoc.Descendants(ns + "row"))
        {
            var cells = new List<string>();
            foreach (XElement c in row.Elements(ns + "c"))
            {
                string type = (string?)c.Attribute("t") ?? "";
                XElement? v = c.Element(ns + "v");
                XElement? inline = c.Element(ns + "is");

                string text = type switch
                {
                    "s" when v is not null && int.TryParse(v.Value, out int idx) && idx >= 0 && idx < shared.Count
                        => shared[idx],
                    "inlineStr" when inline is not null
                        => string.Concat(inline.Descendants(ns + "t").Select(t => t.Value)),
                    _ => v?.Value ?? ""
                };

                cells.Add(text.Trim());
            }

            rows.Add(cells.ToArray());
        }

        return rows;
    }

    private static Encoding DetectEncoding(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            int read = fs.Read(head);
            if (read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return new UTF8Encoding(true);
            }
        }
        catch
        {
            // 读不了就用默认
        }

        return new UTF8Encoding(false);
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "…";

    // ---------------- YAML（无第三方依赖的最小实现） ----------------

    private static List<string[]> YamlToRows(YamlNode root)
    {
        var rows = new List<string[]>();
        CollectYaml(root, null, rows);
        return rows;
    }

    private static void CollectYaml(YamlNode node, string? name, List<string[]> rows)
    {
        if (node.IsScalar)
        {
            // 只有真的含两个时间才当作一条时段，否则会把 name: 周一 这种键也误收进来
            if (TimeRegex.Matches(node.Scalar).Count >= 2)
            {
                rows.Add(string.IsNullOrWhiteSpace(name)
                    ? new[] { node.Scalar }
                    : new[] { name, node.Scalar });
            }

            return;
        }

        if (node.IsList)
        {
            foreach (YamlNode item in node.List)
            {
                CollectYaml(item, name, rows);
            }

            return;
        }

        string? start = Pick(node, "start", "开始", "begin", "from", "startTime", "start_time", "起");
        string? end = Pick(node, "end", "结束", "to", "止", "endTime", "end_time");
        string? explicitName = Pick(node, "name", "名称", "subject", "科目", "label");
        string? lockValue = Pick(node, "lock", "locked", "上锁", "锁", "lockDuring");
        string? earlyValue = Pick(node, "early", "earlyUnlock", "earlyUnlockMinutes", "提前", "提前解锁");
        string entryName = explicitName ?? name ?? "";

        if (!string.IsNullOrWhiteSpace(start) && !string.IsNullOrWhiteSpace(end))
        {
            var cells = new List<string> { entryName, start, end };
            if (lockValue is not null)
            {
                bool locked = !lockValue.Trim().ToLowerInvariant()
                    .IsOneOf("false", "no", "0", "off", "否", "不");
                cells.Add(locked ? "是" : "否");
            }

            if (earlyValue is not null)
            {
                while (cells.Count < 4)
                {
                    cells.Add("");
                }

                cells.Add(earlyValue);
            }

            rows.Add(cells.ToArray());
            return;
        }

        foreach (KeyValuePair<string, YamlNode> kv in node.Map)
        {
            CollectYaml(kv.Value, kv.Key, rows);
        }
    }

    private static string? Pick(YamlNode node, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (node.Map.TryGetValue(key, out YamlNode? v) && v.IsScalar && v.Scalar.Length > 0)
            {
                return v.Scalar;
            }
        }

        return null;
    }

    /// <summary>极简 YAML 子集解析：缩进 + 映射 + 列表 + 标量，够读课表用。</summary>
    private static YamlNode ParseYamlTree(string text)
    {
        var root = new YamlNode();
        var stack = new List<(int Indent, YamlNode Node)> { (-1, root) };

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = StripYamlComment(raw);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent++;
            }

            string content = line.Substring(indent).TrimEnd();
            if (content.Length == 0 || content == "---" || content == "...")
            {
                continue;
            }

            bool isItem = content.StartsWith("- ") || content == "-";

            // 弹出到正确的父节点。关键差别：
            //   · 普通 key 行：缩进 <= 栈顶就弹
            //   · 列表项行：要保留“同一缩进的列表宿主”，否则 YAML 的缩进式序列会被弹掉——
            //     schedules: 换行后直接跟 - name: 周一（同缩进），ClassIsland / CESE 导出就是这个风格。
            while (stack.Count > 1)
            {
                (int Indent, YamlNode Node) top = stack[^1];
                bool isListOwner = top.Node.IsList || top.Node.Pending;
                bool shouldPop = isItem
                    ? indent < top.Indent || (indent == top.Indent && !isListOwner)
                    : indent <= top.Indent;

                if (!shouldPop)
                {
                    break;
                }

                stack.RemoveAt(stack.Count - 1);
            }

            YamlNode parent = stack[^1].Node;
            parent.Pending = false;

            if (isItem)
            {
                string rest = content.Length > 1 ? content.Substring(1).Trim() : "";
                parent.IsList = true;

                var item = new YamlNode();
                parent.List.Add(item);
                parent.Map.Clear();

                if (rest.Length == 0)
                {
                    stack.Add((indent, item));
                }
                else if (TrySplitPair(rest, out string k, out string v))
                {
                    var child = new YamlNode();
                    if (v.Length == 0)
                    {
                        item.Map[k] = child;
                    }
                    else
                    {
                        child.IsScalar = true;
                        child.Scalar = v;
                        item.Map[k] = child;
                    }

                    stack.Add((indent + 1, item));
                }
                else
                {
                    item.IsScalar = true;
                    item.Scalar = Unquote(rest);
                    stack.Add((indent, item));
                }

                continue;
            }

            if (TrySplitPair(content, out string key, out string value))
            {
                var node = new YamlNode();
                parent.IsList = false;
                parent.Map[key] = node;

                if (value.Length == 0)
                {
                    // 空值的 key：下一行可能是同缩进的序列，也可能是子映射
                    node.Pending = true;
                    stack.Add((indent, node));
                }
                else
                {
                    node.IsScalar = true;
                    node.Scalar = value;
                    stack.Add((indent, node));
                }

                continue;
            }

            parent.IsList = true;
            parent.List.Add(new YamlNode { IsScalar = true, Scalar = Unquote(content) });
        }

        return root;
    }

    private static bool TrySplitPair(string content, out string key, out string value)
    {
        key = "";
        value = "";

        bool inSingle = false;
        bool inDouble = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == ':' && !inSingle && !inDouble)
            {
                // 时间里的冒号（08:00）不算分隔符：后面得是结尾或空格
                if (i + 1 < content.Length && !char.IsWhiteSpace(content[i + 1]))
                {
                    continue;
                }

                key = Unquote(content.Substring(0, i).Trim());
                value = Unquote(content.Substring(Math.Min(i + 1, content.Length)).Trim());
                return key.Length > 0;
            }
        }

        return false;
    }

    private static string StripYamlComment(string line)
    {
        bool inSingle = false;
        bool inDouble = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line.Substring(0, i);
            }
        }

        return line;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            return s.Substring(1, s.Length - 2);
        }

        return s;
    }

    private static bool IsOneOf(this string s, params string[] options) => options.Contains(s);

    /// <summary>极简 YAML 节点：要么是标量，要么是列表，要么是映射。</summary>
    private sealed class YamlNode
    {
        public Dictionary<string, YamlNode> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<YamlNode> List { get; } = new();

        public string Scalar { get; set; } = "";

        public bool IsScalar { get; set; }

        public bool IsList { get; set; }

        /// <summary>刚由「key:」声明、还没拿到任何子节点的映射（用于识别缩进式序列）。</summary>
        public bool Pending { get; set; }
    }
}
