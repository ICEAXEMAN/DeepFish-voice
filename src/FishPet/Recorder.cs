using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FishPet;

// 开发用：透明帧导出（宣传片录制，本机加 2026-11-08）。默认关着，平时只有每半秒看一眼哨兵文件在不在。
//   插件\record\on.json 出现 → 开录；内容 {"clip":"R1"}，可选 "out"（输出文件夹，默认 插件\..\promo-video\assets\pet\<clip>）、
//   "supersample"（1–3，渲染器按窗口的几倍像素出图，屏幕上缩着显示；默认 1）。删掉 on.json → 停录、写 meta.json。
//   插件\record\status.json 随时写着录到哪了。
// 输出：<out>\frame_000000.png …（渲染器原始帧，带透明通道，不含气泡），<out>\meta.json（帧时间、气泡逐字时间、事件）。
// 退回：删掉本文件，并把 Player.cs / Bubble.cs / PetWindow.cs 换回备份。
internal static class Recorder
{
    private static string Folder => Path.Combine(Places.Root, "record");
    private static string Switch => Path.Combine(Folder, "on.json");
    private static string StatusFile => Path.Combine(Folder, "status.json");
    private const long MaxPendingBytes = 1L << 30; // 排队等编码的帧超过 1 GB 就丢帧（meta 里如实少一帧）
    private static volatile int expectWidth, expectHeight;
    private static Task finishing = Task.CompletedTask;

    private static volatile Session? current;
    private static DispatcherTimer? watch;
    private static string? switchSeen; // on.json 的内容，变了就换一段
    private static DateTime nextStatus;
    private static Func<object>? layout;

    internal static bool Active => current != null;
    // 渲染器出图要比窗口大几倍（supersample）；变了时 PetWindow 重新发 resize。
    internal static double Boost => current?.Supersample ?? 1;
    internal static event Action? BoostChanged;

    // 渲染器该出多大的帧（PetWindow 每次 resize 时告诉这里）；尺寸对不上的帧是切换中的旧帧，不录。
    internal static void Expect(int width, int height) { expectWidth = width; expectHeight = height; }

