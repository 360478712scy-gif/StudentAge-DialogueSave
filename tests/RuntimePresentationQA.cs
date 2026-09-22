using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.Storage;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using View.Evt;

public static class RuntimePresentationQA
{
    static void RejectFingerprint(){throw new InvalidOperationException("Runtime save attempted global diagnostic fingerprinting");}
    static NewTalkView Talk=>(NewTalkView)UIMgr.GetView<NewTalkView>(false);
    public static IEnumerator Visual(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;
        yield return until(()=>adapter.CanCapture(out _)&&adv.PresentationVisible,25,"visual event ready");
        yield return new WaitForSecondsRealtime(1);
        check(!UIMgr.IsViewOpened<View.Hint.CommonComfirmView>(),"no fixture popup covers visual checks");
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/event-adv.png"));yield return null;
        Singleton<CommonEvtMgr>.Ins.ShowTalk(1900000001);
        yield return until(()=>Talk.isViewReady&&Talk.talkState==TalkState.AnimEnd,20,"visual native unbound ready");
        check(adv.OwnsPresentation,"unbound dialogue retains ADV");
        yield return new WaitForSecondsRealtime(.5f);
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/unbound-adv.png"));yield return null;
        Singleton<CommonEvtMgr>.Ins.ShowEvent(1);
        yield return until(()=>adv.PresentationVisible&&Talk.talkState==TalkState.AnimEnd,20,"visual event returned");
        bool completed=false;
        AccessTools.Method(typeof(NewTalkView),"WaitBg").Invoke(Talk,new object[]{(Action)(()=>{completed=true;Talk.DoText(Talk.tmpTalks[Talk.tmpTalkIdx]);})});
        check(!adv.PresentationVisible,"transition hides overlay before render");
        yield return new WaitForSecondsRealtime(1.2f);
        check(DialoguePresentationPolicy.IsTransition(Talk)&&!adv.PresentationVisible,"transition stays native");
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/intertitle-native.png"));yield return null;
        yield return until(()=>completed&&adv.PresentationVisible,10,"reading returns after intertitle");
    }
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,IDialogueUiService api,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetView<View.Hint.CommonComfirmView>(false);
        if(notice!=null && notice.GetType().Name=="CommonComfirmView")UIMgr.CloseView(notice);
        yield return until(()=>adapter.CanCapture(out _) && Talk.talkState==TalkState.AnimEnd,25,"presentation fixture stable");
        var adv=AdvDialogueController.Active;
        check(!UIMgr.IsViewOpened<View.Hint.CommonComfirmView>(),"fixture warning is closed before visual validation");
        var baseline=adapter.Capture();
        check(DialoguePresentationPolicy.EventId(Talk)==1&&adv.OwnsPresentation,"queued event owns ADV independently of native evtId");
        yield return new WaitForSecondsRealtime(.6f);
        int refreshes=ui.ToolbarRefreshCount;
        var clock=new System.Diagnostics.Stopwatch();var clicks=new List<double>();var postClickFrames=new List<float>();var ticks=new List<double>();
        var cfg=(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk);
        var speed=AccessTools.Field(typeof(NewTalkView),"txtSpeed");speed.SetValue(Talk,30f);
        var line="这是一段逐字显示的隔离性能测试，用来检查浮现途中点击补全文字时是否重复重建界面。";
        for(int i=0;i<12;i++)
        {
            Talk.txtex_content.DOKill(false);
            AccessTools.Field(typeof(NewTalkView),"waitFrame").SetValue(Talk,0);
            Talk.tmpTalks=new List<string>{line};Talk.tmpTalkIdx=0;Talk.DoText(line);
            for(int f=0;f<8;f++){yield return null;ticks.Add(adv.TickMilliseconds);}
            check(Talk.talkState==TalkState.Anim&&Talk.txtex_content.text.Length<line.Length,"text is typing before completion click");
            int count=adv.History.Entries.Count;
            clock.Restart();Talk.OnClickNext();clock.Stop();clicks.Add(clock.Elapsed.TotalMilliseconds);
            check(Talk.txtex_content.text==line,"first click reveals whole line immediately");
            yield return null;postClickFrames.Add(Time.unscaledDeltaTime*1000);yield return null;postClickFrames.Add(Time.unscaledDeltaTime*1000);
            check(Talk.talkState==TalkState.AnimEnd && ((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk)).id==cfg.id,"completion click does not advance dialogue");
            check(adv.History.Entries.Count==count,"completion click does not capture a duplicate world snapshot");
        }
        check(ui.ToolbarRefreshCount==refreshes,"typing and click-to-complete do not rebuild native toolbar");
        File.WriteAllText(Path.Combine(root,"results/presentation-performance.json"),new JObject{
            ["clickMedianMs"]=clicks.OrderBy(x=>x).ElementAt(6),["clickMaxMs"]=clicks.Max(),
            ["postClickFrameMaxMs"]=postClickFrames.Max(),["postClickFrameP95Ms"]=postClickFrames.OrderBy(x=>x).ElementAt((int)(postClickFrames.Count*.95)),
            ["tickP95Ms"]=ticks.OrderBy(x=>x).ElementAt((int)(ticks.Count*.95)),["nativeToolbarRefreshes"]=ui.ToolbarRefreshCount-refreshes}.ToString());
        // Exercise the actual periodic autosave entry while text is appearing. Measure
        // whole frames as well as synchronous capture; an accepted request is not a save.
        var service=(DialogueSaveService)api;
        var enabled=AccessTools.Field(typeof(DialogueSaveService),"autoEnabled");
        bool previous=(bool)enabled.GetValue(service);
        var synchronous=new List<double>();var frames=new List<float>();
        var guard=new Harmony("dialoguesave.qa.no-runtime-fingerprints");
        foreach(string method in new[]{"CurrentConfigSnapshot","PluginFingerprint","WarmConfigurationPlansTick"})
            guard.Patch(AccessTools.Method(typeof(DialogueCheckpointAdapter),method),prefix:new HarmonyMethod(typeof(RuntimePresentationQA),nameof(RejectFingerprint)));
        try
        {
            api.List(DialogueUiCategory.Auto);yield return until(()=>!service.IsListing,20,"autosave repository ready");
            enabled.SetValue(service,false);
            for(int sample=0;sample<3;sample++)
            {
                Talk.txtex_content.DOKill(false);
                AccessTools.Field(typeof(NewTalkView),"waitFrame").SetValue(Talk,0);
                Talk.tmpTalks=new List<string>{line};Talk.tmpTalkIdx=0;Talk.DoText(line);
                for(int f=0;f<8;f++)yield return null;
                if(sample==0)
                {
                    var mid=adapter.CaptureAsync();yield return until(()=>mid.IsCompleted,20,"capture during typing completes without fingerprints");
                    check(mid.GetAwaiter().GetResult().Dialogue.Value<string>("phase")=="Anim","mid-typing save retains unfinished phase");
                }
                Talk.OnClickNext();yield return until(()=>adapter.CanCapture(out _),15,"automatic save reaches native stable boundary");
                enabled.SetValue(service,true);
                AccessTools.Field(typeof(DialogueSaveService),"lastAutoKey").SetValue(service,null);
                AccessTools.Field(typeof(DialogueSaveService),"nextAutoTime").SetValue(service,0f);
                clock.Restart();service.TickAutoSave();clock.Stop();synchronous.Add(clock.Elapsed.TotalMilliseconds);
                check((bool)AccessTools.Field(typeof(DialogueSaveService),"autoSaveRequested").GetValue(service),"automatic save accepted at completion boundary");
                float deadline=Time.realtimeSinceStartup+25;
                do
                {
                    yield return null;frames.Add(Time.unscaledDeltaTime*1000);
                }while((bool)AccessTools.Field(typeof(DialogueSaveService),"autoSaveRequested").GetValue(service)&&Time.realtimeSinceStartup<deadline);
                check(!string.IsNullOrEmpty((string)AccessTools.Field(typeof(DialogueSaveService),"lastAutoKey").GetValue(service)),"automatic save committed to disk");
                enabled.SetValue(service,false);yield return null;frames.Add(Time.unscaledDeltaTime*1000);
            }
            var repo=(Repository)AccessTools.Field(typeof(DialogueSaveService),"repository").GetValue(service);
            var record=repo.Scan().Where(r=>r.Status==SaveStatus.Ready&&r.Header.Category=="auto"&&r.Header.LogicalSlot=="1").OrderByDescending(r=>r.Header.CreatedUtc).First();
            var disk=repo.Load(record.Header.RevisionId);
            check(disk.World.Length>0&&disk.Dialogue.Value<string>("configDigest")=="","autosave archive passes integrity check without global diagnostics");
            var load=adapter.RestoreAsync(new GameCheckpoint{WorldBytes=disk.World,Dialogue=disk.Dialogue,Brief=new CheckpointBrief{SteamId=disk.Header.SteamId,RunId=disk.Header.RunId}});yield return until(()=>load.IsCompleted,35,"automatic archive restores");load.GetAwaiter().GetResult();
            check(Talk.talk==disk.Dialogue.Value<string>("talk"),"automatic archive restores dialogue");
            File.WriteAllText(Path.Combine(root,"results/autosave-performance.json"),new JObject{
                ["loadedTalkConfigurations"]=Cfg.TalkCfgMap.Count,["captureMaxMs"]=synchronous.Max(),
                ["frameMaxMs"]=frames.Max(),["frameP95Ms"]=frames.OrderBy(x=>x).ElementAt((int)(frames.Count*.95)),
                ["frameCount"]=frames.Count,["worldBytes"]=disk.World.Length,["cycles"]=3}.ToString());
        }
        finally{enabled.SetValue(service,previous);guard.UnpatchSelf();}
        // Suppressed native refreshes during the restore lease must not hide ADV.
        Talk.RefreshTalk(1900000001);
        check(!(bool)AccessTools.Field(typeof(AdvDialogueController),"transitionPending").GetValue(adv),"blocked refresh does not leave ADV stuck hidden");
        yield return until(()=>adapter.CanAdvancePresentation(Talk),10,"restore releases presentation before next dialogue");
        // Direct ShowTalk retains ADV regardless of event provenance.
        AccessTools.Field(typeof(NewTalkView),"evtId").SetValue(Talk,1);
        Singleton<CommonEvtMgr>.Ins.ShowTalk(1900000001);
        yield return until(()=>Talk.isViewReady && Talk.talkState==TalkState.AnimEnd,20,"unbound native dialogue");
        check(!DialoguePresentationPolicy.IsEvent(Talk)&&adv.OwnsPresentation,"unbound dialogue keeps ADV despite stale evtId");
        check(!Resources.FindObjectsOfTypeAll<RectTransform>().Any(x=>x.name.StartsWith("DialogueSave.Hotkey.")&&x.gameObject.activeInHierarchy),"unbound dialogue has no plugin save buttons");
        Singleton<CommonEvtMgr>.Ins.ShowEvent(1);
        yield return until(()=>adv.PresentationVisible && Talk.talkState==TalkState.AnimEnd,20,"event reuses ADV on same view");
        check(DialoguePresentationPolicy.IsEvent(Talk),"ShowEvent returns to event presentation");
        // Exercise the native transition method used by screenEffect 4008, including story Mods.
        bool completed=false;
        AccessTools.Method(typeof(NewTalkView),"WaitBg").Invoke(Talk,new object[]{(Action)(()=>{completed=true;Talk.DoText(Talk.tmpTalks[Talk.tmpTalkIdx]);})});
        check(!adv.PresentationVisible,"transition hides ADV synchronously before first render");
        int transitionFrames=0;bool leaked=false;float transitionDeadline=Time.realtimeSinceStartup+10;
        while(!completed&&Time.realtimeSinceStartup<transitionDeadline)
        {
            yield return new WaitForEndOfFrame();
            if(DialoguePresentationPolicy.IsTransition(Talk)){transitionFrames++;leaked|=adv.PresentationVisible;}
            if(transitionFrames==40)ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/intertitle-native.png"));
            yield return null;
        }
        check(transitionFrames>10&&!leaked,"ADV stays hidden for every observed native intertitle frame");
        yield return until(()=>completed&&adv.PresentationVisible,10,"ADV returns after transition");
        var restore=adapter.RestoreAsync(baseline);yield return until(()=>restore.IsCompleted,35,"restore event presentation metadata");restore.GetAwaiter().GetResult();
        yield return until(()=>adv.PresentationVisible&&adapter.CanAdvancePresentation(Talk),15,"restored event is visible");
        check(DialoguePresentationPolicy.EventId(Talk)==1,"save restores explicit event presentation origin");
        // Existing saves have no optional presentation field; root-event fallback remains readable.
        baseline.Dialogue.Remove("presentationEvent");
        restore=adapter.RestoreAsync(baseline);yield return until(()=>restore.IsCompleted,35,"restore older checkpoint without presentation field");restore.GetAwaiter().GetResult();
        yield return until(()=>adv.PresentationVisible&&adapter.CanAdvancePresentation(Talk),15,"legacy event presentation visible");
        yield return RuntimeFeedbackQA.Run(adapter,check,until,root);
    }
}
