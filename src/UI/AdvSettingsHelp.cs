using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StudentAgeDialogueSave.UI
{
    // One shared footer hint; event driven text, only opacity animates per frame.
    internal sealed class AdvSettingsHelp : MonoBehaviour
    {
        CanvasGroup group;
        TextMeshProUGUI label;
        SettingsHelpTarget current;
        float target;
        string status="";
        internal float Opacity=>group.alpha;
        internal string Caption=>label.text;
        internal static AdvSettingsHelp Create(Transform parent,TMP_FontAsset font,float width=770)
        {
            var root=AdvWidgets.Rect("Settings.Help",parent,80,980,width,78);
            var help=root.gameObject.AddComponent<AdvSettingsHelp>();
            help.group=root.gameObject.AddComponent<CanvasGroup>();help.group.alpha=0;
            help.group.blocksRaycasts=false;help.group.interactable=false;
            AdvSettingsSkin.Help(root).rectTransform.sizeDelta=new Vector2(width,78);
            help.label=AdvWidgets.Label("Help explanation",root,font,"",130,10,width-156,58,21,new Color32(51,85,119,255));
            help.label.enableWordWrapping=true;help.label.enableAutoSizing=true;help.label.fontSizeMin=16;help.label.fontSizeMax=21;
            return help;
        }
        void Display(string text)
        {label.fontSize=21;label.text=ConfirmationText.Wrap(text??"",label.rectTransform.rect.width-12,t=>label.GetPreferredValues(t,float.PositiveInfinity,float.PositiveInfinity).x);target=string.IsNullOrEmpty(text)?0:1;}
        internal void SetStatus(string text){status=text??"";if(current==null)Display(status);}
        internal void Show(SettingsHelpTarget source,string text){current=source;Display(text);}
        internal void Hide(SettingsHelpTarget source)
        {if(current==source){current=null;Display(status);}}
        internal void Clear(){current=null;target=0;group.alpha=0;label.text="";status="";}
        void Update(){if(group.alpha!=target)group.alpha=Mathf.MoveTowards(group.alpha,target,Time.unscaledDeltaTime/.2f);}
        void OnDisable(){if(group!=null)Clear();}
        internal static string Description(string name,string caption,string section)
        {
            if(name.StartsWith("Settings.Tab"))switch(caption){
                case "游戏":return "调整文本播放和快进；向下滚动可查看模组配置。";
                case "对话框":return "设置各项操作是否询问，可重新开启下次不再提示的确认窗口。";
                case "图像":return "调整显示模式、分辨率、垂直同步和帧率上限。";
                case "音频":return "分别调整游戏主音量、背景音乐、音效和语音。";
                default:return "调整对话界面、水印、按钮音效、阅读速度与跳过范围。";
            }
            if(name=="Settings.Defaults")return "恢复模组默认值：显示水印、ADV、音量80、仅已读、文字30、自动49；应用后保存。";
            if(name=="Settings.Cancel")return "放弃本次设置修改，返回游戏。";
            if(name=="Settings.Apply")return "保存本次设置修改，返回游戏。";
            if(name=="Settings.Confirm.AllOn")return "将所有执行确认设为 ON，包括曾勾选下次不再提示的操作；应用后生效。";
            if(name=="Settings.Confirm.AllOff")return "将所有执行确认设为 OFF，操作将直接执行；应用后生效。";
            switch(section){
                case "文本速度":return "原版文本的显示速度："+caption+"。ADV 浮现速度可在模组配置中调整。";
                case "自动播放间隔":return "原版自动播放的句间等待："+caption+"。";
                case "快进范围":return caption+"；遇到选项仍需手动选择。";
                case "快进倍率":return "将原版快进速度设为 "+caption+"。";
                case "显示模式":return "将游戏显示模式设为“"+caption+"”。";
                case "分辨率":return "将游戏画面分辨率设为 "+caption+"。";
                case "垂直同步":return caption=="开启"?"让画面刷新与显示器同步，减少画面撕裂。":"关闭垂直同步，可单独设置帧率上限。";
                case "帧率上限":return "设置游戏的最高帧率："+caption+"。";
                case "语言":return "将游戏语言设为“"+caption+"”。";
                case "主音量":return "调整游戏的整体音量。";
                case "背景音乐":return "调整背景音乐的播放音量。";
                case "音效":return "调整游戏音效的播放音量。";
                case "语音":return "调整角色语音的播放音量。";
                case "左下角学生时代水印":return caption=="显示"?"显示 ADV 对话框左下角的“学生时代”水印。":"隐藏 ADV 对话框左下角的“学生时代”水印。";
                case "对话界面风格":return caption=="ADV"?"使用 ADV 对话界面及其存档、回看、自动和跳过功能。":"使用游戏原版对话界面。";
                case "按钮音效音量":return "调整模组按钮提示音的音量；松开滑块可试听。";
                case "跳过文本范围":return caption=="全部文本"?"允许跳过未读文本；实际经过的文本会记录为已读，遇到选项停止。":"仅跳过已读文本；遇到未读文本或选项时停止。";
                case "文本浮现速度":return "调整 ADV 每秒显示的字数；下方可实时预览。";
                case "自动阅读速度":return "数值越大，自动翻到下一句的等待越短；下方可实时预览。";
                default:return "调整“"+section+"”："+caption+"。";
            }
        }
    }
    internal sealed class SettingsHelpTarget : MonoBehaviour,IPointerEnterHandler,IPointerExitHandler
    {
        internal AdvSettingsHelp Help;
        internal string Description;
        public void OnPointerEnter(PointerEventData e){Help.Show(this,Description);}
        public void OnPointerExit(PointerEventData e){Help.Hide(this);}
        void OnDisable(){if(Help!=null)Help.Hide(this);}
    }
}
