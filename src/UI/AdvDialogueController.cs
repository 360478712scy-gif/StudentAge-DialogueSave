using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave.GameIntegration;

namespace StudentAgeDialogueSave.UI
{
    // Optional presentation owned by the save plugin. Native dialogue still owns
    // typing, effects and option callbacks; native mode is restored by snapshots.
    internal sealed class AdvDialogueController : IDisposable
    {
        internal static AdvDialogueController Active;
        readonly DialogueCheckpointAdapter adapter;
        readonly DialogueUiController saves;
        readonly IDialogueUiService service;
        readonly ConfigEntry<string> preference;
        readonly ConfigEntry<bool> skipConfirmation,rollbackConfirmation;
        internal readonly AdvPreferences Preferences;
        RawImage watermark;
        readonly Action<string> log;
        readonly DialogueHistory history;
        readonly List<Action> restoreNative=new List<Action>();
        readonly Dictionary<int,GameObject> optionRows=new Dictionary<int,GameObject>();
        readonly HashSet<string> readLines=new HashSet<string>();
        NewTalkView talk;
        GameObject canvas,modal,toolbar,choiceRoot,bodyRoot,choiceVeil,rollbackPrompt,backlogCache;
        int backlogRevision=-1,backlogCount;string backlogLast;
        TextMeshProUGUI nameLabel,shownText;
        AdvPaperPlane endMarker;
        string fullLine;
        string measuredLine;
        TextMeshProUGUI measuredText;
        bool measuredDark;
        float measuredHeight;
        float nameWidth=160;
        bool hasSpeaker;
        AdvVeil veil;
        RawImage cgVeil;
        Image cgVeilSource;
        int cgVeilScreenWidth,cgVeilScreenHeight;
        Vector2 cgVeilNativeSize,cgVeilNativePos;
        float cgBaseLineHeight;
        AdvPaper namePaper;
        RectTransform nameRule,bookmark,bookmarkGold;
        AdvSkinButton autoSkin,skipSkin,foldSkin;
        bool sceneSkipPending,escapeOwned;
        readonly HashSet<int> shielded=new HashSet<int>();
        Action restoreText;
        bool dark;
        TMP_FontAsset font;
        IDisposable modalPause,hiddenPause;
        float nextScan,nextHistory,nextSkip,toolbarSlide;
        bool folded,hidden,skipping,disposed,firstPrompt,modalKeepsDialogue;
        int consumedFrame=-1;
        string name;
        bool optionBindingsReady;
        readonly List<int> optionCellIds=new List<int>(),optionIds=new List<int>();
        internal bool IsAdv=>preference.Value=="ADV";
        internal bool UsesAdv(NewTalkView view)=>IsAdv && view!=null && !IsComic(view);
        internal bool OwnsPresentation=>talk!=null && UsesAdv(talk);
        internal bool PresentationVisible=>canvas!=null && canvas.activeSelf;
        bool transitionPending;
        bool Transition=>transitionPending || DialoguePresentationPolicy.IsTransition(talk);
        internal bool ModalOpen=>modal!=null || AdvSavePage.Active!=null || AdvConfirmation.IsOpen || AdvSettingsTransition.Active!=null;
        internal bool BlocksActions=>ModalOpen || hidden;
        internal DialogueHistory History=>history;
        internal string Mode=>preference.Value;
        internal IDisposable PauseConfirmation()=>adapter.IsDialogueContext?adapter.PauseForMenu():null;
        internal ConfigFile Configuration=>preference.ConfigFile;
        internal bool IsHidden=>hidden;
        internal bool IsFolded=>folded;
        internal double TickMilliseconds {get;private set;}
        internal bool IsCgPresentation=>dark;
        internal bool LastAttachedBeforeReady {get;private set;}
        internal void LatePresentation()
        {
            if(disposed || talk==null || canvas==null)return;
            if(Transition){canvas.SetActive(false);return;}
            if(canvas.activeSelf){SyncReadingSurface();if(choiceRoot!=null && choiceRoot.activeSelf!=(talk.talkState==TalkState.Option && !hidden))SyncOptions();}
        }
        static readonly System.Reflection.FieldInfo CfgField=AccessTools.Field(typeof(NewTalkView),"cfg"),
            AutoField=AccessTools.Field(typeof(NewTalkView),"enableAutoTalk"),TypeField=AccessTools.Field(typeof(NewTalkView),"talkType"),
            CgField=AccessTools.Field(typeof(NewTalkView),"isShowingCG");
        static readonly System.Reflection.FieldInfo SpeedField=AccessTools.Field(typeof(NewTalkView),"txtSpeed");
        static readonly System.Reflection.MethodInfo AutoMethod=AccessTools.Method(typeof(NewTalkView),"AutoTalk"),
            NextMethod=AccessTools.Method(typeof(NewTalkView),"OnClickNext"),
            TextMethod=AccessTools.Method(typeof(NewTalkView),"GetTalkTxt"),
            TalkGroupMethod=AccessTools.Method(typeof(NewTalkView),"GetTalkGroup"),
            NextObjectMethod=AccessTools.Method(typeof(NewTalkView),"GetNextObj"),
            NameMethod=AccessTools.Method(typeof(NewTalkView),"GetNameTxt");

