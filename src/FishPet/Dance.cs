using System;

namespace FishPet;

// 音乐律动的「拍子换算」（本机加，2026-11-10）：BPM 决定一拍多长，舞步就走多快。
//
// 现在真正演的是身体动作里现成的那三个（摇摆一 / 摇摆二 / 慢摇，见 Body.cs 的 Words），
// 这里只负责算「这一句该演几秒」：BPM 越高一句越短，律动就跟着变快；BPM 越低一句越长，律动就慢下来。
// （用户 2026-11-10 要的：律动频率跟着 BPM 走。）
internal static class Dance
{
    // 每个舞步本来是按拍子编的，一句一共走这么多拍：
    //   摇摆一 左右各六次、摇摆二 六拍、慢摇 三拍（越摇越慢）。
    internal static int Beats(string word) => word switch
    {
        "摇摆一" => 12,
        "摇摆二" => 6,
        "慢摇" => 3,
        _ => 6,
    };

    // 一拍多长（秒）：BPM 越高越短。40~190 BPM 之外按边界走，快歌别把她甩飞。
    internal static double BeatSeconds(double bpm) => Math.Clamp(60 / Math.Clamp(bpm, 40, 190), .32, 1.5);

    // 这一句该演多久（秒）= 拍数 × 一拍的长度。120 BPM 时：摇摆一 6 秒、摇摆二 3 秒、慢摇 1.5 秒。
    internal static double Seconds(string word, double bpm) => Beats(word) * BeatSeconds(bpm);
}
