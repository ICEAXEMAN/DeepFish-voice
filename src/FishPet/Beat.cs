using System;

namespace FishPet;

// 拍点同步用的节拍追踪（本机加，2026-11-12）：不再靠「相邻两个拍点的间隔」估速度，改成听一整段（8 秒）的低音起音曲线：
//   · 速度：对起音曲线做自相关，在 60~180 BPM 里找最强的周期（带一点偏向 115 BPM 的先验，并把 2 倍、4 倍周期也算进来，
//     免得把 74 听成 148）。锁定以后，遇到和当前速度成 2 倍、1/2、3/2、2/3 这类关系的候选一律不换（同一首歌里八度和三连音的
//     来回跳是最大的不稳来源），只有真正不相干的速度连续占优 3 秒才重新锁定；锁定后每 0.5 秒在 ±4% 内微调。
//   · 相位：把最近 8 秒按当前周期折起来，找「起音最集中」的那个相位，就是拍点落在哪；每次只往新相位靠 30%，
//     所以不抖；偏差太大（比如差了半拍）要连续几次才肯跟过去。
//   · 对外是一个连续的「拍位置」Position(t)：整数就是一拍落下的那一刻；序号还带奇偶，舞步用它决定先往哪边摆。
// 纯逻辑（不碰窗口、不碰 IO），测试里喂假的起音序列。时间用调用方给的单调秒数。
internal sealed class BeatTracker
{
    private const double Cell = .025, Delay = .035, UpdateEvery = .5;
    private const int Ring = 1024, Window = 320, MinCells = 160, Mask = Ring - 1;
    private const double MinBpm = 60, MaxBpm = 180, PriorBpm = 115, PriorWidth = 0.7;
    private const double LockSharp = 3.0, LooseSharp = 1.5;

    private readonly float[] ring = new float[Ring];
    private readonly double[] smooth = new double[Window], centered = new double[Window], scores = new double[(int)(MaxBpm - MinBpm) + 1];
    private static readonly double[] Kernel = MakeKernel();

    private long head = -1, firstCell = -1;
    private double nextUpdate, quietSince = -1, lastClearAt;
    private double period = .6;
    private bool locked, anchored;
    private double anchor;
    private long anchorIndex;
    private double candidate;
    private int candidateRuns, strangerRuns, jumpRuns;

    // 日志（Music 里接到 Places.Log）；测试里不接。
    internal Action<string>? Log { get; set; }

    internal bool Locked => locked && anchored;
    internal bool HasPhase => anchored;
    internal double Period => period;
    internal double Bpm => 60 / period;
    // 最近一次分析里最强周期有多突出（和中位数比）；3 以上才算有清楚的拍子。
    internal double Clarity { get; private set; }

    internal void Reset()
    {
        Array.Clear(ring);
        head = firstCell = -1;
        nextUpdate = 0; quietSince = -1; lastClearAt = 0;
        locked = anchored = false;
        candidate = 0; candidateRuns = strangerRuns = jumpRuns = 0;
        Clarity = 0;
    }

    private static double[] MakeKernel()
    {
        var weights = new double[9];
        double total = 0;
        for (int i = 0; i < 9; i++) { double x = (i - 4) / 1.4; weights[i] = Math.Exp(-.5 * x * x); total += weights[i]; }
        for (int i = 0; i < 9; i++) weights[i] /= total;
        return weights;
    }

    // 一帧起音（低音往上冲了多少）到了：t 是它到达的时间（秒），先往前挪 Delay（这一帧是前 50ms 的平均，再加传输），
    // 摊到相邻两格里；每 0.5 秒分析一次。
    internal void Add(double t, double onset)
    {
        if (!(onset >= 0) || double.IsInfinity(onset)) onset = 0;
        double x = (t - Delay) / Cell;
        long cell = (long)Math.Floor(x);
        double part = x - cell;
        if (head < 0) { head = cell; firstCell = cell; Array.Clear(ring); }
        else if (cell + 1 - head >= Ring) { Array.Clear(ring); head = cell; firstCell = cell; }
        else if (cell < head - Window) return; // 迟到太久的，丢掉
        while (head < cell + 1) { head++; ring[head & Mask] = 0; }
        ring[cell & Mask] += (float)(onset * (1 - part));
        ring[(cell + 1) & Mask] += (float)(onset * part);
        if (t >= nextUpdate && head - firstCell >= MinCells + 2)
        {
            nextUpdate = t + UpdateEvery;
            Analyze(t, head - 2);
        }
    }

    // 某一时刻的拍位置（整数 = 一拍落下）；还没对上相位时是 NaN。
    internal double Position(double t) => anchored ? anchorIndex + (t - anchor) / period : double.NaN;

    private static double Prior(double bpm)
    {
        double x = Math.Log2(bpm / PriorBpm) / PriorWidth;
        return Math.Exp(-.5 * x * x);
    }

    // 自相关：lag 取 1、2、4 个周期（后两个权重小），用分数位置的线性插值。
    private static double Score(double[] z, int n, double bpm)
    {
        double lag = 60 / bpm / Cell, total = 0;
        for (int m = 1; m <= 4; m *= 2)
        {
            double length = lag * m;
            int i = (int)length;
            double f = length - i;
            if (i + 1 >= n) continue;
            double sum = 0;
            int count = n - i - 1;
            for (int k = 0; k < count; k++) sum += z[k] * (z[k + i] * (1 - f) + z[k + i + 1] * f);
            total += (m == 1 ? 1 : m == 2 ? .5 : .25) * sum / count;
        }
        return total;
    }

