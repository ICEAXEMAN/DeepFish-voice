using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace FishPet;

// 听到电脑在放音乐就跟着跳（本机加，2026-11-10）：
//   · Music 拉起 audio\probe.ps1 采集系统声音，算出响度、低音、拍点；
//   · 「跳」用的是身体动作里现成的那三个：摇摆一 / 摇摆二 / 慢摇（2026-11-10 用户要求），
//     一句演完再换下一句；重拍时左右交替摇摆一、摇摆二，轻的时候只用慢摇；
//   · 说话、摆姿势、被摸、被拖、被藏起来的时候让位，演完当前这一句就停。
internal sealed partial class PetWindow
{
    private Music? music;
    private DispatcherTimer? danceTimer;
    private DateTime danceLogAt;
    private DateTime danceMissAt;

    private DateTime danceNextAt;
    private int danceStep;
    private bool danceSlow;
    private readonly Dictionary<string, double> danceSent = [];

    // 她这会儿是不是真的在跟着音乐动（镜子/自检/日志用）。
    internal bool Dancing { get; private set; }

    private void StartMusic()
    {
        if (test || !settings.MusicDance) return;
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

        if (ears.Playing && ears.Rhythmic && ears.AppPlaying(settings.MusicApps) && !standby && !exiting) EnsureDanceTimer();
    }

    private void EnsureDanceTimer()
    {
        danceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => DanceStep(), Dispatcher);
        if (danceTimer.IsEnabled) return;
        danceNextAt = DateTime.MinValue;
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
        bool allowed = beat.Playing && ears.Rhythmic && ears.AppPlaying(settings.MusicApps) && DancingAllowed();
        if (!allowed)
        {
            // 拍子/识别偶尔断一下不算停：忍 1.5 秒，别一帧一帧地跳停（2026-11-10）
            if (Dancing)
            {
                if (danceMissAt == default) { danceMissAt = now; return; }
                if ((now - danceMissAt).TotalSeconds < 1.5) return;
                Dancing = false;
                Places.Log("跟着音乐停下来了");
            }
            if (!beat.Playing || !ears.Rhythmic) StopDanceTimer();
            return;
        }
        danceMissAt = default;

        if (!Dancing)
        {
            Dancing = true;
            danceNextAt = DateTime.MinValue; // 一进来就先动一下
        }
        if (now < danceNextAt) return;

        // 轻/慢的时候只用慢摇；正常的时候摇摆一、摇摆二交替。
        bool slow = beat.Level < .32 || (ears.Tempo > 0 && ears.Tempo < 84);
        string word;
        if (slow)
        {
            danceSlow = true;
            word = "慢摇";
        }
        else
        {
            if (danceSlow) { danceStep = 0; danceSlow = false; }
            word = danceStep % 2 == 0 ? "摇摆一" : "摇摆二";
            danceStep++;
        }
        // 这一句演多久跟着 BPM 走：一拍多长（Dance.Seconds）就演几拍，BPM 越高律动越快（用户要的，2026-11-10）。
        // 用户 2026-11-10：摇得太快、幅度太大 —— 频率减半（一句演两倍时长），幅度减半（size 0.5）
        double wanted = Dance.Seconds(word, ears.Tempo) * 2;
        double length = body.DanceToBeat(word, wanted, .5);
        if (length <= 0) length = wanted;
        danceNextAt = now.AddSeconds(Math.Clamp(length * .92, .5, 24)); // 上一句快演完再接下一句
        if (now - danceLogAt > TimeSpan.FromSeconds(30))
        {
            danceLogAt = now;
            Places.Log(FormattableString.Invariant($"跟着音乐跳：{word}（{ears.Source}）{length:0.0} 秒 响度 {beat.Level:0.00} 低音 {beat.Bass:0.00} 节拍 {ears.Tempo:0} BPM"));
        }
    }

    // 收工：动作是渲染器的 anim，演完自己回去，这里只清旧的实时参数（兼容上一版）。
    private void ReleaseDance(bool clear)
    {
        if (clear)
            foreach (var id in danceSent.Keys)
                if (!body.Shown.ContainsKey(id)) Send($"clear {id}");
        danceSent.Clear();
        Dancing = false;
        danceSlow = false;
    }

    private bool DancingAllowed()
    {
        // 说话、摆姿势、被摸、被拖、头晕、藏起来的时候让位：那几样也在动同一批参数，不能抢。
        return !test && !exiting && !standby && !hiding && !hiddenForFullscreen
            && !stroking && !dragging && !dizzyOn
            && !body.Posing && !SkitOn && silent == null && voice == null && !speech.IsSaying;
    }
}






