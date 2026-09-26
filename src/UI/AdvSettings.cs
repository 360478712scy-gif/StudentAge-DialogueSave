using System;
using System.Collections.Generic;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Main;

namespace StudentAgeDialogueSave.UI
{
    // Redrawn extracted skin provides all visible surfaces. Native settings
    // remain the authority for application, persistence and cancellation.
    internal sealed class AdvSettings : MonoBehaviour
    {
        internal static AdvSettings Active;
        static AdvSettings cached;
        readonly Dictionary<int,RectTransform> pages=new Dictionary<int,RectTransform>();
        readonly Dictionary<int,List<Action>> bindings=new Dictionary<int,List<Action>>();
        Transform pageHost;
        TextMeshProUGUI cachedTyping,cachedAuto;
        internal int BuiltPages {get;private set;}
        internal static void ReleaseCache(){Active?.Close();if(cached!=null)Destroy(cached.gameObject);cached=null;if(AdvSettingsTransition.Active!=null)Destroy(AdvSettingsTransition.Active.gameObject);}
        static readonly Color Navy=new Color32(51,85,119,255),Blue=new Color32(74,112,155,255),Pale=new Color32(225,237,245,255);
        SettingView view;
        string optionsSignature;
        AdvDialogueController owner;
        TMP_FontAsset font;
        Transform page;
        AdvSettingsHelp help;
        readonly List<GameObject> originals=new List<GameObject>();
        readonly List<Button> tabs=new List<Button>();
        IDisposable pause;
        bool closed,watermark,unread;
        Dictionary<string,bool> confirmations;
        readonly HashSet<string> confirmationDirty=new HashSet<string>();
        bool resetNativeConfirmations;
        string mode;
        float volume,textSpeed,autoSpeed;
        TextMeshProUGUI typingPreview,autoPreview;
        float previewStart;
        const string Sample="放学后的风吹过走廊，新的故事正等着我们。";
        internal int Tab {get;private set;}
        internal static AdvSettings Open(SettingView view,AdvDialogueController owner,TMP_FontAsset font,IDisposable pause)
        {
            Active?.Close();
            string signature=string.Join("|",view.dropdown_resolution.options.ConvertAll(o=>o.text))+"/"+string.Join("|",view.dropdown_fullscreen.options.ConvertAll(o=>o.text))+"/"+string.Join("|",view.dropdown_lanuage.options.ConvertAll(o=>o.text))+"/"+view.group_lanuage.gameObject.activeSelf;
            if(cached!=null && (cached.owner!=owner || cached.optionsSignature!=signature)){Destroy(cached.gameObject);cached=null;}
            var result=cached;
            if(result==null){var root=AdvWidgets.Canvas("DialogueSave.Settings",30500);AdvWidgets.CenterDesign(root);result=root.AddComponent<AdvSettings>();cached=result;}
            result.closed=false;Active=result;result.gameObject.SetActive(true);
            result.optionsSignature=signature;result.view=view;result.owner=owner;result.font=font;result.pause=pause;
            result.confirmations=ConfirmationOptions.Snapshot(owner.Configuration);result.resetNativeConfirmations=false;result.confirmationDirty.Clear();
            var p=owner.Preferences;result.watermark=p.Watermark.Value;result.unread=p.SkipUnread.Value;
            result.mode=owner.Mode;result.volume=p.ButtonVolume.Value;result.textSpeed=p.TextSpeed.Value;result.autoSpeed=p.AutoSpeed.Value;
            // Preserve the original hierarchy for future opens/unloading.
            foreach(Transform child in view.gameObject.transform)if(child.gameObject.activeSelf){result.originals.Add(child.gameObject);child.gameObject.SetActive(false);}
            AccessTools.Field(typeof(SettingView),"isConfirm").SetValue(view,false);
            if(result.pageHost==null)result.Build();else result.ShowTab(0);result.StartCoroutine(result.AnimateOpen());return result;
        }
        CanvasGroup inputGroup;
        bool transitionBusy;
        System.Collections.IEnumerator AnimateOpen()
        {
            transitionBusy=true;if(inputGroup==null)inputGroup=gameObject.AddComponent<CanvasGroup>();
            inputGroup.alpha=0;inputGroup.blocksRaycasts=true;
            yield return new WaitForEndOfFrame();
            var snapshot=AdvSettingsTransition.Capture();
            if(closed){RenderTexture.ReleaseTemporary(snapshot);yield break;}
            inputGroup.alpha=1;
            Canvas.ForceUpdateCanvases();
            // Unlock on the actual final rendered transition frame, not an
            // independent timer which can consume a click just after the wipe.
            AdvSettingsTransition.Play(snapshot,true,()=>{if(!closed){transitionBusy=false;inputGroup.blocksRaycasts=true;StartCoroutine(WarmPages());}});
        }
        System.Collections.IEnumerator WarmPages()
        {
            for(int id=0;id<5;id++)
            {
                yield return null;
                if(closed)yield break;
                while(transitionBusy){yield return null;if(closed)yield break;}
                if(pages.ContainsKey(id))continue;
                int previous=Tab;ShowTab(id);
                Canvas.ForceUpdateCanvases();
                ShowTab(previous);
            }
        }
        void RequestTab(int id)
        {
            if(transitionBusy || closed || id==Tab)return;
            transitionBusy=true;StartCoroutine(AnimateTab(id));
        }
        System.Collections.IEnumerator AnimateTab(int id)
        {
            yield return new WaitForEndOfFrame();
            var snapshot=AdvSettingsTransition.Capture();
            if(closed){RenderTexture.ReleaseTemporary(snapshot);yield break;}
            ShowTab(id);
            Canvas.ForceUpdateCanvases();
            AdvSettingsTransition.Play(snapshot,true,()=>{if(!closed)transitionBusy=false;},true);
        }
        void RequestClose(Action action)
        {
            if(transitionBusy || closed)return;
            transitionBusy=true;inputGroup.blocksRaycasts=false;
            StartCoroutine(AnimateClose(action));
        }
        System.Collections.IEnumerator AnimateClose(Action action)
        {
            yield return new WaitForEndOfFrame();
            var snapshot=AdvSettingsTransition.Capture();
            AdvSettingsTransition.Play(snapshot,false);
            inputGroup.alpha=0;
            action();
        }
        void Build()
        {
            AdvSkin.PrepareAudio();
            AdvSettingsSkin.Background(transform);
            AdvSettingsSkin.Lettering("System Setting",transform,236,16,326,100);
            help=AdvSettingsHelp.Create(transform,font);
            string[] names={DescCtrl.GetTxt(1001),DescCtrl.GetTxt(1002),DescCtrl.GetTxt(1003),"模组配置","对话框"};
            const float tabStart=575,tabTotal=1176,tabGap=32;
            float tabWidth=(tabTotal-(names.Length-1)*tabGap)/names.Length;
            for(int i=0;i<names.Length;i++){int id=i;tabs.Add(Button("Settings.Tab"+i,names[i],transform,tabStart+i*(tabWidth+tabGap),40,tabWidth,64,()=>RequestTab(id),1));}
            pageHost=AdvWidgets.Rect("Setting cards",transform,116,142,1688,750);
            const float footerStart=874,footerWidth=294,footerGap=40;
            Button("Settings.Defaults","恢复默认",transform,footerStart,980,footerWidth,78,RestoreDefaults,2);
            Button("Settings.Cancel","取消",transform,footerStart+footerWidth+footerGap,980,footerWidth,78,()=>RequestClose(()=>view.CloseView()),2);
            Button("Settings.Apply","应用并返回",transform,footerStart+2*(footerWidth+footerGap),980,footerWidth,78,()=>RequestClose(Apply),2);
            ShowTab(0);
        }
        TextMeshProUGUI Label(string text,Transform parent,float x,float y,float w,float h,float size,Color ink)
            =>AdvWidgets.Label(text,parent,font,text,x,y,w,h,size,ink);
        Button Button(string name,string caption,Transform parent,float x,float y,float w,float h,Action action,int row=0)
        {
            var image=AdvSettingsSkin.Control(name,parent,row,x,y,w,h);
            var b=image.gameObject.AddComponent<AdvButton>();b.targetGraphic=image;AdvSettingsSkin.Style(b,row);
            var nav=b.navigation;nav.mode=Navigation.Mode.None;b.navigation=nav;
            float inset=row==2?w*.24f:8;
            float textHeight=row==1?30:28;
            var art=row!=2?null:AdvSettingsSkin.Lettering(caption,b.transform,inset,(h-textHeight)/2-(row==1?4:0),w-inset-8,textHeight);
            var ink=b.gameObject.AddComponent<SettingsButtonInk>();
            ink.Text=art;ink.Choice=row==0;ink.TopTab=row==1;
            if(row==1)ink.Brush=AdvSettingsSkin.TabBrush(b.transform,w,h,caption.Length);
            if(art==null){var t=Label(caption,b.transform,inset,0,w-inset-8,h,row==1?28:24,Navy);t.alignment=TextAlignmentOptions.Center;if(row==1 || row==2)t.fontStyle=FontStyles.Bold;ink.Text=t;}
            AttachHelp(b.gameObject,AdvSettingsHelp.Description(name,caption,parent.name));
            b.onClick.AddListener(()=>{if(transitionBusy)return;action();});
            return b;
        }
        RectTransform Card(string title,int column,float y,float h=164)
        {
            float x=column*852;
            var card=AdvSettingsSkin.Card(title,page,x,y,836,h).rectTransform;
            Label(title,card,28,12,776,40,22,Navy).fontStyle=FontStyles.Bold;
            return card;
        }
        internal void ShowTab(int id)
        {
            foreach(var option in ConfirmationOptions.Items)if(!confirmationDirty.Contains(option.Key))confirmations[option.Key]=ConfirmationOptions.Entry(owner.Configuration,option.Key).Value;
            var defaultsHint=transform.Find("Settings.Defaults")?.GetComponent<SettingsHelpTarget>();
            if(defaultsHint!=null)defaultsHint.Description=id==4?"将所有执行确认恢复为 ON；应用后生效。":AdvSettingsHelp.Description("Settings.Defaults","恢复默认","");
            Tab=id;typingPreview=id==3?cachedTyping:null;autoPreview=id==3?cachedAuto:null;
            foreach(var pair in pages)pair.Value.gameObject.SetActive(pair.Key==id);
            for(int i=0;i<tabs.Count;i++)
            {
                AdvSettingsSkin.Style(tabs[i],1,i==id);
                tabs[i].GetComponent<SettingsButtonInk>().SetSelected(i==id);
            }
            if(pages.TryGetValue(id,out var existing)){page=existing;foreach(var refresh in bindings[id])refresh();return;}
            page=AdvWidgets.Rect("Settings.Page"+id,pageHost,0,0,1688,750);pages[id]=(RectTransform)page;bindings[id]=new List<Action>();BuiltPages++;
            var settings=Singleton<SettingCtrl>.Ins;
            if(id==0)
            {
                Choice(Card("文本速度",0,0),view.TXT_SPEED,()=>settings.txtSpeed,n=>{settings.txtSpeed=n;Invoke("RefreshTxtSpeed");ShowTab(0);});
                Choice(Card("自动播放间隔",1,0),view.TXT_SPEED,()=>settings.txtTimeSpan,n=>{settings.txtTimeSpan=n;Invoke("RefreshTimeSpan");ShowTab(0);});
                Choice(Card("快进范围",0,172),view.TXT_SPEED_UP_MODE,()=>settings.txtSpeedUpMode,n=>{settings.txtSpeedUpMode=n;Invoke("RefreshSpeedUpMode");ShowTab(0);});
                Choice(Card("快进倍率",1,172),view.TXT_SPEED_UP.ConvertAll(n=>"× "+n).ToArray(),()=>view.TXT_SPEED_UP.IndexOf(settings.txtSpeedUp),n=>{settings.txtSpeedUp=view.TXT_SPEED_UP[n];Invoke("RefreshTxtSpeedUp");ShowTab(0);});
                if(view.group_lanuage.gameObject.activeSelf)DropdownCard("语言",0,344,view.dropdown_lanuage);
            }
            else if(id==1)
            {
                float secondRow=Mathf.Max(ChoiceHeight(view.dropdown_fullscreen.options.Count),ChoiceHeight(view.dropdown_resolution.options.Count))+8;
                DropdownCard("显示模式",0,0,view.dropdown_fullscreen);DropdownCard("分辨率",1,0,view.dropdown_resolution);
                var v=Card("垂直同步",0,secondRow);Choice(v,new[]{"关闭","开启"},()=>settings.vSyncCount>0?1:0,n=>{Invoke(n==0?"OnClickVsyncOff":"OnClickVsyncOn");ShowTab(id);});
                var frameCard=DropdownCard("帧率上限",1,secondRow,view.dropdown_frame);
                bindings[id].Add(()=>frameCard.gameObject.SetActive(settings.vSyncCount==0));
            }
            else if(id==2)
            {
                NativeVolume("主音量",0,0,view.slider_volume_master);NativeVolume("背景音乐",1,0,view.slider_volume_music);
                NativeVolume("音效",0,172,view.slider_volume_sound);NativeVolume("语音",1,172,view.slider_volume_talk);
            }
            else if(id==3)BuildModControls(0);
            else BuildConfirmationOptions();
            foreach(var refresh in bindings[id])refresh();
        }
        void BuildModControls(float y)
        {
            Choice(Card("左下角学生时代水印",0,y),new[]{"隐藏","显示"},()=>watermark?1:0,n=>{watermark=n==1;ShowTab(3);});
            Choice(Card("对话界面风格",1,y),new[]{"原版","ADV"},()=>mode=="ADV"?1:0,n=>{mode=n==1?"ADV":"Original";ShowTab(3);});
            Range(Card("按钮音效音量",0,y+172),"ButtonVolume",0,100,volume*100,n=>{volume=n/100;AdvSkin.ButtonVolume=volume;},n=>Mathf.RoundToInt(n)+"",true,()=>volume*100);
            Choice(Card("跳过文本范围",1,y+172),new[]{"仅已读文本","全部文本"},()=>unread?1:0,n=>{unread=n==1;ShowTab(3);});
            var text=Card("文本浮现速度",0,y+344,388);
            Range(text,"TextSpeed",5,80,textSpeed,n=>{textSpeed=n;previewStart=Time.unscaledTime;},n=>Mathf.RoundToInt(n)+"",false,()=>textSpeed);
            typingPreview=Preview(text,"文本浮现预览");
            var auto=Card("自动阅读速度",1,y+344,388);
            Range(auto,"AutoSpeed",0,100,autoSpeed,n=>{autoSpeed=n;previewStart=Time.unscaledTime;},n=>Mathf.RoundToInt(n)+"",false,()=>autoSpeed);
            autoPreview=Preview(auto,"自动翻句预览");cachedTyping=typingPreview;cachedAuto=autoPreview;previewStart=Time.unscaledTime;
        }
        void SetAllConfirmations(bool enabled)
        {
            foreach(var item in ConfirmationOptions.Items){confirmations[item.Key]=enabled;confirmationDirty.Add(item.Key);}
            resetNativeConfirmations=true;ShowTab(4);
        }
        void BuildConfirmationOptions()
        {
            Button("Settings.Confirm.AllOn","全部 ON",page,526,4,300,53,()=>SetAllConfirmations(true));
            Button("Settings.Confirm.AllOff","全部 OFF",page,862,4,300,53,()=>SetAllConfirmations(false));
            const float gap=12,width=(1688-2*gap)/3;
            int rows=(ConfirmationOptions.Items.Length+2)/3;
            float height=(672-(rows-1)*gap)/rows;
            for(int i=0;i<ConfirmationOptions.Items.Length;i++)
            {
                var item=ConfirmationOptions.Items[i];int column=i/rows,row=i%rows;
                var card=AdvSettingsSkin.Card(item.Caption,page,column*(width+gap),78+row*(height+gap),width,height).rectTransform;
                Label(item.Caption,card,20,8,width-40,32,21,Navy).fontStyle=FontStyles.Bold;
                for(int option=0;option<2;option++)
                {
                    bool value=option==0;
                    var button=Button("Settings.Confirm."+item.Key+"."+(value?"On":"Off"),value?"ON":"OFF",card,76+option*228,58,178,48,()=>{
                        confirmations[item.Key]=value;confirmationDirty.Add(item.Key);if(item.Key=="NativeOther")resetNativeConfirmations=true;ShowTab(4);
                    });
                    bindings[4].Add(()=>{bool selected=confirmations[item.Key]==value;AdvSettingsSkin.Style(button,0,selected);button.GetComponent<SettingsButtonInk>().SetSelected(selected);});
                    button.GetComponent<SettingsHelpTarget>().Description=item.Caption+"：ON 在执行前询问；OFF 直接执行。应用后生效。";
                }
            }
        }
        void AttachHelp(GameObject target,string description)
        {var hint=target.AddComponent<SettingsHelpTarget>();hint.Help=help;hint.Description=description;}
        TextMeshProUGUI Preview(Transform card,string title)
        {
            Label(title,card,94,185,688,28,18,Blue);
            var label=Label(Sample,card,94,225,688,95,22,Navy);label.enableWordWrapping=true;return label;
        }
        static float ChoiceHeight(int count)=>Mathf.Max(164,80+Mathf.Ceil(count/4f)*60+14);
        void Choice(Transform card,string[] names,Func<int> selected,Action<int> action)
        {
            int columns=Mathf.Min(4,names.Length);if(columns==0)return;
            float width=columns<=2?640:780,left=(836-width)*.5f,cell=width/columns;
            for(int i=0;i<names.Length;i++)
            {
                int n=i;
                var b=Button("Settings.Choice."+names[i],names[i],card,left+(i%columns)*cell,72+(i/columns)*60,cell-12,53,()=>action(n));
                bindings[Tab].Add(()=>{bool chosen=n==selected();AdvSettingsSkin.Style(b,0,chosen);b.GetComponent<SettingsButtonInk>().SetSelected(chosen);});
                var label=b.GetComponentInChildren<TextMeshProUGUI>();
                if(label!=null){label.enableAutoSizing=true;label.fontSizeMin=16;label.fontSizeMax=24;}
            }
        }
        RectTransform DropdownCard(string title,int column,float y,Dropdown native)
        {
            Func<Dropdown> current=()=>title=="显示模式"?view.dropdown_fullscreen:title=="分辨率"?view.dropdown_resolution:title=="语言"?view.dropdown_lanuage:view.dropdown_frame;
            var names=native.options.ConvertAll(o=>o.text).ToArray();
            var card=Card(title,column,y,ChoiceHeight(names.Length));
            if(names.Length==0)Label("当前显示器模式",card,98,72,640,53,24,Navy).alignment=TextAlignmentOptions.Center;
            else Choice(card,names,()=>current().value,n=>{current().value=n;if(!closed)ShowTab(Tab);});
            return card;
        }
        void NativeVolume(string title,int column,float y,Slider native)
        {
            Func<Slider> current=()=>title=="主音量"?view.slider_volume_master:title=="背景音乐"?view.slider_volume_music:title=="音效"?view.slider_volume_sound:view.slider_volume_talk;
            Range(Card(title,column,y),title,native.minValue,native.maxValue,native.value,n=>current().value=n,n=>Mathf.RoundToInt(n)+"",false,()=>current().value);
        }
        void Range(Transform card,string name,float min,float max,float value,Action<float> change,Func<float,string> format,bool sound=false,Func<float> read=null)
        {
            var count=Label(format(value),card,724,86,80,32,22,Blue);count.alignment=TextAlignmentOptions.Center;count.fontStyle=FontStyles.Bold;
            AdvWidgets.Box("Value underline",card,740,114,48,2,Blue);
            var root=AdvWidgets.Rect("Settings.Slider."+name,card,150,78,552,54);
            var hit=root.gameObject.AddComponent<Image>();hit.color=Color.clear;
            AttachHelp(root.gameObject,AdvSettingsHelp.Description(name,"",card.name));
            var slider=root.gameObject.AddComponent<Slider>();slider.minValue=min;slider.maxValue=max;slider.wholeNumbers=false;
            // A single full-length gradient rail, as in the reference. The
            // thumb alone indicates the value; no second inset fill boundary.
            AdvSettingsSkin.Rail("Track",root,0,23,552,8);
            var area=AdvWidgets.Rect("Handle area",root,0,27,552,0);
            var handle=AdvSettingsSkin.Control("Paper plane thumb",area,3,0,0,56,56);handle.rectTransform.pivot=new Vector2(.5f,.5f);handle.raycastTarget=false;
            slider.handleRect=handle.rectTransform;slider.targetGraphic=handle;slider.SetValueWithoutNotify(value);
            AdvSettingsSkin.Style(slider,3);
            var navigation=slider.navigation;navigation.mode=Navigation.Mode.None;slider.navigation=navigation;
            slider.onValueChanged.AddListener(n=>{var caption=format(n);if(count.text!=caption)count.text=caption;change(n);});
            if(read!=null)bindings[Tab].Add(()=>{var n=read();slider.SetValueWithoutNotify(n);count.text=format(n);});
            if(sound){var release=root.gameObject.AddComponent<SettingsSoundPreview>();release.Play=()=>AdvSkin.PlayClick(false);}
        }
        void Invoke(string name)=>AccessTools.Method(typeof(SettingView),name).Invoke(view,null);
        void RestoreDefaults()
        {
            if(Tab==4){AdvConfirmation.Show(font,"恢复默认","将所有执行确认恢复为 ON 吗？",()=>SetAllConfirmations(true));return;}
            AdvConfirmation.Show(font,"恢复默认","恢复模组默认设置吗？",()=>{
                watermark=true;unread=false;mode="ADV";volume=.8f;textSpeed=30;autoSpeed=49;
                AdvSkin.ButtonVolume=volume;previewStart=Time.unscaledTime;ShowTab(0);
            });
        }
        void Apply()
        {
            var p=owner.Preferences;p.Watermark.Value=watermark;p.SkipUnread.Value=unread;p.ButtonVolume.Value=volume;p.TextSpeed.Value=textSpeed;p.AutoSpeed.Value=autoSpeed;
            var changed=new Dictionary<string,bool>();foreach(string key in confirmationDirty)changed[key]=confirmations[key];
            ConfirmationOptions.Apply(owner.Configuration,changed,resetNativeConfirmations);
            owner.SelectMode(mode);owner.ApplyPreferences();Invoke("OnClickConfirm");
        }
        void Update()
        {
            if(view==null || view.viewState!=ViewState.Opened){Close();return;}
            if(typingPreview==null)return;
            float duration=Sample.Length/textSpeed;float elapsed=Time.unscaledTime-previewStart;
            typingPreview.maxVisibleCharacters=Mathf.FloorToInt(Mathf.Repeat(elapsed,duration+1.5f)*textSpeed);
            const string second="翻开下一页，把今天的心情写进青春的日记。";
            float delay=Mathf.Lerp(4,.2f,autoSpeed/100),firstDuration=duration+delay;
            float phase=Mathf.Repeat(elapsed,firstDuration+second.Length/textSpeed+delay);
            bool first=phase<firstDuration;string sentence=first?Sample:second;
            if(autoPreview.text!=sentence)autoPreview.text=sentence;
            autoPreview.maxVisibleCharacters=Mathf.FloorToInt((first?phase:phase-firstDuration)*textSpeed);
        }
        internal void Close()
        {
            if(closed)return;closed=true;
            foreach(var child in originals)if(child!=null)child.SetActive(true);
            originals.Clear();AdvSkin.ButtonVolume=owner.Preferences.ButtonVolume.Value;
            pause?.Dispose();pause=null;
            if(Active==this)Active=null;gameObject.SetActive(false);
        }
        void OnDestroy(){if(!closed)Close();}
    }
    internal sealed class SettingsScrollAudio:MonoBehaviour,UnityEngine.EventSystems.IPointerEnterHandler,UnityEngine.EventSystems.IPointerUpHandler
    {
        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData data)=>AdvSkin.PlayHover();
        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData data)=>AdvSkin.PlayClick(false);
    }
    internal sealed class SettingsSoundPreview:MonoBehaviour,UnityEngine.EventSystems.IPointerUpHandler
    {
        internal Action Play;public void OnPointerUp(UnityEngine.EventSystems.PointerEventData data)=>Play?.Invoke();
    }
    internal sealed class SettingsButtonInk:MonoBehaviour,UnityEngine.EventSystems.IPointerEnterHandler,UnityEngine.EventSystems.IPointerExitHandler,UnityEngine.EventSystems.IPointerDownHandler,UnityEngine.EventSystems.IPointerUpHandler
    {
        internal Graphic Text;
        internal Graphic Brush;
        internal bool Choice,TopTab;
        bool selected,hovered,pressed;
        static readonly Color Ink=new Color32(51,85,119,255);
        static readonly Color HoverInk=new Color32(30,120,200,255);
        void RefreshTop()
        {
            var label=Text as TextMeshProUGUI;if(label==null)return;
            bool decorated=!selected && !hovered && !pressed;
            if(Brush!=null)Brush.enabled=decorated;
            label.color=selected || pressed?Color.white:new Color32(16,30,44,255);
            var material=AdvWidgets.Outline(label.font);
            if(label.fontSharedMaterial!=material){label.fontSharedMaterial=material;label.UpdateMeshPadding();}
        }
        void RefreshInk()
        {
            var button=GetComponent<Button>();
            if(button!=null && !button.IsInteractable())
            {if(Text!=null)Text.color=new Color32(140,150,157,255);if(Brush!=null)Brush.enabled=false;return;}
            if(TopTab){RefreshTop();return;}
            if(Text==null)return;
            if(pressed){Text.color=Choice && selected?Ink:Color.white;return;}
            Text.color=Choice?(selected || !hovered?Ink:Color.white):selected?Color.white:hovered?HoverInk:Ink;
        }
        internal void SetSelected(bool value){selected=value;RefreshInk();}
        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData e){hovered=true;RefreshInk();}
        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e){hovered=false;RefreshInk();}
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e)
        {if(e.button==UnityEngine.EventSystems.PointerEventData.InputButton.Left){pressed=true;RefreshInk();}}
        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e)
        {if(e.button==UnityEngine.EventSystems.PointerEventData.InputButton.Left){pressed=false;RefreshInk();}}
        void OnDisable(){pressed=hovered=false;RefreshInk();}
    }
}
