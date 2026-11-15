using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FishPet;

// 外面发来的“事件”（本机加，2026-11-06）：比如 Claude Code 的 hooks 在“等你授权”“这轮做完了”时，
// 往 /call/event 发一个事件名，她按 events.txt 里写的演一小下。全程不经过模型，不花 token。
//
// events.txt：「# 事件名 冷却秒数 [优先]」开头一个事件，之后每行一个候选「台词|动作|停留秒数」，每次随机演其中一个。
//   台词可空（只做动作），秒数可省（3.5）。冷却：同一事件两次之间至少隔这么久，防吵。
//   优先：可以打断她正在演的嘀咕、小剧场（没人开口说话的时候）；不写就是她闲着才演。
//   轮流：（用户 2026-11-14 加）每条机会均等、一轮里不重复：把所有候选洗牌，一条条演完一遍再洗下一遍（新一遍的第一条不会和上一遍最后一条相同）。不写就是每次随机挑。
internal sealed record EventLine(string Text, string? Cue, double Seconds);

internal sealed record EventDef(string Name, int Cooldown, bool Priority, EventLine[] Lines, bool Rotate = false);

internal sealed class EventBook
{
    internal const double DefaultSeconds = 3.5;

    private Dictionary<string, EventDef> defs = [];
    private readonly Dictionary<string, DateTime> lastPlayed = [];
    private readonly Dictionary<string, int> lastLine = [];
    private readonly Dictionary<string, Queue<int>> bags = [];
    private DateTime loadedAt;
    private string? loadedFrom;

    internal IReadOnlyCollection<string> Names => defs.Keys;

    // 文件改过就重读（每次来事件时看一眼修改时间，便宜）。
    internal void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) { defs = []; return; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (path == loadedFrom && stamp == loadedAt) return;
            defs = Parse(File.ReadAllLines(path));
            loadedFrom = path; loadedAt = stamp;
        }
        catch { /* 读不了就沿用旧的 */ }
    }

    internal static Dictionary<string, EventDef> Parse(IEnumerable<string> lines)
    {
        var found = new Dictionary<string, EventDef>();
        string? name = null; int cooldown = 0; bool priority = false, rotate = false;
        var pending = new List<EventLine>();
        void Close()
        {
            if (name != null && pending.Count > 0) found[name] = new EventDef(name, cooldown, priority, [.. pending], rotate);
            pending.Clear();
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('#'))
            {
                Close();
                var head = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                name = head.Length > 0 ? head[0] : null;
                cooldown = head.Skip(1).Select(word => int.TryParse(word, out var seconds) ? seconds : -1).FirstOrDefault(seconds => seconds >= 0);
                priority = head.Skip(1).Contains("优先");
                rotate = head.Skip(1).Contains("轮流");
                continue;
            }
            if (name == null) continue;
            var parts = line.Split('|', 3);
            var text = parts[0].Trim();
            var cue = parts.Length > 1 ? parts[1].Trim() : "";
            // 动作读不懂就只说话，不让一行写错拖垮整个事件。
            string? usable = cue.Length > 0 && Body.CueReadable(cue, 8) ? cue : null;
            double seconds = parts.Length > 2 && double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var given) && given is >= 1 and <= 20 ? given : DefaultSeconds;
            if (text.Length == 0 && usable == null) continue;
            pending.Add(new EventLine(text, usable, seconds));
        }
        Close();
        return found;
    }

    internal bool TryGet(string name, out EventDef def) => defs.TryGetValue(name, out def!);

    // 冷却没到不演。不改任何状态：真的演了才调 Played。
    internal bool Cooling(EventDef def, DateTime now) => lastPlayed.TryGetValue(def.Name, out var at) && (now - at).TotalSeconds < def.Cooldown;

    // 随机挑一条；有两条以上时不连着演同一条。
    internal EventLine Choose(EventDef def, Random random)
    {
        if (def.Rotate && def.Lines.Length > 1)
        {
            // 一轮不重复：袋子空了就把全部候选洗牌装满；新一轮的第一条不能是上一轮最后演的那条。
            if (!bags.TryGetValue(def.Name, out var bag) || bag.Count == 0 || bag.Any(i => i >= def.Lines.Length))
            {
                var order = Enumerable.Range(0, def.Lines.Length).OrderBy(_ => random.Next()).ToList();
                if (lastLine.TryGetValue(def.Name, out var previous) && order[0] == previous) { int swap = random.Next(1, order.Count); (order[0], order[swap]) = (order[swap], order[0]); }
                bags[def.Name] = bag = new Queue<int>(order);
            }
            int next = bag.Dequeue();
            lastLine[def.Name] = next;
            return def.Lines[next];
        }
        int index = random.Next(def.Lines.Length);
        if (def.Lines.Length > 1 && lastLine.TryGetValue(def.Name, out var before) && index == before)
            index = (index + 1 + random.Next(def.Lines.Length - 1)) % def.Lines.Length;
        lastLine[def.Name] = index;
        return def.Lines[index];
    }

    internal void Played(EventDef def, DateTime now) => lastPlayed[def.Name] = now;
}
