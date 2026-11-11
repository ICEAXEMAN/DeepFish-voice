using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FishPet;

// 大肥鱼的耳朵（本机加，2026-11-10）：拉起 audio\probe.ps1 常驻采集电脑正在播放的声音
// （WASAPI loopback，不录音、不上传），读它每 50ms 一行的 {"peak","rms","bands"}，
// 算出「是不是在放音乐」「有多响」「这一拍落在哪」，交给 Dance 变成动作。
//
// 借自 dsh-whale-pulse 的采集器（MIT）：那边给 DSH 的轮次轨道做声波，这边只借耳朵；
// 拍点检测和身体动作是新写的。
internal sealed class Music : IDisposable
{
    // 声卡底噪大约 0.0008，比这一档高才算听到声音。
    private const double NoiseFloor = .0022, BeatFloor = .008;
    // 放歌中间的空拍、间奏不算停；真的静下来这么久才算音乐停了。
    private static readonly TimeSpan HoldAfterSound = TimeSpan.FromSeconds(7);
    // 连续听到这么久才算「在放音乐」，免得一条提示音就让她跳起来。
    private const double ConfirmAfter = 1.1;
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(6);

    internal readonly record struct Snapshot(bool Playing, double Level, double Bass);

    private static readonly Snapshot Rest = new(false, 0, 0);

    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly bool wanted;
    private Process? probe;
    private bool disposed, playing, announced;
    private int generation, failures;
    private readonly List<DateTime> beatTimes = [];
    private readonly TempoTracker tempo = new();
    private double rms, bass, fluxAverage = .012, soundFor, level, bassSmooth, warmup;
    private readonly Dictionary<string, double> apps = new(StringComparer.OrdinalIgnoreCase); // 在出声的应用 → 电平
    private DateTime lastLineAt = DateTime.MinValue, lastStartAt = DateTime.MinValue, lastSoundAt = DateTime.MinValue;
    private TimeSpan lastRead;

    internal Music(bool wanted) => this.wanted = wanted;

    private static string Script => Path.Combine(Places.Root, "audio", "probe.ps1");

    internal bool Playing
    {
        get { lock (gate) return playing; }
    }

    // 现在的节拍速度（日志用）。
    internal double Tempo
    {
        get { lock (gate) return tempo.Bpm; }
    }

    // 最近这几秒里拍子够不够密：够密才像音乐，够密才值得跟着跳。
    // 说话、播客之类也有声音，但拍子零碎，这时她只慢慢晃，不会乱蹦。
    // 调试用：当前各应用电平。
    internal string AppsText
    {
        get { lock (gate) return apps.Count == 0 ? "(空)" : string.Join(", ", apps.Select(pair => pair.Key + "=" + pair.Value.ToString("0.000"))); }
    }

    // 现在最响的那个应用（日志用）。
    internal string Source
    {
        get
        {
            lock (gate)
            {
                string best = "";
                double top = 0;
                foreach (var (name, level) in apps) if (level > top) { top = level; best = name; }
                return best;
            }
        }
    }

