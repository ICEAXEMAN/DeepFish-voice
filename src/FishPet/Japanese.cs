using System;
using System.Text;
using System.Text.RegularExpressions;

namespace FishPet;

// 实时语音合成前的日语文本规整（本机加，2026-11-12）：
// 时间「24:00」「24：00」「24時00分」改成日语习惯的「深夜0時」「12時30分」，
// 小数「0.3」改成「零点三」（合成器会把 0.3 念成「0 3」，小数点和小数位全丢，2026-11-12 复测确认）。
// 版本号（1.2.3）、IP（192.168.1.1）这种一串点的数字不动。
internal static partial class Japanese
{
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = FullWidthDigits().Replace(text, m => ((char)('0' + (m.Value[0] - '０'))).ToString());
        s = Decimal().Replace(s, m => DecimalPhrase(m.Value, m.Groups["i"].Value, m.Groups["f"].Value));
        // 百分号合成器直接吞掉（2026-11-12 复测：30% 只念出「30」），写成「パーセント」才念得出来。
        s = Percent().Replace(s, "パーセント");
        s = Clock().Replace(s, m => Replace(m, int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["m"].Value)));
        s = HourMinute().Replace(s, m => Replace(m, int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["m"].Value)));

        // 括号旁白连内容一起去掉（2026-11-12 实测）：
        // 「（笑）」会被念成「笑い」、「（ため息）」念成「ため息」，串进句子里意思就变了。
        s = Aside().Replace(s, "");
        // 区间符号 〜 ～ 会被吞掉或连读错：「一〜三」念成「いっさ」、「そう〜なの」长音也没了。
        // 数字之间改成「から」（一から三 → いちからさん，实测正确），其余情况直接去掉。
        s = Range().Replace(s, "から");
        s = LooseWave().Replace(s, "");
        // 全角斜杠让两边字母连读：「A／B」念成「エビ」。换成顿号，念成「エー、ビー」。
        s = Slash().Replace(s, "、");
        // 剩下的括号、书名号只去符号、留内容（里面的字是台词，得念）
        s = Bracket().Replace(s, "");
        return s;
    }

    // 小数：整数位用汉字数词，小数位一位一位念（0.25 → れいてんにご）。
    // 位数太多、或者整数位长得不像数量的（多半是版本号、ID），原样不动。
    private static string DecimalPhrase(string original, string whole, string fraction)
    {
        if (whole.Length > 6 || fraction.Length > 6) return original;
        if (!long.TryParse(whole, out var value)) return original;
        var text = new StringBuilder(Kanji(value));
        text.Append('点');
        foreach (var ch in fraction) text.Append(KanjiDigit(ch - '0'));
        return text.ToString();
    }

    private static string KanjiDigit(int digit) => digit switch
    {
        0 => "零", 1 => "一", 2 => "二", 3 => "三", 4 => "四",
        5 => "五", 6 => "六", 7 => "七", 8 => "八", 9 => "九",
        _ => "",
    };

    // 0 ~ 9999万 的整数写成日语数词：12 → 十二、105 → 百五、12000 → 一万二千。
    private static string Kanji(long value)
    {
        if (value is < 0 or >= 1_0000_0000) return value.ToString();
        if (value == 0) return "零";
        var text = new StringBuilder();
        var high = value / 1_0000;
        if (high > 0)
        {
            text.Append(Group4((int)high)).Append('万');
            value %= 1_0000;
        }
        if (value > 0 || text.Length == 0) text.Append(Group4((int)value));
        return text.ToString();
    }

    private static string Group4(int value)
    {
        if (value is < 0 or > 9999) return value.ToString();
        var units = new[] { "千", "百", "十", "" };
        var digits = new[] { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        var text = new StringBuilder();
        var place = 1000;
        for (var i = 0; i < 4; i++, place /= 10)
        {
            var digit = value / place % 10;
            if (digit == 0) continue;
            // 十／百／千 前面不写「一」（十、百、千），个位照写
            if (digit != 1 || i == 3) text.Append(digits[digit - 1]);
            text.Append(units[i]);
        }
        return text.ToString();
    }

    // 比例、版本号之类不碰：小时只认 0~29，分钟只认 0~59。
    private static string Replace(Match match, int hour, int minute)
    {
        if (hour is < 0 or > 29 || minute is < 0 or > 59) return match.Value;
        return Phrase(hour, minute);
    }

    private static string Phrase(int hour, int minute)
    {
        if (hour == 24) return minute == 0 ? "深夜0時" : $"深夜0時{minute}分";
        if (minute == 0) return $"{hour}時";
        return $"{hour}時{minute}分";
    }

    [GeneratedRegex("[０-９]")]
    private static partial Regex FullWidthDigits();

    // 一个小数点、左右都是数字，而且前后不紧挨数字、点或英文（v2.0 这种版本号不动）：0.3、12.5、０．２５。
    // 「1.2.3」「192.168.1.1」因为前后被点/数字贴着，匹配不上，原样留着。
    [GeneratedRegex(@"(?<![A-Za-z\d.．])(?<i>\d{1,6})[.．](?<f>\d{1,6})(?![\d.．])")]
    private static partial Regex Decimal();

    [GeneratedRegex("[%％]")]
    private static partial Regex Percent();

    // 24:00、24：00、24 : 00（前后不能紧挨别的数字）
    [GeneratedRegex(@"(?<!\d)(?<h>\d{1,2})\s*[:：]\s*(?<m>\d{2})(?!\d)")]
    private static partial Regex Clock();

    // 已经是「24時00分」「12時5分」的也收一下（00分→整点）
    [GeneratedRegex(@"(?<!\d)(?<h>\d{1,2})時(?<m>\d{1,2})分")]
    private static partial Regex HourMinute();

    // 括号旁白：（笑）（小声）(test) 这种，连内容一起删
    [GeneratedRegex(@"[（(][^（）()]{0,40}[）)]")]
    private static partial Regex Aside();

    // 数字之间的波浪号：一〜三 / 1～3 → 「から」
    [GeneratedRegex(@"(?<=[\d一二三四五六七八九十])\s*[〜～]\s*(?=[\d一二三四五六七八九十])")]
    private static partial Regex Range();

    // 剩下的波浪号、半角 ~ 直接去掉（「そう〜なの」只是拖长音）
    [GeneratedRegex(@"[〜～~]")]
    private static partial Regex LooseWave();

    // 斜杠：A／B → A、B
    [GeneratedRegex(@"[／/]")]
    private static partial Regex Slash();

    // 括号、书名号：只去符号留内容
    [GeneratedRegex(@"[《》〈〉「」『』【】〔〕［］\[\]｛｝{}<>＜＞]")]
    private static partial Regex Bracket();
}
