using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishPet;

// 记忆小本本（本机加，2026-11-08，方案见 dev/记忆系统方案-最终版.md）：老大亲口告诉她、下次还要想起来的事。
// Claude 和 DSH 都经过同一个 FishPet.Mcp.exe 找到同一个她，所以只有这里存一份，两边读写的是同一本。
// 硬规矩：只在被问到（remember / recall / forget）时才动，hooks、说话、待机都不碰；交给模型的每次最多 10 条（默认 5）、
// 每条不超过 40 字，记多少条都一样多。全在内存里算，不调模型。和 Affection 一样只管数据，不碰窗口，整份放进 FishPet.Tests 里测。
//
//   memory.json          正在用的，最多 120 条（事实、纪念日不算进淘汰，但算在条数里）
//   memory-archive.json  被淘汰、被新的取代（supersededAt）、被删掉（forgottenAt）的，只有 recall 带 archive 时才读
internal sealed class MemoryItem
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "偏好";       // 偏好 | 事实 | 日程 | 纪念日
    public string? Slot { get; set; }                    // 同一个 slot 只留一条，新的取代旧的
    public string Source { get; set; } = "said";         // said 老大亲口说的 | guess 推测的
    public int Importance { get; set; } = 1;             // 1 普通 2 重要 3 很重要
    public long CreatedAt { get; set; }
    public long? LastRecalledAt { get; set; }
    public int RecallCount { get; set; }
    public string? Date { get; set; }                    // 日程：哪天（yyyy-MM-dd）；纪念日：每年的哪天（yyyy-MM-dd 或 MM-dd）
    public string From { get; set; } = "ai";
    // 第二版的主动表现用，现在只存不用（以后接上不用改格式）
    public MemoryFollow? Follow { get; set; }
    public bool Gentle { get; set; }
    // 进了归档才有
    public long? ArchivedAt { get; set; }
    public string? Why { get; set; }                     // superseded 被取代 | forgotten 被删 | expired 过期 | faded 久不想起 | full 满了
}

internal sealed class MemoryFollow
{
    public string On { get; set; } = "";
    public string Ask { get; set; } = "";
    public bool Done { get; set; }
}

internal sealed class MemoryBook
{
    internal const int Max = 120, MaxContent = 40, MaxRecall = 10, PerMinute = 5;
    internal static readonly string[] Categories = ["偏好", "事实", "日程", "纪念日"];
    private const long ArchiveBytes = 1_000_000;

    public int Version { get; set; } = 1;
    public string Tidied { get; set; } = "";
    public int Archived { get; set; }
    public List<MemoryItem> Items { get; set; } = [];

    private string? path, archivePath;
    private List<MemoryItem>? archive; // 用到才读
    private bool archiveDirty;
    private readonly Queue<DateTime> recent = new();
    private int serial;

    internal int Count => Items.Count;

    internal sealed record Recalled(MemoryItem Item, bool Old);

    // ---- 记 ----