    // 白名单里的音乐软件此刻有没有在出声。
    // ① 它自己有活跃会话、电平 ≥ 0.03，而且没被别的软件彻底压住（≥ 最响的 25%）就算。
    //    DirectSound 的会话表常常偏低、发死（2026-11-11 实测：网易云 DirectSound 一直是 0.045，
    //    设备整体却在 0.04~0.17 之间起伏），以前“固定要最响的一半”会一直判成没在放。
    // ② 连会话都没有（DirectSound「主声音驱动程序」）时：白名单软件在跑、别的软件都没出声，
    //    也算它在放；有别的软件在响就不算，免得拿视频、游戏的声音乱跳。
    internal bool AppPlaying(string whitelist)
    {
        if (string.IsNullOrWhiteSpace(whitelist)) return false;
        lock (gate)
        {
            double top = 0, mine = 0;
            foreach (var (name, level) in apps) if (level > top) top = level;
            foreach (var raw in whitelist.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                if (apps.TryGetValue(name, out var level) && level > mine) mine = level;
            }
            if (mine >= .03 && mine >= top * .25) return true;
            if (mine > 0) return false;
            // 没有它的会话：只有“白名单软件在跑 + 除了白名单没人出声”才当它在放。
            foreach (var (name, level) in apps)
                if (level >= .03 && !Listed(name, whitelist)) return false;
            RefreshProcesses();
            foreach (var raw in whitelist.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries))
                if (Running.Contains(raw.Trim())) return true;
            return false;
        }
    }

    private static readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime nextRunningLook;

    // 白名单软件在不在跑（没有会话时用），3 秒看一次就够。
    private static void RefreshProcesses()
    {
        if (DateTime.UtcNow < nextRunningLook) return;
        nextRunningLook = DateTime.UtcNow.AddSeconds(3);
        Running.Clear();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try { Running.Add(process.ProcessName); } catch { } finally { process.Dispose(); }
            }
        }
        catch { }
    }

    private static bool Listed(string name, string whitelist)
    {
        foreach (var raw in whitelist.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(raw.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal bool Rhythmic
    {
        get { lock (gate) return RecentBeats() >= 3; }
    }

    private int RecentBeats()
    {
        var now = DateTime.UtcNow;
        return beatTimes.Count(at => (now - at).TotalSeconds < 8);
    }

    // ---- 采集进程 ----
    // 只有一个采集器：spawn 和收掉都在同一把锁里做，免得一次重启留下两个 PowerShell 一起跑。

    private bool Spawn()
    {
        if (!File.Exists(Script)) { Places.Log("没找到 audio\\probe.ps1，音乐律动不开了"); return false; }
        lock (gate)
        {
            if (disposed || !wanted || probe != null) return false;
            var now = DateTime.UtcNow;
            // 连续起不来时退避一下，别把 CPU 花在无穷重启上。
            if (failures > 0 && now - lastStartAt < TimeSpan.FromSeconds(Math.Min(30, 2 * failures))) return false;
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
                "-IntervalMs", "50", "-ParentPid", Environment.ProcessId.ToString() }) info.ArgumentList.Add(arg);
            var process = new Process { StartInfo = info };
            int mine = ++generation;
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Line(mine, line); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } text) Places.Log("耳朵：" + text); };
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception error)
            {
                process.Dispose();
                failures++;
                Places.Log("耳朵起不来：" + error.Message);
                return false;
            }
            probe = process;
            lastStartAt = now;
            lastLineAt = now;
            return true;
        }
    }

    // 收掉当前的采集器，并让它的输出作废。
    private void Drop(string why)
    {
        Process? old;
        lock (gate)
        {
            old = probe;
            probe = null;
            generation++;
        }
        if (old == null) return;
        try { old.Kill(true); } catch { }
        old.Dispose();
        Places.Log("耳朵停了：" + why);
    }

    private void Line(int mine, string line)
    {
        lock (gate)
        {
            if (mine != generation || disposed) return;
            lastLineAt = DateTime.UtcNow;
            try { Interpret(line); } catch { /* 半行、坏行：丢掉这一行就好 */ }
        }
    }

    private void Interpret(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var detail = root.TryGetProperty("message", out var message) ? message.GetString() : "";
            Places.Log("耳朵报告：" + error.GetString() + " " + detail);
            return;
        }
        if (root.TryGetProperty("ready", out _))
        {
            failures = 0;
            Places.Log("耳朵听着：" + (root.TryGetProperty("device", out var device) ? device.GetString() : ""));
            return;
        }
        if (!root.TryGetProperty("rms", out var rmsValue) || rmsValue.ValueKind != JsonValueKind.Number) return;

        // 哪些应用在出声（音乐软件白名单用）：[["spotify",0.42],["chrome",0.11]]
        if (root.TryGetProperty("apps", out var appsValue) && appsValue.ValueKind == JsonValueKind.Array)
            lock (gate)
            {
                apps.Clear();
                foreach (var item in appsValue.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
                    var name = item[0].GetString();
                    if (string.IsNullOrWhiteSpace(name) || item[1].ValueKind != JsonValueKind.Number) continue;
                    double value = item[1].GetDouble();
                    apps[name] = apps.TryGetValue(name, out var before) ? Math.Max(before, value) : value;
                }
            }

        double loudness = rmsValue.GetDouble();
        double low = 0;
        int used = 0;
        // 只看最低两段（大约 0~3kHz）：鼓、贝斯、军鼓的身子在这里；踩镲、镲片在高频，
        // 把它们算进来会让人误以为拍子在加倍（70 BPM 的歌听成 140）。
        if (root.TryGetProperty("bands", out var bands) && bands.ValueKind == JsonValueKind.Array)
            foreach (var band in bands.EnumerateArray())
            {
                if (used >= 2) break;
                if (band.ValueKind != JsonValueKind.Number) continue;
                low += band.GetDouble();
                used++;
            }
        low = used > 0 ? low / used : 0;

        var now = DateTime.UtcNow;
        rms = loudness;

        // 拍点：低频能量突然往上跳，而且比最近的起伏明显。
        double rise = Math.Max(0, low - bass);
        bass = low;
        fluxAverage += (rise - fluxAverage) * .06;
        bool beat = rise > fluxAverage * 1.6 + .003 && low > BeatFloor;

        if (loudness >= NoiseFloor)
        {
            double gap = (now - lastSoundAt).TotalSeconds;
            soundFor = gap is > 0 and < 1.5 ? soundFor + gap : .05;
            lastSoundAt = now;
        }
        else soundFor = Math.Max(0, soundFor - .15);

        if (!playing)
        {
            if (soundFor >= ConfirmAfter)
            {
                playing = true;
                tempo.Reset();
                beatTimes.Clear();
                announced = false;
            }
            return;
        }

        if (beat && tempo.Beat(now))
        {
            beatTimes.Add(now);
            beatTimes.RemoveAll(at => (now - at).TotalSeconds > 20);
            if (!announced && RecentBeats() >= 3)
            {
                announced = true;
                Places.Log("听到音乐了，开始跟着跳");
            }
        }
    }

    // ---- 每帧取一次：响度和低音的平滑 ----

    internal Snapshot Read()
    {
        if (!wanted || disposed) return Rest;
        lock (gate)
        {
            var now = clock.Elapsed;
            double dt = Math.Clamp((now - lastRead).TotalSeconds, 0, .25);
            lastRead = now;

            if (playing && DateTime.UtcNow - lastSoundAt > HoldAfterSound)
            {
                playing = false;
                soundFor = 0;
                Places.Log("音乐停了");
            }
            if (playing && DateTime.UtcNow - lastLineAt > LineTimeout)
            {
                playing = false;
                _ = Task.Run(() => Restart("6 秒没有新数据"));
                return Rest;
            }
            if (!playing) return Rest;

            level += (Signal(rms) - level) * (1 - Math.Exp(-dt / .18));
            bassSmooth += (bass - bassSmooth) * (1 - Math.Exp(-dt / .10));
            warmup += (1 - warmup) * (1 - Math.Exp(-dt / .5));
            return new Snapshot(true, level * (.62 + .38 * warmup), bassSmooth);
        }
    }

    // 音量（rms）→ 0~1：底噪是 0，0.2 已经很响。用对数，小声也看得出差别。
    private static double Signal(double value)
    {
        if (value <= NoiseFloor) return 0;
        return Math.Clamp((Math.Log10(value) - Math.Log10(NoiseFloor)) / 2.0, 0, 1);
    }

    private void Restart(string why)
    {
        Drop(why);
        Spawn();
    }

    internal void Start() => Spawn();

    // 采集器自己走了（改脚本、被杀）时再拉一次。
    internal void Watch()
    {
        bool dead;
        lock (gate) dead = probe is { HasExited: true };
        if (dead) Restart("进程退出了");
    }

    public void Dispose()
    {
        Process? process;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            process = probe;
            probe = null;
        }
        try { process?.Kill(true); } catch { }
        process?.Dispose();
    }
}






