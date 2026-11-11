param([int]$IntervalMs = 50, [int]$ParentPid = 0)

# 大肥鱼的耳朵：抓电脑正在播放的声音，每 IntervalMs 毫秒往 stdout 打一行 JSON：
#   {"peak":0.0578,"rms":0.0181,"bands":[16 个 0~1]}
# 设备级 WASAPI loopback（和 OBS 一样），不录音、不落盘，只算音量和 16 段频谱。
# 借自 dsh-whale-pulse 的 runtime/probe.ps1（MIT），去掉了按应用抓取，改成从 $PSScriptRoot 读 NAudio.dll。
# 本文件刻意全用 ASCII：PowerShell 5.1 读无 BOM 的 UTF-8 中文会乱码，注释里就不写中文了。

$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot 'NAudio.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Output '{"error":"naudio-missing"}'
    exit 1
}
try {
    [System.Reflection.Assembly]::LoadFrom($dll) | Out-Null
} catch {
    Write-Output ('{"error":"naudio-load-failed","message":"' + ($_.Exception.Message -replace '"', "'") + '"}')
    exit 1
}

$cs = @'
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;

public static class WhaleEar
{
    private const int FftSize = 1024;
    private const int FftLog = 10;
    private const int Bands = 16;

    private static readonly object Gate = new object();
    private static readonly float[] Samples = new float[FftSize];
    private static int _pos;
    private static int _parentPid;
    private static double _peak;
    private static double _energy;
    private static long _count;

    public static void Run(int intervalMs, int parentPid)
    {
        _parentPid = parentPid;
        if (intervalMs < 20) intervalMs = 20;
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        var captures = new System.Collections.Generic.List<WasapiLoopbackCapture>();
        string name = "(default)";
        try
        {
            name = OpenAll(captures);
        }
        catch (Exception error)
        {
            Line("{\"error\":\"device-open-failed\",\"message\":\"" + Json(error.Message) + "\"}");
            Environment.Exit(1);
            return;
        }

        int started = 0;
        foreach (var capture in captures)
        {
            capture.DataAvailable += OnData;
            try { capture.StartRecording(); started++; }
            catch { try { capture.Dispose(); } catch { } }
        }
        if (started == 0)
        {
            Line("{\"error\":\"start-failed\",\"message\":\"no loopback device could start\"}");
            Environment.Exit(1);
            return;
        }

        Line("{\"ready\":true,\"device\":\"" + Json(name) + "\"}");

        var bands = new double[Bands];
        int appTick = 0;
        string apps = "[]";
        var text = new StringBuilder(260);
        while (true)
        {
            Thread.Sleep(intervalMs);
            if (_parentPid > 0 && !ParentAlive()) return;

            double peak, rms;
            lock (Gate)
            {
                peak = _peak;
                rms = Math.Sqrt(_count > 0 ? _energy / _count : 0);
                _peak = 0;
                _energy = 0;
                _count = 0;
            }
            ComputeBands(bands);
            // which app is making the sound (music-app whitelist, 2026-11-10): refresh about once a second
            if (++appTick % 20 == 0) apps = AppsJson();

            text.Length = 0;
            text.Append("{\"peak\":").Append(F(peak, 4));
            text.Append(",\"rms\":").Append(F(rms, 4));
            text.Append(",\"bands\":[");
            for (int b = 0; b < Bands; b++)
            {
                if (b > 0) text.Append(',');
                text.Append(F(bands[b], 3));
            }
            text.Append("],\"apps\":").Append(apps).Append("}");
            Line(text.ToString());
        }
    }

    private static readonly MMDeviceEnumerator Devices = new MMDeviceEnumerator();

