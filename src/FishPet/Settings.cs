using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FishPet;

// What is kept between starts, in settings.json beside the plugin. Only the agent changes it (the tools
// in Control.cs); there is no settings window.
internal sealed class Settings
{
    public string Shape { get; set; } = "full";
    public string[] Outfit { get; set; } = [];
    public int Fps { get; set; } = 60;
    public double Scale { get; set; } = 1.5;
    // 立绘清晰度：贴图缩放档位（1 = 高清 4096 原图、0.5 = 标准、0.25 = 流畅）。改档位要重启渲染器才生效。
    public double TexScale { get; set; } = 1;
    // Her speech bubble, words and all, apart from her own size.
    public double BubbleScale { get; set; } = 1;
    // 语音音量 0–1（本机加，2026-11-09）：固定台词的录音按这个音量放；0 就是静音，只出气泡。
    public double Volume { get; set; } = 0.25;
    // 语音总开关（本机加，2026-11-10）：关掉后所有说话都不出声、只出气泡；托盘里实时语音/自动启动/音量一起藏起来。
    public bool VoiceEnabled { get; set; } = true;
    // 固定台词用哪套录音（本机加，2026-11-10）：second = voice\（第二套），calm = voice_calm\（冷静、沉稳）。
    public string VoicePack { get; set; } = "second";
    // 实时语音（本机加，2026-11-09）：say 的现编台词交给本机 GPT-SoVITS（v5turbo）现场合成。
    // 服务没开就当没有，只打字；tts_speed 是合成语速，跟预录音的 0.95 保持一致。
    // 默认关闭：必须由用户本人在控制台打开并同意，才会连接/调用 GPT-SoVITS（2026-11-11）。
    public bool TtsEnabled { get; set; } = false;
    public string TtsUrl { get; set; } = "http://127.0.0.1:9880";
    // 语音服务没在跑的时候，由桌宠自己把它拉起来（2026-11-10 加，DSH 重启后不用手动开）
    // 默认关闭：不会自行拉起语音服务；用户手动打开才生效。
    public bool TtsAutostart { get; set; } = false;
    // 语音服务目录（空 = 自动找：先看桌宠上一级的「语音服务」，再看 D 盘那份）
    public string TtsDir { get; set; } = "";
    public double TtsSpeed { get; set; } = 0.95;
    // How long the bubble stays after her last word: 0 by how much she said (5 to 20 s), -1 until her next line or a click.
    public int BubbleSeconds { get; set; }
    // Out of the way while something else fills the screen, drawing nothing meanwhile (as the BigBlueFish pet does).
    public bool HideOnFullscreen { get; set; } = true;
    // Whether she looks on GitHub for a newer plugin when she starts and twice a day (Updates).
    public bool CheckUpdates { get; set; } = true;
    public bool FollowMouse { get; set; } = true;
    // 小剧场演出期间暂时关掉视线跟随鼠标（演完自动恢复）；在 settings.json 里改，不进 update_settings。（本机加，2026-11-06）
    public bool SkitPausesFollow { get; set; } = true;
    // 只有这些音乐软件在出声才跟着跳（进程名，逗号分隔；2026-11-10 用户要的白名单）
    public string MusicApps { get; set; } = "cloudmusic,qqmusic,qqmusicclient,spotify,applemusic,itunes,music,kugou,kuwo,qishui,musicbee,foobar2000,aimp";
    // 听到电脑在放音乐就跟着跳（本机加，2026-11-10）：在 settings.json 里改成 false 就只放歌不跳舞。
    public bool MusicDance { get; set; } = true;
    // 桌面实况（本机加，2026-11-12）：让 DSH 那边的 dafeiyu-context 插件看着前台窗口聊天——
    // 她知道你在用什么软件/网页，场景站稳了还会自己搭话（说话走实时语音）。
    // 关掉 = 那个插件当没这个功能：不写提示词、不开口。默认开，托盘的“更多设置”里能关。
    public bool ContextChat { get; set; } = false;
    // 启动时先收着：桌宠程序在运行，但她不露面，等用户叫她（show / say / coax）才出场。
    public bool StartHidden { get; set; }
    // 拖到屏幕边上会弹回来，脸始终留在工作区里。默认关：能拖出屏幕，只留一小截在外面能拖回来。
    public bool KeepOnScreen { get; set; } = false;

