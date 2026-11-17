namespace FishPet;

// 小剧场每句之间留多久（本机加，2026-11-16）：用户发现带省略号的句子之间接得太急。
// 原来是「有录音就按录音长度接下一句、只留 0.45 秒气口」，脚本第三栏的停留秒数只在没录音时才用得上；
// 于是「挖到了！」（0.53 秒的录音）后面只隔 0.98 秒就接「……是上次在角落没修好的bug」，
// 说一半停住的地方反倒比普通句子还赶。
// 现在：这句尾巴是省略号（说一半停住），或者下一句从「……」起头，就按脚本里的停留秒数留够；
// 别的地方照旧只留 0.45 秒气口。只补省略号、不整段拖慢，短剧场（比如魔法少女）的节奏不动。
// 事件那边 PlayEvent 本来就是 max(脚本秒数, 录音 + 0.4)，这里补上之后两边就一致了。
internal static class SkitPace
{
    internal const double Breath = 0.45;   // 普通句子之间的气口（秒）
    internal const double Shortest = 0.9;  // 再短的录音，这句也至少停这么久

    // 这句台词是不是说一半停在那儿（尾巴是省略号）。
    internal static bool TrailsOff(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith("……") || t.EndsWith("…") || t.EndsWith("...");
    }

    // 这句台词是不是从省略号起头（念它之前要留白）。
    internal static bool LeadsIn(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("……") || t.StartsWith("…") || t.StartsWith("...");
    }

    // 说完这一句之后等多久。voice 是录音长度（没有录音就是 null），script 是脚本第三栏的停留秒数，
    // next 是下一句台词（最后一句就是 null）：最后一句尾巴也是省略号的话照样留够，让她说完再收尾。
    internal static double Wait(string said, double? voice, double script, string? next)
    {
        var wait = said.Length > 0 && voice != null
            ? Math.Max(Shortest, voice.Value + Breath)
            : Math.Max(script, said.Length * 0.12 + 0.3); // 没录音时按字数留足气泡出完的时间（上游 2026-11-12）
        if (said.Length > 0 && voice != null && (TrailsOff(said) || (next != null && LeadsIn(next))))
            wait = Math.Max(wait, script);
        return wait;
    }
}
