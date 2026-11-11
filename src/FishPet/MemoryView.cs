using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishPet;

// 记忆小本本的“看得见”那一半（本机加，2026-11-10）：给老大看的，不给模型看，所以不花 token。
// 只管两件事，都不碰小本本本身（Memory.cs 一行没改，正本仍是 memory.json）：
//   memory.md          把正在用的记忆排成一份只读的 Markdown，记、删、改、每天整理之后重写一次（查不重写）
//   memory-log.jsonl   一次记、取代、纠正、删除追加一行，带“改之前”的内容；只追加，超过 1 MB 才丢掉最老的一半
// 路径给 null 就什么也不做（测试和 test 模式用）。写不进去也不报错，不能因为看的文件坏了耽误记东西。
internal static class MemoryView
{
    internal const long LogBytes = 1_000_000;

    // ---- 排成 Markdown ----

    internal static string Render(IReadOnlyList<MemoryItem> items, DateTime now)
    {
        var text = new StringBuilder();
        text.AppendLine("# 大肥鱼的记忆小本本").AppendLine();
        text.AppendLine("> 自动生成的只读视图，正本是 memory.json；在这里改了不会同步回去，下次记东西时还会被覆盖。");
        text.AppendLine("> 想改：跟她说一声，她会纠正或删掉。每次变动见 memory-log.jsonl。");
        text.AppendLine($"> 更新于 {now:yyyy-MM-dd HH:mm} · 现有 {items.Count} 条").AppendLine();
        if (items.Count == 0) return text.AppendLine("（还没有记什么）").ToString();

        foreach (var category in MemoryBook.Categories)
        {
            var group = Sorted(items.Where(item => item.Category == category), category).ToList();
            if (group.Count == 0) continue;
            text.AppendLine($"## {category}（{group.Count}）").AppendLine();
            foreach (var item in group) text.AppendLine(Line(item));
            text.AppendLine();
        }
        return text.ToString();
    }

    // 日程按日子、纪念日按月日往后排；其余重要的在前，同样重要的新的在前
    private static IEnumerable<MemoryItem> Sorted(IEnumerable<MemoryItem> group, string category) => category switch
    {
        "日程" => group.OrderBy(item => item.Date, StringComparer.Ordinal),
        "纪念日" => group.OrderBy(item => item.Date is { Length: >= 5 } date ? date[^5..] : "", StringComparer.Ordinal),
        _ => group.OrderByDescending(item => item.Importance).ThenByDescending(item => item.CreatedAt),
    };

    private static string Line(MemoryItem item)
    {
        var notes = new List<string>();
        if (item.Source == "guess") notes.Add("她猜的");
        if (item.Slot != null) notes.Add(item.Slot);
        if (item.Importance >= 2) notes.Add(item.Importance >= 3 ? "很重要" : "重要");
        notes.Add("记于 " + DateTimeOffset.FromUnixTimeSeconds(item.CreatedAt).LocalDateTime.ToString("yyyy-MM-dd"));
        notes.Add("`" + item.Id + "`");
        return "- " + (item.Date != null ? item.Date + "　" : "") + item.Content + " · " + string.Join(" · ", notes);
    }

    internal static void Export(string? path, IReadOnlyList<MemoryItem> items, DateTime now)
    {
        if (path == null) return;
        try
        {
            var temp = path + ".tmp"; // 和 Memory.cs 一样：先写临时文件再整个换掉
            File.WriteAllText(temp, Render(items, now));
            File.Move(temp, path, overwrite: true);
        }
        catch { }
    }

    // ---- 变更记录 ----

    private static readonly JsonSerializerOptions Format = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // op：remember 新记 | replace 同一个 slot 取代了旧的 | correct 改了内容或来源 | forget 删掉
    // content 是改之后的（删掉时没有），before 是改之前的（新记时没有）。
    internal static void Log(string? path, string op, string id, string? content, string? before, string? source, DateTime now, long limit = LogBytes)
    {
        if (path == null) return;
        try
        {
            File.AppendAllText(path, JsonSerializer.Serialize(new { t = now.ToString("s"), op, id, content, before, source }, Format) + Environment.NewLine);
            if (new FileInfo(path).Length <= limit) return;
            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, lines[(lines.Length / 2)..]);
        }
        catch { }
    }
}