    internal (MemoryItem Item, string? Replaced, bool Fresh) Remember(string content, string? category, string? slot, int? importance, string? source, string? date, string from, DateTime now)
    {
        content = Clean(content);
        if (content.Length == 0) throw new ArgumentException("content 不能是空的：用一句话写清要记的事。");
        if (content.Length > MaxContent) content = content[..MaxContent];
        category = string.IsNullOrWhiteSpace(category) ? "偏好" : category.Trim();
        if (!Categories.Contains(category)) throw new ArgumentException("category 只能是 偏好、事实、日程、纪念日。");
        source = string.IsNullOrWhiteSpace(source) ? "said" : source.Trim();
        if (source is not ("said" or "guess")) throw new ArgumentException("source 只能是 said（老大亲口说的）或 guess（你推测的）。");
        string? day = date == null ? null : Day(date, category == "纪念日") ?? throw new ArgumentException("date 写成 2026-11-11 这样" + (category == "纪念日" ? "（纪念日也可以只写 05-20）" : "") + "。");
        if (category == "日程" && day == null) throw new ArgumentException("日程要带 date（哪天，比如 2026-11-11），过了日子会自动收起来。");
        if (category == "纪念日" && day == null) throw new ArgumentException("纪念日要带 date（每年的哪天，比如 05-20）。");
        slot = string.IsNullOrWhiteSpace(slot) ? null : Clean(slot) is { Length: > 0 } s ? (s.Length > 20 ? s[..20] : s) : null;
        int weight = Math.Clamp(importance ?? (category is "事实" or "纪念日" ? 2 : 1), 1, 3);
        long stamp = Unix(now);

        // 一模一样的：不重复记，只把它往前提一提
        var same = Items.FirstOrDefault(item => Same(item.Content, content));
        if (same != null)
        {
            same.LastRecalledAt = stamp;
            same.Importance = Math.Max(same.Importance, weight);
            if (source == "said") same.Source = "said";
            if (slot != null) same.Slot ??= slot;
            if (day != null) same.Date = day;
            Save();
            return (same, null, false);
        }

        while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
        if (recent.Count >= PerMinute) throw new InvalidOperationException("一分钟里记了太多条了，挑最要紧的，过一会儿再记。");
        recent.Enqueue(now);

        string? replaced = null;
        if (slot != null && Items.FirstOrDefault(item => item.Slot == slot) is { } old)
        {
            replaced = old.Content;
            Retire(old, "superseded", stamp);
        }
        var fresh = new MemoryItem
        {
            Id = NewId(now), Content = content, Category = category, Slot = slot, Source = source, Importance = weight,
            CreatedAt = stamp, Date = day, From = from,
        };
        Items.Add(fresh);
        Cap(stamp);
        Save();
        return (fresh, replaced, true);
    }

    // ---- 查 ----

    internal List<Recalled> Recall(string? query, int limit, bool includeArchive, DateTime now)
    {
        limit = Math.Clamp(limit, 1, MaxRecall);
        var words = Words(query);
        long stamp = Unix(now);
        var today = DateOnly.FromDateTime(now);
        IEnumerable<(MemoryItem Item, bool Old)> pool = Items.Select(item => (item, false));
        if (includeArchive) pool = pool.Concat(LoadArchive().Select(item => (item, true)));
        var picked = pool
            .Select(entry => (entry.Item, entry.Old, Hit: Hit(entry.Item, words)))
            .Where(entry => words.Count == 0 || entry.Hit > 0)
            .Select(entry => (entry.Item, entry.Old, Score: Score(entry.Item, entry.Hit, today, stamp) - (entry.Old ? 3 : 0)))
            .OrderByDescending(entry => entry.Score).ThenByDescending(entry => entry.Item.LastRecalledAt ?? entry.Item.CreatedAt)
            .Take(limit)
            .Select(entry => new Recalled(entry.Item, entry.Old))
            .ToList();
        bool touched = false;
        foreach (var found in picked.Where(found => !found.Old))
        {
            found.Item.LastRecalledAt = stamp;
            found.Item.RecallCount++;
            touched = true;
        }
        // 想起来几次只是排序用的小账，丢了也不要紧：半分钟内刚写过文件就先不写，下次记、删、改时一起写，查得再勤也不老写盘
        if (touched && (DateTime.UtcNow - lastSaved).TotalSeconds > 30) Save();
        return picked;
    }

    // 分数：关键词命中 ×4、重要 ×1.5、日子临近、最近常用；24 小时内刚给出去过的往后放；推测的打对折。
    internal static double Score(MemoryItem item, double hit, DateOnly today, long now)
    {
        double score = hit * 4 + item.Importance * 1.5;
        if (DaysUntil(item, today) is { } days && days >= 0)
            score += item.Category == "纪念日" ? (days == 0 ? 8 : days <= 7 ? 2 : 0) : days == 0 ? 6 : days == 1 ? 4 : days <= 7 ? 2 : 0;
        if (item.LastRecalledAt is { } last)
        {
            long ago = now - last;
            if (ago < 86400) score -= 2;
            else if (ago < 7 * 86400) score += 1;
        }
        return item.Source == "guess" ? score * 0.5 : score;
    }

