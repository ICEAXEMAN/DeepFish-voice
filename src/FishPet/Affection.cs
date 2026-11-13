using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FishPet;

// 亲密度和养成（本机加，2026-11-08）：0–200 分，五个等级。存在插件文件夹的 affection.json，和设置分开。
// 只管算分，不碰窗口和身体（PetWindow 拿结果去演），所以整份放进 FishPet.Tests 里测。
//
//   加分：摸头 +1（每天最多 5 次）、聊天 +1（用户发一条消息，每天最多 6 次）、完成一个番茄钟 +1（每天最多 3 次）、
//         按时睡（前一晚最后一次动静在 0:30 前）+3、被哄才睡（0:30–0:50）+1。
//   扣分：戳裙子 -1（每天最多 3 次）、连点 5 下挨打 -2（每天最多 2 次）、过了 1 点还在活动 -2、
//         连续两天以上一点动静都没有：回来时从第 3 天起每天 -2。
//   每天加分合计最多 15 分、扣分合计最多 5 分（用户定的，2026-11-08）：各项自己的次数上限照旧，加起来超出的部分不算；
//   加分和扣分各算各的，互不占额度（冷落、熬夜也算在当天的 5 分里）。
//   掉级：会掉，但到了“磨合”（20 分）以后最多掉回 20，不会再掉回“初识”。
//
// “动静”是用户本人在：发消息、点她、拖她、点托盘。按时睡觉看的是“前一晚最后一次动静”，在第二天早上 5 点后第一次有动静时算。
internal sealed class Affection
{
    internal const int Max = 200;
    internal const int DailyGain = 15, DailyLoss = 5;
    internal static readonly (int From, string Name)[] Levels = [(0, "初识"), (20, "磨合"), (60, "同频"), (110, "默契"), (160, "羁绊")];

    // 每种加减分：分数、每天最多算几次（0 不限）。按时睡、被哄才睡、熬夜、冷落一晚 / 一天只会算一次，不用上限。
    internal static readonly Dictionary<string, (int Points, int PerDay)> Rules = new()
    {
        ["摸头"] = (1, 5), ["聊天"] = (1, 6), ["番茄"] = (1, 3),
        ["融化"] = (3, 1), // 摸头彩蛋（20 秒）：一天一次，取代这一下的普通 +1
        ["按时睡"] = (3, 0), ["被哄才睡"] = (1, 0),
        ["戳裙子"] = (-1, 3), ["挨打"] = (-2, 2), ["熬夜"] = (-2, 0), ["冷落"] = (-2, 0),
    };

    // 一天从早上 5 点算起（半夜两点还算前一天晚上）。
    internal static DateTime DayOf(DateTime local) => local.AddHours(-5).Date;

    public int Points { get; set; }
    public string Day { get; set; } = "";
    public Dictionary<string, int> Counts { get; set; } = [];
    public int TodayNet { get; set; }
    // 今天已经加了多少（只算加分，用来卡每天 15 分的上限）
    public int TodayGain { get; set; }
    // 今天已经扣了多少（正数，用来卡每天 5 分的上限）
    public int TodayLoss { get; set; }
    public DateTime? LastActive { get; set; }
    public string JudgedNight { get; set; } = "";
    // 还没说出口的话：events.txt 里的事件名（早安夸奖、熬夜念叨、想你了、升级、降级），她下次露面、闲着时说。
    public List<string> Pending { get; set; } = [];

    internal int Level => LevelOf(Points);
    internal string LevelName => Levels[Level - 1].Name;
    internal static int LevelOf(int points) { int level = 1; for (int i = 0; i < Levels.Length; i++) if (points >= Levels[i].From) level = i + 1; return level; }
    // 这一级的起点、下一级的起点（满级时就是 200）。
    internal int LevelFrom => Levels[Level - 1].From;
    internal int NextAt => Level < Levels.Length ? Levels[Level].From : Max;

