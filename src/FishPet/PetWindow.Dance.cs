using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace FishPet;

// 听到电脑在放音乐就跟着跳（本机加，2026-11-10）：
//   · Music 拉起 audio\probe.ps1 采集系统声音，算出响度、低音、拍点；
//   · 「跳」用的是身体动作里的「摇摆一」（2026-11-12 用户定了：身子左右转、头反着甩，转体那一支），
//     一句演完再换下一句，节奏跟着 BPM 走；
//   · 跳舞时的脸不跟拍子：开跳约 30 秒演一个表情，演 15 秒收回，再等约 30 秒轮到下一个，
//     一轮三个不重样（眯眼笑＋微笑 / 眯眼笑＋大笑 / 闭眼＋挑眉，用户定的）；
//   · 说话、摆姿势、被摸、被拖、被藏起来的时候让位，演完当前这一句就停。
// （2026-11-12 试过按拍点相位实时驱动，用户看了效果不好，已回滚到这个一句一句的做法；BeatTracker 只留着给 BPM 用。）
internal sealed partial class PetWindow
{
    private Music? music;
    private DispatcherTimer? danceTimer;
    private DateTime danceLogAt;
    private DateTime danceMissAt;
    private string danceMissWhy = "";

    private DateTime danceNextAt;
    // 跳舞时的表情（用户 2026-11-12 定）：平时默认脸，跳起来约 30 秒演一个，演 15 秒收回，
    // 再冷静约 30 秒轮到下一个；一轮三个不重样，顺序随机。
    private int danceMood = -1;
    private double danceFaceLeft = -1, danceFaceShown; // 还剩多少「在跳的秒数」到下个表情 / 当前表情还剩多少
    private DateTime danceFaceClock;
    private readonly List<int> danceFaces = [];
    private int danceLast = -1;
    private readonly Dictionary<string, double> danceSent = [];

    // 她这会儿是不是真的在跟着音乐动（镜子/自检/日志用）。
    internal bool Dancing { get; private set; }

    // 收着待命的时候不用听：那样一个 PowerShell 采集进程会白开一整天（用户 2026-11-12 要的），
    // 叫她出来的时候再开（见 PetWindow.Wake）。
    private void StartMusic()
    {
        if (test || !settings.MusicDance || standby || hiding || exiting) return;
        if (music != null) return;
        music = new Music(true);
        music.Start();
    }

    private void StopMusic()
    {
        StopDanceTimer();
        ReleaseDance(clear: true);
        music?.Dispose();
        music = null;
    }

    // 主计时器（33ms）里看一眼：有音乐、而且真有拍子，就把跳舞的计时器带起来。
    private void MusicTick()
    {
        if (music is not { } ears) return;
        ears.Watch();
        // 只有白名单里的音乐软件在出声才跳（2026-11-10 用户要的）：别被聊天、视频、系统提示带动

        if (ears.Playing && ears.Rhythmic && ears.AppPlaying(settings.MusicApps, Dancing) && !standby && !exiting) EnsureDanceTimer();
    }

