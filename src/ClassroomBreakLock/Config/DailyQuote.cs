using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassroomBreakLock.Config;

/// <summary>
/// 每日一言。显示在锁屏左上角（原先是个永远显示"状态加载中…"的死控件）。
///
/// 设计取舍：
///   - **纯离线**：内置语录库，不请求网络。教室机器经常没有外网，
///     而且锁屏界面卡在等接口上会很难看。
///   - **按天稳定**：同一天内反复锁定/解锁显示同一句，
///     不会每次上锁都跳一句（那会显得很吵）。换天才换。
///   - **可扩展**：想加句子直接往数组里加即可。
/// </summary>
public static class DailyQuote
{
    /// <summary>语录库。格式：内容 —— 出处（出处可省略）。</summary>
    private static readonly string[] Quotes =
    {
        // —— 学习与成长 ——
        "学而不思则罔，思而不学则殆。 —— 《论语》",
        "知之者不如好之者，好之者不如乐之者。 —— 《论语》",
        "博学之，审问之，慎思之，明辨之，笃行之。 —— 《礼记·中庸》",
        "纸上得来终觉浅，绝知此事要躬行。 —— 陆游",
        "问渠那得清如许？为有源头活水来。 —— 朱熹",
        "读书破万卷，下笔如有神。 —— 杜甫",
        "业精于勤，荒于嬉；行成于思，毁于随。 —— 韩愈",
        "不积跬步，无以至千里；不积小流，无以成江海。 —— 荀子",

        // —— 坚持与意志 ——
        "锲而不舍，金石可镂。 —— 荀子",
        "宝剑锋从磨砺出，梅花香自苦寒来。 —— 《警世贤文》",
        "千磨万击还坚劲，任尔东西南北风。 —— 郑燮",
        "长风破浪会有时，直挂云帆济沧海。 —— 李白",
        "路漫漫其修远兮，吾将上下而求索。 —— 屈原",
        "天行健，君子以自强不息。 —— 《周易》",

        // —— 思考与方法 ——
        "学贵有疑，小疑则小进，大疑则大进。 —— 陈献章",
        "尽信书，则不如无书。 —— 孟子",
        "温故而知新，可以为师矣。 —— 《论语》",
        "授人以鱼，不如授人以渔。 —— 《淮南子》",

        // —— 心态与格局 ——
        "静以修身，俭以养德。 —— 诸葛亮",
        "非淡泊无以明志，非宁静无以致远。 —— 诸葛亮",
        "海纳百川，有容乃大。 —— 林则徐",
        "会当凌绝顶，一览众山小。 —— 杜甫",
        "天生我材必有用，千金散尽还复来。 —— 李白",

        // —— 现代格言 ——
        "教育不是灌满一桶水，而是点燃一把火。 —— 叶芝",
        "把简单的事情做好就是不简单。 —— 佚名",
        "种一棵树最好的时间是十年前，其次是现在。 —— 谚语",
        "你今天多学的每一点，都会变成明天的底气。 —— 佚名",
        "慢慢来，比较快。 —— 佚名",
        "所有的努力，都不会白费，只是回报来得晚一些。 —— 佚名",
    };

    /// <summary>取今天的每日一言（同一天内恒定）。</summary>
    public static string ForToday() => ForDate(DateTime.Today);

    /// <summary>取指定日期对应的那一句（按日期确定性选取）。</summary>
    public static string ForDate(DateTime date)
    {
        if (Quotes.Length == 0) return "";

        // 用"自纪元起的天数"做索引：同一天恒定，跨天变化。
        // 不依赖随机数，这样每次启动看到的都一致。
        var days = (int)(date.Date - new DateTime(1970, 1, 1)).TotalDays;
        var index = ((days % Quotes.Length) + Quotes.Length) % Quotes.Length;
        return Quotes[index];
    }

    /// <summary>语录总数（供自检/测试）。</summary>
    public static int Count => Quotes.Length;

    /// <summary>取全部语录（供测试校验格式）。</summary>
    public static IReadOnlyList<string> All => Quotes;

    /// <summary>带前缀的展示文本，例如「每日一言 · 学而不思则罔…」。</summary>
    public static string ForDisplay(DateTime? date = null)
    {
        var q = date is null ? ForToday() : ForDate(date.Value);
        return string.IsNullOrEmpty(q) ? "" : $"每日一言 · {q}";
    }
}