    // 第一次装好（还没有 settings.json）时她自己说一段欢迎；说完记成 true，以后不再说（本机加，2026-11-13，见 Welcome.cs）。
    // 默认 true：老用户的 settings.json 里没有这一项，读出来还是 true，不会补说。
    public bool Welcomed { get; set; } = true;
    // Seconds between the small things she does by herself when nobody talks to her.
    public int IdleSeconds { get; set; } = 240;
    // 到点叫人（本机加，2026-11-08）：按这台电脑的时钟，00:30 起四层哄睡、11:30 午饭提醒。默认关，托盘“更多”里能开。
    public bool CoaxSleep { get; set; } = true;
    public bool CoaxLunch { get; set; } = true;
    // 用户的生日（MM-dd，比如 10-21）：那天她会说生日快乐、演生日小剧场（mutter.txt、skits.txt 里写着 @生日 的）。空着就没有。
    public string Birthday { get; set; } = "";
    // 待机自言自语：桌宠自己调大模型现编嘀咕。
    public string LlmApiKey { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "https://api.deepseek.com";
    public string LlmModel { get; set; } = "deepseek-chat";
    public double MutterChance { get; set; } = 0.5;
    public double? Left { get; set; }
    public double? Top { get; set; }

    internal static readonly int[] FpsChoices = [30, 60, 90, 120];
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    internal static Settings Load()
    {
        try
        {
            if (File.Exists(Places.SettingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(Places.SettingsFile)) is { } loaded)
                return loaded.Checked();
        }
        catch (Exception error)
        {
            Places.Log("设置读不出来，用默认的：" + error.Message);
            // 读坏的原件留一份，不然下一次存盘就把它盖掉了（里面可能有 LLM key，还有别的设置）
            try { File.Copy(Places.SettingsFile, Places.SettingsFile + ".bad", overwrite: true); } catch { }
        }
        // 没有设置文件：全新安装（或读坏了，读坏的走上面的 catch，不算新装）。新装的第一次出场要说欢迎
        return new Settings { Welcomed = !Welcome.Pending(File.Exists(Places.SettingsFile)) };
    }

    internal Settings Checked()
    {
        Shape = FishPet.Shape.Find(Shape).Id;
        Outfit = (Outfit ?? []).Where(item => FishPet.Outfit.Expression(item) != null).Select(item => FishPet.Outfit.Expression(item)!).Distinct().ToArray();
        if (!FpsChoices.Contains(Fps)) Fps = 60;
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, .5, 2);
        TexScale = TexScale >= 1 ? 1 : TexScale >= 0.5 ? 0.5 : 0.25;
        BubbleScale = Math.Clamp(double.IsFinite(BubbleScale) && BubbleScale > 0 ? BubbleScale : 1, .5, 2);
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : .8, 0, 1);
        VoicePack = string.Equals(VoicePack, "calm", StringComparison.OrdinalIgnoreCase) ? "calm" : "second";
        TtsSpeed = Math.Clamp(double.IsFinite(TtsSpeed) ? TtsSpeed : .95, .7, 1.5);
        TtsUrl = string.IsNullOrWhiteSpace(TtsUrl) ? "http://127.0.0.1:9880" : TtsUrl.Trim();
        TtsDir = (TtsDir ?? "").Trim();
        MusicApps = (MusicApps ?? "").Trim();
        BubbleSeconds = StayFor(BubbleSeconds);
        IdleSeconds = Math.Clamp(IdleSeconds, 15, 600);
        Birthday = Condition.NormalBirthday(Birthday) ?? "";
        MutterChance = Math.Clamp(double.IsFinite(MutterChance) ? MutterChance : 0.5, 0, 1);
        return this;
    }

    internal static int StayFor(int seconds) => seconds is -1 or 0 ? seconds : Math.Clamp(seconds, 5, 180);

    internal void Save()
    {
        try
        {
            // 先写临时文件再换过去：写到一半被杀进程、断电，也不会留下半截 settings.json（里面有 LLM key）
            var temp = Places.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Format));
            File.Move(temp, Places.SettingsFile, overwrite: true);
        }
        catch (Exception error) { Places.Log("设置存不下来：" + error.Message); }
    }
}