    private void EnsureDanceTimer()
    {
        danceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => DanceStep(), Dispatcher);
        if (danceTimer.IsEnabled) return;
        // 不重置 danceNextAt：上一句动作发给渲染器就收不回了，断了又续上时要等它演完，不然两句叠在一起（2026-11-12 修）。
        danceTimer.Start();
    }

    private void StopDanceTimer()
    {
        danceTimer?.Stop();
    }

    private void DanceStep()
    {
        if (music is not { } ears)
        {
            StopDanceTimer();
            return;
        }
        var now = DateTime.UtcNow;
        var beat = ears.Read();
        bool allowed = beat.Playing && ears.Rhythmic && ears.AppPlaying(settings.MusicApps, Dancing) && DancingAllowed();
        if (!allowed)
        {
            // 拍子/识别偶尔断一下不算停：忍 1.5 秒，别一帧一帧地跳停（2026-11-10）
            if (Dancing)
            {
                if (danceMissAt == default) { danceMissAt = now; danceMissWhy = StopReason(ears, beat); return; } // 原因在第一次掉的那一刻记下来，1.5 秒后状态可能已经变了
                if ((now - danceMissAt).TotalSeconds < 1.5) return;
                Dancing = false;
                body.DanceFace(-1); // 不跳了，脸上收回去
                // 只有「表情正演着一半被打断」才重新等一个；还没到点的，已经累计的时间留着（用户 2026-11-12 要的）
                if (danceMood >= 0) { danceMood = -1; danceFaceShown = 0; danceFaceLeft = WaitBeforeFace(); }
                Places.Log("跟着音乐停下来了：" + danceMissWhy);
            }
            danceFaceClock = now; // 这中间的停顿不算「在跳的时间」
            if (!beat.Playing || !ears.Rhythmic) StopDanceTimer();
            return;
        }
        danceMissAt = default;

        if (!Dancing)
        {
            Dancing = true; // 上一句还没演完就等它演完（danceNextAt 留着），没有上一句就马上动
            // 表情的计时按「在跳的时间」累计：中途被打断不白等，回来接着数（用户 2026-11-12 要的）。
            if (danceFaceLeft < 0) danceFaceLeft = WaitBeforeFace();
            danceFaceClock = now;
            danceMood = -1;
        }
        DanceFaceTick(now);
        if (now < danceNextAt) return;

        // 用户 2026-11-12：放歌跳「摇摆一」——身子左右转、头反着甩一下，幅度和速度都收着来。
        const string word = "摇摆一";
        // 这一句演多久跟着 BPM 走：一拍多长（Dance.Seconds）就演几拍，BPM 越高律动越快（用户要的，2026-11-10）。
        // ×2：频率减半；size 0.4：幅度减半（2026-11-10 用户要的）再少 20%（2026-11-12 用户要的）。
        double wanted = Dance.Seconds(word, ears.Tempo) * 2;
        double length = body.DanceToBeat(word, wanted, .4);
        if (length <= 0) length = wanted;
        // 摇摆一最后正好回到正中（0），所以下一句等这一句演完再接，不会两条轨道叠在一起抢。
        danceNextAt = now.AddSeconds(Math.Clamp(length, .5, 24));
        if (now - danceLogAt > TimeSpan.FromSeconds(30))
        {
            danceLogAt = now;
            Places.Log(FormattableString.Invariant($"跟着音乐跳：{word}（{ears.Source}）{length:0.0} 秒 响度 {beat.Level:0.00} 低音 {beat.Bass:0.00} 节拍 {ears.Tempo:0} BPM"));
        }
    }

    // 表情的时间轴（用户 2026-11-12 定）：跳起来累计约 30 秒才演第一个，一个演 15 秒，
    // 收回后累计约 30 秒再来下一个。只算「真的在跳」的时间，被打断不白等（用户 2026-11-12 改）。
    private static double WaitBeforeFace() => 30 + (Random.Shared.NextDouble() * 6 - 3); // 大约 30 秒，前后差几秒，别太像定时器
    private const double FaceLasts = 15;

    private void DanceFaceTick(DateTime now)
    {
        double dt = (now - danceFaceClock).TotalSeconds;
        danceFaceClock = now;
        if (dt <= 0 || dt > 1) dt = 0; // 刚从停顿里回来：这段不算在跳
        if (danceMood >= 0)
        {
            danceFaceShown -= dt;
            if (danceFaceShown > 0) return;
            body.DanceFace(-1); // 演够了，回到默认脸
            danceMood = -1;
            danceFaceLeft = WaitBeforeFace();
            return;
        }
        danceFaceLeft -= dt;
        if (danceFaceLeft > 0) return;
        if (danceFaces.Count == 0)
        {
            // 新一轮：三个都排进去，顺序打乱；刚演过的那个不排在最前面
            danceFaces.AddRange([0, 1, 2]);
            for (int i = danceFaces.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (danceFaces[i], danceFaces[j]) = (danceFaces[j], danceFaces[i]);
            }
            if (danceFaces[0] == danceLast && danceFaces.Count > 1) (danceFaces[0], danceFaces[^1]) = (danceFaces[^1], danceFaces[0]);
        }
        danceMood = danceFaces[0];
        danceFaces.RemoveAt(0);
        danceLast = danceMood;
        body.DanceFace(danceMood);
        danceFaceShown = FaceLasts;
        Places.Log(FormattableString.Invariant($"跳舞时的表情：{danceMood + 1} 号，演 {FaceLasts:0} 秒"));
    }

    // 收工：动作是渲染器的 anim，演完自己回去，这里只清旧的实时参数（兼容上一版）。
    private void ReleaseDance(bool clear)
    {
        if (clear)
            foreach (var id in danceSent.Keys)
                if (!body.Shown.ContainsKey(id)) Send($"clear {id}");
        danceSent.Clear();
        Dancing = false;
        body.DanceFace(-1); // 跳舞时的表情也一起收掉
        danceMood = -1;
        danceFaces.Clear(); // 下一首歌重新排一轮
        danceLast = -1;
        danceFaceLeft = -1; // 下一首歌重新开始累计
        danceFaceShown = 0;
    }

    private bool DancingAllowed() => DanceBlockedBy() == null;

    // 说话、摆姿势、被摸、被拖、头晕、藏起来的时候让位：那几样也在动同一批参数，不能抢。返回挡住她的那一样，没有就是 null。
    private string? DanceBlockedBy()
    {
        if (test) return "测试模式";
        if (exiting) return "正在退场";
        if (standby) return "收起待命";
        if (hiding) return "正在收起";
        if (hiddenForFullscreen) return "全屏让位";
        if (stroking) return "被摸";
        if (dragging) return "被拖";
        if (dizzyOn) return "头晕";
        if (body.Posing) return "摆姿势";
        if (SkitOn) return "演小剧场";
        if (silent != null) return "做静默表情";
        if (voice != null) return "放语音";
        if (speech.IsSaying) return "说话";
        return null;
    }

    // 停下来的原因（只写日志，查为什么一会儿跳一会儿停用）。
    private string StopReason(Music ears, Music.Snapshot beat)
    {
        var reasons = new List<string>();
        if (!beat.Playing) reasons.Add("没在放（响度停了或采集器没数据）");
        if (!ears.Rhythmic) reasons.Add($"拍子不够密（8 秒内 {ears.BeatCount} 个拍点，要 ≥3）");
        if (!ears.AppPlaying(settings.MusicApps, true)) reasons.Add("白名单软件电平不够（各应用 " + ears.AppsText + "）");
        if (DanceBlockedBy() is { } blocked) reasons.Add("让位：" + blocked);
        return reasons.Count == 0 ? "（掉的那一刻已经恢复了）" : string.Join("；", reasons);
    }
}
