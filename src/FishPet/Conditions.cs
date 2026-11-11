using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FishPet;

// 台词和小剧场的“条件”（本机加，2026-11-06）：写在 mutter.txt 每行最后一栏、skits.txt 标题行的「@」后面。
// 条件之间用空格隔开；同一类里是“或”，不同类之间是“且”。没写条件的台词永远可用，和以前一样。
//   时段   清晨(5–7点) 上午(8–10) 中午(11–13) 下午(14–16) 傍晚(17–18) 晚上(19–22) 深夜(23–4)
//   几点   8点、23点、0点……（本地时间的小时）
//   星期   周一……周日、工作日、周末
//   日期   10月21日 这样的固定日子，或 元旦 情人节 妇女节 劳动节 儿童节 教师节 国庆 平安夜 圣诞 跨年，
//          和农历的 春节 除夕 元宵 端午 七夕 中秋 重阳（用系统自带的农历，1901–2100 年都能算）
//   生日   用户自己的生日：在 settings.json 的 Birthday 里写（比如 10-21，update_settings 的 birthday 也能改），没写就永远不出现
//   整点   这条是“整点报时”专用：每个钟点刚到的前几分钟演一次，不参加平时的抽签
//   亲密   亲密1…亲密5：亲密度到了这一级（含）以上才出现（本机加，2026-11-08，见 Affection.cs）；和别的条件是“且”
// 抽签时：条件符合的加权（时段、星期、几点 ×4；有日期的 ×15，那天几乎一定能碰上），不符合的这次不抽。
internal sealed class Condition
{
    internal static readonly Condition Always = new([], [], [], [], false, 0);

    // 用户的生日（MM-dd），桌宠启动和改设置时填进来；空着就没有生日这天。
    internal static string? Birthday { get; set; }

    private static readonly (string Name, int From, int To)[] Periods =
    [
        ("清晨", 5, 8), ("上午", 8, 11), ("中午", 11, 14), ("下午", 14, 17), ("傍晚", 17, 19), ("晚上", 19, 23),
    ];
    private static readonly Dictionary<string, DayOfWeek> Days = new()
    {
        ["周一"] = DayOfWeek.Monday, ["周二"] = DayOfWeek.Tuesday, ["周三"] = DayOfWeek.Wednesday, ["周四"] = DayOfWeek.Thursday,
        ["周五"] = DayOfWeek.Friday, ["周六"] = DayOfWeek.Saturday, ["周日"] = DayOfWeek.Sunday,
    };
    private static readonly Dictionary<string, (int Month, int Day)> SolarFestivals = new()
    {
        ["元旦"] = (1, 1), ["情人节"] = (2, 14), ["妇女节"] = (3, 8), ["劳动节"] = (5, 1), ["儿童节"] = (6, 1),
        ["教师节"] = (9, 10), ["国庆"] = (10, 1), ["平安夜"] = (12, 24), ["圣诞"] = (12, 25), ["跨年"] = (12, 31),
    };
    private static readonly Dictionary<string, (int Month, int Day)> LunarFestivals = new()
    {
        ["春节"] = (1, 1), ["元宵"] = (1, 15), ["端午"] = (5, 5), ["七夕"] = (7, 7), ["中秋"] = (8, 15), ["重阳"] = (9, 9),
    };
    private static readonly Regex FixedDate = new(@"^(\d{1,2})月(\d{1,2})日$", RegexOptions.Compiled);
    private static readonly Regex HourOfDay = new(@"^(\d{1,2})点$", RegexOptions.Compiled);
    private static readonly Regex Closeness = new(@"^亲密([1-5])$", RegexOptions.Compiled);

    private readonly string[] periods;
    private readonly int[] hours;
    private readonly string[] days;
    private readonly string[] dates;

    private Condition(string[] periods, int[] hours, string[] days, string[] dates, bool chime, int minLevel)
    {
        this.periods = periods; this.hours = hours; this.days = days; this.dates = dates; Chime = chime; MinLevel = minLevel;
    }

    // 亲密度至少要到第几级（0 就是不管）。
    internal int MinLevel { get; }

    // 整点报时专用。
    internal bool Chime { get; }
    internal bool Plain => periods.Length + hours.Length + days.Length + dates.Length == 0 && !Chime && MinLevel == 0;
    // 符合时的权重：有日期 15，有别的条件 4，什么都没写 1。
    internal int Boost => dates.Length > 0 ? 15 : periods.Length + hours.Length + days.Length > 0 || MinLevel > 0 ? 4 : 1;

