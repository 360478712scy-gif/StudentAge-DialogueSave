using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Sdk;
using View.Hint;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeDialogueSave.UI
{
    internal static class AdvConfirmation
    {
        static readonly Dictionary<string,Sprite> sprites=new Dictionary<string,Sprite>();
        static GameObject modal;
        static Action cancel;
        static IDisposable pause;
        static bool closing,releasing;
        static int lifecycle;
        static readonly Queue<Action> pending=new Queue<Action>();
        static ConfigEntry<bool> Preference(string key)=>ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,key);
        static bool Enabled(string key)=>ConfirmationOptions.Enabled(AdvDialogueController.Active.Configuration,key);
        static string nativeScope;
        static readonly Dictionary<string,string> nativeAliases=new Dictionary<string,string>();
        static string ResolveNative(string hash)=>nativeAliases.TryGetValue(hash,out var known)?known:hash;
        static string NativeKey(string title,string description,string yes,string no,Action callback,string tips)
        {
            using(var hash=System.Security.Cryptography.SHA256.Create())
                return "Native_"+BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(title+"\n"+description+"\n"+yes+"\n"+no+"\n"+tips+"\n"+callback?.Method.DeclaringType?.FullName+"."+callback?.Method.Name))).Replace("-","");
        }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(HintHelper),"ShowConfirm"),prefix:new HarmonyMethod(typeof(AdvConfirmation),nameof(NativeAsk)));
            harmony.Patch(AccessTools.Method(typeof(CommonComfirmView),"OnOpen"),postfix:new HarmonyMethod(typeof(AdvConfirmation),nameof(NativeOpened)));
        }
        static bool NativeAsk(string _desc,Action _ok,Action _close,bool _showCloseBtn,string _title,string _okTxt,string _closeTxt,string _tipsTxt)
        {
            var owner=AdvDialogueController.Active;if(owner==null)return true;
            string key=NativeKey(_title,_desc,_okTxt,_closeTxt,_ok,_tipsTxt);
            if(nativeScope!=null){nativeAliases[key]=nativeScope;if(nativeAliases.Count>256){nativeAliases.Clear();nativeAliases[key]=nativeScope;}}
            key=ResolveNative(key);
            if(!Enabled(key)){_ok?.Invoke();return false;}
            if(!owner.IsAdv)return true;
            Show(AdvWidgets.ReadingFont(TMP_Settings.defaultFontAsset),key,
                (string.IsNullOrEmpty(_title)?"":_title+"\n")+_desc+(string.IsNullOrEmpty(_tipsTxt)?"":"\n"+_tipsTxt),
                _ok,_close,_okTxt??"是",_closeTxt??"否",_showCloseBtn);
            return false;
        }
        // Original mode keeps its native frame, labels and close/callback path.
        static void NativeOpened(CommonComfirmView __instance)
        {
            if(AdvDialogueController.Active==null)return;
            var view=__instance;var old=view.transform.Find("DialogueSave.Remember");
            if(old!=null){old.gameObject.SetActive(false);UnityEngine.Object.Destroy(old.gameObject);}
            string key=NativeKey(view.parms.Length>4?view.parms[4] as string:null,view.parms[0] as string,
                view.parms.Length>5?view.parms[5] as string:null,view.parms.Length>6?view.parms[6] as string:null,view.parms.Length>1?view.parms[1] as Action:null,view.parms.Length>7?view.parms[7] as string:null);
            key=ResolveNative(key);
            var row=AdvWidgets.Rect("DialogueSave.Remember",view.transform,0,0,300,40);
            row.anchorMin=row.anchorMax=row.pivot=new Vector2(.5f,0);
            row.anchoredPosition=new Vector2(0,16);
            var check=Remember(row,AdvWidgets.ReadingFont(TMP_Settings.defaultFontAsset));
            var field=AccessTools.Field(typeof(CommonComfirmView),"ok");var ok=field.GetValue(view) as Action;
            field.SetValue(view,(Action)(()=>{if(check!=null && check.Selected)Preference(key).Value=false;ok?.Invoke();}));
        }
        internal static bool IsOpen=>modal!=null || closing;
        static readonly Color Ink=new Color32(51,85,119,255),Blue=new Color32(45,160,225,255);
        internal static Sprite Sprite(string name)
        {
            if(sprites.TryGetValue(name,out var sprite))return sprite;
            var texture=AdvSkin.Texture("confirm-"+name+".png");
            sprite=UnityEngine.Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
            sprites[name]=sprite;return sprite;
        }
        internal static void Build(Transform parent,TMP_FontAsset font,string key,string title,Action no,Action<bool> yes,bool remember,string yesText="是",string noText="否",bool showNo=true)
        {
            var frame=AdvWidgets.Rect(key+" confirmation",parent,0,0,904,512);
            frame.anchorMin=frame.anchorMax=frame.pivot=new Vector2(.5f,.5f);frame.anchoredPosition=Vector2.zero;
            var header=AdvWidgets.Rect("Campus blue header",frame,48,40,808,240).gameObject.AddComponent<RawImage>();
            header.texture=AdvSkin.Texture("confirm-header.png");header.raycastTarget=false;
            var original=AdvWidgets.Rect("Source white frame",frame,0,0,904,512).gameObject.AddComponent<Image>();
            original.sprite=Sprite("frame__null_");original.raycastTarget=true;
            var message=AdvWidgets.Label("Title",frame,font,"",144,72,616,176,32,Ink);
            message.text=ConfirmationText.Wrap(title,592,t=>message.GetPreferredValues(t,float.PositiveInfinity,float.PositiveInfinity).x);
            message.enableAutoSizing=true;message.fontSizeMin=23;message.fontSizeMax=32;
            message.enableWordWrapping=true;message.alignment=TextAlignmentOptions.Center;
            AdvConfirmToggle check=null;
            MakeButton("ADV."+key+"Confirm",frame,font,yesText,showNo?122:294,310,()=>yes(check!=null && check.Selected));
            if(showNo)MakeButton("ADV."+key+"Cancel",frame,font,noText,466,310,no);
            if(remember)
            {
                var row=AdvWidgets.Rect("ADV."+key+"Remember",frame,316,386,280,40);
                check=Remember(row,font);
            }
        }
        static AdvConfirmToggle Remember(RectTransform row,TMP_FontAsset font)
        {
            var hit=row.gameObject.AddComponent<Image>();hit.color=Color.clear;
            var button=row.gameObject.AddComponent<AdvButton>();button.targetGraphic=hit;button.transition=Selectable.Transition.None;
            var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
            var icon=AdvWidgets.Rect("Checked",row,3,3,34,34).gameObject.AddComponent<Image>();icon.raycastTarget=false;
            var label=AdvWidgets.Label("Caption",row,font,"下次不再提示",43,0,257,40,22,Ink);
            var check=row.gameObject.AddComponent<AdvConfirmToggle>();check.Icon=icon;check.Label=label;check.Refresh();
            button.onClick.AddListener(check.Toggle);return check;
        }
        static void MakeButton(string name,Transform frame,TMP_FontAsset font,string text,float x,float y,Action action)
        {
            var image=AdvWidgets.Rect(name,frame,x,y,308,53).gameObject.AddComponent<Image>();image.sprite=Sprite("_yesno_off");
            var button=image.gameObject.AddComponent<AdvButton>();button.targetGraphic=image;button.transition=Selectable.Transition.SpriteSwap;
            button.spriteState=new SpriteState{highlightedSprite=Sprite("_yesno_over"),pressedSprite=Sprite("_yesno_on"),selectedSprite=Sprite("_yesno_off")};
            var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
            var label=AdvWidgets.Label("Caption",image.transform,font,text,4,0,300,53,28,Ink);label.alignment=TextAlignmentOptions.Center;
            var tint=image.gameObject.AddComponent<SettingsButtonInk>();tint.Text=label;tint.Choice=true;
            button.onClick.AddListener(()=>action());
        }
        internal static void Show(TMP_FontAsset font,string key,string title,Action yes,Action no=null,string yesText="是",string noText="否",bool showNo=true)
        {
            if(releasing){no?.Invoke();return;}
            if(IsOpen){pending.Enqueue(()=>Show(font,key,title,yes,no,yesText,noText,showNo));return;}
            var preference=Preference(key);
            if(!Enabled(key)){yes?.Invoke();return;}
            var opening=AdvSettingsTransition.Capture();
            modal=AdvWidgets.Canvas("DialogueSave.Confirmation",32000);cancel=no;
            pause=AdvDialogueController.Active?.PauseConfirmation();
            var shade=AdvWidgets.Box("Shade",modal.transform,0,0,1920,1080,new Color(0,.04f,.08f,.32f),true);AdvWidgets.Fill(shade.rectTransform);
            try {Build(modal.transform,font,key,title,Cancel,remember=>{
                if(remember)preference.Value=false;
                Close(yes);
            },true,yesText,noText,showNo);AdvSettingsTransition.Play(opening,true,sortingOrder:32500,horizontal:true);opening=null;}
            catch {if(opening!=null)RenderTexture.ReleaseTemporary(opening);var failed=modal;modal=null;cancel=null;pause?.Dispose();pause=null;
                if(failed!=null)UnityEngine.Object.Destroy(failed);no?.Invoke();throw;}
        }
        internal static void Ask(string title,Action yes,Action no=null,string key="Action")
        {
            if(AdvDialogueController.Active?.IsAdv!=true)
            {var previous=nativeScope;nativeScope=key;try{HintHelper.ShowConfirm(title,yes,no);}finally{nativeScope=previous;}return;}
            Show(AdvWidgets.ReadingFont(TMP_Settings.defaultFontAsset),key,title,yes,no);
        }
        internal static void Cancel(){if(modal!=null && !closing && (releasing || AdvSettingsTransition.Active==null))Close(cancel);}
        static void Close(Action after=null)
        {
            if(closing)return;
            closing=true;cancel=null;int expectedLifecycle=lifecycle;
            Action dismiss=()=>{
                if(modal!=null){modal.SetActive(false);UnityEngine.Object.Destroy(modal);}modal=null;
            };
            Action finish=()=>{if(expectedLifecycle!=lifecycle)return;pause?.Dispose();pause=null;closing=false;after?.Invoke();if(!IsOpen && pending.Count>0)pending.Dequeue()();};
            if(releasing){dismiss();finish();}else AdvSettingsTransition.Exit(dismiss,finish,horizontal:true);
        }
        internal static void Release()
        {
            lifecycle++;releasing=true;closing=false;Cancel();pause?.Dispose();pause=null;while(pending.Count>0)pending.Dequeue()();
            foreach(var s in sprites.Values)UnityEngine.Object.Destroy(s);sprites.Clear();nativeAliases.Clear();nativeScope=null;releasing=false;
        }
    }
    internal sealed class AdvConfirmToggle:MonoBehaviour,IPointerEnterHandler,IPointerExitHandler,IPointerDownHandler,IPointerUpHandler
    {
        internal Image Icon;internal TextMeshProUGUI Label;internal bool Selected;bool hover,pressed;
        internal void Toggle(){Selected=!Selected;Refresh();}
        internal void Refresh(){Icon.sprite=AdvConfirmation.Sprite("chk_"+(hover && !pressed?"over_":"normal_")+(Selected?"on":"off"));Label.color=hover && !pressed?new Color32(45,160,225,255):new Color32(51,85,119,255);}
        public void OnPointerEnter(PointerEventData e){hover=true;Refresh();}
        public void OnPointerExit(PointerEventData e){hover=false;Refresh();}
        public void OnPointerDown(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=true;Refresh();}}
        public void OnPointerUp(PointerEventData e){if(e.button==PointerEventData.InputButton.Left){pressed=false;Refresh();}}
        void OnDisable(){hover=pressed=false;}
    }
}
