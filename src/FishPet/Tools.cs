using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FishPet;

// What an agent can do with her, as MCP tools. The descriptions are what the agent reads: plain and complete.
internal static class Tools
{
    internal static string PipeName => "FishPet." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "|" +
        System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/').ToUpperInvariant()))).Substring(0, 20);
    internal static object Text(object value) => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } } };
    internal static object Error(string message) => new { isError = true, content = new[] { new { type = "text", text = message } } };
    // A result with one more piece of text after what it already says.
    internal static object WithNotice(object result, string notice)
    {
        var node = JsonSerializer.SerializeToNode(result)!.AsObject();
        if (node["content"] is System.Text.Json.Nodes.JsonArray content) content.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = notice });
        return node;
    }
    internal static object Image(string dataUrl, object metadata) => new { content = new object[] {
        new { type = "text", text = JsonSerializer.Serialize(metadata) },
        new { type = "image", mimeType = dataUrl[5..dataUrl.IndexOf(';')], data = dataUrl[(dataUrl.IndexOf(',') + 1)..] } } };

    private static string Labels((string Label, string Expression)[] items) => string.Join("、", items.Select(item => item.Label));
    // Where the agent finds the words for its body: the descriptions name it in full, wherever the plugin was put.
    internal static string BodyGuide => System.IO.Path.Combine(Places.Root, "prompt", "身体说明.md");
    // The guide's version, so an agent whose persona holds an older copy sees it in the say description (2026-11-05).
    internal static string? GuideVersion => System.IO.File.Exists(BodyGuide) ? Body.GuideVersion(System.IO.File.ReadAllText(BodyGuide)) : null;
    private static string GuideNote => GuideVersion is { } version ? "身体说明现在是 " + version + " 版；你人设里【你的身体·完】那行写的版本对不上或者没有，就是旧版：告诉用户，照 " +
        System.IO.Path.Combine(Places.Root, "AGENTS.md") + " 最后“更新插件”的第 6 步换成新版。" : "";

    // Said to the agent as its own body, not a figure it works: told about "her", agents only moved her when asked to.
    internal static object[] Definitions => [
        Tool("say", "用你在桌面上的身体说话（头上冒气泡）。每次回用户都用它说，不用等用户提醒。要说的几句先想好，写在同一个 text 里一次发出去：一行一个气泡，" +
            "会一句接一句说完；别说一句、想一会儿、再调一次。text 里用〔〕写神情和动作（词和规则见 " + BodyGuide + "），〔〕不会显示，身体照做；说之前在心里把这几句演一遍，说到哪儿脸和身子怎么变，就在哪儿写〔〕；只在开头写一个，整句身体就只动那一下。" +
            "字按说话的节奏一个个出来，碰到〔〕先做动作，动作跟着后面那段话的长短和语气伸缩。audio 可选：这段话的语音（本机 wav 文件的完整路径），" +
            "会播放一遍，嘴按音量动，〔〕按语音进度做；有语音时整段是一个气泡。speech 可选：要念出来的日文（字幕 text 仍是中文，只有声音说日文），" +
            "现场合成；给了 speech 就整段合成一个气泡。" +
            "会等之前的话演完再开始；返回时第一句已经开始说了；返回里有 note 的，是身体想跟你说的话（哪些词没看懂、要不要边说边动）。" + GuideNote,
            """{"text":{"type":"string","minLength":1,"maxLength":2000},"audio":{"type":"string","maxLength":1000},"speech":{"type":"string","maxLength":1000}}""", ["text"]),
        Tool("look_in_mirror", "照镜子，看你现在的样子（白底图，只照头到腰）。try 可选：写上想试的神情和动作（〔〕里那些词，不用写括号），" +
            "会先真的做一遍，从头到做完按先后给六张（三列两行，从左到右、从上到下）。", """{"try":{"type":"string","maxLength":200}}""", []),
        Tool("look_back", "回头看你刚才说话时的样子：say 的每句话都会自动拍下来，从头到说完按先后六张，留半小时、最多 20 句。" +
            "which 可选：1 是最近一句，2 是再前一句……不填就是最近一句。返回里有那句话（said）、几秒前（secondsAgo）和还能看的几句（recent）。",
            """{"which":{"type":"integer","minimum":1,"maximum":20}}""", []),
        Tool("set_activity", "你开始或做完一件要盯着看的事（看视频、读文章、写东西……kind 随便起个名字）。有事在做时你会戴上黑框眼镜，全做完才摘；" +
            "用户自己给你戴着眼镜时不叠戴、也不摘。", """{"kind":{"type":"string","minLength":1,"maxLength":40},"active":{"type":"boolean"}}""", ["kind", "active"]),
        Tool("dress", "换衣服、戴或摘配饰，会一直保持、下次启动也记得。衣服：" + Labels(Outfit.Clothes) + "；配饰：" + Labels(Outfit.Accessories) +
            "（兔牙张嘴时才看得到）。wear 是要穿戴上的，remove 是要脱掉摘下的。返回现在穿戴着的。",
            """{"wear":{"type":"array","items":{"type":"string"}},"remove":{"type":"array","items":{"type":"string"}}}""", []),
        Tool("get_state", "读你现在的状态和设置：体型、穿戴、帧率、大小、气泡大小和停多久、全屏时藏不藏（现在是不是正藏着）、视线是否跟着鼠标、空闲小动作的间隔、窗口位置、是否正在说话；" +
            "插件有新版本时 update 里写着落后几处改动、各是什么；affection 是你和用户的亲密度（0–200 分，五级：初识、熟悉、亲近、黏人、最喜欢），说话的亲疏可以跟着它走；" +
            "focus 是番茄钟（phase：work 专注中、break 休息中、none 没开）；memory 只是小本本里有几条（内容用 recall 查）；voice_pack 是固定台词语音包（second 第二套 / calm 冷静、沉稳）。", "{}"),
        Tool("focus", "番茄钟、专注陪伴：用户说要专注、学习、陪他认真一会儿时用。action：start 开始专注 25 分钟（你会戴上眼镜，只做安静的小动作，不嘀咕、不演小剧场），" +
            "到点会响一声提示音、伸个懒腰叫他休息，休息 5 分钟后再响一声问他继续不继续；stop 提前结束。专注时别用 say 打扰他，除非他先找你。",
            """{"action":{"type":"string","enum":["start","stop"]}}""", ["action"]),
        Tool("remember", "记进小本本（Claude、DSH 共用）。只记老大自己的、以后用得上的事，一句话≤40字；不记工作细节、闲聊、密码。日程、纪念日要带 date（纪念日可只写 05-20）；slot 同类只留最新（如 偏好·饮料）；source guess 是你猜的。",
            """{"content":{"type":"string"},"category":{"type":"string","enum":["偏好","事实","日程","纪念日"]},"date":{"type":"string"},"slot":{"type":"string"},"importance":{"type":"integer","minimum":1,"maximum":3},"source":{"type":"string","enum":["said","guess"]}}""", ["content"]),
        Tool("recall", "查小本本：和老大本人有关时才查，一轮最多一次。query 关键词；archive 为 true 连以前的也查。",
            """{"query":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":10},"archive":{"type":"boolean"}}""", []),
        Tool("forget", "记错时用：只给 id 是删掉；给 content 是改正；source said 是确认猜对了。id 从 recall 来。",
            """{"id":{"type":"string"},"content":{"type":"string"},"source":{"type":"string","enum":["said","guess"]}}""", ["id"]),
        Tool("update_settings", "改设置，只改给了的项，保存下来。shape 体型：small（小只，默认）、chibi（Q 版）、full（全身）；fps 帧率：30、60、90、120；" +
            "scale 大小 0.5–2；bubble_scale 气泡大小 0.5–2（字和气泡一起缩放，跟 scale 无关）；bubble_seconds 说完后气泡停多久：0 按字数自动（5–20 秒，默认），" +
            "5–180 固定秒数，-1 一直留着到下一句或被点掉；volume 语音音量 0–1（0 静音只出气泡，默认 0.8）；voice_enabled 语音总开关（关掉后预录和实时语音都不出声，托盘里实时语音/自动启动/音量一起隐藏）；voice_pack 固定台词语音包：second（第二套，默认）、calm（冷静、沉稳）；" +
            "tts_enabled 现编的话要不要现场合成语音（默认开，靠本机 GPT-SoVITS 服务）；tts_url 服务地址（默认 http://127.0.0.1:9880）；tts_speed 合成语速（默认 0.95）；tts_autostart 服务没在跑时由桌宠自己拉起来（默认开）；tts_dir 服务目录（留空自动找）；" +
            "hide_on_fullscreen 别的程序全屏（游戏、视频）时藏起来、不画，默认开；" +
            "follow_mouse 视线跟着鼠标；start_hidden 启动时先收起待命（用户叫才出场）；keep_on_screen 拖到屏幕边会弹回来（默认开），关掉就能拖出屏幕外；idle_seconds 没人理你时多久做一个小动作（15–600 秒）；check_updates 启动时和每 12 小时去 GitHub 看有没有新版本，默认开；coax_sleep 每晚 00:30 起自己出来哄睡（四层），coax_lunch 每天 11:30 提醒吃午饭，都按这台电脑的时钟、默认开；birthday 用户的生日（10-21 这样，那天你会说生日快乐；空字符串清掉）；" +
            "tex_scale 立绘清晰度：1 高清（默认，4096 原图）、0.5 标准、0.25 流畅（省显存），改档位立绘会重新加载几秒；" +
            "music_dance 听到电脑在放音乐就跟着跳（默认开）：只认有拍子的声音，说话、播客不会让她乱蹦；music_apps 白名单（进程名，逗号分隔），只有这些音乐软件在出声才跳。" +
            "context_chat 桌面实况（默认开）：DSH 那边的 dafeiyu-context 插件会看着你正在用的软件/网页，场景站稳了自己搭话；关掉就当没这个功能，托盘“更多设置”里也能关。",
            """{"shape":{"type":"string","enum":["small","chibi","full"]},"fps":{"type":"integer","enum":[30,60,90,120]},"scale":{"type":"number","minimum":0.5,"maximum":2},"bubble_scale":{"type":"number","minimum":0.5,"maximum":2},"bubble_seconds":{"type":"integer","minimum":-1,"maximum":180},"volume":{"type":"number","minimum":0,"maximum":1},"voice_enabled":{"type":"boolean"},"voice_pack":{"type":"string","enum":["second","calm"]},"tts_enabled":{"type":"boolean"},"tts_url":{"type":"string"},"tts_speed":{"type":"number","minimum":0.7,"maximum":1.5},"tts_autostart":{"type":"boolean"},"tts_dir":{"type":"string"},"hide_on_fullscreen":{"type":"boolean"},"follow_mouse":{"type":"boolean"},"start_hidden":{"type":"boolean"},"keep_on_screen":{"type":"boolean"},"idle_seconds":{"type":"integer","minimum":15,"maximum":600},"check_updates":{"type":"boolean"},"coax_sleep":{"type":"boolean"},"coax_lunch":{"type":"boolean"},"birthday":{"type":"string"},"tex_scale":{"type":"number","enum":[0.25,0.5,1]},"music_dance":{"type":"boolean"},"music_apps":{"type":"string"},"context_chat":{"type":"boolean"}}""", []),
        Tool("coax", "哄睡演示：把桌宠窗口摆到屏幕正中（配合放大和表情，快速看各层递进的动作效果）。stage：1 第一层（温柔关心）、2 第二层（严肃）、3 第三层（生气「时间到了」）、4 第四层（黑脸质问）、0 回到原来的位置。text 可选：第四层的质问台词，由 AI 实时生成传入（一个字一个字慢慢说）。5 演示全部待机小动作；6 演示一个待机小剧场（text 给小剧场名，不给就随机，不用调 say）。",
            """{"stage":{"type":"integer","minimum":0,"maximum":6},"text":{"type":"string"}}""", ["stage"]),
        Tool("recent_events", "用户最近怎么碰了你：点了哪里（zone：head 头、face 脸、hand 手、skirt 裙子、tail 尾巴、body 身上）、你的反应（reaction）、拖动和放下。" +
            "after 可选：只要这个序号之后的。反应身体自己会做，这里只是让你知道，要不要接一句话由你决定。", """{"after":{"type":"integer","minimum":0}}""", []),
        Tool("show", "出场：用户叫你出来时用。你本来收着（启动时默认收着，get_state 里 hidden 为 true）就会现身并打个招呼；已经在就什么也不变。出场以后才用 say 在桌面上说话。", "{}"),
        Tool("hide", "收起：用户让你先退下、但不关掉时用。会等话说完、鞠躬、淡出，桌宠程序和接口都还开着，只是不露面（待机表演也停了）；之后只在聊天窗口说话，用户再叫你就 show。想彻底关掉才用 quit。", "{}"),
        Tool("quit", "鞠躬告别，然后收起你的身体（关掉桌宠）。用户让你收起来时用；收起后只在聊天窗口说话，下次用任何工具时会自动再出来。", "{}"),
    ];

    private static object Tool(string name, string description, string properties, string[]? required = null) => new {
        name, description, inputSchema = new { type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties),
            required = required ?? [], additionalProperties = false } };

    internal static string RequiredText(JsonElement args, string name, int maximum = 2000)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"{name} 要是 1–{maximum} 个字的文字。");
        return value.GetString()!;
    }

    internal static string? OptionalText(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    internal static string[] Texts(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).ToArray() : [];
}