    internal sealed record Change(string Kind, int Delta, int Before, int After)
    {
        internal int LevelBefore => LevelOf(Before);
        internal int LevelAfter => LevelOf(After);
    }

    // 加减一次分。超过当天次数上限的不算（返回 Delta 0）。
    internal Change Apply(string kind, DateTime local, int times = 1)
    {
        var (points, perDay) = Rules[kind];
        Roll(local);
        Counts.TryGetValue(kind, out var used);
        int count = perDay > 0 ? Math.Max(0, Math.Min(times, perDay - used)) : times;
        int before = Points;
        if (count > 0)
        {
            Counts[kind] = used + count;
            int change = points * count;
            if (change > 0) change = Math.Min(change, Math.Max(0, DailyGain - TodayGain));
            else change = Math.Max(change, -Math.Max(0, DailyLoss - TodayLoss));
            int target = Points + change;
            int floor = Points >= Levels[1].From ? Levels[1].From : 0;
            Points = Math.Clamp(target, points < 0 ? Math.Min(floor, Points) : 0, Max);
            TodayNet += Points - before;
            if (Points > before) TodayGain += Points - before;
            else TodayLoss += before - Points;
            NoteLevel(before);
        }
        return new Change(kind, Points - before, before, Points);
    }

    private void NoteLevel(int before)
    {
        int was = LevelOf(before), now = Level;
        if (now > was) Pending.Add("升级");
        else if (now < was) Pending.Add("降级");
    }

    private void Roll(DateTime local)
    {
        var day = DayOf(local).ToString("yyyy-MM-dd");
        if (day == Day) return;
        Day = day; Counts.Clear(); TodayNet = 0; TodayGain = 0; TodayLoss = 0;
    }

    // 用户有动静了。先看隔了多久（冷落）、前一晚几点睡的（按时睡 / 熬夜），再记下这次。返回这次因此加减的分。
    internal List<Change> Seen(DateTime local)
    {
        var changes = new List<Change>();
        var previous = LastActive;
        LastActive = local;
        Roll(local);
        if (previous is not { } last || local <= last) return changes;
        int gap = (DayOf(local) - DayOf(last)).Days;
        if (gap >= 3)
        {
            var change = Apply("冷落", local, gap - 2);
            if (change.Delta != 0) changes.Add(change);
            Pending.Add("想你了");
        }
        // 前一晚：最后一次动静落在那天傍晚 18 点以后才算（那晚压根没来，就不评）；一晚只评一次。
        var night = DayOf(local).AddDays(-1);
        var nightKey = night.ToString("yyyy-MM-dd");
        if (gap >= 1 && DayOf(last) == night && JudgedNight != nightKey && last - night >= TimeSpan.FromHours(18))
        {
            JudgedNight = nightKey;
            var after = last - night; // 从那天 0 点算起：24:30 就是第二天 0:30
            string? kind = after < TimeSpan.FromHours(24.5) ? "按时睡" : after < new TimeSpan(24, 50, 0) ? "被哄才睡" : after < TimeSpan.FromHours(25) ? null : "熬夜";
            if (kind != null)
            {
                changes.Add(Apply(kind, local));
                Pending.Add(kind switch { "按时睡" => "早安夸奖", "熬夜" => "熬夜念叨", _ => "早安" });
            }
        }
        return changes;
    }

    // ---- 存取 ----

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static Affection Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Affection>(File.ReadAllText(path)) is { } loaded)
            {
                loaded.Points = Math.Clamp(loaded.Points, 0, Max);
                loaded.Counts ??= []; loaded.Pending ??= []; loaded.Day ??= ""; loaded.JudgedNight ??= "";
                if (loaded.TodayGain == 0 && loaded.TodayNet > 0) loaded.TodayGain = loaded.TodayNet; // 加上限之前存的文件没有这一项
                return loaded;
            }
        }
        catch
        {
            // 读坏的原件留一份：不然下一次存盘就把它盖成一份全新的，羁绊点数无声归零
            try { if (File.Exists(path)) File.Copy(path, path + ".bad", overwrite: true); } catch { }
        }
        return new Affection();
    }

    internal void Save(string path)
    {
        try
        {
            // 先写临时文件再换过去，写到一半被杀进程、断电也不会留下半截文件
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Format));
            File.Move(temp, path, overwrite: true);
        }
        catch { }
    }
}

