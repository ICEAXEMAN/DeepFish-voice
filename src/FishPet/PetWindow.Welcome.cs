using System;
using System.Threading;
using System.Threading.Tasks;

namespace FishPet;

// 第一次真正出场时说欢迎（台词和规则见 Welcome.cs、welcome.txt / welcome_calm.txt）。
// 每句先看语音包里有没有这条录音（W-0x.wav），有就放、气泡跟着录音走；没有就只冒气泡。
internal sealed partial class PetWindow
{
    private bool welcoming;

    // 她露面的时候调一下：还没欢迎过、不是自检用的看不见的桌宠、也不是收着待命，才会说。
    // Ready()（程序一开就出场）和 Wake()（本来收着、被叫出来）都会调到这里。
    private void WelcomeSoon()
    {
        if (settings.Welcomed || test || standby || welcoming) return;
        welcoming = true;
        _ = WelcomeAsync();
    }

    private async Task WelcomeAsync()
    {
        try
        {
            await Task.Delay(1800); // 让出场的问候动作先演完
            // 全屏的东西盖着、正在收起、还没画出来：等到她真的看得见（最多一分钟）。等不到就算了，下次出场再说，不记“说过了”。
            for (int wait = 0; wait < 120 && (standby || hiding || hiddenForFullscreen || !ready) && !exiting; wait++) await Task.Delay(500);
            if (exiting || standby || hiding || hiddenForFullscreen || !ready) return;
            settings.Welcomed = true;
            settings.Save();
            var text = Welcome.Text(Places.Root, settings.VoicePack);
            var bubbles = Welcome.Bubbles(text);
            Places.Log($"第一次出场：说欢迎（{settings.VoicePack}，{Welcome.FileFor(settings.VoicePack)}，{bubbles.Length} 句）");
            // 一句一句来：先放录音（气泡跟着录音走），这一句演完再歇一口气说下一句。
            // 查录音要用去掉〔动作〕的正文，index.json 的键就是那个（Voice.Norm 只去空格）——2026-11-13 修。
            for (int i = 0; i < bubbles.Length && !exiting; i++)
            {
                var bubble = bubbles[i];
                var voiceSeconds = Speak(Tts.Plain(bubble));
                Places.Log($"欢迎 {i + 1}/{bubbles.Length}：{(voiceSeconds is null ? "没有录音，只打字" : $"录音 {voiceSeconds:0.0} 秒")}");
                await InTurn(() => { if (!exiting) SayLine(bubble, voiceSeconds); return Task.CompletedTask; }, CancellationToken.None);
                for (int wait = 0; wait < 120 && speech.IsSaying && !exiting; wait++) await Task.Delay(100);
                if (exiting) return;
                await Task.Delay(900); // 气口：别一句贴着一句
            }
        }
        finally { welcoming = false; }
    }
}
