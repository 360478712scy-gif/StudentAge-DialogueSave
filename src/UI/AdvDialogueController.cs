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
        readonly Action<string> log;
        readonly DialogueHistory history;
        readonly List<Action> restoreNative=new List<Action>();
        readonly Dictionary<int,GameObject> optionRows=new Dictionary<int,GameObject>();
        readonly HashSet<string> readLines=new HashSet<string>();
        NewTalkView talk;
        GameObject canvas,modal,toolbar,choiceRoot,bodyRoot,settingsButton,rollbackPrompt;
        TextMeshProUGUI nameLabel,hintLabel,shownText;
        AdvPaperPlane endMarker;
        string fullLine,positionedLine;
        TextMeshProUGUI positionedText;
        bool positionedDark;
        string measuredLine;
        TextMeshProUGUI measuredText;
        bool measuredDark;
        float measuredHeight;
        Vector2 markerAnchor;
        float nameWidth=160;
        bool hasSpeaker;
        AdvVeil veil;
        AdvPaper namePaper;
        RectTransform nameRule,bookmark,bookmarkGold;
        readonly List<AdvIcon> icons=new List<AdvIcon>();
        readonly List<TextMeshProUGUI> toolbarCaptions=new List<TextMeshProUGUI>();
        bool sceneSkipPending,escapeOwned;
        readonly HashSet<int> shielded=new HashSet<int>();
        Action restoreText;
        bool dark;
        TMP_FontAsset font;
        IDisposable modalPause,hiddenPause;
        float nextScan,nextHistory,nextSkip;
        bool folded,hidden,skipping,disposed,firstPrompt,modalKeepsDialogue;
        int consumedFrame=-1;
        string optionSignature,name;
        internal bool IsAdv=>preference.Value=="ADV";
        internal bool ModalOpen=>modal!=null;
        internal bool BlocksActions=>ModalOpen || hidden;
        internal DialogueHistory History=>history;
        internal string Mode=>preference.Value;
        internal bool IsHidden=>hidden;
        internal bool IsFolded=>folded;
        internal double TickMilliseconds {get;private set;}
        internal bool IsCgPresentation=>dark;
        internal bool LastAttachedBeforeReady {get;private set;}
        internal void LatePresentation(){if(!disposed && talk!=null && canvas!=null)SyncReadingSurface();}
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
            skipConfirmation=preference.ConfigFile.Bind("Interface","ConfirmStorySkip",true,"跳过剧情前显示确认；可在确认框选择不再提示。");
            rollbackConfirmation=preference.ConfigFile.Bind("Interface","ConfirmHistoryJump",true,"跳转对话前显示确认；可在确认框选择不再提示。");
            history=new DialogueHistory(adapter,shutdown,log);Active=this;
            adapter.PresentationTextSpeed=speed=>IsAdv && speed<=0?30:speed;
            adapter.BeforePresentationRebuild=Detach;
            adapter.PreparingPresentation=view=>{if(IsAdv){if(!ReferenceEquals(talk,view)){Detach();Attach(view);}SyncReadingSurface();}};
            harmony.Patch(AccessTools.Method(typeof(BaseView),"HotKeyInput"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(GlobalKeyPrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnClickHistory"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(HistoryPrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"DoText"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TextStarting)),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TextChanged)),finalizer:new HarmonyMethod(typeof(AdvDialogueController),nameof(TextFinished)));
            harmony.Patch(NextMethod,prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(AdvancePrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnHotKeyInput"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(KeyPrefix)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnOpen"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TalkOpened)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"RefreshTalk",new[]{typeof(int),typeof(bool)}),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(TalkStarting)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"ShowOption"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(HistoryBoundary)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"NextTalk"),prefix:new HarmonyMethod(typeof(AdvDialogueController),nameof(HistoryBoundary)));
            harmony.Patch(AccessTools.Method(typeof(SettingView),"OnOpen"),postfix:new HarmonyMethod(typeof(AdvDialogueController),nameof(SettingsOpened)));
        }
        static bool GlobalKeyPrefix(int _hotKey,ref bool __result)
        {
            var a=Active;
            if(a?.HandleEscape()==true || a?.HandleSpace()==true){__result=true;return false;}
            if(a?.ModalOpen!=true)return true;
            if((_hotKey==108 || _hotKey==121) && a.preference.Value!="Ask")a.CloseModal();
            __result=true;return false;
        }
        static bool HistoryPrefix(){if(Active?.IsAdv!=true)return true;Active.OpenHistory();return false;}
        static bool AdvancePrefix()
        {
            var a=Active;if(a==null || !a.IsAdv)return true;
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
            if(!a.IsAdv)return true;
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
            if(!IsAdv || talk==null || Keyboard.current==null)return false;
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
            var a=Active;if(a?.IsAdv!=true || ReferenceEquals(a.talk,__instance) ||
                (NewTalkType)TypeField.GetValue(__instance)!=NewTalkType.Talk)return;
            a.Detach();a.Attach(__instance);
        }
        static void TalkStarting(NewTalkView __instance)
        {
            var a=Active;if(a?.IsAdv!=true || ReferenceEquals(a.talk,__instance) || __instance.txtex_content==null)return;
            // Native RefreshTalk can spend several frames loading art before marking
            // the view ready. Own presentation before any of those frames can render.
            a.Detach();a.Attach(__instance);
        }
        static void TextStarting(NewTalkView __instance,string txt,out float? __state)
        {
            __state=null;var a=Active;if(a?.IsAdv!=true)return;
            // Keep native typing, end effects and the click-to-complete state machine.
            // ADV's default cannot inherit the original mode's instant-text setting.
            float speed=(float)SpeedField.GetValue(__instance);
            if(speed<=0){__state=speed;SpeedField.SetValue(__instance,30f);}
            if(ReferenceEquals(a.talk,__instance)){a.fullLine=txt;a.positionedLine=null;}
        }
        static Exception TextFinished(NewTalkView __instance,float? __state,Exception __exception)
        {if(__state.HasValue)SpeedField.SetValue(__instance,__state.Value);return __exception;}
        static void HistoryBoundary(NewTalkView __instance){Active?.history.Tick(__instance);}
        static void TextChanged(NewTalkView __instance)
        {
            if(ReferenceEquals(Active?.talk,__instance))Active.SyncReadingSurface();
            HistoryBoundary(__instance);
        }
        static void SettingsOpened(SettingView __instance){Active?.AttachSettings(__instance);}

        internal void Tick()
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            try{TickCore();}finally{TickMilliseconds=watch.Elapsed.TotalMilliseconds;}
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
                bool supported=IsAdv && view?.gameObject!=null && view.viewState==ViewState.Opened &&
                    (NewTalkType)TypeField.GetValue(view)==NewTalkType.Talk;
                if(supported && !ReferenceEquals(view,talk)){Detach();Attach(view);}
                else if(!supported && talk!=null)Detach();
                var setting=UIMgr.GetView<SettingView>(false) as SettingView;
                if(setting?.isViewReady==true && settingsButton==null)AttachSettings(setting);
            }
            if(talk==null || canvas==null)return;
            bool visible=UIMgr.GetTopView(ViewType.Guide,ViewType.Side)==talk && !adapter.IsRestoring;
            canvas.SetActive(visible && (!ModalOpen || modalKeepsDialogue) && !hidden);
            if(ModalOpen)
            {
                return;
            }
            if(!visible)return;
            SyncReadingSurface();
            for(int i=0;i<icons.Count;i++)
            {
                bool active=(icons[i].Symbol==5 && (bool)AutoField.GetValue(talk)) || (icons[i].Symbol==6 && skipping);
                icons[i].color=active?AdvWidgets.Gold:(dark?new Color(.96f,.94f,.89f,1):AdvWidgets.Ink);
            }
            foreach(var caption in toolbarCaptions)caption.color=dark?new Color(.96f,.94f,.89f,1):AdvWidgets.Ink;
            SyncOptions();
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
                if(talk.talkState==TalkState.Option || key==null || !readLines.Contains(key))skipping=false;
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
            talk=view;font=AdvWidgets.ReadingFont(view.txtex_content.font,true);fullLine=view.tmpTalks!=null && view.tmpTalkIdx>=0 && view.tmpTalkIdx<view.tmpTalks.Count?view.tmpTalks[view.tmpTalkIdx]:view.txtex_content.text;
            nextHistory=Time.unscaledTime+.35f;
            canvas=AdvWidgets.Canvas("DialogueSave.ADV",29000);
            var root=AdvWidgets.Rect("Frame",canvas.transform,0,0,1920,1080);
            root.anchorMin=root.anchorMax=new Vector2(.5f,.5f);root.pivot=new Vector2(.5f,.5f);root.anchoredPosition=Vector2.zero;
            bodyRoot=AdvWidgets.Rect("Reading",root,0,0,1920,1080).gameObject;
            veil=AdvWidgets.Rect("Soft upper edge",bodyRoot.transform,0,695,1920,385).gameObject.AddComponent<AdvVeil>();veil.raycastTarget=false;veil.SetDark(false);
            bookmark=AdvWidgets.Box("Bookmark",bodyRoot.transform,150,834,4,105,AdvWidgets.Blue).rectTransform;
            bookmarkGold=AdvWidgets.Box("Bookmark gold",bodyRoot.transform,150,950,4,22,AdvWidgets.Gold).rectTransform;
            namePaper=AdvWidgets.Card("Name paper",bodyRoot.transform,185,792,248,52,AdvWidgets.Paper);
            nameRule=AdvWidgets.Box("Name underline",bodyRoot.transform,207,841,186,1,new Color(.74f,.63f,.45f,.5f)).rectTransform;
            nameLabel=AdvWidgets.Label("Speaker",bodyRoot.transform,font,"",209,792,206,52,28,AdvWidgets.Ink);
            endMarker=AdvWidgets.Rect("ADV.EndPaperPlane",bodyRoot.transform,0,0,24,24).gameObject.AddComponent<AdvPaperPlane>();endMarker.raycastTarget=false;endMarker.color=AdvWidgets.Paper;endMarker.rectTransform.pivot=new Vector2(0,.5f);
            shownText=view.txtex_content;restoreText=MoveText(shownText,bodyRoot.transform);
            HideGroup(view.group_talk.gameObject);HideGroup(view.group_option.gameObject);
            var top=UIMgr.GetView<TopView>(false) as TopView;if(top?.itemgroup_key!=null)HideGroup(top.itemgroup_key.gameObject);
            toolbar=AdvWidgets.Rect("Toolbar",root,910,1018,856,48).gameObject;
            hintLabel=AdvWidgets.Label("Toolbar hint",root,font,"",810,982,945,30,18,AdvWidgets.Ink);hintLabel.alignment=TextAlignmentOptions.Right;
            string[] labels={"回看","保存","读取","快存","快读","自动","快进","隐藏","设置","跳过剧情"};
            string[] captions={"","SAVE","LOAD","Q.SAVE","Q.LOAD","","","","",""};
            Action[] actions={OpenHistory,()=>saves.Open(true),()=>saves.Open(false),service.QuickSave,service.QuickLoad,ToggleAuto,ToggleSkip,ToggleHidden,OpenSettings,RequestSceneSkip};
            float x=0;
            int[] order={1,2,3,4,0,5,6,9,7,8};
            foreach(int i in order)
            {
                int index=i;float width=i==1 || i==2?88:(i==3 || i==4?124:52);
                var b=AdvWidgets.Button("ADV."+labels[i],toolbar.transform,font,captions[i],x,0,width,42,actions[i]);x+=width+12;
                if(i>=1 && i<=4)
                {
                    var caption=b.GetComponentInChildren<TextMeshProUGUI>();caption.fontSize=24;caption.fontStyle=FontStyles.Bold;toolbarCaptions.Add(caption);
                }
                else
                {
                    var icon=AdvWidgets.Rect("Symbol",b.transform,11,6,30,30).gameObject.AddComponent<AdvIcon>();
                    icon.Symbol=i==9?10:i;icon.color=AdvWidgets.Ink;icon.raycastTarget=false;icons.Add(icon);
                }
                var hint=b.gameObject.AddComponent<AdvHint>();
                hint.Show=show=>{if(hintLabel!=null)hintLabel.text=show?Hint(index):"";};
            }
            var fold=AdvWidgets.Button("ADV.ToolbarToggle",root,font,"",1810,1018,52,42,ToggleToolbar);
            var foldIcon=AdvWidgets.Rect("Symbol",fold.transform,11,6,30,30).gameObject.AddComponent<AdvIcon>();
            foldIcon.Symbol=9;foldIcon.color=AdvWidgets.Ink;foldIcon.raycastTarget=false;
            foldIcon.rectTransform.pivot=new Vector2(.5f,.5f);foldIcon.rectTransform.anchoredPosition=new Vector2(26,-21);
            foldIcon.rectTransform.localEulerAngles=new Vector3(0,0,folded?180:0);icons.Add(foldIcon);
            var foldHint=fold.gameObject.AddComponent<AdvHint>();foldHint.Show=show=>{if(hintLabel!=null)hintLabel.text=show?(folded?"展开操作栏 · T":"收起操作栏 · T"):"";};
            choiceRoot=AdvWidgets.Rect("Choices",root,0,0,1920,760).gameObject;
            toolbar.SetActive(!folded);name=null;optionSignature=null;dark=false;SyncReadingSurface();
        }
        Action MoveText(TextMeshProUGUI text,Transform parent)
        {
            var r=text.rectTransform;var oldParent=r.parent;int sibling=r.GetSiblingIndex();
            var amin=r.anchorMin;var amax=r.anchorMax;var pivot=r.pivot;var pos=r.anchoredPosition;var size=r.sizeDelta;var scale=r.localScale;
            var color=text.color;var originalFont=text.font;var originalMaterial=text.fontSharedMaterial;bool originalGradient=text.enableVertexGradient;float fs=text.fontSize;var align=text.alignment;var overflow=text.overflowMode;
            bool auto=text.enableAutoSizing, raycast=text.raycastTarget;float spacing=text.lineSpacing;
            Action restore=()=>{if(text==null || oldParent==null)return;r.SetParent(oldParent,false);r.SetSiblingIndex(sibling);
                r.anchorMin=amin;r.anchorMax=amax;r.pivot=pivot;r.anchoredPosition=pos;r.sizeDelta=size;r.localScale=scale;
                text.font=originalFont;text.fontSharedMaterial=originalMaterial;text.enableVertexGradient=originalGradient;text.color=color;text.fontSize=fs;text.alignment=align;text.overflowMode=overflow;text.enableAutoSizing=auto;text.lineSpacing=spacing;text.raycastTarget=raycast;};
            r.SetParent(parent,false);r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=new Vector2(0,1);
            r.anchoredPosition=new Vector2(210,-874);r.sizeDelta=new Vector2(1490,124);r.localScale=Vector3.one;
            text.font=font;text.fontSharedMaterial=AdvWidgets.Outline(font);text.enableVertexGradient=false;
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
        void SyncReadingSurface()
        {
            if(talk==null || bodyRoot==null)return;
            var nativeNext=NextObjectMethod.Invoke(talk,null) as RectTransform;
            if(nativeNext!=null)HideGroup(nativeNext.gameObject);
            var actual=TextMethod.Invoke(talk,null) as TextMeshProUGUI;
            if(actual!=null && actual!=shownText)
            {
                restoreText?.Invoke();shownText=actual;restoreText=MoveText(actual,bodyRoot.transform);
                var group=TalkGroupMethod.Invoke(talk,null) as GameObject;
                if(group!=null && group!=actual.gameObject)HideGroup(group);
            }
            bool cg=(bool)CgField.GetValue(talk);
            if(cg!=dark)
            {
                dark=cg;veil.SetDark(cg);positionedLine=null;
                bookmark.gameObject.SetActive(!cg);bookmarkGold.gameObject.SetActive(!cg);
                foreach(var row in optionRows.Values)AdvWidgets.ThemeButton(row.GetComponent<Button>(),true,cg);
                foreach(var b in toolbar.GetComponentsInChildren<Button>(true))AdvWidgets.ThemeButton(b,false,cg);
            }
            if(shownText!=null)
            {
                var ink=cg?new Color(.97f,.965f,.945f,1):AdvWidgets.Ink;
                if(shownText.color!=ink)shownText.color=ink;
                shownText.fontSize=cg?32:31;shownText.lineSpacing=cg?0:13;
                shownText.alignment=cg?TextAlignmentOptions.Top:TextAlignmentOptions.TopLeft;
                var r=shownText.rectTransform;
                // CG stays close to the lower edge. Longer text grows upward so larger
                // lettering never occupies the controls or forces a tiny font.
                if(measuredText!=shownText || measuredLine!=fullLine || measuredDark!=cg)
                {
                    measuredText=shownText;measuredLine=fullLine;measuredDark=cg;
                    measuredHeight=cg?Mathf.Max(44,shownText.GetPreferredValues(fullLine??shownText.text,1490,0).y):124;
                }
                float height=measuredHeight;
                var pos=new Vector2(210,cg?-(1000-height):-874);
                var size=new Vector2(1490,height);
                if(r.anchoredPosition!=pos){r.anchoredPosition=pos;positionedLine=null;}
                if(r.sizeDelta!=size){r.sizeDelta=size;positionedLine=null;}
                SyncSpeaker();
                float nameTop=cg?1000-height-46:792;
                float paperX=cg?(1920-nameWidth)*.5f:185;
                float paperHeight=cg?40:52;
                namePaper.rectTransform.anchoredPosition=new Vector2(paperX,-nameTop);
                namePaper.rectTransform.sizeDelta=new Vector2(nameWidth,paperHeight);
                nameLabel.rectTransform.anchoredPosition=new Vector2(paperX+24,-nameTop);
                nameLabel.rectTransform.sizeDelta=new Vector2(nameWidth-48,paperHeight);
                nameLabel.alignment=TextAlignmentOptions.Center;
                nameLabel.fontSize=28;nameLabel.color=AdvWidgets.Ink;
                nameRule.anchoredPosition=new Vector2(paperX+20,-(nameTop+paperHeight-1));
                nameRule.sizeDelta=new Vector2(nameWidth-40,1);
                float veilTop=cg?(hasSpeaker?nameTop-25:1000-height-28):695;
                veil.rectTransform.anchoredPosition=new Vector2(0,-veilTop);
                veil.rectTransform.sizeDelta=new Vector2(1920,1080-veilTop);
                PositionEndMarker();
            }
        }
        void SyncSpeaker()
        {
            var cfg=CfgField.GetValue(talk) as TalkCfg;
            string currentName=((UnityEngine.UI.Text)NameMethod.Invoke(talk,null))?.text??"";
            hasSpeaker=cfg!=null && NewTalkView.IsSomebodyTalking(cfg.roleIds) && !string.IsNullOrWhiteSpace(currentName) && currentName!="旁白";
            namePaper.gameObject.SetActive(hasSpeaker);nameLabel.gameObject.SetActive(hasSpeaker);nameRule.gameObject.SetActive(hasSpeaker);
            if(!hasSpeaker)return;
            if(name!=currentName)
            {
                name=currentName;nameLabel.text=name;
                nameWidth=Mathf.Clamp(nameLabel.GetPreferredValues(name,1440,52).x+48,94,1490);
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
            if(!ready || shownText==null)return;
            if(positionedText!=shownText || positionedLine!=shownText.text || positionedDark!=dark || shownText.havePropertiesChanged)
            {
                shownText.ForceMeshUpdate();
                for(int i=shownText.textInfo.characterCount-1;i>=0;i--)
                {
                    var c=shownText.textInfo.characterInfo[i];if(!c.isVisible)continue;
                    Vector3 local=bodyRoot.transform.InverseTransformPoint(shownText.transform.TransformPoint(new Vector3(c.topRight.x,(c.ascender+c.descender)*.5f,0)));
                    markerAnchor=new Vector2(local.x+10,local.y);
                    endMarker.color=dark?Color.white:AdvWidgets.Paper;break;
                }
                positionedText=shownText;positionedLine=shownText.text;positionedDark=dark;
            }
            // Transform-only idle animation: no new texture, mesh or tween allocation.
            float phase=Time.unscaledTime*2.8f;
            endMarker.rectTransform.anchoredPosition=markerAnchor+new Vector2(0,Mathf.Sin(phase)*2.2f);
            endMarker.rectTransform.localRotation=Quaternion.Euler(0,0,Mathf.Sin(phase+.7f)*4);
        }

        void SyncOptions()
        {
            bool shown=talk.talkState==TalkState.Option;
            choiceRoot.SetActive(shown && !hidden);
            if(!shown){optionSignature=null;return;}
            skipping=false;
            var cells=talk.itemgroup_options.GetCells();
            string signature=string.Join(",",cells.Select(c=>c.gameObject.GetInstanceID()+":"+((CommonEvtOptionData)c.data).id));
            if(signature==optionSignature)return;optionSignature=signature;
            foreach(var row in optionRows.Values)UnityEngine.Object.Destroy(row);optionRows.Clear();
            float start=Mathf.Min(500-cells.Count*44,680-((cells.Count-1)*88+68));
            for(int i=0;i<cells.Count;i++)
            {
                var cell=cells[i];var data=cell.data as CommonEvtOptionData;
                var label=cell.gameObject.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t=>!string.IsNullOrEmpty(t.text));
                string caption=label?.text??(Cfg.OptionCfgMap.TryGetValue(data.id,out var cfg)?cfg.content:"选项");
                var b=AdvWidgets.Button("ADV.Choice."+i,choiceRoot.transform,font,caption,460,start+i*88,1000,68,()=>
                {
                    if(talk==null || talk.talkState!=TalkState.Option || ModalOpen)return;
                    var native=cell.gameObject.GetComponentsInChildren<Button>(true).FirstOrDefault(x=>x.interactable);
                    if(native!=null)native.onClick.Invoke();
                },true,true);
                var captionText=b.GetComponentInChildren<TextMeshProUGUI>();captionText.fontSize=27;
                captionText.rectTransform.anchoredPosition=new Vector2(160,0);
                captionText.rectTransform.sizeDelta=new Vector2(680,68);
                captionText.enableWordWrapping=true;captionText.enableAutoSizing=true;captionText.fontSizeMin=21;captionText.fontSizeMax=27;
                AdvWidgets.ThemeButton(b,true,dark);optionRows[i]=b.gameObject;
            }
        }
        internal void ToggleToolbar(){folded=!folded;if(toolbar!=null)toolbar.SetActive(!folded);
            var icon=icons.FirstOrDefault(i=>i.Symbol==9);if(icon!=null)icon.rectTransform.localEulerAngles=new Vector3(0,0,folded?180:0);}
        internal void ToggleHidden()
        {
            if(talk==null || ModalOpen)return;hidden=!hidden;skipping=false;
            if(hidden)hiddenPause=adapter.PauseForMenu();else{hiddenPause?.Dispose();hiddenPause=null;}
            bodyRoot.SetActive(!hidden);choiceRoot.SetActive(!hidden && talk.talkState==TalkState.Option);toolbar.SetActive(!hidden && !folded);
            canvas.SetActive(!hidden);consumedFrame=Time.frameCount;
        }
        void ToggleAuto(){if(talk!=null){skipping=false;AutoMethod.Invoke(talk,new object[]{!(bool)AutoField.GetValue(talk)});}}
        void ToggleSkip(){if(talk!=null){AutoMethod.Invoke(talk,new object[]{false});skipping=!skipping;}}
        void RequestSceneSkip()
        {
            if(talk==null || ModalOpen || hidden)return;
            skipping=false;
            if(!skipConfirmation.Value){BeginSceneSkip();return;}
            CreateModal(true);
            BuildConfirmation(modal.transform,"Skip","确定要跳过当前剧情吗？","返回对话","跳过剧情",skipConfirmation,
                CloseModal,()=>{CloseModal();BeginSceneSkip();});
        }
        void BuildConfirmation(Transform parent,string key,string title,string cancelText,string confirmText,
            ConfigEntry<bool> preferenceEntry,Action cancelAction,Action confirmAction)
        {
            var frame=AdvWidgets.Rect(key+" confirmation",parent,0,0,600,280);
            frame.anchorMin=frame.anchorMax=frame.pivot=new Vector2(.5f,.5f);frame.anchoredPosition=Vector2.zero;
            var paper=frame.gameObject.AddComponent<AdvPaper>();paper.color=AdvWidgets.Paper;paper.raycastTarget=true;
            AdvWidgets.Label("Title",frame,font,title,48,26,504,60,30,AdvWidgets.Ink);
            bool remember=false;
            var rememberButton=AdvWidgets.Button("ADV."+key+"Remember",frame,font,"不再显示此提示",48,114,260,44,()=>{});
            var mark=AdvWidgets.Box("Check box",rememberButton.transform,4,12,20,20,new Color(.79f,.74f,.64f,1));
            var tick=AdvWidgets.Box("Checked",mark.transform,4,4,12,12,AdvWidgets.Ink);tick.gameObject.SetActive(false);
            var label=rememberButton.GetComponentInChildren<TextMeshProUGUI>();label.rectTransform.anchoredPosition=new Vector2(40,0);label.rectTransform.sizeDelta=new Vector2(216,44);label.alignment=TextAlignmentOptions.MidlineLeft;
            rememberButton.onClick.AddListener(()=>{remember=!remember;tick.gameObject.SetActive(remember);});
            var cancel=AdvWidgets.Button("ADV."+key+"Cancel",frame,font,cancelText,48,202,180,48,cancelAction,true);
            var confirm=AdvWidgets.Button("ADV."+key+"Confirm",frame,font,confirmText,372,202,180,48,()=>{
                if(remember){preferenceEntry.Value=false;preference.ConfigFile.Save();}
                confirmAction();
            },true);
            var colors=cancel.colors;colors.normalColor=new Color(.94f,.92f,.88f,1);cancel.colors=colors;
            colors=confirm.colors;colors.normalColor=new Color(.90f,.85f,.74f,1);confirm.colors=colors;
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
            if(settingsButton!=null)UnityEngine.Object.Destroy(settingsButton);
            var b=AdvWidgets.Button("DialogueSave.UiPreference",view.gameObject.transform,font,"对话界面",0,0,190,48,()=>OpenPicker(false),true);
            var r=(RectTransform)b.transform;r.anchorMin=r.anchorMax=new Vector2(1,0);r.pivot=new Vector2(1,0);r.anchoredPosition=new Vector2(-90,26);
            settingsButton=b.gameObject;
        }
        internal void SelectMode(string mode)
        {
            if(mode!="Original" && mode!="ADV")throw new ArgumentException(nameof(mode));
            preference.Value=mode;CloseModal();Detach();preference.ConfigFile.Save();nextScan=0;saves.RefreshActions();
            var view=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            if(mode=="ADV" && view?.gameObject!=null && view.viewState==ViewState.Opened && (NewTalkType)TypeField.GetValue(view)==NewTalkType.Talk)Attach(view);
        }
        internal void OpenPicker(bool first)
        {
            if(font==null || ModalOpen)return;
            CreateModal();var frame=ModalFrame("选择你的阅读界面","以后可在 设置 → 对话界面 随时切换");
            AddModeCard(frame,"Original","原版","不支持其他拓展功能，仅支持对话中保存",96);
            AddModeCard(frame,"ADV","ADV 风","支持插件拓展功能（跳过,对话跳转等）",756);
            if(!first)AdvWidgets.Button("ADV.ClosePicker",frame,font,"返回",1210,692,170,54,CloseModal);
        }
        void AddModeCard(RectTransform frame,string mode,string title,string detail,float x)
        {
            var card=AdvWidgets.Button(mode=="ADV"?"ADV.ChooseAdv":"ADV.ChooseOriginal",frame,font,"",x,206,624,452,()=>SelectMode(mode),true);
            var preview=AdvWidgets.Rect("Preview",card.transform,12,12,600,337.5f).gameObject.AddComponent<AdvModePreview>();
            preview.Load(mode.ToLowerInvariant());
            var heading=AdvWidgets.Label("Mode title",card.transform,font,title,12,355,600,48,32,AdvWidgets.Ink);
            heading.alignment=TextAlignmentOptions.Center;
            var description=AdvWidgets.Label("Mode detail",card.transform,font,detail,12,408,600,34,23,AdvWidgets.Muted);
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
        RectTransform ModalFrame(string title,string subtitle)
        {
            var r=AdvWidgets.Rect("Paper",modal.transform,0,0,1476,770);r.anchorMin=r.anchorMax=new Vector2(.5f,.5f);
            r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=Vector2.zero;
            var paper=r.gameObject.AddComponent<AdvPaper>();paper.color=AdvWidgets.Paper;paper.raycastTarget=true;paper.Cut=18;
            AdvWidgets.Box("Binding",r,40,44,4,64,AdvWidgets.Blue);
            AdvWidgets.Label("Title",r,font,title,72,34,1000,72,42,AdvWidgets.Ink);
            AdvWidgets.Label("Subtitle",r,font,subtitle,76,115,1260,45,22,AdvWidgets.Muted);
            AdvWidgets.Box("Rule",r,76,179,1324,1,new Color(.74f,.63f,.45f,.45f));return r;
        }
        internal void OpenHistory()
        {
            if(!IsAdv || talk==null || ModalOpen || hidden)return;skipping=false;
            history.Tick(talk);
            CreateModal(true);
            modal.transform.Find("Shade").GetComponent<Image>().color=Color.clear;
            var data=AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk) as List<TalkData>;
            var reader=modal.AddComponent<AdvBacklog>();
            reader.Build(modal.transform,font,data,history,RequestRollback,CloseModal);
        }

        void RequestRollback(int index)
        {
            if(history.Restoring || rollbackPrompt!=null)return;
            if(!rollbackConfirmation.Value){Rollback(index);return;}
            rollbackPrompt=AdvWidgets.Rect("Rollback confirmation overlay",modal.transform,0,0,1920,1080).gameObject;
            AdvWidgets.Fill((RectTransform)rollbackPrompt.transform);
            var shade=AdvWidgets.Box("Shade",rollbackPrompt.transform,0,0,1920,1080,new Color(.17f,.15f,.12f,.40f),true);
            AdvWidgets.Fill(shade.rectTransform);
            BuildConfirmation(rollbackPrompt.transform,"Jump","确定要跳转到这段对话吗？","返回日志","跳转对话",rollbackConfirmation,
                CloseRollbackPrompt,()=>{CloseRollbackPrompt();Rollback(index);});
        }
        void CloseRollbackPrompt()
        {
            if(rollbackPrompt==null)return;
            rollbackPrompt.SetActive(false);UnityEngine.Object.Destroy(rollbackPrompt);rollbackPrompt=null;consumedFrame=Time.frameCount;
        }

        async void Rollback(int index)
        {
            if(history.Restoring)return;
            try
            {
                await history.Restore(index);
                CloseModal();
                var restored=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                if(!ReferenceEquals(talk,restored))
                {
                    Detach();
                    if(IsAdv && restored?.gameObject!=null && restored.viewState==ViewState.Opened &&
                        (NewTalkType)TypeField.GetValue(restored)==NewTalkType.Talk)Attach(restored);
                }
                nextScan=0;
            }
            catch(Exception ex){log("对话跳转失败："+ex);}
        }
        internal void CloseModal()
        {
            if(history.Restoring)return;
            if(rollbackPrompt!=null){CloseRollbackPrompt();return;}
            if(modal!=null){modal.SetActive(false);UnityEngine.Object.Destroy(modal);}modal=null;modalKeepsDialogue=false;
            modalPause?.Dispose();modalPause=null;consumedFrame=Time.frameCount;
        }
        void Detach()
        {
            hiddenPause?.Dispose();hiddenPause=null;hidden=false;skipping=false;sceneSkipPending=false;
            // Move original text home before destroying its temporary parent.
            restoreText?.Invoke();restoreText=null;shownText=null;
            for(int i=restoreNative.Count-1;i>=0;i--)try{restoreNative[i]();}catch(Exception e){log(e.Message);}
            restoreNative.Clear();shielded.Clear();icons.Clear();toolbarCaptions.Clear();
            if(canvas!=null){canvas.SetActive(false);UnityEngine.Object.Destroy(canvas);}canvas=null;talk=null;optionRows.Clear();
        }
        public void Dispose(){if(disposed)return;disposed=true;CloseRollbackPrompt();CloseModal();Detach();if(settingsButton!=null)UnityEngine.Object.Destroy(settingsButton);history.Clear();adapter.PresentationTextSpeed=null;adapter.BeforePresentationRebuild=null;adapter.PreparingPresentation=null;AdvWidgets.ReleaseMaterials();if(Active==this)Active=null;}
    }
}