    // Loudest few audio sessions right now: [["spotify",0.42],["chrome",0.11]] (music-app whitelist).
    private static string AppsJson()
    {
        try
        {
            var found = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in Devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        AudioSessionControl session = null;
                        try
                        {
                            session = sessions[i];
                            if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                            double level = session.AudioMeterInformation.MasterPeakValue;
                            if (level < 0.004) continue;
                            uint pid = session.GetProcessID;
                            if (pid == 0) continue;
                            string name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                            double before;
                            if (!found.TryGetValue(name, out before) || level > before) found[name] = level;
                        }
                        catch { }
                        finally { if (session != null) { try { session.Dispose(); } catch { } } }
                    }
                }
                catch { }
            }
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>(found);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder("[");
            int take = Math.Min(4, list.Count);
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("[\"").Append(Json(list[i].Key)).Append("\",").Append(F(list[i].Value, 3)).Append(']');
            }
            return sb.Append(']').ToString();
        }
        catch { return "[]"; }
    }
    private static string F(double value, int digits)
    {
        return value.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    private static void Line(string text)
    {
        Console.WriteLine(text);
        Console.Out.Flush();
    }

    // JSON-escape a string, and keep the whole line pure ASCII: non-ASCII is written as \uXXXX, so the
    // console code page can never turn a device name into mojibake on the way to the host.
    private static string Json(string text)
    {
        if (text == null) return "";
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool ParentAlive()
    {
        try
        {
            using (var process = Process.GetProcessById(_parentPid)) return !process.HasExited;
        }
        catch { return false; }
    }

    private static bool HasSession(MMDevice device)
    {
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
                if ((int)sessions[i].State == 1) return true;
        }
        catch { }
        return false;
    }

    // FxSound / Voicemeeter / VB-Audio / CABLE endpoints are pass-through: capturing them gives silence.
    private static bool IsVirtual(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name.ToLowerInvariant();
        return n.Contains("virtual") || n.Contains("fxsound") || n.Contains("voicemeeter")
            || n.Contains("vb-audio") || n.Contains("vb audio") || n.Contains("cable input")
            || n.Contains("cable output") || n.Contains("stereo mix");
    }

    private static string OpenAll(System.Collections.Generic.List<WasapiLoopbackCapture> captures)
    {
        var names = new System.Collections.Generic.List<string>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            try
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (IsVirtual(device.FriendlyName)) continue;
                    try
                    {
                        captures.Add(new WasapiLoopbackCapture(device));
                        names.Add(device.FriendlyName);
                    }
                    catch { }
                }
            }
            catch { }
            if (captures.Count == 0)
            {
                try
                {
                    var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    captures.Add(new WasapiLoopbackCapture(fallback));
                    names.Add(fallback.FriendlyName);
                }
                catch { }
            }
        }
        if (captures.Count == 0) captures.Add(new WasapiLoopbackCapture());
        if (names.Count == 0) names.Add("(default)");
        return string.Join(" + ", names);
    }

    private static void OnData(object sender, WaveInEventArgs e)
    {
        int cnt = e.BytesRecorded / 4;
        double peak = 0, energy = 0;
        lock (Gate)
        {
            for (int i = 0; i < cnt; i++)
            {
                float f = BitConverter.ToSingle(e.Buffer, i * 4);
                Samples[_pos] = f;
                _pos++;
                if (_pos >= FftSize) _pos = 0;
                double v = Math.Abs(f);
                if (v > peak) peak = v;
                energy += v * v;
            }
            if (peak > _peak) _peak = peak;
            _energy += energy;
            _count += cnt;
        }
    }

    private static void ComputeBands(double[] bands)
    {
        var c = new Complex[FftSize];
        lock (Gate)
        {
            for (int i = 0; i < FftSize; i++)
            {
                int index = (_pos + i) % FftSize;
                double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1));
                c[i].X = (float)(Samples[index] * window);
                c[i].Y = 0f;
            }
        }
        try { FastFourierTransform.FFT(true, FftLog, c); }
        catch
        {
            for (int b = 0; b < Bands; b++) bands[b] = 0;
            return;
        }
        int lo = 1, hi = FftSize / 2;
        for (int b = 0; b < Bands; b++)
        {
            int a = lo + (hi - lo) * b / Bands;
            int z = lo + (hi - lo) * (b + 1) / Bands;
            double sum = 0;
            int n = 0;
            for (int i = a; i < z; i++)
            {
                sum += Math.Sqrt((double)c[i].X * c[i].X + (double)c[i].Y * c[i].Y);
                n++;
            }
            double avg = n > 0 ? sum / n : 0;
            bands[b] = Math.Min(1.0, Math.Sqrt(avg) * 3.0);
        }
    }
}
'@

Add-Type -TypeDefinition $cs -ReferencedAssemblies @($dll, 'System.dll') -Language CSharp

[WhaleEar]::Run($IntervalMs, $ParentPid)




