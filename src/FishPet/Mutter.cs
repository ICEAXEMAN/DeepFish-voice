using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FishPet;

// 待机自言自语：桌宠自己调大模型现编一句嘀咕，连同配套的神情动作和临时道具。
internal static class Mutter
{
    // 一句嘀咕：话、配的动作（可空）、临时戴的道具（可空）。
    internal sealed record Spoken(string Text, string? Cue, string[] Props);

    internal static async Task<Spoken?> Ask(HttpClient http, string baseUrl, string key, string model, string action)
    {
        var prompt = "你是桌面上的大肥鱼 Live2D 桌宠，正在待机做小动作「" + action + "」。小声嘀咕一句心里话（12 字以内，可爱自然，带点小剧场的想象力，比如突然入戏、自己吐槽自己），再给这句话配上神情动作。\n" +
            "只回复一行，格式：话|动作|道具\n" +
            "动作：从下面的词里挑 2～4 个，空格分隔，要和话的情绪对得上（可以先一个神情、再转折一个）：" + Body.MutterVocabulary + "\n" +
            "道具：临时戴一下的小东西，只能从「" + string.Join("、", PetProps.Names) + "」里选 0～1 个，用不着就留空；要选得合理（比如装侦探戴单片眼镜、装学霸戴黑框眼镜）。\n" +
            "不要引号、不要解释。例：哼哼，这案子我接了|皱眉 看左边 竖手指|单片眼镜";
        var payload = new
        {
            model,
            messages = new object[] { new { role = "user", content = prompt } },
            max_tokens = 80,
            temperature = 1.1,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return Parse(reply);
    }

    // 解析「话|动作|道具」。动作或道具不合规就丢掉，只留话；一句话都没有就是 null。
    internal static Spoken? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var line = reply.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var parts = line.Replace('｜', '|').Split('|');
        var words = parts[0].Trim().Trim('"', '“', '”', '「', '」');
        if (words.Length == 0) return null;
        string? cue = parts.Length > 1 && Body.CueReadable(parts[1].Trim(), 5) ? parts[1].Trim() : null;
        var props = parts.Length > 2
            ? parts[2].Split([' ', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries).Where(name => PetProps.Names.Contains(name)).Distinct().Take(1).ToArray()
            : [];
        return new Spoken(words, cue, props);
    }
}

// 能临时戴一下的小东西（配饰）。她自己戴着的、用户 dress 过的不在此列的处理由 PetWindow 管。
internal static class PetProps
{
    internal static readonly string[] Names = Outfit.Accessories.Select(item => item.Label).ToArray();
}