        internal AdvDialogueController(DialogueCheckpointAdapter adapter,DialogueUiController saves,IDialogueUiService service,
            ConfigEntry<string> preference,CancellationToken shutdown,Action<string> log,Harmony harmony)
        {
            this.adapter=adapter;this.saves=saves;this.service=service;this.preference=preference;this.log=log;
            Preferences=new AdvPreferences(preference.ConfigFile);AdvSkin.ButtonVolume=Preferences.ButtonVolume.Value;
            skipConfirmation=preference.ConfigFile.Bind("Interface","ConfirmStorySkip",true,"跳过剧情前显示确认；可在确认框选择不再提示。");
            rollbackConfirmation=preference.ConfigFile.Bind("Interface","ConfirmHistoryJump",true,"跳转对话前显示确认；可在确认框选择不再提示。");
            history=new DialogueHistory(adapter,shutdown,log);Active=this;
            adapter.PresentationTextSpeed=speed=>OwnsPresentation?Preferences.TextSpeed.Value:speed;
            adapter.BeforePresentationRebuild=Detach;
            adapter.PreparingPresentation=view=>{if(UsesAdv(view)){if(!ReferenceEquals(talk,view)){Detach();Attach(view);}transitionPending=false;SyncReadingSurface();}};
            harmony.Patch(AccessTools.Method(typeof(BaseView),"HotKeyInput"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(GlobalKeyPrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnClickHistory"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(HistoryPrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"DoText"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TextStarting)),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TextChanged)),finalizer:new HarmonyMethod(typeof(AdvDialogueController),nameof(TextFinished)));
            foreach(var method in new[]{"WaitBg","BlackBg","BlackBg2"})
                harmony.Patch(AccessTools.Method(typeof(NewTalkView),method),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TransitionStarting)));
            harmony.Patch(NextMethod,prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(AdvancePrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnHotKeyInput"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(KeyPrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnOpen"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TalkOpened)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"RefreshTalk",new[]{typeof(int),typeof(bool)}),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TalkStarting)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"ShowOption"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(HistoryBoundary)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"NextTalk"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(HistoryBoundary)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"ShowComic"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(ComicStarting)),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(ComicStarted)));
            harmony.Patch(AccessTools.Method(typeof(SettingView),"OnOpen"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(SettingsOpened)));
            harmony.Patch(AccessTools.Method(typeof(SettingView),"OnClose"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(SettingsClosed)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"IniSetting"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(NativeSettingsChanged)));
            AdvReadPolicy.Install(harmony);AdvConfirmation.Install(harmony);
        }
        static bool GlobalKeyPrefix(int _hotKey,ref bool __result)
        {
            var a=Active;
            if(AdvSavePage.Active?.EditorOpen==true){if(_hotKey==108 || _hotKey==121)AdvSavePage.Active.CloseEditor();__result=true;return false;}
            if(a?.HandleEscape()==true || a?.HandleSpace()==true){__result=true;return false;}
            if(a?.ModalOpen!=true)return true;
            if((_hotKey==108 || _hotKey==121) && a.preference.Value!="Ask")a.CloseModal();
            __result=true;return false;
        }
        static bool HistoryPrefix(){if(Active?.OwnsPresentation!=true)return true;Active.OpenHistory();return false;}
        static bool AdvancePrefix()
        {
            var a=Active;if(a==null || !a.OwnsPresentation)return true;
            if(a.HandleSpace())return false;
            if(a.hidden){a.ToggleHidden();a.consumedFrame=Time.frameCount;return false;}
            bool advance=!a.ModalOpen && a.consumedFrame!=Time.frameCount;
            if(advance)a.history.Tick(a.talk);
            return advance;
        }
        static bool KeyPrefix(int _keyAction,ref bool __result)
        {
            var a=Active;if(a==null)return true;
            if(a.HandleEscape()){__result=true;return false;}
            if(a.ModalOpen){if(_keyAction==108 || _keyAction==121)a.CloseModal();__result=true;return false;}
            if(!a.OwnsPresentation)return true;
            if(a.HandleSpace()){__result=true;return false;}
            if(a.hidden){if(_keyAction==126 || _keyAction==120 || _keyAction==108)a.ToggleHidden();__result=true;return false;}
            if(_keyAction==126){a.ToggleHidden();__result=true;return false;}
            return true;
        }
        bool HandleEscape()
        {
            var key=Keyboard.current?.escapeKey;if(key==null)return false;
            if(escapeOwned)
            {
                if(key.wasReleasedThisFrame || !key.isPressed)escapeOwned=false;
                return true;
            }
            if(ModalOpen && preference.Value!="Ask" && key.wasPressedThisFrame)
            {
                escapeOwned=true;CloseModal();consumedFrame=Time.frameCount;return true;
            }
            return false;
        }
        internal bool HandleSpace()
        {
            if(!OwnsPresentation || talk==null || Keyboard.current==null)return false;
            var space=Keyboard.current.spaceKey;
            if(!space.wasPressedThisFrame && !space.wasReleasedThisFrame)return false;
            if(ModalOpen)return true;
            if(UIMgr.GetTopView(ViewType.Guide,ViewType.Side)!=talk)return false;
            var selected=EventSystem.current?.currentSelectedGameObject;
            if(selected!=null && (selected.GetComponent<TMP_InputField>()!=null || selected.GetComponent<InputField>()!=null))return false;
            // The native Control dispatches click actions on key release. Consume
            // both edges, but toggle only on press, so release cannot undo the hide.
            if(space.wasPressedThisFrame && consumedFrame!=Time.frameCount)ToggleHidden();
            else consumedFrame=Time.frameCount;
            return true;
        }
        static void TalkOpened(NewTalkView __instance)
        {
            var a=Active;if(a==null || !a.UsesAdv(__instance) || ReferenceEquals(a.talk,__instance) ||
                (NewTalkType)TypeField.GetValue(__instance)!=NewTalkType.Talk)return;
            a.Detach();a.Attach(__instance);
        }
        static void TalkStarting(NewTalkView __instance,bool __runOriginal)
        {
            var a=Active;if(a==null || !__runOriginal || !a.adapter.CanAdvancePresentation(__instance))return;
            if(!a.UsesAdv(__instance)){if(ReferenceEquals(a.talk,__instance))a.Detach();a.saves.RefreshActions();return;}
            a.transitionPending=true;if(a.canvas!=null)a.canvas.SetActive(false);
            if(ReferenceEquals(a.talk,__instance) || __instance.txtex_content==null)return;
            // Native RefreshTalk can spend several frames loading art before marking
            // the view ready. Own presentation before any of those frames can render.
            a.Detach();a.Attach(__instance);
        }
        static void TextStarting(NewTalkView __instance,string txt,out float? __state)
        {
            __state=null;var a=Active;if(a==null || !a.UsesAdv(__instance))return;
            // A comic can end inside RefreshTalk after its prefix. Reattach before
            // DoText draws the next normal/CG line, not at the periodic discovery tick.
            if(!ReferenceEquals(a.talk,__instance) && __instance.txtex_content!=null)
            {a.Detach();a.Attach(__instance);}
            // Keep native typing, end effects and the click-to-complete state machine.
            // ADV's default cannot inherit the original mode's instant-text setting.
            float speed=(float)SpeedField.GetValue(__instance);
            __state=speed;SpeedField.SetValue(__instance,a.Preferences.TextSpeed.Value);
            AccessTools.Field(typeof(NewTalkView),"autoTalkTimeSpan").SetValue(__instance,a.Preferences.AutoDelay);
            if(ReferenceEquals(a.talk,__instance)){a.transitionPending=false;a.fullLine=txt;}
        }
        static Exception TextFinished(NewTalkView __instance,float? __state,Exception __exception)
        {if(__state.HasValue)SpeedField.SetValue(__instance,__state.Value);return __exception;}
        static void HistoryBoundary(NewTalkView __instance){if(Active?.UsesAdv(__instance)==true)Active.history.Tick(__instance);}
        static void TransitionStarting(NewTalkView __instance)
        {
            var a=Active;a?.saves.SuspendDialogueControls();
            if(a!=null && ReferenceEquals(a.talk,__instance)){a.transitionPending=true;a.canvas?.SetActive(false);}
        }
        static void TextChanged(NewTalkView __instance)
        {
            if(ReferenceEquals(Active?.talk,__instance))Active.SyncReadingSurface();
            HistoryBoundary(__instance);
        }
        static void SettingsOpened(SettingView __instance)
        {
            foreach(var option in __instance.dropdown_lanuage.options)
            {
                if(option.text.StartsWith("简体中文",StringComparison.Ordinal))option.text="简体中文";
                else if(option.text.StartsWith("繁體中文",StringComparison.Ordinal) || option.text.StartsWith("繁体中文",StringComparison.Ordinal))option.text="繁体中文";
            }
            __instance.dropdown_lanuage.RefreshShownValue();
            Active?.AttachSettings(__instance);
        }
        static void SettingsClosed(SettingView __instance)
        {
            __instance.gameObject.GetComponent<NativeModeSetting>()?.Complete(__instance);
            if(AdvSettings.Active!=null && AdvSettingsTransition.Active==null)
                AdvSettingsTransition.Play(AdvSettingsTransition.Capture(),false,sortingOrder:32500);
            AdvSettings.Active?.Close();
        }
        static void NativeSettingsChanged(NewTalkView __instance)
        {if(Active?.UsesAdv(__instance)==true)AccessTools.Field(typeof(NewTalkView),"autoTalkTimeSpan").SetValue(__instance,Active.Preferences.AutoDelay);}
        internal void ApplyPreferences()
        {AdvSkin.ButtonVolume=Preferences.ButtonVolume.Value;if(watermark!=null)watermark.gameObject.SetActive(Preferences.Watermark.Value && talk!=null && !(bool)CgField.GetValue(talk));if(talk!=null)NativeSettingsChanged(talk);}

        internal void Tick()
        {
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            try{TickCore();}finally{TickMilliseconds=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;}
        }
        void TickCore()
        {
            if(disposed || !DialogueUiController.IsUiReady())return;
            if(Application.isFocused)HandleEscape();
            if(Time.unscaledTime>=nextScan)
            {
                nextScan=Time.unscaledTime+.2f;
                if(font==null)
                {
                    var title=UIMgr.GetView<EntryView>(false) as EntryView;
                    if(title?.isViewReady==true)
                        font=title.gameObject.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t=>t.font).FirstOrDefault(f=>f!=null);
                    if(font==null && TMP_Settings.defaultFontAsset!=null)font=TMP_Settings.defaultFontAsset;
                }
                if(!firstPrompt && preference.Value!="Original" && preference.Value!="ADV" && font!=null &&
                    UIMgr.GetView<EntryView>(false)?.isViewReady==true)
                {firstPrompt=true;OpenPicker(true);}
                var view=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                bool supported=UsesAdv(view) && view?.gameObject!=null && view.viewState==ViewState.Opened &&
                    (NewTalkType)TypeField.GetValue(view)==NewTalkType.Talk;
                if(supported && !ReferenceEquals(view,talk)){Detach();Attach(view);}
                else if(!supported && talk!=null)Detach();
                var setting=UIMgr.GetView<SettingView>(false) as SettingView;
                if(setting?.isViewReady==true && setting.viewState==ViewState.Opened && AdvSettings.Active==null)AttachSettings(setting);
            }
            if(talk==null || canvas==null)return;
            bool visible=UIMgr.GetTopView(ViewType.Guide,ViewType.Side)==talk && !adapter.IsRestoring && !Transition;
            canvas.SetActive(visible && (!ModalOpen || modalKeepsDialogue || AdvConfirmation.IsOpen || AdvSettingsTransition.Active!=null) && !hidden);
            if(ModalOpen)
            {
                return;
            }
            if(!visible)return;
            autoSkin?.SetActive((bool)AutoField.GetValue(talk));skipSkin?.SetActive(skipping);
            SyncOptions();UpdateToolbar();
            var mouse=Mouse.current;var keyboard=Keyboard.current;
            if(Application.isFocused)
            {
                HandleSpace();
                if(consumedFrame!=Time.frameCount && mouse?.rightButton.wasPressedThisFrame==true){ToggleHidden();consumedFrame=Time.frameCount;}
                else if(consumedFrame!=Time.frameCount && hidden && mouse?.leftButton.wasPressedThisFrame==true){ToggleHidden();consumedFrame=Time.frameCount;}
                else if(!hidden && mouse!=null && mouse.scroll.ReadValue().y>0 && !adapter.IsPausedForMenu)OpenHistory();
                if(keyboard?.f5Key.wasPressedThisFrame==true && !BlocksActions)service.QuickSave();
                if(keyboard?.f9Key.wasPressedThisFrame==true && !BlocksActions)service.QuickLoad();
                if(keyboard?.tKey.wasPressedThisFrame==true && !hidden)ToggleToolbar();
            }
            if(hidden || ModalOpen)return;
            if(sceneSkipPending){ContinueSceneSkip();if(talk==null || talk.viewState!=ViewState.Opened)return;}
            var cfg=CfgField.GetValue(talk) as Config.TalkCfg;
            string key=cfg==null?null:cfg.id+":"+talk.tmpTalkIdx;
            if(skipping)
            {
                if(talk.talkState==TalkState.Option || key==null || (!Preferences.SkipUnread.Value && !readLines.Contains(key) && !Singleton<GlobalMgr>.Ins.HasSeenTalk(cfg.id)))skipping=false;
                else if(Time.unscaledTime>=nextSkip && adapter.CanCapture(out _))
                {nextSkip=Time.unscaledTime+.1f;NextMethod.Invoke(talk,null);}
            }
            if(Time.unscaledTime>=nextHistory)
            {
                nextHistory=Time.unscaledTime+.15f;
                history.Tick(talk);
                if(key!=null && (talk.talkState==TalkState.AnimEnd || talk.talkState==TalkState.Option))readLines.Add(key);
            }
        }

        void Attach(NewTalkView view)
        {
            LastAttachedBeforeReady=!view.isViewReady;
            talk=view;transitionPending=DialoguePresentationPolicy.IsTransition(view);font=AdvWidgets.DialogueFontOverride??AdvWidgets.ReadingFont(view.txtex_content.font,true);fullLine=view.tmpTalks!=null && view.tmpTalkIdx>=0 && view.tmpTalkIdx<view.tmpTalks.Count?view.tmpTalks[view.tmpTalkIdx]:view.txtex_content.text;
            nextHistory=Time.unscaledTime+.35f;
            canvas=AdvWidgets.Canvas("DialogueSave.ADV",29000);
            var root=AdvWidgets.Rect("Frame",canvas.transform,0,0,1920,1080);
            root.anchorMin=root.anchorMax=new Vector2(.5f,.5f);root.pivot=new Vector2(.5f,.5f);root.anchoredPosition=Vector2.zero;
            bodyRoot=AdvWidgets.Rect("Reading",root,0,0,1920,1080).gameObject;
            veil=AdvWidgets.Rect("Soft upper edge",bodyRoot.transform,0,761,1920,376).gameObject.AddComponent<AdvVeil>();veil.raycastTarget=false;veil.SetReadingTone();
            cgVeil=AdvWidgets.Rect("Native CG veil",bodyRoot.transform,0,0,1920,1).gameObject.AddComponent<RawImage>();cgVeil.raycastTarget=false;cgVeil.gameObject.SetActive(false);cgVeilSource=null;
            watermark=AdvSkin.Logo(bodyRoot.transform);watermark.gameObject.SetActive(Preferences.Watermark.Value);
            bookmark=AdvWidgets.Box("Bookmark",bodyRoot.transform,150,834,4,105,AdvWidgets.Blue).rectTransform;
            bookmarkGold=AdvWidgets.Box("Bookmark gold",bodyRoot.transform,150,950,4,22,AdvWidgets.Gold).rectTransform;
            namePaper=AdvWidgets.Card("Name paper",bodyRoot.transform,185,792,248,52,AdvWidgets.Paper);
            nameRule=AdvWidgets.Box("Name separator",bodyRoot.transform,207,841,186,4,new Color(.075f,.075f,.085f,.9f)).rectTransform;
            var ruleShadow=nameRule.gameObject.AddComponent<Shadow>();ruleShadow.effectColor=new Color(0,0,0,.5f);ruleShadow.effectDistance=new Vector2(0,-2);
            for(int band=0;band<2;band++)
            {
                var line=AdvWidgets.Box("Separator highlight "+band,nameRule,1,1+band,1,1,band==0?new Color(.76f,.76f,.72f,1):new Color(.48f,.48f,.46f,1)).rectTransform;
                line.anchorMax=new Vector2(1,1);line.sizeDelta=new Vector2(-2,1);
            }
            nameLabel=AdvWidgets.Label("Speaker",bodyRoot.transform,font,"",209,792,206,52,36,AdvWidgets.Ink);
            nameLabel.font=font;
            nameLabel.fontStyle=FontStyles.Normal;
            nameLabel.fontSharedMaterial=AdvWidgets.DialogueShadow(font);
            endMarker=AdvWidgets.Rect("ADV.EndPaperPlane",bodyRoot.transform,1644,1000,30,30).gameObject.AddComponent<AdvPaperPlane>();endMarker.raycastTarget=false;endMarker.color=new Color(.96f,.95f,.9f,.95f);endMarker.rectTransform.pivot=new Vector2(.5f,.5f);
            endMarker.gameObject.AddComponent<Canvas>();
            shownText=view.txtex_content;restoreText=MoveText(shownText,bodyRoot.transform);
            HideGroup(view.group_talk.gameObject);HideGroup(view.group_option.gameObject);
            var top=UIMgr.GetView<TopView>(false) as TopView;if(top?.itemgroup_key!=null)HideGroup(top.itemgroup_key.gameObject);
            bookmark.gameObject.SetActive(false);bookmarkGold.gameObject.SetActive(false);
            var choiceTint=AdvWidgets.Rect("Choice lower lavender",root,0,990,1920,90).gameObject.AddComponent<RawImage>();
            choiceTint.texture=AdvSkin.Texture("window.png");choiceTint.color=new Color(.54f,.46f,.72f,.38f);choiceTint.raycastTarget=false;
            choiceVeil=choiceTint.gameObject;choiceVeil.SetActive(false);
            toolbar=AdvWidgets.Rect("Toolbar",root,1050,1030,808,50).gameObject;
            string[] labels={"回看","保存","读取","快存","快读","自动","快进","隐藏","设置","跳过剧情"};
            Action[] actions={OpenHistory,()=>saves.Open(true),()=>saves.Open(false),service.QuickSave,service.QuickLoad,ToggleAuto,ToggleSkip,ToggleHidden,OpenSettings,RequestSceneSkip};
            float x=0;
            int[] order={1,2,3,4,0,5,6,9,7,8};
            string[] assets={"log","save","load","qsave","qload","auto","skip","hide","option","next"};
            foreach(int i in order)
            {
                int index=i;float width=(float)AdvSkin.Layout[assets[i]]["width"];
                var b=AdvSkinButton.Create("ADV."+labels[i],assets[i],toolbar.transform,x,0,actions[i]);x+=width+12;
                if(i==5)autoSkin=b;if(i==6)skipSkin=b;
            }
            var fold=AdvSkinButton.Create("ADV.ToolbarToggle","hold",toolbar.transform,760,0,ToggleToolbar);
            foldSkin=fold;fold.SetActive(!folded);
            choiceRoot=AdvWidgets.Rect("Choices",root,0,0,1920,760).gameObject;
            toolbarSlide=0;toolbar.SetActive(true);name=null;optionBindingsReady=false;optionCellIds.Clear();optionIds.Clear();dark=false;SyncReadingSurface();saves.RefreshActions();
        }
        sealed class DialogueQuotes:ITextPreprocessor
        {
            readonly AdvDialogueController owner;readonly ITextPreprocessor previous;
            internal DialogueQuotes(AdvDialogueController owner,ITextPreprocessor previous){this.owner=owner;this.previous=previous;}
            public string PreprocessText(string text)
            {
                var result=previous==null?text:previous.PreprocessText(text);
                var cfg=owner.talk==null?null:CfgField.GetValue(owner.talk) as TalkCfg;
                if(string.IsNullOrEmpty(result) || cfg==null || (bool)CgField.GetValue(owner.talk) || !NewTalkView.IsSomebodyTalking(cfg.roleIds) || result.TrimStart().StartsWith("「"))return result;
                return "「"+result+(text==owner.fullLine?"」":"");
            }
        }
        Action MoveText(TextMeshProUGUI text,Transform parent)
        {
            var r=text.rectTransform;var oldParent=r.parent;int sibling=r.GetSiblingIndex();
            var amin=r.anchorMin;var amax=r.anchorMax;var pivot=r.pivot;var pos=r.anchoredPosition;var size=r.sizeDelta;var scale=r.localScale;
            var color=text.color;var originalFont=text.font;var originalMaterial=text.fontSharedMaterial;bool originalGradient=text.enableVertexGradient;float fs=text.fontSize;var align=text.alignment;var overflow=text.overflowMode;
            var preprocessor=text.textPreprocessor;
            bool auto=text.enableAutoSizing, raycast=text.raycastTarget;float spacing=text.lineSpacing;
            var fitter=text.GetComponent<ContentSizeFitter>();bool fitEnabled=fitter!=null && fitter.enabled;
            if(fitter!=null)fitter.enabled=false;
            Action restore=()=>{if(text==null || oldParent==null)return;if(fitter!=null)fitter.enabled=fitEnabled;r.SetParent(oldParent,false);r.SetSiblingIndex(sibling);
                r.anchorMin=amin;r.anchorMax=amax;r.pivot=pivot;r.anchoredPosition=pos;r.sizeDelta=size;r.localScale=scale;
                text.textPreprocessor=preprocessor;text.font=originalFont;text.fontSharedMaterial=originalMaterial;text.enableVertexGradient=originalGradient;text.color=color;text.fontSize=fs;text.alignment=align;text.overflowMode=overflow;text.enableAutoSizing=auto;text.lineSpacing=spacing;text.raycastTarget=raycast;};
            r.SetParent(parent,false);r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=new Vector2(0,1);
            r.anchoredPosition=new Vector2(210,-874);r.sizeDelta=new Vector2(1490,124);r.localScale=Vector3.one;
            text.textPreprocessor=new DialogueQuotes(this,preprocessor);
            text.font=font;text.fontSharedMaterial=AdvWidgets.DialogueShadow(font);text.UpdateMeshPadding();text.enableVertexGradient=false;
            text.raycastTarget=false;text.color=AdvWidgets.Ink;text.fontSize=31;text.enableAutoSizing=false;text.lineSpacing=13;
            text.alignment=TextAlignmentOptions.TopLeft;text.overflowMode=TextOverflowModes.Overflow;return restore;
        }
        void HideGroup(GameObject go)
        {
            if(go==null || !shielded.Add(go.GetInstanceID()))return;
            // A separate parent shield cannot be reset by native fades on its own
            // CanvasGroup. It exists before the first render, preventing old-UI flashes.
            var r=go.GetComponent<RectTransform>();var parent=r.parent;int sibling=r.GetSiblingIndex();
            var shield=AdvWidgets.Rect("ADV.NativeShield",parent,0,0,1,1);AdvWidgets.Fill(shield);shield.SetSiblingIndex(sibling);
            var cg=shield.gameObject.AddComponent<CanvasGroup>();cg.alpha=0;cg.interactable=false;cg.blocksRaycasts=false;
            r.SetParent(shield,false);
            restoreNative.Add(()=>{if(r!=null && parent!=null){r.SetParent(parent,false);r.SetSiblingIndex(sibling);}
                if(shield!=null)UnityEngine.Object.Destroy(shield.gameObject);});
        }
        string Hint(int index)
        {
            string[] names={"对话回看 · Tab","保存","读取","快速保存","快速读取","自动阅读 · Shift","已读快进","隐藏界面 · 空格 / 鼠标右键","设置","跳过当前剧情"};
            if(index>=1 && index<=4)return names[index]+" · "+saves.KeyLabel((DialogueHotkeyAction)(index-1));
            return names[index];
        }
        internal static bool IsComic(NewTalkView view) => view!=null && (bool)ComicField.GetValue(view);
        static void ComicStarting(){if(Active?.talk!=null)Active.Detach();}
        static void ComicStarted(){Active?.saves.RefreshActions();}
        static readonly System.Reflection.FieldInfo ComicField=AccessTools.Field(typeof(NewTalkView),"isShowingComic");
        void SyncReadingSurface()
        {
            if(talk==null || bodyRoot==null)return;
            if(IsComic(talk)){Detach();return;}
            var nativeNext=NextObjectMethod.Invoke(talk,null) as RectTransform;
            if(nativeNext!=null)HideGroup(nativeNext.gameObject);
            var actual=TextMethod.Invoke(talk,null) as TextMeshProUGUI;
            if(actual!=null && actual!=shownText)
            {
                restoreText?.Invoke();shownText=actual;restoreText=MoveText(actual,bodyRoot.transform);
                // Comic names are children of the moved text; hide their original graphics too.
                foreach(var label in actual.GetComponentsInChildren<UnityEngine.UI.Text>(true))HideGroup(label.gameObject);
                var group=TalkGroupMethod.Invoke(talk,null) as GameObject;
                if((bool)CgField.GetValue(talk) && group!=null)
                {
                    cgVeilSource=group.GetComponent<Image>();
                    if(cgVeilSource!=null)
                    {
                        cgVeil.texture=AdvSkin.NativeAlpha(cgVeilSource.mainTexture);
                        var sprite=cgVeilSource.overrideSprite;
                        cgVeil.uvRect=sprite==null?new Rect(0,0,1,1):new Rect(sprite.textureRect.x/cgVeil.texture.width,sprite.textureRect.y/cgVeil.texture.height,sprite.textureRect.width/cgVeil.texture.width,sprite.textureRect.height/cgVeil.texture.height);
                        cgVeil.color=new Color(0,0,0,cgVeilSource.color.a);
                        SyncCgVeilGeometry();
                    }
                }
                if(group!=null && group!=actual.gameObject)HideGroup(group);
            }
            bool cg=(bool)CgField.GetValue(talk);
            bool nativeCgVeil=cg && cgVeilSource!=null;
            if(veil.gameObject.activeSelf==nativeCgVeil)veil.gameObject.SetActive(!nativeCgVeil);
            if(cgVeil.gameObject.activeSelf!=nativeCgVeil)cgVeil.gameObject.SetActive(nativeCgVeil);
            if(nativeCgVeil && (cgVeilScreenWidth!=Screen.width || cgVeilScreenHeight!=Screen.height))SyncCgVeilGeometry();
            if(cg!=dark)
            {
                dark=cg;
                // Mode changes can keep the same raw string and speaker. Reparse
                // once here, not every frame, to update presentation-only quotes.
                if(shownText!=null)shownText.ForceMeshUpdate(false,true);
            }
            bool showWatermark=Preferences.Watermark.Value && !cg;
            if(watermark!=null && watermark.gameObject.activeSelf!=showWatermark)watermark.gameObject.SetActive(showWatermark);
            if(shownText!=null)
            {
                var ink=new Color(.97f,.965f,.945f,1);
                if(shownText.color!=ink)shownText.color=ink;
                shownText.fontSize=cg?38:36;shownText.lineSpacing=cg?0:6;
                shownText.alignment=cg?TextAlignmentOptions.Top:TextAlignmentOptions.TopLeft;
                var r=shownText.rectTransform;
                // CG stays close to the lower edge. Longer text grows upward so larger
                // lettering never occupies the controls or forces a tiny font.
                if(measuredText!=shownText || measuredLine!=fullLine || measuredDark!=cg)
                {
                    measuredText=shownText;measuredLine=fullLine;measuredDark=cg;
                    measuredHeight=cg?Mathf.Max(44,shownText.GetPreferredValues(fullLine??shownText.text,1490,0).y):160;
                    if(cg)cgBaseLineHeight=Mathf.Max(1,shownText.GetPreferredValues("国",1490,0).y);
                }
                float height=measuredHeight;
                if(nativeCgVeil)
                {
                    // Size from the complete line, so typewriter progress never
                    // pumps the panel. One/two/three normal lines use 50/75/100%.
                    float scale=.5f+.5f*Mathf.Clamp01((height/cgBaseLineHeight-1)/2);
                    float panelHeight=cgVeilNativeSize.y*scale;
                    cgVeil.rectTransform.sizeDelta=new Vector2(cgVeilNativeSize.x,panelHeight);
                    cgVeil.rectTransform.anchoredPosition=cgVeilNativePos+Vector2.down*(cgVeilNativeSize.y-panelHeight);
                }
                var pos=new Vector2(cg?210:468,cg?-(1000-height):-875);
                var size=new Vector2(cg?1490:1154,height);
                if(r.anchoredPosition!=pos)r.anchoredPosition=pos;
                if(r.sizeDelta!=size)r.sizeDelta=size;
                SyncSpeaker(cg);
                float nameTop=cg?1000-height-52:830;
                float paperX=cg?(1920-nameWidth)*.5f:426;
                float paperHeight=cg?40:52;
                namePaper.rectTransform.anchoredPosition=new Vector2(paperX,-nameTop);
                namePaper.rectTransform.sizeDelta=new Vector2(nameWidth,paperHeight);
                nameLabel.rectTransform.anchoredPosition=new Vector2(paperX+24,-nameTop);
                nameLabel.rectTransform.sizeDelta=new Vector2(nameWidth-48,paperHeight);
                nameLabel.alignment=cg?TextAlignmentOptions.Center:TextAlignmentOptions.MidlineLeft;
                nameLabel.fontSize=36;nameLabel.color=ink;
                // A separate rule in the gap, not a text underline: one extra
                // full-width character on each side of the centered CG name.
                float ruleWidth=nameWidth-48+nameLabel.fontSize*2;
                nameRule.anchoredPosition=new Vector2((1920-ruleWidth)*.5f,-(nameTop+paperHeight+4));
                nameRule.sizeDelta=new Vector2(ruleWidth,4);
                // The normal plate stays unchanged; CG retains the original
                // alpha mask with the requested lower one/two-line panel.
                PositionEndMarker();
            }
        }
        void SyncCgVeilGeometry()
        {
            var corners=new Vector3[4];cgVeilSource.rectTransform.GetWorldCorners(corners);
            var sourceCanvas=cgVeilSource.canvas;
            var camera=sourceCanvas.renderMode==RenderMode.ScreenSpaceOverlay?null:sourceCanvas.worldCamera;
            var parent=(RectTransform)bodyRoot.transform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,RectTransformUtility.WorldToScreenPoint(camera,corners[0]),null,out var bottomLeft);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,RectTransformUtility.WorldToScreenPoint(camera,corners[2]),null,out var topRight);
            cgVeil.rectTransform.anchoredPosition=new Vector2(bottomLeft.x,topRight.y);
            cgVeil.rectTransform.sizeDelta=topRight-bottomLeft;
            cgVeilNativeSize=cgVeil.rectTransform.sizeDelta;cgVeilNativePos=cgVeil.rectTransform.anchoredPosition;
            cgVeilScreenWidth=Screen.width;cgVeilScreenHeight=Screen.height;
        }
        void SyncSpeaker(bool cg)
        {
            var cfg=CfgField.GetValue(talk) as TalkCfg;
            string currentName=((UnityEngine.UI.Text)NameMethod.Invoke(talk,null))?.text??"";
            hasSpeaker=cfg!=null && NewTalkView.IsSomebodyTalking(cfg.roleIds) && !string.IsNullOrWhiteSpace(currentName) && currentName!="旁白";
            namePaper.gameObject.SetActive(false);nameLabel.gameObject.SetActive(hasSpeaker);nameRule.gameObject.SetActive(cg && hasSpeaker);
            if(!hasSpeaker)return;
            if(name!=currentName || string.IsNullOrEmpty(nameLabel.text))
            {
                name=currentName;nameLabel.text="【"+currentName.Trim('【','】')+"】";
                nameWidth=Mathf.Clamp(nameLabel.GetPreferredValues(nameLabel.text,1440,52).x+48,94,1490);
            }
            int gender=0;
            if(cfg.roleIds?.Count==1)
            {
                int id=cfg.roleIds[0];
                if(id==0)gender=(int)Singleton<RoleMgr>.Ins.GetRole().Sex;
                else if(Cfg.PersonCfgMap.TryGetValue(id,out var person))gender=person.gender;
            }
            // Only the nameplate changes with the known speaker's gender. Group or
            // unidentified speakers keep the neutral paper colour; never infer from names.
            namePaper.color=gender==2?new Color(.96f,.81f,.83f,.9f):
                gender==1?new Color(.75f,.86f,.88f,.9f):AdvWidgets.Paper;
        }
        void PositionEndMarker()
        {
            bool ready=talk.talkState==TalkState.AnimEnd;
            endMarker.gameObject.SetActive(ready);
        }

        void SyncOptions()
        {
            bool shown=talk.talkState==TalkState.Option;
            bodyRoot.SetActive(!shown && !hidden);
            choiceVeil.SetActive(shown && !hidden);
            choiceRoot.SetActive(shown && !hidden);
            if(!shown){optionBindingsReady=false;optionCellIds.Clear();optionIds.Clear();return;}
            skipping=false;
            var cells=talk.itemgroup_options.GetCells();
            bool unchanged=optionBindingsReady && cells.Count==optionCellIds.Count;
            for(int i=0;unchanged && i<cells.Count;i++)
                unchanged=optionCellIds[i]==cells[i].gameObject.GetInstanceID() && optionIds[i]==((CommonEvtOptionData)cells[i].data).id;
            if(unchanged)return;
            optionBindingsReady=true;optionCellIds.Clear();optionIds.Clear();
            foreach(var cell in cells){optionCellIds.Add(cell.gameObject.GetInstanceID());optionIds.Add(((CommonEvtOptionData)cell.data).id);}
            foreach(var row in optionRows.Values)UnityEngine.Object.Destroy(row);optionRows.Clear();
            float height=cells.Count>6?68:80,pitch=height+26;
            float start=Mathf.Max(70,450-(cells.Count*pitch-26)*.5f);
            for(int i=0;i<cells.Count;i++)
            {
                var cell=cells[i];var data=cell.data as CommonEvtOptionData;
                var nativeCell=cell as GenUI.Common.Cell_CommonOptionItemUI;
                var native=nativeCell?.btn_click;
                var label=nativeCell?.txtex_content;
                string caption=label?.text??(Cfg.OptionCfgMap.TryGetValue(data.id,out var cfg)?cfg.content:"选项");
                var b=AdvChoiceSkin.Create("ADV.Choice."+i,choiceRoot.transform,font,caption,454,start+i*pitch,1012,height,()=>
                {
                    if(talk==null || talk.talkState!=TalkState.Option || ModalOpen)return;
                    if(native?.btn!=null && native.interactable && ReferenceEquals(cell.data,data))native.btn.onClick.Invoke();
                });
                optionRows[i]=b.gameObject;
                b.gameObject.AddComponent<AdvChoiceBinding>().Bind(b,native);
            }
        }
        internal void ToggleToolbar()
        {
            folded=!folded;toolbarSlide=0;foldSkin?.SetActive(!folded);
            if(toolbar!=null){toolbar.SetActive(true);((RectTransform)toolbar.transform).anchoredPosition=new Vector2(1050,-1030);}
        }
        void UpdateToolbar()
        {
            if(toolbar==null)return;
            var rect=(RectTransform)toolbar.transform;
            var mouse=Mouse.current;
            bool near=false;
            if(mouse!=null && RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)rect.parent,mouse.position.ReadValue(),null,out var point))
            {
                var bounds=((RectTransform)rect.parent).rect;
                float x=point.x-bounds.xMin,y=point.y-bounds.yMin;
                near=x>=1020 && x<=1888 && y<=104 && y>=0;
            }
            // Keep a held control in place until release. The hidden toolbar's
            // activation strip remains at the screen edge and has no raycast plate.
            float target=folded && !near?64:0;
            if(mouse?.leftButton.isPressed!=true || !folded)
                toolbarSlide=Mathf.MoveTowards(toolbarSlide,target,Time.unscaledDeltaTime*360);
            rect.anchoredPosition=new Vector2(1050,-1030-toolbarSlide);
        }
        internal void ToggleHidden()
        {
            if(talk==null || ModalOpen)return;hidden=!hidden;skipping=false;
            if(hidden)hiddenPause=adapter.PauseForMenu();else{hiddenPause?.Dispose();hiddenPause=null;}
            SyncOptions();toolbar.SetActive(!hidden);
            canvas.SetActive(!hidden);consumedFrame=Time.frameCount;
        }
        void ToggleAuto(){if(talk!=null){skipping=false;AutoMethod.Invoke(talk,new object[]{!(bool)AutoField.GetValue(talk)});}}
        void ToggleSkip(){if(talk!=null){AutoMethod.Invoke(talk,new object[]{false});skipping=!skipping;}}
        void RequestSceneSkip()
        {
            if(talk==null || ModalOpen || hidden)return;
            skipping=false;
            if(!skipConfirmation.Value){BeginSceneSkip();return;}
            var opening=AdvSettingsTransition.Capture();
            CreateModal(true);
            BuildConfirmation(modal.transform,"Skip","确定要跳过当前剧情吗？","返回对话","跳过剧情",skipConfirmation,
                CloseModal,()=>{CloseModal();BeginSceneSkip();});
            AdvSettingsTransition.Play(opening,true,sortingOrder:32500,horizontal:true);
        }
        void BuildConfirmation(Transform parent,string key,string title,string cancelText,string confirmText,
            ConfigEntry<bool> preferenceEntry,Action cancelAction,Action confirmAction)
        {
            AdvConfirmation.Build(parent,font,key,title,cancelAction,remember=>{
                if(remember){preferenceEntry.Value=false;preference.ConfigFile.Save();}
                confirmAction();
            },true);
        }
        void BeginSceneSkip()
        {
            if(talk==null)return;
            AutoMethod.Invoke(talk,new object[]{false});skipping=false;sceneSkipPending=true;
        }
        void ContinueSceneSkip()
        {
            if(!adapter.CanAdvancePresentation(talk))return;
            if(talk.talkState==TalkState.Option || talk.talkState==TalkState.ShowPaper){sceneSkipPending=false;return;}
            if((bool)AccessTools.Field(typeof(NewTalkView),"isDelaying").GetValue(talk))return;
            var cfg=CfgField.GetValue(talk) as TalkCfg;if(cfg==null){sceneSkipPending=false;return;}
            if(!Preferences.SkipUnread.Value && !Singleton<GlobalMgr>.Ins.HasSeenTalk(cfg.id)){sceneSkipPending=false;return;}
            if(cfg.GetNextTalk()==0 && (cfg.option==null || cfg.option.Count==0) && (cfg.miniGame==null || cfg.miniGame.Count==0))
            {
                // Native advance owns final effects and the continuation callback.
                NextMethod.Invoke(talk,null);
                if(talk.viewState!=ViewState.Opened)sceneSkipPending=false;
                return;
            }
            sceneSkipPending=false;
            AccessTools.Method(typeof(NewTalkView),"OnClickSkip").Invoke(talk,null);
            // The game's skip stops at choices, minigames, or the last line. Only
            // finish that last line; never fabricate a branch or a completion callback.
            cfg=CfgField.GetValue(talk) as TalkCfg;
            if(cfg!=null && cfg.GetNextTalk()==0 && (cfg.option==null || cfg.option.Count==0) && (cfg.miniGame==null || cfg.miniGame.Count==0))sceneSkipPending=true;
        }
        void OpenSettings(){skipping=false;UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});}
        void AttachSettings(SettingView view)
        {
            if(view?.gameObject==null || font==null)return;
            if(!IsAdv)
            {
                var setting=view.gameObject.GetComponent<NativeModeSetting>()??view.gameObject.AddComponent<NativeModeSetting>();
                setting.Begin(view,this);
                return;
            }
            view.gameObject.GetComponent<NativeModeSetting>()?.Hide();
            if(AdvSettings.Active!=null)return;
            AdvSettings.Open(view,this,font,adapter.IsDialogueContext?adapter.PauseForMenu():null);
        }
        internal void SelectMode(string mode)
        {
            if(mode!="Original" && mode!="ADV")throw new ArgumentException(nameof(mode));
            preference.Value=mode;CloseModal();Detach();preference.ConfigFile.Save();nextScan=0;saves.RefreshActions();
            var view=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            if(UsesAdv(view) && view?.gameObject!=null && view.viewState==ViewState.Opened && (NewTalkType)TypeField.GetValue(view)==NewTalkType.Talk)Attach(view);
        }
        internal void OpenPicker(bool first)
        {
            if(font==null || ModalOpen)return;
            CreateModal();AdvSettingsSkin.Background(modal.transform);
            var frame=ModePickerFrame();
            AddModeCard(frame,"Original","原版","保留原版界面，支持对话中保存",96);
            AddModeCard(frame,"ADV","ADV","支持跳过、对话跳转等拓展功能",756);
            if(!first)
            {
                var image=AdvSettingsSkin.Control("ADV.ClosePicker",modal.transform,2,1542,980,294,78);
                var button=image.gameObject.AddComponent<AdvButton>();button.targetGraphic=image;
                AdvSettingsSkin.Style(button,2);
                var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
                var ink=button.gameObject.AddComponent<SettingsButtonInk>();
                ink.Text=AdvSettingsSkin.Lettering("取消",button.transform,70,25,210,28);
                button.onClick.AddListener(CloseModal);
            }
        }
        void AddModeCard(RectTransform frame,string mode,string title,string detail,float x)
        {
            var surface=AdvSettingsSkin.Card(mode=="ADV"?"ADV.ChooseAdv":"ADV.ChooseOriginal",frame,x,166,624,500);
            surface.raycastTarget=true;
            var card=surface.gameObject.AddComponent<AdvButton>();card.targetGraphic=surface;
            card.transition=Selectable.Transition.ColorTint;
            var colors=card.colors;colors.normalColor=Color.white;colors.selectedColor=Color.white;
            colors.highlightedColor=new Color32(219,242,255,255);colors.pressedColor=new Color32(173,216,246,255);
            colors.fadeDuration=.1f;card.colors=colors;
            var nav=card.navigation;nav.mode=Navigation.Mode.None;card.navigation=nav;
            card.onClick.AddListener(()=>SelectMode(mode));
            var navy=new Color32(26,49,77,255);
            var heading=AdvWidgets.Label("Mode title",card.transform,font,title,mode=="ADV"?210:12,8,mode=="ADV"?112:600,40,30,navy);
            heading.fontStyle=FontStyles.Bold;heading.alignment=TextAlignmentOptions.Center;
            if(mode=="ADV")
            {
                var badge=AdvSettingsSkin.Control("ADV.Recommended",card.transform,1,334,11,88,34);
                badge.raycastTarget=false;
                var text=AdvWidgets.Label("Recommended label",badge.transform,font,"推荐",0,0,88,34,21,new Color32(30,120,200,255));
                text.fontStyle=FontStyles.Bold;text.alignment=TextAlignmentOptions.Center;
            }
            var preview=AdvWidgets.Rect("Preview",card.transform,12,70,600,337.5f).gameObject.AddComponent<AdvModePreview>();
            preview.Load(mode.ToLowerInvariant());
            var description=AdvWidgets.Label("Mode detail",card.transform,font,detail,16,429,592,48,24,new Color32(51,85,119,255));
            description.alignment=TextAlignmentOptions.Center;
        }
        void CreateModal(bool keepDialogue=false)
        {
            modalKeepsDialogue=keepDialogue;
            if(talk!=null && adapter.IsDialogueContext)modalPause=adapter.PauseForMenu();
            modal=AdvWidgets.Canvas("DialogueSave.ADV.Modal",31000);
            var shade=AdvWidgets.Box("Shade",modal.transform,0,0,1920,1080,new Color(.17f,.15f,.12f,.7f),true);AdvWidgets.Fill(shade.rectTransform);
            if(canvas!=null && !keepDialogue)canvas.SetActive(false);
        }
        RectTransform ModePickerFrame()
        {
            var r=AdvWidgets.Rect("Campus mode chooser",modal.transform,222,150,1476,720);
            var title=AdvWidgets.Label("Title",r,font,"选择你的阅读界面",96,16,1284,64,42,new Color32(26,49,77,255));
            title.fontStyle=FontStyles.Bold;title.alignment=TextAlignmentOptions.Center;
            var subtitle=AdvWidgets.Label("Subtitle",r,font,"点击下方预览选择，以后可在设置中随时切换",96,92,1284,40,24,new Color32(51,85,119,255));
            subtitle.alignment=TextAlignmentOptions.Center;
            return r;
        }
        internal void OpenHistory()
        {
            if(!OwnsPresentation || talk==null || ModalOpen || hidden)return;skipping=false;
            history.Tick(talk);
            var data=AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk) as List<TalkData>;
            int count=data?.Count??0;string last=count>0?data[count-1].content:null;
            if(backlogCache!=null && backlogRevision==history.Revision && backlogCount==count && backlogLast==last)
            {
                modal=backlogCache;modalKeepsDialogue=true;modalPause=adapter.PauseForMenu();
                modal.SetActive(true);modal.GetComponent<AdvBacklog>().AnimateOpen();return;
            }
            if(backlogCache!=null)
            {
                modal=backlogCache;modalKeepsDialogue=true;modalPause=adapter.PauseForMenu();
                modal.SetActive(true);
                var cached=modal.GetComponent<AdvBacklog>();cached.Reload(data,history);
                backlogRevision=history.Revision;backlogCount=count;backlogLast=last;
                cached.AnimateOpen();return;
            }
            CreateModal(true);backlogCache=modal;backlogRevision=history.Revision;backlogCount=count;backlogLast=last;
            modal.transform.Find("Shade").GetComponent<Image>().color=Color.clear;
            var reader=modal.AddComponent<AdvBacklog>();
            reader.Build(modal.transform,font,data,history,RequestRollback,CloseModal);
            reader.AnimateOpen();
        }

        void RequestRollback(int index)
        {
            if(history.Restoring || rollbackPrompt!=null)return;
            if(!rollbackConfirmation.Value){Rollback(index);return;}
            var opening=AdvSettingsTransition.Capture();
            rollbackPrompt=AdvWidgets.Rect("Rollback confirmation overlay",modal.transform,0,0,1920,1080).gameObject;
            AdvWidgets.Fill((RectTransform)rollbackPrompt.transform);
            var shade=AdvWidgets.Box("Shade",rollbackPrompt.transform,0,0,1920,1080,new Color(.17f,.15f,.12f,.40f),true);
            AdvWidgets.Fill(shade.rectTransform);
            BuildConfirmation(rollbackPrompt.transform,"Jump","确定要跳转到这段对话吗？","返回日志","跳转对话",rollbackConfirmation,
                CloseRollbackPrompt,()=>CloseRollbackPromptThen(()=>Rollback(index)));
            AdvSettingsTransition.Play(opening,true,sortingOrder:32500,horizontal:true);
        }
        void CloseRollbackPrompt()=>CloseRollbackPromptThen(null);
        void CloseRollbackPromptThen(Action after)
        {
            if(rollbackPrompt==null){if(!disposed)after?.Invoke();return;}
            if(!disposed && AdvSettingsTransition.Active!=null)return;
            Action hide=()=>{rollbackPrompt.SetActive(false);UnityEngine.Object.Destroy(rollbackPrompt);rollbackPrompt=null;consumedFrame=Time.frameCount;};
            if(disposed)hide();else AdvSettingsTransition.Exit(hide,()=>{if(!disposed)after?.Invoke();},horizontal:true);
        }

        async void Rollback(int index)
        {
            if(disposed)return;
            if(AdvConfirmation.IsOpen){AdvConfirmation.Cancel();consumedFrame=Time.frameCount;return;}
            if(AdvSavePage.Active!=null){AdvSavePage.Active.RequestClose();consumedFrame=Time.frameCount;return;}
            if(history.Restoring)return;
            skipping=false;sceneSkipPending=false;
            try
            {
                await history.Restore(index);
                CloseModal();
                var restored=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                if(!ReferenceEquals(talk,restored))
                {
                    Detach();
                    if(UsesAdv(restored) && restored?.gameObject!=null && restored.viewState==ViewState.Opened &&
                        (NewTalkType)TypeField.GetValue(restored)==NewTalkType.Talk)Attach(restored);
                }
                nextScan=0;
            }
            catch(Exception ex){log("对话跳转失败："+ex);}
        }
        internal void CloseModal()
        {
            if(AdvConfirmation.IsOpen){AdvConfirmation.Cancel();consumedFrame=Time.frameCount;return;}
            if(history.Restoring)return;
            if(rollbackPrompt!=null){CloseRollbackPrompt();return;}
            if(modal==null)return;
            if(!disposed && AdvSettingsTransition.Active!=null)return;
            Action dismiss=()=>{var closing=modal;modal=null;modalKeepsDialogue=false;
                if(closing!=null){closing.SetActive(false);if(closing!=backlogCache || disposed)UnityEngine.Object.Destroy(closing);}
                modalPause?.Dispose();modalPause=null;consumedFrame=Time.frameCount;};
            if(disposed)dismiss();else AdvSettingsTransition.Exit(dismiss,horizontal:modal!=backlogCache);
        }
        void Detach()
        {
            hiddenPause?.Dispose();hiddenPause=null;hidden=false;skipping=false;sceneSkipPending=false;
            // Move original text home before destroying its temporary parent.
            restoreText?.Invoke();restoreText=null;shownText=null;
            for(int i=restoreNative.Count-1;i>=0;i--)try{restoreNative[i]();}catch(Exception e){log(e.Message);}
            restoreNative.Clear();shielded.Clear();
            autoSkin=skipSkin=foldSkin=null;
            if(canvas!=null){canvas.SetActive(false);UnityEngine.Object.Destroy(canvas);}canvas=null;talk=null;optionRows.Clear();
        }
        public void Dispose(){if(disposed)return;disposed=true;NativeModeSetting.Release();AdvConfirmation.Release();AdvSettings.ReleaseCache();CloseRollbackPrompt();CloseModal();if(backlogCache!=null)UnityEngine.Object.Destroy(backlogCache);backlogCache=null;Detach();history.Clear();adapter.PresentationTextSpeed=null;adapter.BeforePresentationRebuild=null;adapter.PreparingPresentation=null;AdvArchiveSkin.Release();AdvBacklogSkin.Release();AdvSettingsSkin.Release();AdvWidgets.ReleaseMaterials();AdvSkin.Release();if(Active==this)Active=null;}
    }
}
