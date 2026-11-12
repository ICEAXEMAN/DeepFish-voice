using System;
using System.Text.RegularExpressions;

namespace FishPet;

// 实时语音合成前的日语文本规整（本机加，2026-11-12）：
// 时间「24:00」「24：00」「24時00分」改成日语习惯的「深夜0時」「12時30分」，
// 避免 GPT-SoVITS 把冒号和数字一个一个念出来。只动时间，不动别的文本。
internal static partial class Japanese
{
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = FullWidthDigits().Replace(text, m => ((char)('0' + (m.Value[0] - '０'))).ToString());
        s = Clock().Replace(s, m => Replace(m, int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["m"].Value)));
        s = HourMinute().Replace(s, m => Replace(m, int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["m"].Value)));
        return s;
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

    // 24:00、24：00、24 : 00（前后不能紧挨别的数字）
    [GeneratedRegex(@"(?<!\d)(?<h>\d{1,2})\s*[:：]\s*(?<m>\d{2})(?!\d)")]
    private static partial Regex Clock();

    // 已经是「24時00分」「12時5分」的也收一下（00分→整点）
    [GeneratedRegex(@"(?<!\d)(?<h>\d{1,2})時(?<m>\d{1,2})分")]
    private static partial Regex HourMinute();
}