// 番茄钟（本机加，2026-11-08）：专注 25 分钟，休息 5 分钟。只管计时，到点告诉 PetWindow 去演、去响铃。
internal sealed class FocusTimer
{
    internal static readonly TimeSpan Work = TimeSpan.FromMinutes(25), Break = TimeSpan.FromMinutes(5);
    internal enum Phase { None, Work, Break }
    internal enum Turn { None, WorkDone, BreakDone }

    internal Phase Now { get; private set; }
    internal DateTime Ends { get; private set; }
    internal int RoundsToday { get; private set; }
    private DateTime roundsDay;

    internal void Start(DateTime now) { Now = Phase.Work; Ends = now + Work; }
    internal void Stop() { Now = Phase.None; Ends = default; }
    internal TimeSpan Left(DateTime now) => Now == Phase.None ? TimeSpan.Zero : Ends > now ? Ends - now : TimeSpan.Zero;
    internal TimeSpan Length => Now == Phase.Break ? Break : Work;

    internal Turn Tick(DateTime now)
    {
        if (Now == Phase.None || now < Ends) return Turn.None;
        if (Now == Phase.Work)
        {
            var day = Affection.DayOf(now.ToLocalTime());
            if (day != roundsDay) { roundsDay = day; RoundsToday = 0; }
            RoundsToday++;
            Now = Phase.Break; Ends = now + Break;
            return Turn.WorkDone;
        }
        Stop();
        return Turn.BreakDone;
    }
}

// 连续工作提醒（本机加，2026-11-08）：用户那边的事件一直在来，中间没断过 20 分钟以上，满 2 小时提醒起来动一动；提醒过再等 2 小时。
internal sealed class WorkStreak
{
    internal static readonly TimeSpan Gap = TimeSpan.FromMinutes(20), Span = TimeSpan.FromHours(2);
    private DateTime? start, last, reminded;

    // 记一次动静；返回现在该不该提醒。
    internal bool Note(DateTime now)
    {
        if (last is not { } before || now - before > Gap) start = now;
        last = now;
        return now - start >= Span && (reminded is not { } at || now - at >= Span);
    }

    internal void Reminded(DateTime now) => reminded = now;
    // 休息过了（番茄钟的休息结束），从头算。
    internal void Rested(DateTime now) { start = now; reminded = null; }
}

// 番茄钟的提示音：自己合成两声柔和的铃（正弦波加一点泛音，快起慢落），16 位单声道 wav，不带任何音频文件（本机加，2026-11-08）。
internal static class ChimeSound
{
    internal static byte[] Wave(double[] notes)
    {
        const int rate = 44100;
        double gap = 0.22, ring = 1.1;
        int length = (int)(rate * (gap * (notes.Length - 1) + ring));
        var samples = new double[length];
        for (int n = 0; n < notes.Length; n++)
        {
            int start = (int)(rate * gap * n);
            for (int i = 0; start + i < length && i < rate * ring; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Min(1, t / 0.008) * Math.Exp(-t * 4.2);
                double tone = Math.Sin(2 * Math.PI * notes[n] * t) + 0.28 * Math.Sin(2 * Math.PI * notes[n] * 2 * t) * Math.Exp(-t * 3) + 0.08 * Math.Sin(2 * Math.PI * notes[n] * 3.01 * t);
                samples[start + i] += tone * envelope * 0.22;
            }
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + length * 2); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(length * 2);
        foreach (var sample in samples) writer.Write((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }
}
