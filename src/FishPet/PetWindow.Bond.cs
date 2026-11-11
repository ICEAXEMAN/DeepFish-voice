using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;

namespace FishPet;

// 亲密度、番茄钟、连续工作提醒接到她身上（本机加，2026-11-08）。算分和计时在 Affection.cs，这里只管什么时候记、什么时候演。
// 要说的话都写在 events.txt（早安夸奖、熬夜念叨、想你了、升级、降级、番茄开始、番茄结束、休息结束、该休息），改台词不用改代码。
internal sealed partial class PetWindow
{
    private static string AffectionFile => Path.Combine(Places.Root, "affection.json");
    private readonly Affection affection = Affection.Load(AffectionFile);
    private readonly FocusTimer focus = new();
    private readonly WorkStreak streak = new();
    private readonly MediaPlayer chimePlayer = new();
    private DateTime nextPendingLook;

    // 用户本人有动静（点她、拖她、点托盘、发消息……）。
    private void BondSeen()
    {
        if (test) return;
        affection.Seen(DateTime.Now);
        affection.Save(AffectionFile);
    }

    private void BondGain(string kind)
    {
        if (test) return;
        affection.Apply(kind, DateTime.Now);
        affection.Save(AffectionFile);
    }

    // 摸头彩蛋（20 秒）：「融化」一天一次 +3；当天用过了就退回普通摸头 +1（还是 60 秒一次）。
    private void BondMelt()
    {
        if (test) return;
        var change = affection.Apply("融化", DateTime.Now);
        if (change.Delta == 0)
        {
            if (DateTime.UtcNow - lastStrokeGain <= TimeSpan.FromSeconds(60)) return;
            affection.Apply("摸头", DateTime.Now);
        }
        lastStrokeGain = DateTime.UtcNow;
        affection.Save(AffectionFile);
    }

    // 点她之后：先记动静，再按反应加减分（摸头加，戳裙子、挨打扣；满档时戳裙子是“娇羞”，也照扣）。
    private void BondTouched(string? reaction)
    {
        BondSeen();
        switch (reaction)
        {
            case "摸头": BondGain("摸头"); break;
            case "护裙": case "娇羞": case "娇羞前倾": case "娇羞抚胸": BondGain("戳裙子"); break;
            case "挨打": BondGain("挨打"); break;
        }
    }

    // 外面的事件（hooks）：用户发了消息算聊天；定时任务自己开的会话（auto）不算用户在。返回现在该不该提醒“起来动一动”。
    private bool BondEvent(string name, bool auto)
    {
        if (auto || test) return false;
        BondSeen();
        if (name == "收到") BondGain("聊天");
        bool due = streak.Note(DateTime.UtcNow);
        return due && focus.Now == FocusTimer.Phase.None;
    }

    // 演 events.txt 里一段她自己要说的话（不经过冷却、不挑忙闲：调用的人自己看时机）。{等级} 换成现在的等级名。
    private bool PlayOwn(string name)
    {
        events.Load(Path.Combine(Places.Root, "events.txt"));
        if (!events.TryGet(name, out var def) || def.Lines.Length == 0) return false;
        var line = events.Choose(def, random);
        PlayEvent(line with { Text = line.Text.Replace("{等级}", affection.LevelName) });
        return true;
    }

    // 她闲着、露着面的时候，才把攒着的话说出来。
    private bool FreeToTalk => !standby && !hiding && !hiddenForFullscreen && !test && ready && !MutterBlocked && behavior.Showing == null && !body.Posing;

    // 每帧（Tick）看一眼：番茄钟到点了没有、攒着的话能不能说。
    private void BondTick()
    {
        var now = DateTime.UtcNow;
        behavior.Quiet = focus.Now == FocusTimer.Phase.Work;
        switch (focus.Tick(now))
        {
            case FocusTimer.Turn.WorkDone:
                Chime(rising: true);
                behavior.Activity("专注", false);
                BondGain("番茄");
                YieldMutter();
                if (!standby) { PlayOwn("番茄结束"); RestSkitLater(); }
                break;
            case FocusTimer.Turn.BreakDone:
                Chime(rising: false);
                streak.Rested(now);
                if (!standby) { YieldMutter(); PlayOwn("休息结束"); }
                break;
        }
        if (now < nextPendingLook || affection.Pending.Count == 0 || focus.Now == FocusTimer.Phase.Work || !FreeToTalk) return;
        nextPendingLook = now.AddSeconds(3);
        var said = affection.Pending[0];
        affection.Pending.RemoveAt(0);
        affection.Save(AffectionFile);
        PlayOwn(said);
    }