    // layout: 气泡锚点等版面信息（帧像素坐标），开录时记进 meta。
    internal static void Init(Dispatcher dispatcher, Func<object> layoutInfo)
    {
        layout = layoutInfo;
        watch = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Look(), dispatcher);
        watch.Start();
    }

    private static void Look()
    {
        string? content = null;
        try { if (File.Exists(Switch)) content = File.ReadAllText(Switch); }
        catch (IOException) { return; } // 正在被写，下次再看
        if (content != switchSeen)
        {
            Stop();
            if (!finishing.IsCompleted) return; // 上一段还在收尾（可能是同一个文件夹），收完再开新的
            switchSeen = content;
            if (content != null) Begin(content);
        }
        if (current is { } session && DateTime.UtcNow >= nextStatus) { nextStatus = DateTime.UtcNow.AddSeconds(1); Status(session, true); }
    }

    private static void Begin(string content)
    {
        try
        {
            string clip = "clip";
            string? output = null;
            double supersample = 1;
            if (content.Trim().Length > 0)
            {
                using var json = JsonDocument.Parse(content);
                var root = json.RootElement;
                if (root.TryGetProperty("clip", out var c) && c.GetString() is { Length: > 0 } name) clip = name;
                if (root.TryGetProperty("out", out var o) && o.GetString() is { Length: > 0 } folder) output = folder;
                if (root.TryGetProperty("supersample", out var s) && s.TryGetDouble(out var k)) supersample = Math.Clamp(k, 1, 3);
            }
            foreach (var bad in Path.GetInvalidFileNameChars()) clip = clip.Replace(bad, '_');
            output ??= Path.Combine(Path.GetDirectoryName(Places.Root)!, "promo-video", "assets", "pet", clip);
            Directory.CreateDirectory(output);
            // 同名 clip 重录：先清掉旧帧，免得新旧混在一起。
            foreach (var old in Directory.EnumerateFiles(output, "frame_*.png")) File.Delete(old);
            File.Delete(Path.Combine(output, "meta.json"));
            current = new Session(clip, output, supersample, layout?.Invoke());
            if (supersample != 1) BoostChanged?.Invoke();
            Status(current, true);
        }
        catch (Exception error) { Places.Log("录制开不起来：" + error.Message); WriteStatus(new { recording = false, error = error.Message }); }
    }

    // 停录：等排队的帧编码完，写 meta.json。关桌宠时也会调。
    internal static void Stop(bool wait = false)
    {
        if (current is { } session)
        {
            current = null;
            if (session.Supersample != 1) BoostChanged?.Invoke();
            var before = finishing;
            WriteStatus(new { recording = false, finishing = true, clip = session.Clip, output = session.Output, frames = session.Kept });
            // 排队的帧在后台编完再写 meta.json，界面线程不等。
            finishing = Task.Run(async () =>
            {
                await before;
                try { session.Finish(); Status(session, false); }
                catch (Exception error) { Places.Log("录制收尾出错：" + error.Message); WriteStatus(new { recording = false, error = error.Message }); }
            });
        }
        if (wait) finishing.Wait(TimeSpan.FromSeconds(60));
    }

    private static void Status(Session session, bool recording) => WriteStatus(new
    {
        recording, clip = session.Clip, output = session.Output, frames = session.Kept, dropped = session.Dropped,
        seconds = Math.Round(session.Clock.Elapsed.TotalSeconds, 2), fps = session.Clock.Elapsed.TotalSeconds > 0 ? Math.Round(session.Kept / session.Clock.Elapsed.TotalSeconds, 1) : 0,
        width = session.Width, height = session.Height,
    });

    private static void WriteStatus(object status)
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(StatusFile, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception) { }
    }

    // 渲染器每来一帧（读帧的后台线程上，premultiplied BGRA）。没在录就只是一次判空。
    internal static void Frame(byte[] data, int width, int height)
    {
        if (current is { } session && width == expectWidth && height == expectHeight) session.Frame(data, width, height);
    }

    // 气泡：which 是 "speech"（正式说话）或 "mutter"（嘀咕），text 是此刻气泡里显示的字（已去掉〔〕）。
    internal static void Bubble(string which, string text) => current?.Bubble(which, text);
    internal static void BubbleHidden(string which) => current?.BubbleHidden(which);
    internal static void BubbleNewLine(string which) => current?.BubbleNewLine(which);
    internal static void Event(string kind, object? detail = null) => current?.Event(kind, detail);

    private sealed class Session
    {
        internal readonly string Clip, Output;
        internal readonly double Supersample;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly object? layout;
        private readonly BlockingCollection<(int Index, byte[] Data, int Width, int Height)> queue = new();
        private readonly Thread[] workers;
        private readonly List<(int I, long Ms, int W, int H)> frames = [];
        private readonly List<BubbleRecord> bubbles = [];
        private readonly Dictionary<string, BubbleRecord> open = [];
        private readonly List<object> events = [];
        private int index;
        private long pendingBytes;
        internal int Kept => index;
        internal int Dropped;
        internal int Width, Height;

        internal Session(string clip, string output, double supersample, object? layout)
        {
            Clip = clip; Output = output; Supersample = supersample; this.layout = layout;
            int count = Math.Clamp(Environment.ProcessorCount - 4, 2, 16);
            workers = Enumerable.Range(0, count).Select(n => new Thread(Encode) { IsBackground = true, Name = "FishPet.Recorder." + n, Priority = ThreadPriority.BelowNormal }).ToArray();
            foreach (var worker in workers) worker.Start();
        }

        internal void Frame(byte[] data, int width, int height)
        {
            long ms = Clock.ElapsedMilliseconds;
            long bytes = (long)width * height * 4;
            if (Interlocked.Read(ref pendingBytes) + bytes > MaxPendingBytes) { Interlocked.Increment(ref Dropped); return; }
            var copy = ArrayPool<byte>.Shared.Rent(width * height * 4);
            Buffer.BlockCopy(data, 0, copy, 0, width * height * 4);
            int i;
            lock (frames)
            {
                i = index++;
                frames.Add((i, ms, width, height));
                if (Width == 0) { Width = width; Height = height; }
            }
            Interlocked.Add(ref pendingBytes, bytes);
            try { queue.Add((i, copy, width, height)); }
            catch (InvalidOperationException) { ArrayPool<byte>.Shared.Return(copy); }
        }

        private void Encode()
        {
            foreach (var (i, data, width, height) in queue.GetConsumingEnumerable())
            {
                try
                {
                    using var file = new FileStream(Path.Combine(Output, $"frame_{i:000000}.png"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    Png.Write(file, data, width, height);
                }
                catch (Exception error) { Places.Log("录制帧存不下来：" + error.Message); }
                finally { ArrayPool<byte>.Shared.Return(data); Interlocked.Add(ref pendingBytes, -(long)width * height * 4); }
            }
        }

        internal void Bubble(string which, string text)
        {
            long ms = Clock.ElapsedMilliseconds;
            lock (bubbles)
            {
                // 字是在原来的基础上往后长的，就是同一个气泡在逐字出现；不然就是新的一个。
                if (open.TryGetValue(which, out var bubble) && !bubble.Fresh && text.StartsWith(bubble.Text, StringComparison.Ordinal))
                {
                    if (text.Length == bubble.Text.Length) return;
                    Add(bubble, text[bubble.Text.Length..], ms);
                    bubble.Text = text;
                    return;
                }
                if (bubble != null) bubble.EndMs = ms;
                bubble = new BubbleRecord { Which = which, StartMs = ms };
                Add(bubble, text, ms);
                bubble.Text = text;
                open[which] = bubble;
                bubbles.Add(bubble);
            }
        }

        private static void Add(BubbleRecord bubble, string words, long ms)
        {
            var letters = System.Globalization.StringInfo.GetTextElementEnumerator(words);
            while (letters.MoveNext()) bubble.Chars.Add(new { ch = letters.GetTextElement(), ms });
        }

        internal void BubbleHidden(string which)
        {
            lock (bubbles)
                if (open.Remove(which, out var bubble)) bubble.EndMs = Clock.ElapsedMilliseconds;
        }

        internal void BubbleNewLine(string which)
        {
            lock (bubbles)
                if (open.TryGetValue(which, out var bubble)) bubble.Fresh = true;
        }

        internal void Event(string kind, object? detail)
        {
            long ms = Clock.ElapsedMilliseconds;
            lock (events) events.Add(detail == null ? new { ms, kind } : new { ms, kind, detail });
        }

        internal void Finish()
        {
            long end = Clock.ElapsedMilliseconds;
            queue.CompleteAdding();
            foreach (var worker in workers) worker.Join();
            List<(int I, long Ms, int W, int H)> taken;
            lock (frames) taken = [.. frames];
            lock (bubbles) foreach (var bubble in open.Values) bubble.EndMs ??= end;
            double seconds = taken.Count > 1 ? (taken[^1].Ms - taken[0].Ms) / 1000.0 : 0;
            var meta = new
            {
                width = Width, height = Height, fps = 60,
                measuredFps = seconds > 0 ? Math.Round((taken.Count - 1) / seconds, 2) : 0,
                durationMs = end, dropped = Dropped, clip = Clip,
                frames = taken.Select(f => f.W == Width && f.H == Height ? (object)new { i = f.I, ms = f.Ms } : new { i = f.I, ms = f.Ms, w = f.W, h = f.H }).ToArray(),
                bubbles = bubbles.Select(b => new { text = b.Text, startMs = b.StartMs, endMs = b.EndMs ?? end, which = b.Which, chars = b.Chars }).ToArray(),
                events = events.ToArray(),
                layout,
                note = "ms 从开录算起，是每帧从渲染器到达的真实时间（掉帧会体现为间隔变大）。帧是预乘转直通的透明 PNG，不含气泡。",
            };
            File.WriteAllText(Path.Combine(Output, "meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            queue.Dispose();
        }
    }

    private sealed class BubbleRecord
    {
        internal string Which = "", Text = "";
        internal long StartMs;
        internal long? EndMs;
        internal bool Fresh;
        internal List<object> Chars = [];
    }
}

// 快速 PNG：RGBA 8 位，每行 Up 滤波，ZLib 最快档。WPF 的 PngBitmapEncoder 大图时太慢（1.5 倍出图就跟不上 60fps）。
// 进来的是渲染器的 premultiplied BGRA，这里顺手转成 PNG 要的非预乘 RGBA。
internal static class Png
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static void Write(Stream output, byte[] bgra, int width, int height)
    {
        int stride = width * 4;
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; // 8 位 RGBA
        Chunk(output, "IHDR", header);
        using (var compressed = new MemoryStream(stride * height / 6))
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            {
                var row = new byte[stride + 1];
                var previous = new byte[stride];
                var current = new byte[stride];
                row[0] = 2; // Up
                for (int y = 0; y < height; y++)
                {
                    int at = y * stride;
                    for (int x = 0; x < stride; x += 4)
                    {
                        byte b = bgra[at + x], g = bgra[at + x + 1], r = bgra[at + x + 2], a = bgra[at + x + 3];
                        if (a == 0) { r = g = b = 0; }
                        else if (a != 255)
                        {
                            r = (byte)Math.Min(255, (r * 255 + a / 2) / a);
                            g = (byte)Math.Min(255, (g * 255 + a / 2) / a);
                            b = (byte)Math.Min(255, (b * 255 + a / 2) / a);
                        }
                        current[x] = r; current[x + 1] = g; current[x + 2] = b; current[x + 3] = a;
                    }
                    for (int x = 0; x < stride; x++) row[x + 1] = (byte)(current[x] - previous[x]);
                    zlib.Write(row);
                    (previous, current) = (current, previous);
                }
            }
            Chunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        Chunk(output, "IEND", []);
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        output.Write(four);
        Span<byte> name = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(name);
        output.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (var x in name) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in data) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(four, crc ^ 0xFFFFFFFFu);
        output.Write(four);
    }
}