    // 读不懂的词返回 null（调用的人记一笔、跳过这行，不让写错的条件变成“永远可用”）。
    internal static Condition? Parse(string? spec, out string? bad)
    {
        bad = null;
        if (string.IsNullOrWhiteSpace(spec)) return Always;
        var periods = new List<string>(); var hours = new List<int>(); var days = new List<string>(); var dates = new List<string>(); bool chime = false; int minLevel = 0;
        foreach (var token in spec.Split([' ', '　', ',', '，', '、'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "整点") chime = true;
            else if (Closeness.Match(token) is { Success: true } close) minLevel = Math.Max(minLevel, int.Parse(close.Groups[1].Value));
            else if (token == "深夜" || Periods.Any(period => period.Name == token)) periods.Add(token);
            else if (HourOfDay.Match(token) is { Success: true } hour && int.Parse(hour.Groups[1].Value) is >= 0 and <= 23 and var value) hours.Add(value);
            else if (Days.ContainsKey(token) || token is "工作日" or "周末") days.Add(token);
            else if (FixedDate.Match(token) is { Success: true } date && ValidDay(int.Parse(date.Groups[1].Value), int.Parse(date.Groups[2].Value))) dates.Add(token);
            else if (SolarFestivals.ContainsKey(token) || LunarFestivals.ContainsKey(token) || token is "除夕" or "生日") dates.Add(token);
            else { bad = token; return null; }
        }
        return new Condition([.. periods], [.. hours], [.. days], [.. dates], chime, minLevel);
    }

    // 10-21、10/21、1021、10月21日 都认，统一成 MM-dd；读不懂返回 null。
    internal static string? NormalBirthday(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = System.Text.RegularExpressions.Regex.Matches(text, @"\d+").Select(match => int.Parse(match.Value)).ToArray();
        if (digits.Length == 1 && digits[0] is >= 101 and <= 1231) digits = [digits[0] / 100, digits[0] % 100];
        if (digits.Length != 2 || digits[0] is < 1 or > 12 || digits[1] < 1 || digits[1] > DateTime.DaysInMonth(2024, digits[0])) return null;
        return $"{digits[0]:00}-{digits[1]:00}";
    }

    private static bool ValidDay(int month, int day) => month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2024, month);

    // 本地时间 local 符不符合（整点这个标记不管，由调用的人看 Chime）；level 是现在的亲密度等级。
    internal bool Matches(DateTime local, int level) => level >= MinLevel && Matches(local);

    internal bool Matches(DateTime local) =>
        (periods.Length == 0 || periods.Any(period => InPeriod(period, local.Hour)))
        && (hours.Length == 0 || hours.Contains(local.Hour))
        && (days.Length == 0 || days.Any(day => OnDay(day, local.DayOfWeek)))
        && (dates.Length == 0 || dates.Any(date => OnDate(date, local)));

    private static bool InPeriod(string name, int hour) =>
        name == "深夜" ? hour >= 23 || hour < 5 : Periods.Any(period => period.Name == name && hour >= period.From && hour < period.To);

    private static bool OnDay(string name, DayOfWeek day) => name switch
    {
        "工作日" => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        "周末" => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
        _ => Days[name] == day,
    };

    private static bool OnDate(string name, DateTime local)
    {
        if (FixedDate.Match(name) is { Success: true } fixedDate) return local.Month == int.Parse(fixedDate.Groups[1].Value) && local.Day == int.Parse(fixedDate.Groups[2].Value);
        if (SolarFestivals.TryGetValue(name, out var solar)) return local.Month == solar.Month && local.Day == solar.Day;
        if (name == "除夕") return Lunar(local.AddDays(1)) is (1, 1);
        if (name == "生日") return Birthday is { Length: 5 } birthday && local.ToString("MM-dd") == birthday;
        return LunarFestivals.TryGetValue(name, out var lunar) && Lunar(local) is { } today && today == lunar;
    }

    private static readonly ChineseLunisolarCalendar Calendar = new();

    // 农历的月、日（闰月不算节日，返回 null）；超出系统自带范围也返回 null。
    internal static (int Month, int Day)? Lunar(DateTime date)
    {
        try
        {
            int year = Calendar.GetYear(date), month = Calendar.GetMonth(date), leap = Calendar.GetLeapMonth(year);
            if (leap > 0 && month == leap) return null;
            if (leap > 0 && month > leap) month--;
            return (month, Calendar.GetDayOfMonth(date));
        }
        catch { return null; }
    }
}
