using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FishPet;

// 固定台词的录音（本机加，2026-11-09）：voice\index.json 里按台词文本查 wav 文件名，
// 查到就放录音、嘴跟着音量动；查不到就照旧只冒气泡打字。
// 索引是「文本 → 文件名」，文本去掉空格后比对；多气泡的台词（用「｜」分隔）按每一句单独查。
internal sealed class Voice
{
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

    internal int Count => files.Count;

    internal static Voice? Load(string folder)
    {
        try
        {
            var index = Path.Combine(folder, "index.json");
            if (!File.Exists(index)) return null;
            var voice = new Voice();
            using var doc = JsonDocument.Parse(File.ReadAllText(index));
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                var name = entry.Value.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var path = Path.Combine(folder, name);
                if (!File.Exists(path)) continue;
                var key = Norm(entry.Name);
                if (key.Length > 0) voice.files[key] = path;
            }
            return voice.files.Count > 0 ? voice : null;
        }
        catch
        {
            return null; // 索引坏了就当没有录音，不影响她说话
        }
    }

    internal string? Find(string text)
    {
        var key = Norm(text);
        return key.Length > 0 && files.TryGetValue(key, out var path) ? path : null;
    }

    private static string Norm(string text) => text.Replace(" ", "").Replace("\u3000", "").Trim();
}
