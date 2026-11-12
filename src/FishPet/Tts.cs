using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FishPet;

// 实时语音（本机加，2026-11-09）：她现编的话（say）没有预录，就交给本机的 GPT-SoVITS 现场合成。
// 服务是 api_v2.py（v5turbo），地址在 settings.TtsUrl；没开、超时、合成失败都只是没声音，照旧打字。
// 实时语音用日语参考：两套都叫 voice/ref\语音参考.wav、voice_calm/ref\语音参考.wav。
// 中文台词先翻成日语再合成（2026-11-11）。
internal static class Tts
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) }; // 冷启动建模可能超过 20 秒
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = false };
    private static bool warned; // 服务没开只记一次，别把 errors.log 刷满
    private static bool warnedTranslate; // 中文翻日语失败也只记一次
    private static string? lastReference; // 上次用的实时语音参考，换了记一行
    private static readonly HttpClient TranslateHttp = CreateTranslateHttp();
    private static HttpClient CreateTranslateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
        return http;
    }
    private static readonly Dictionary<string, string> Translations = new();
    private static DateTime lastSpawn = DateTime.MinValue; // 上次拉起服务的时间
    private static bool spawnLogged; // 拉起服务成功/失败只记一次

    // 语音服务在不在？/docs 是 FastAPI 自带的，2 秒内回 200 就算在。
    private static async Task<bool> Reachable(Settings settings, CancellationToken token)
    {
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, probe.Token);
            using var response = await Http.GetAsync(settings.TtsUrl.TrimEnd('/') + "/docs", linked.Token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // 找语音服务目录：settings.TtsDir 优先；否则先看桌宠上一级的「语音服务」（完整包就是这么摆的），
    // 再看 D 盘那份 GPT-SoVITS。
    private static string? FindDir(Settings settings)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.TtsDir)) candidates.Add(settings.TtsDir);
        // 桌宠在 <包>\大肥鱼桌宠\bin\ 里：先看同级、再看上一级（完整包的结构是 完整包\语音服务\）
        var petRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var outer = Directory.GetParent(petRoot)?.FullName;
        candidates.Add(Path.Combine(petRoot, "语音服务"));
        if (outer != null)
        {
            candidates.Add(Path.Combine(outer, "语音服务"));
            // 名字不叫「语音服务」也行：上一级里哪个文件夹有 api_v2.py 就用哪个（2026-11-10）
            try
            {
                foreach (var dir in Directory.GetDirectories(outer))
                    if (File.Exists(Path.Combine(dir, "api_v2.py"))) candidates.Add(dir);
            }
            catch { }
        }
        candidates.Add(@"D:\GPT-SoVITS-main\GPT-SoVITS-v2pro-20261006");
        foreach (var dir in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "api_v2.py")) && File.Exists(Path.Combine(dir, "runtime", "python.exe")))
                    return dir;
            }
            catch { }
        }
        return null;
    }

    // 关掉实时语音时顺手停掉服务，省显存和内存（2026-11-10 用户要的）：
    // 只杀监听 tts_url 那个端口、而且是 python 的进程，别的 python 不碰。
    internal static void Stop(Settings settings)
    {
        try
        {
            var port = new Uri(settings.TtsUrl).Port;
            var psi = new System.Diagnostics.ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var probe = System.Diagnostics.Process.Start(psi);
            if (probe == null) return;
            var output = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains(":" + port + " ") || !line.Contains("LISTENING")) continue;
                var fields = line.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 5 || !int.TryParse(fields[^1], out var pid)) break;
                using var target = System.Diagnostics.Process.GetProcessById(pid);
                if (!target.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase)) break;
                target.Kill(true);
                Places.Log($"实时语音关了，顺手停掉语音服务（PID {pid}）");
                return;
            }
        }
        catch (Exception error)
        {
            Places.Log("停语音服务失败：" + error.Message);
        }
    }

    // 把语音服务拉起来（无窗口），然后等它就绪。DSH 重启后桌宠自己会叫醒它。
    internal static async Task<bool> Ensure(Settings settings, CancellationToken token, int waitSeconds = 90)
    {
        if (!settings.TtsEnabled || !settings.VoiceEnabled) return false;
        if (await Reachable(settings, token)) return true;
        if (!settings.TtsAutostart) return false;
        var dir = FindDir(settings);
        if (dir == null)
        {
            if (!spawnLogged) { spawnLogged = true; Places.Log("找不到语音服务目录，实时语音只能手动开"); }
            return false;
        }
        // 刚拉起过就别重复拉，等服务自己加载完
        if (DateTime.UtcNow - lastSpawn > TimeSpan.FromMinutes(2))
        {
            try
            {
                var port = new Uri(settings.TtsUrl).Port.ToString();
                // 用 explorer 转一手再启动：这样服务不在桌宠/DSH 的进程树里，
                // DSH 退出时不会被一起带走，下次开 DSH 它就是热的（2026-11-10）。
                var script = Path.Combine(Path.GetTempPath(), "fishpet-tts-start.vbs");
                var body = "\r\nSet sh = CreateObject(\"WScript.Shell\")\r\n" +
                           $"sh.CurrentDirectory = \"{dir}\"\r\n" +
                           $"sh.Run \"\"\"{Path.Combine(dir, "runtime", "python.exe")}\"\" api_v2.py -a 127.0.0.1 -p {port} -c tts_infer_turbo.yaml\", 0, False\r\n";
                File.WriteAllText(script, body, Encoding.Unicode); // UTF-16，WScript 认；路径里有中文（完整包）也不会乱码
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + script + "\"",
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                System.Diagnostics.Process.Start(start);
                lastSpawn = DateTime.UtcNow;
                Places.Log("语音服务没在跑，已由桌宠拉起：" + dir);
            }
            catch (Exception error)
            {
                Places.Log("拉起语音服务失败：" + error.Message);
                return false;
            }
        }
        // 加载模型要二三十秒；启动时等 90 秒，说话时只等 20 秒（别把 say 卡住）
        for (var i = 0; i < waitSeconds; i++)
        {
            try { await Task.Delay(1000, token); } catch { return false; }
            if (await Reachable(settings, token)) return true;
        }
        return false;
    }

    // 参考音频和它对应的原文（GPT-SoVITS 要求 prompt 和参考音频一致）
    internal const string PromptJa = "わたくしへのご意見、ご要望などはこちらの意見欄にご記入ください。";
    // 第二套（冷静、沉稳）的参考和对应原文
    internal const string PromptCalm = "自信がないと言いますか、なんかよくわからないです。";
    // 旧的中文参考：2026-11-11 起实时语音改成中文→日语翻译，不再直接用中文参考
    internal const string PromptZh = "报告一个好消息，所有测试都过了";

    internal static string RefJa => Path.Combine(Places.Root, "voice", "ref", "语音参考.wav");
    internal static string RefCalm => Path.Combine(Places.Root, "voice_calm", "ref", "语音参考.wav");
    internal static string RefZh => Path.Combine(Places.Root, "voice", "ref", "zh_boot.wav");

    internal static string Cache => Path.Combine(Path.GetTempPath(), "fishpet-tts");

    // 有点日文的就当日语念，否则按中文。
    internal static bool Japanese(string text)
    {
        foreach (var ch in text)
            if (ch is >= '\u3041' and <= '\u30ff') return true; // 平假名/片假名
        return false;
    }

    internal static string Plain(string said) => Body.Strip(said).Trim();

    // 合成一句，返回 wav 路径；做不成就 null。
    private static int busy;
    internal static bool Busy => Volatile.Read(ref busy) > 0;

    internal static async Task<string?> Speak(string said, Settings settings, CancellationToken token, bool retried = false)
    {
        if (!settings.TtsEnabled || !settings.VoiceEnabled) return null;
        Interlocked.Increment(ref busy);
        try { return await SpeakCore(said, settings, token, retried); }
        finally { Interlocked.Decrement(ref busy); }
    }

    private static async Task<string?> SpeakCore(string said, Settings settings, CancellationToken token, bool retried = false)
    {
        if (!settings.TtsEnabled || !settings.VoiceEnabled) return null;
        var plain = Plain(said);
        if (plain.Length == 0 || plain.Length > 120) return null; // 太长的不合成，等太久不如直接打字（120 字上限，2026-11-09）

        bool japanese = Japanese(plain);
        // 字幕是中文，声音仍然是日语：没有假名的中文先翻成日语，再用日语参考合成（2026-11-11 用户定的）。
        // 有假名就直接按日语念。翻译失败就只打字，不出中文配音。
        string spoken;
        if (japanese)
        {
            spoken = await English.ToSpoken(plain, word => Translate(word, "en", token));
        }
        else
        {
            var translated = await Translate(plain, "zh-CN", token);
            if (string.IsNullOrWhiteSpace(translated)) return null;
            spoken = await English.ToSpoken(translated, word => Translate(word, "en", token));
        }
        // 时间「24:00」会被合成器一个字一个字念；先规整成「深夜0時」「12時30分」（2026-11-12）。
        spoken = FishPet.Japanese.Normalize(spoken);
        // 实时语音跟着当前语音包走：第一套 voice/ref\语音参考.wav；第二套 voice_calm/ref\语音参考.wav。
        var useCalm = settings.VoicePack == "calm" && File.Exists(RefCalm);
        var reference = useCalm ? RefCalm : RefJa;
        var prompt = useCalm ? PromptCalm : PromptJa;
        if (lastReference != reference)
        {
            lastReference = reference;
            Places.Log("实时语音参考：语音参考.wav（" + (useCalm ? "第二套" : "第一套") + "）");
        }
        const string promptLang = "ja";
        if (!File.Exists(reference)) return null;

        Directory.CreateDirectory(Cache);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", spoken, promptLang, settings.TtsSpeed.ToString("0.##"), reference, prompt, "t07"))));
        var file = Path.Combine(Cache, key[..16] + ".wav");
        if (File.Exists(file) && new FileInfo(file).Length > 1000) return file;

        var body = JsonSerializer.Serialize(new
        {
            text = spoken,
            text_lang = promptLang,
            ref_audio_path = reference,
            prompt_text = prompt,
            prompt_lang = promptLang,
            top_k = 10,
            top_p = 1,
            temperature = 0.7,
            text_split_method = "cut5",
            batch_size = 1,
            speed_factor = settings.TtsSpeed,
            media_type = "wav",
            streaming_mode = false,
            parallel_infer = true,
            repetition_penalty = 1.35,
        }, Format);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.TtsUrl.TrimEnd('/') + "/tts")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await Http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                Places.Log($"TTS 合成失败（{response.StatusCode}）：{plain[..Math.Min(20, plain.Length)]}");
                return null;
            }
            var audio = await response.Content.ReadAsByteArrayAsync(token);
            if (audio.Length < 1000) return null;
            await File.WriteAllBytesAsync(file + ".tmp", audio, token);
            File.Move(file + ".tmp", file, true); // 半截文件不算数，写完整再换名
            warned = false;
            return file;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            // 连不上：可能是服务被关了（比如 DSH 退出时一起没了），自己拉起来再试一次
            if (!retried && settings.TtsAutostart && await Ensure(settings, token, 20))
                return await Speak(said, settings, token, true);
            if (!warned)
            {
                warned = true;
                Places.Log("TTS 连不上（现编的话就先只打字）：" + error.Message);
            }
            return null;
        }
    }

    // 中文 → 日语：字幕保持中文，只把要念的文本翻成日语。走 Google 的公开翻译端点，只翻这一句，结果缓存在内存里。
    // 翻译不成就返回 null（只打字），绝不退回中文配音。
    private static async Task<string?> Translate(string text, string source, CancellationToken token)
    {
        var cacheKey = source + "|" + text;
        lock (Translations)
            if (Translations.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=" + Uri.EscapeDataString(source) + "&tl=ja&dt=t&q=" + Uri.EscapeDataString(text);
            using var response = await TranslateHttp.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                var fallback = await TranslateFallback(text, source, token);
                if (fallback != null) { lock (Translations) Translations[cacheKey] = fallback; return fallback; }
                if (!warnedTranslate) { warnedTranslate = true; Places.Log($"中文翻日语失败（{response.StatusCode}），这句只打字"); }
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            var parts = doc.RootElement[0];
            if (parts.ValueKind != JsonValueKind.Array) return null;
            var translated = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0)
                    translated.Append(part[0].GetString());
            var result = translated.ToString().Trim();
            if (result.Length == 0) return null;
            lock (Translations) Translations[cacheKey] = result;
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            var fallback = await TranslateFallback(text, source, token);
            if (fallback != null) { lock (Translations) Translations[cacheKey] = fallback; return fallback; }
            if (!warnedTranslate) { warnedTranslate = true; Places.Log("中文翻日语失败，这句只打字：" + error.Message); }
            return null;
        }
    }

    // Google 偶尔 429/限流，换 MyMemory 再试一次（同样只发这一句，结果缓存）。
    private static async Task<string?> TranslateFallback(string text, string source, CancellationToken token)
    {
        try
        {
            var pair = source.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN|ja" : "en|ja";
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(pair);
            using var response = await TranslateHttp.GetAsync(url, token);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("responseData", out var data) || !data.TryGetProperty("translatedText", out var translated)) return null;
            var result = translated.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch { return null; }
    }

    // 启动时偷偷合成一句，把第一次十几秒的冷启动挪到她还不说话的时候。
    internal static async Task Warm(Settings settings)
    {
        try
        {
            if (!await Ensure(settings, CancellationToken.None)) return;
            await Speak("起動テスト", settings, CancellationToken.None);
        }
        catch { /* 暖机失败无所谓 */ }
    }
}