    // 关键词命中：整词出现算 1；中文没有空格，再按两个字一组看重合了多少（“想吃鱼了”能碰上“爱吃鱼”）。
    internal static double Hit(MemoryItem item, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return 0;
        string text = item.Content + " " + item.Slot;
        double best = 0;
        foreach (var word in words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase) || (word.Length >= 2 && word.Contains(item.Content, StringComparison.OrdinalIgnoreCase))) { best = Math.Max(best, 1); continue; }
            if (word.Length < 2) continue;
            int pairs = 0, matched = 0;
            for (int i = 0; i + 1 < word.Length; i++)
            {
                pairs++;
                if (text.Contains(word.Substring(i, 2), StringComparison.OrdinalIgnoreCase)) matched++;
            }
            if (matched > 0) best = Math.Max(best, 0.5 + 0.5 * matched / pairs);
        }
        return best;
    }

    private static List<string> Words(string? query) => string.IsNullOrWhiteSpace(query) ? [] :
        query.Split([' ', ',', '，', '、', '。', '？', '?', '！', '!', '；', ';', '：', ':', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => word.Length > 0).Distinct().Take(8).ToList();

    // ---- 删、改 ----

    internal (MemoryItem Item, string Done) Forget(string id, string? content, string? source, DateTime now)
    {
        var item = Items.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("没有 id 是 " + id + " 的记忆（先用 recall 查到它的 id）。");
        long stamp = Unix(now);
        if (content == null && source == null)
        {
            Retire(item, "forgotten", stamp);
            Save();
            return (item, "forgotten");
        }
        if (content != null)
        {
            content = Clean(content);
            if (content.Length == 0) throw new ArgumentException("content 不能是空的。");
            item.Content = content.Length > MaxContent ? content[..MaxContent] : content;
        }
        if (source != null)
        {
            if (source is not ("said" or "guess")) throw new ArgumentException("source 只能是 said 或 guess。");
            item.Source = source;
        }
        item.LastRecalledAt = stamp;
        Save();
        return (item, "corrected");
    }

    // ---- 整理：每天第一次见老大时做一次（和亲密度的早安判定一起），不靠模型 ----

    internal bool Tidy(DateTime now)
    {
        var dayKey = Affection.DayOf(now).ToString("yyyy-MM-dd");
        if (Tidied == dayKey) return false;
        Tidied = dayKey;
        long stamp = Unix(now);
        var today = DateOnly.FromDateTime(now);
        // 1. 过了日子 3 天的日程收起来（留 3 天给第二版“昨天考试怎么样”）
        foreach (var item in Items.Where(item => item.Category == "日程" && DaysUntil(item, today) is < -3).ToList()) Retire(item, "expired", stamp);
        // 2. 内容一样的合成一条
        foreach (var group in Items.GroupBy(item => Clean(item.Content).ToLowerInvariant()).Where(group => group.Count() > 1).ToList())
        {
            var keep = group.OrderByDescending(item => item.Importance).ThenByDescending(item => item.LastRecalledAt ?? item.CreatedAt).First();
            foreach (var extra in group.Where(item => item != keep).ToList()) { keep.RecallCount += extra.RecallCount; Retire(extra, "superseded", stamp); }
        }
        // 3. 不重要、60 天没想起的偏好收起来
        foreach (var item in Items.Where(item => item.Category == "偏好" && item.Importance <= 1 && stamp - (item.LastRecalledAt ?? item.CreatedAt) > 60L * 86400).ToList()) Retire(item, "faded", stamp);
        // 4. 还超出上限，就按最久没想起的收
        Cap(stamp);
        // 5. 归档太大：只留最近两年
        if (archivePath != null && File.Exists(archivePath) && new FileInfo(archivePath).Length > ArchiveBytes)
        {
            var old = LoadArchive();
            old.RemoveAll(item => stamp - (item.ArchivedAt ?? item.CreatedAt) > 730L * 86400);
            Archived = old.Count;
            archiveDirty = true;
        }
        Save();
        return true;
    }

    private void Cap(long stamp)
    {
        while (Items.Count > Max)
        {
            var loser = Items.Where(item => item.Category is not ("事实" or "纪念日"))
                .OrderBy(item => item.Importance).ThenBy(item => item.LastRecalledAt ?? item.CreatedAt).FirstOrDefault();
            if (loser == null) break; // 全是事实和纪念日：一条也不扔
            Retire(loser, "full", stamp);
        }
    }

    private void Retire(MemoryItem item, string why, long stamp)
    {
        Items.Remove(item);
        item.ArchivedAt = stamp;
        item.Why = why;
        LoadArchive().Add(item);
        Archived = archive!.Count;
        archiveDirty = true;
    }

    // ---- 日子 ----

    // 离那天还有几天（过了是负数）。纪念日看今年的那天，过了就看明年的。
    internal static int? DaysUntil(MemoryItem item, DateOnly today)
    {
        if (item.Date == null) return null;
        if (item.Category == "纪念日")
        {
            var parts = item.Date.Split('-');
            if (parts.Length < 2 || !int.TryParse(parts[^2], out var month) || !int.TryParse(parts[^1], out var dayOfMonth) || month is < 1 or > 12) return null;
            DateOnly On(int year) => new(year, month, Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month)));
            var next = On(today.Year);
            if (next < today) next = On(today.Year + 1);
            return next.DayNumber - today.DayNumber;
        }
        return DateOnly.TryParse(item.Date, System.Globalization.CultureInfo.InvariantCulture, out var on) ? on.DayNumber - today.DayNumber : null;
    }

    private static string? Day(string text, bool yearly)
    {
        text = text.Trim();
        if (text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var full))
            return full.ToString("yyyy-MM-dd");
        if (yearly && DateOnly.TryParseExact("2000-" + text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var md))
            return md.ToString("MM-dd");
        return null;
    }

    // 给模型看的“多久以前”，口语
    internal static string Ago(long then, long now)
    {
        long seconds = Math.Max(0, now - then);
        if (seconds < 3600) return "刚才";
        if (seconds < 86400) return seconds / 3600 + " 小时前";
        long days = seconds / 86400;
        if (days == 1) return "昨天";
        if (days < 30) return days + " 天前";
        if (days < 365) return days / 30 + " 个月前";
        return days / 365 + " 年多前";
    }

    // ---- 存取：先写临时文件再整个换掉，写到一半被杀也不坏 ----

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ArchiveFile { public List<MemoryItem> Items { get; set; } = []; }

    internal static MemoryBook Load(string? path, string? archivePath)
    {
        MemoryBook book = new();
        try
        {
            if (path != null && File.Exists(path) && JsonSerializer.Deserialize<MemoryBook>(File.ReadAllText(path), Format) is { } loaded) book = loaded;
        }
        catch { }
        book.Items ??= [];
        book.Items.RemoveAll(item => item == null || string.IsNullOrWhiteSpace(item.Content));
        book.Tidied ??= "";
        book.path = path;
        book.archivePath = archivePath;
        return book;
    }

    private List<MemoryItem> LoadArchive()
    {
        if (archive != null) return archive;
        archive = [];
        try
        {
            if (archivePath != null && File.Exists(archivePath) && JsonSerializer.Deserialize<ArchiveFile>(File.ReadAllText(archivePath), Format) is { } loaded)
                archive = loaded.Items?.Where(item => item != null).ToList() ?? [];
        }
        catch { }
        return archive;
    }

    private DateTime lastSaved;

    internal void Save()
    {
        if (path == null) return;
        lastSaved = DateTime.UtcNow;
        Write(path, JsonSerializer.Serialize(this, Format));
        if (archiveDirty && archivePath != null && archive != null)
        {
            Write(archivePath, JsonSerializer.Serialize(new ArchiveFile { Items = archive }, Format));
            archiveDirty = false;
        }
    }

    private static void Write(string target, string text)
    {
        try
        {
            var temp = target + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, target, overwrite: true);
        }
        catch { }
    }

    // ---- 小工具 ----

    private static string Clean(string text) => string.Join(" ", text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static bool Same(string a, string b) => string.Equals(Clean(a).TrimEnd('。', '.', '！', '!'), Clean(b).TrimEnd('。', '.', '！', '!'), StringComparison.OrdinalIgnoreCase);
    internal static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
    private string NewId(DateTime now)
    {
        string id;
        do id = "m" + now.ToString("yyMMddHHmmss") + (serial++ % 100).ToString("00");
        while (Items.Any(item => item.Id == id) || (archive?.Any(item => item.Id == id) ?? false));
        return id;
    }
}
