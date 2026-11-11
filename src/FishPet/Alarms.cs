using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FishPet;

// 到点叫人（本机加，2026-11-08）：按这台电脑自己的时钟，不靠哪个 AI 的定时任务，谁拿到插件都一样到点就来。
//   00:30 哄睡第一层（温柔）、00:40 第二层（严肃）、00:45 第三层（生气「时间到了」）、00:47 第四层（黑脸逐字质问）
//   11:30 午饭提醒
// 设置里 CoaxSleep / CoaxLunch 分别开关（托盘“更多”里有开关）。台词在插件根目录 alarms.txt，每次随机挑一条，不调模型、不花 token。
// 只在到点后 2 分钟内补演：电脑睡着、桌宠没开时错过的就算了，不会一开机连演一串。
internal static class Alarms
{
    internal sealed record Slot(int Hour, int Minute, string Name, int Stage, bool Sleep);

    internal static readonly Slot[] Slots =
    [
        new(0, 30, "哄睡1", 1, true),
        new(0, 40, "哄睡2", 2, true),
        new(0, 45, "哄睡3", 3, true),
        new(0, 47, "哄睡4", 4, true),
        new(11, 30, "午饭", 1, false),
    ];

    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    // 上次看到这次之间，有哪些点到了（只算 2 分钟内的）。
    internal static List<Slot> Due(DateTime last, DateTime now, bool sleep, bool lunch)
    {
        var due = new List<Slot>();
        if (now <= last) return due;
        foreach (var slot in Slots)
        {
            if (slot.Sleep ? !sleep : !lunch) continue;
            var at = now.Date.AddHours(slot.Hour).AddMinutes(slot.Minute); // 2 分钟的宽限只会落在今天
            if (at > last && at <= now && now - at < Grace) due.Add(slot);
        }
        return due;
    }

    // alarms.txt：「# 名字」开头一段，之后每行一条候选台词；「／」隔开的是一个个气泡，〔〕写动作。// 开头的是注释。
    internal static Dictionary<string, List<string>> Parse(IEnumerable<string> lines)
    {
        var all = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('#')) { var name = line[1..].Trim(); current = all.TryGetValue(name, out var have) ? have : all[name] = []; continue; }
            current?.Add(line);
        }
        return all;
    }

    // 文件丢了也有话说
    internal static readonly Dictionary<string, string> Fallback = new()
    {
        ["哄睡1"] = "〔前倾 八字眉 看过来〕这么晚了还不睡呀……／〔抚胸 微笑〕早点休息，明天再做也来得及",
        ["哄睡2"] = "〔皱眉 正视〕都十分钟了，怎么还没去睡／〔竖手指 微摇头〕熬夜伤身体，我是认真的",
        ["哄睡3"] = "〔皱眉 鼓腮〕时间到了！／〔竖手指〕现在、立刻、马上去睡觉！",
        ["哄睡4"] = "为什么还没睡？",
        ["午饭"] = "〔前倾 竖手指 微笑〕十一点半啦，该吃午饭咯／〔放下手 眨单眼〕先吃饱，再接着忙",
    };
}