    // 专注陪伴正在进行（Work 段）：这时候只剩「摸她」和「工作区进展」会出声，
    // 嘀咕、固定闲聊、小剧场（连同语音）都停掉（本机加，2026-11-09）。
    internal bool FocusWork => focus.Now == FocusTimer.Phase.Work;

    // 专注期间也允许的工作事件：授权/等你（要你处理）、完成、出错、测试结果、该休息、番茄钟自己那几句。
    private static readonly HashSet<string> FocusEvents =
    [
        "授权", "等你", "完成", "长任务完成", "子任务完成", "出错", "编译失败", "测试通过", "该休息",
        "番茄开始", "番茄提前结束", "番茄结束", "休息结束",
    ];

    internal static bool FocusEvent(string name) => FocusEvents.Contains(name);

    // 番茄结束那段话说完，休息里演一个平常的小剧场（不挑带条件、彩蛋的）。
    private async void RestSkitLater()
    {
        int token = mutterToken;
        await Task.Delay(7000);
        if (exiting || standby || focus.Now != FocusTimer.Phase.Break || speech.IsSaying) return;
        var plain = skits.Keys.Where(name => !skitConds.ContainsKey(name)).ToArray();
        if (plain.Length > 0 && (token == mutterToken || !textIdleActive)) PlaySkit(plain[random.Next(plain.Length)], force: true);
    }

    private object FocusCommand(string action)
    {
        var now = DateTime.UtcNow;
        switch (action)
        {
            case "start":
                Wake(); YieldMutter();
                focus.Start(now);
                behavior.Quiet = true;
                behavior.Activity("专注", true);
                PlayOwn("番茄开始");
                break;
            case "stop":
                bool working = focus.Now == FocusTimer.Phase.Work;
                focus.Stop();
                behavior.Quiet = false;
                behavior.Activity("专注", false);
                if (working && !standby) { YieldMutter(); PlayOwn("番茄提前结束"); }
                break;
            default:
                throw new ArgumentException("action 只能是 start（开始专注 25 分钟）或 stop（结束）。");
        }
        return Tools.Text(FocusState());
    }

    private object FocusState() => new
    {
        phase = focus.Now switch { FocusTimer.Phase.Work => "work", FocusTimer.Phase.Break => "break", _ => "none" },
        seconds_left = (int)Math.Ceiling(focus.Left(DateTime.UtcNow).TotalSeconds),
        seconds_total = (int)focus.Length.TotalSeconds,
        rounds_today = focus.RoundsToday,
    };

    private object AffectionState() => new
    {
        points = affection.Points, max = Affection.Max, level = affection.Level, name = affection.LevelName,
        from = affection.LevelFrom, next = affection.NextAt,
        next_name = affection.Level < Affection.Levels.Length ? Affection.Levels[affection.Level].Name : null,
        today = affection.TodayNet,
    };

    // ---- 提示音：自己合成两声柔和的铃（正弦波加一点泛音，快起慢落），不带任何音频文件 ----

    private static readonly Dictionary<bool, string> ChimeFiles = [];

    private void Chime(bool rising)
    {
        if (test) return;
        try
        {
            if (!ChimeFiles.TryGetValue(rising, out var file) || !File.Exists(file))
            {
                file = Path.Combine(Path.GetTempPath(), $"fishpet-chime-{(rising ? "up" : "down")}.wav");
                File.WriteAllBytes(file, ChimeSound.Wave(rising ? [880, 1318.5] : [1318.5, 987.8]));
                ChimeFiles[rising] = file;
            }
            chimePlayer.Open(new Uri(file));
            chimePlayer.Volume = settings.Volume * VoiceGain * 0.75; // 跟着控制台的音量走（比说话轻一点，也乘总衰减）
            chimePlayer.Play();
        }
        catch (Exception error) { Places.Log("提示音放不出来：" + error.Message); }
    }
}

