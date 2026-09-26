using BepInEx.Configuration;
using UnityEngine;

namespace StudentAgeDialogueSave.UI
{
    internal sealed class AdvPreferences
    {
        internal readonly ConfigEntry<bool> Watermark,SkipUnread;
        internal readonly ConfigEntry<float> ButtonVolume,TextSpeed,AutoSpeed;
        internal float AutoDelay=>Mathf.Lerp(4f,.2f,AutoSpeed.Value/100f);
        internal AdvPreferences(ConfigFile file)
        {
            Watermark=file.Bind("Interface","ShowWatermark",true,"显示左下角学生时代水印");
            SkipUnread=file.Bind("Interface","SkipUnread",false,"剧情跳过与快进允许跳过未读文本；跳过后计入已读");
            ButtonVolume=file.Bind("Interface","ButtonVolume",.8f,new ConfigDescription("按钮音效音量",new AcceptableValueRange<float>(0,1)));
            TextSpeed=file.Bind("Interface","TextRevealSpeed",30f,new ConfigDescription("ADV 每秒浮现字数",new AcceptableValueRange<float>(5,80)));
            AutoSpeed=file.Bind("Interface","AutoReadSpeed",49f,new ConfigDescription("ADV 自动阅读速度",new AcceptableValueRange<float>(0,100)));
        }
    }
}