    private static bool Related(double ratio)
    {
        foreach (var h in new[] { 1, 2, .5, 1.5, 2.0 / 3 })
            if (Math.Abs(ratio / h - 1) < .04) return true;
        return false;
    }

    private void Analyze(double now, long end)
    {
        int n = (int)Math.Min(Window, end - firstCell + 1);
        if (n < MinCells) return;
        long begin = end - n + 1;
        // 平滑（高斯，约 35ms）+ 去均值
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int k = -4; k <= 4; k++)
            {
                long cell = begin + i + k;
                if (cell < begin || cell > end) continue;
                sum += ring[cell & Mask] * Kernel[k + 4];
            }
            smooth[i] = sum;
            mean += sum;
        }
        mean /= n;
        // 几乎没有起音：静音或者一段没有鼓点的东西。别拿噪声去锁。
        if (mean < .0008)
        {
            if (quietSince < 0) quietSince = now;
            if (locked && now - quietSince > 3) { locked = false; Log?.Invoke("节拍追踪：静下来了，先放开"); }
            return;
        }
        quietSince = -1;
        for (int i = 0; i < n; i++) centered[i] = smooth[i] - mean;

        double best = double.MinValue, topBpm = 0;
        for (int i = 0; i < scores.Length; i++)
        {
            double bpm = MinBpm + i;
            scores[i] = Score(centered, n, bpm) * Prior(bpm);
            if (scores[i] > best) { best = scores[i]; topBpm = bpm; }
        }
        var magnitudes = new double[scores.Length];
        for (int i = 0; i < scores.Length; i++) magnitudes[i] = Math.Abs(scores[i]);
        Array.Sort(magnitudes);
        double median = magnitudes[magnitudes.Length / 2];
        double clarity = best / (median + 1e-12);
        Clarity = clarity;
        if (clarity >= LooseSharp) lastClearAt = now;

        if (!locked)
        {
            if (clarity < LockSharp) { candidateRuns = 0; return; }
            if (candidateRuns > 0 && Math.Abs(topBpm / candidate - 1) < .05) candidateRuns++;
            else { candidate = topBpm; candidateRuns = 1; }
            if (candidateRuns < 2) return;
            period = 60 / topBpm;
            locked = true; anchored = false; strangerRuns = jumpRuns = 0; lastClearAt = now;
            Log?.Invoke(FormattableString.Invariant($"节拍锁定：{topBpm:0} BPM（清晰度 {clarity:0.0}）"));
        }
        else
        {
            double current = 60 / period;
            double currentScore = double.MinValue;
            for (double bpm = current * .96; bpm <= current * 1.04; bpm += .5) currentScore = Math.Max(currentScore, Score(centered, n, bpm));
            currentScore *= Prior(current);
            // 换速度：连续占优才换。和现在成 2 倍、1/2、3/2、2/3 关系的，多半是同一首歌里的另一种数法（八度、三连音），
            // 要现在这个的分数垮到 40% 以下才肯换；不相干的速度，现在这个比它差 30% 就换。
            if (best > (Related(topBpm / current) ? 2.5 : 1.4) * currentScore && clarity >= LockSharp) strangerRuns++;
            else strangerRuns = Math.Max(0, strangerRuns - 1);
            if (strangerRuns >= 6)
            {
                period = 60 / topBpm;
                anchored = false; strangerRuns = jumpRuns = 0;
                Log?.Invoke(FormattableString.Invariant($"节拍重新锁定：{topBpm:0} BPM（清晰度 {clarity:0.0}）"));
            }
            else
            {
                // 微调：±4% 里找最好的，每次往那边靠 15%
                double bestFine = current, bestFineScore = double.MinValue;
                for (double bpm = current * .96; bpm <= current * 1.04; bpm += .25)
                {
                    double s = Score(centered, n, bpm);
                    if (s > bestFineScore) { bestFineScore = s; bestFine = bpm; }
                }
                period = .85 * period + .15 * (60 / bestFine);
            }
            // 好久没有清楚的拍子（一首歌放完、换成说话）：放开，下次重新锁
            if (now - lastClearAt > 12) { locked = false; candidateRuns = 0; Log?.Invoke("节拍追踪：很久没有清楚的拍子了，先放开"); return; }
        }
        FindPhase(smooth, n, end);
    }

    // 把最近的起音按周期折起来，找最集中的相位（越近的拍权重越大）。
    private void FindPhase(double[] e, int n, long end)
    {
        double cells = period / Cell;
        double bestPhase = 0, bestValue = double.MinValue;
        for (double phase = 0; phase < cells; phase += .2)
        {
            double sum = 0, weight = 1, total = 0;
            for (double x = n - 1 - phase; x >= 0; x -= cells)
            {
                int i = (int)x;
                double f = x - i;
                double v = i + 1 < n ? e[i] * (1 - f) + e[i + 1] * f : e[i];
                sum += weight * v; total += weight; weight *= .93;
            }
            if (total > 0 && sum / total > bestValue) { bestValue = sum / total; bestPhase = phase; }
        }
        double lastBeat = (end - bestPhase) * Cell;
        if (!anchored)
        {
            anchor = lastBeat; anchorIndex = 0; anchored = true; jumpRuns = 0;
            return;
        }
        double m = Math.Round((lastBeat - anchor) / period);
        double error = lastBeat - (anchor + m * period);
        double gain = .3;
        if (Math.Abs(error) > period * .25)
        {
            jumpRuns++;
            gain = jumpRuns >= 4 ? 1 : .08; // 差得很远：先少信一点，连着 4 次都这样才真的跟过去
            if (jumpRuns >= 4) jumpRuns = 0;
        }
        else jumpRuns = 0;
        anchor += m * period + gain * error;
        anchorIndex += (long)m;
    }
}
