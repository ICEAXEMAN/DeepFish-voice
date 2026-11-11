using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FishPet;

// 英文读法（本机加，2026-11-09；2026-11-11 改成「单词翻译、文件名拼读」）：
// - 文件名/扩展名（.jpg .wav .png、photo.jpg 等）按字母逐个拼，方便听清是哪个文件；
// - 完整的英文单词先查常用表；表里没有的，交给 Tts 用 Google（en→ja）翻成日语再合成，
//   不再把 type 这种单词逐字读成「ティーワイピーイー」。
internal static partial class English
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bug"] = "バグ", ["bugs"] = "バグ", ["debug"] = "デバッグ",
        ["token"] = "トークン", ["tokens"] = "トークン",
        ["deadline"] = "デッドライン", ["api"] = "エーピーアイ", ["ai"] = "エーアイ",
        ["gpt"] = "ジーピーティー", ["chatgpt"] = "チャットジーピーティー",
        ["deepseek"] = "ディープシーク", ["deepfish"] = "ディープフィッシュ", ["claude"] = "クロード", ["llm"] = "エルエルエム",
        ["image"] = "イメージ", ["fish"] = "フィッシュ", ["model"] = "モデル",
        ["code"] = "コード", ["error"] = "エラー", ["log"] = "ログ", ["logs"] = "ログ",
        ["server"] = "サーバー", ["data"] = "データ", ["test"] = "テスト", ["tests"] = "テスト",
        ["app"] = "アプリ", ["pc"] = "ピーシー", ["cpu"] = "シーピーユー", ["gpu"] = "ジーピーユー",
        ["ok"] = "オーケー", ["ng"] = "エヌジー", ["yes"] = "イエス", ["no"] = "ノー",
        ["hello"] = "ハロー", ["bye"] = "バイバイ", ["good"] = "グッド", ["nice"] = "ナイス",
        ["new"] = "ニュー", ["old"] = "オールド", ["sns"] = "エスエヌエス",
        ["url"] = "ユーアールエル", ["id"] = "アイディー", ["pdf"] = "ピーディーエフ",
        ["csv"] = "シーエスブイ", ["json"] = "ジェイソン", ["html"] = "エイチティーエムエル",
        ["ver"] = "バージョン", ["version"] = "バージョン",
    };

    private static readonly string[] Letters =
    [
        "エー", "ビー", "シー", "ディー", "イー", "エフ", "ジー", "エイチ", "アイ", "ジェー",
        "ケー", "エル", "エム", "エヌ", "オー", "ピー", "キュー", "アール", "エス", "ティー",
        "ユー", "ブイ", "ダブリュー", "エックス", "ワイ", "ゼット",
    ];

    private static readonly string[] Digits =
    [
        "ゼロ", "イチ", "ニ", "サン", "ヨン", "ゴ", "ロク", "ナナ", "ハチ", "キュー",
    ];

    // 文件名/扩展名：.jpg、.wav、photo.jpg、archive.tar.gz 都算；完整单词留给后面的 Word。
    [GeneratedRegex(@"(?<file>(?<![\w])(?:[A-Za-z0-9_\-]+\.)*\.?(?:jpg|jpeg|png|gif|bmp|webp|wav|mp3|mp4|mov|avi|mkv|txt|md|json|csv|py|cs|js|ts|html|css|exe|dll|zip|rar|7z|pdf|docx|xlsx|pptx|log|bat|ps1|yaml|yml|ini|toml|gz|tar)(?![\w]))|(?<word>[A-Za-z]+(?:['\-][A-Za-z]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    // 把要念的文本换掉：文件名拼字母，英文单词查表或交给 translate 翻成日语。
    internal static async Task<string> ToSpoken(string text, Func<string, Task<string?>> translate)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new StringBuilder(text.Length);
        var at = 0;
        foreach (Match match in Token().Matches(text))
        {
            builder.Append(text, at, match.Index - at);
            var file = match.Groups["file"];
            if (file.Success)
            {
                builder.Append(Spell(file.Value));
            }
            else
            {
                var word = match.Value;
                if (Words.TryGetValue(word, out var known)) builder.Append(known);
                else if (word.Length == 1 || (word.Length <= 5 && word.All(char.IsUpper))) builder.Append(Spell(word));
                else
                {
                    var japanese = await translate(word);
                    builder.Append(string.IsNullOrWhiteSpace(japanese) ? Spell(word) : japanese.Trim());
                }
            }
            at = match.Index + match.Length;
        }
        builder.Append(text, at, text.Length - at);
        return builder.ToString();
    }

    private static string Spell(string token)
    {
        var builder = new StringBuilder();
        foreach (var ch in token)
        {
            if (ch is >= 'a' and <= 'z') builder.Append(Letters[ch - 'a']);
            else if (ch is >= 'A' and <= 'Z') builder.Append(Letters[ch - 'A']);
            else if (ch is >= '0' and <= '9') builder.Append(Digits[ch - '0']);
            else if (ch == '.') builder.Append("ドット");
            else if (ch == '-') builder.Append("ハイフン");
            else if (ch == '_') builder.Append("アンダースコア");
            else if (ch is '\\' or '/') builder.Append("スラッシュ");
        }
        return builder.Length > 0 ? builder.ToString() : token;
    }
}
