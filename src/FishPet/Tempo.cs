using System;

namespace FishPet;

// 拍子速度的估计（本机加，2026-11-10）：把耳朵听到的拍点间隔折成「一拍多长」。
//   · 漏一拍会把间隔量成两倍：折回最接近的整数倍，BPM 不跳；
//   · 同一个拍子里多触发一次（贝斯和鼓的起音挨得近）：间隔不到 0.25 秒就当没听见；
//   · 每次只往新值靠 25%（头几拍靠 44%，收敛快一点），所以偶尔一次的怪间隔拽不跑速度。
// 纯逻辑（不碰窗口、不碰 IO），可以在测试里喂假的拍点序列。
internal sealed class TempoTracker
{
    private const double Start = .62, LongestGap = 2.6, ShortestGap = .25, FirstBeats = 3;

    private double seconds = Start;
    private DateTime last = DateTime.MinValue;
    private int beats;

    // 一拍多长（秒）。
    internal double Seconds => seconds;

    // 估出来的 BPM。40~190 之外按边界显示，和 Dance.BeatSeconds 的范围一致。
    internal double Bpm => Math.Clamp(60 / seconds, 40, 190);

    // 这一轮已经听到多少拍。
    internal int Beats => beats;

    internal void Reset()
    {
        seconds = Start;
        last = DateTime.MinValue;
        beats = 0;
    }

    // 一个拍点到了；返回它算不算数（太密的第二次触发、隔太久的重新起算都不算）。
    internal bool Beat(DateTime at)
    {
        if (last == DateTime.MinValue) { last = at; beats++; return true; }
        double gap = (at - last).TotalSeconds;
        if (gap < ShortestGap) return false; // 太密：同一拍里的第二次触发
        if (gap > LongestGap) { last = at; return false; } // 停了很久又响：从这里重新起算

        double unit = gap;
        if (gap > seconds * 1.7)
        {
            double folded = gap / Math.Max(1, Math.Round(gap / seconds));
            if (folded > ShortestGap && Math.Abs(folded - seconds) < seconds * .2) unit = folded;
        }
        unit = Math.Clamp(unit, seconds * .55, seconds * 1.8);
        seconds += (unit - seconds) * (beats < FirstBeats ? .44 : .25);
        last = at;
        beats++;
        return true;
    }
}
