using System;

namespace FishPet;

// 音乐律动的「拍子换算」（本机加，2026-11-10）：BPM 决定一拍多长，舞步就走多快。
//
// 2026-11-12 用户定了：放歌时跳「摇摆一」（身子左右转、头反着甩，见 Body.cs 的 Dance），
// 这里负责算「这一句该演几秒」：BPM 越高一句越短，律动就跟着快；BPM 越低一句越长。
internal static class Dance
{
    // 一句一共走这么多拍（一拍 = 60/BPM 秒）：摇摆一 12 拍、摇摆二 6 拍、慢摇 3 拍。
    internal static int Beats(string word) => word switch
    {
        "摇摆一" => 12,
        "摇摆二" => 6,
        "慢摇" => 3,
        _ => 6,
    };

    // 一拍多长（秒）：BPM 越高越短。40~190 BPM 之外按边界走，快歌别把她甩飞。
    internal static double BeatSeconds(double bpm) => Math.Clamp(60 / Math.Clamp(bpm, 40, 190), .32, 1.5);

    // 这一句该演多久（秒）= 拍数 × 一拍的长度。120 BPM 时：摇摆一 6 秒（PetWindow.Dance 再乘 2）。
    internal static double Seconds(string word, double bpm) => Beats(word) * BeatSeconds(bpm);
}
