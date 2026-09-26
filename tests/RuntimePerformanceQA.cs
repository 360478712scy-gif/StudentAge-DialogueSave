using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using Config;
using MiniGame.Fight;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using View.Main;
using View.Evt;

public static class RuntimePerformanceQA
{
    static int skips;
    static void Skipped(){skips++;}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,IDialogueUiService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _),25,"performance stable dialogue");
        var timing=new JObject();var clock=System.Diagnostics.Stopwatch.StartNew();
        ui.Open(true);yield return until(()=>UIMgr.IsViewOpened<SaveView>() && !service.IsListing && !service.IsPreparingSave,40,"performance list ready");
        timing["saveMenuReadyMs"]=clock.Elapsed.TotalMilliseconds;
        var archive=(StudentAgeDialogueSave.DialogueSaveService)service;int slot=97;
        while(archive.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot))slot++;
        UiResult saved=null;clock.Restart();archive.ArchiveSave(slot,DialogueUiCategory.Manual,archive.ArchiveRun,r=>{if(!r.IsPending)saved=r;});
        yield return until(()=>saved!=null,40,"performance save completed");
        timing["saveCommitMs"]=clock.Elapsed.TotalMilliseconds;check(saved.Success,"performance save commits full checkpoint: "+saved.Message);
        var record=archive.ArchiveList(DialogueUiCategory.Manual).Single(r=>r.Slot==slot);UIMgr.CloseView<SaveView>();yield return null;
        var loads=new JArray();timing["loadMs"]=loads;
        for(int i=0;i<2;i++)
        {
            UiResult loaded=null;clock.Restart();service.Load(record.RevisionId,r=>{if(!r.IsPending)loaded=r;});
            yield return until(()=>loaded!=null,45,"performance actual load "+i);
            loads.Add(clock.Elapsed.TotalMilliseconds);check(loaded.Success,"performance load complete "+i+": "+loaded.Message);
        }
        var owner=AdvDialogueController.Active;owner.SelectMode("Original");
        UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
        yield return until(()=>UIMgr.GetView<SettingView>().isViewReady,15,"original settings ready");yield return new WaitForSecondsRealtime(.4f);
        var native=UIMgr.GetView<SettingView>() as SettingView;
        check(AdvSettings.Active==null && native.btn_cancel.gameObject.activeInHierarchy,"Original mode keeps native settings appearance");
        check(native.btn_cancel.transform.parent.Find("DialogueSave.StylePicker")==null,"native settings has no standalone style button");
        var modeRow=native.line_txtSpeedUp.parent.Find("DialogueSave.ModeSetting");
        check(modeRow!=null && modeRow.GetSiblingIndex()==native.line_txtSpeedUp.GetSiblingIndex()+1,
            "native mode row is directly after fast-forward speed");
        var modeText=modeRow.GetComponentsInChildren<Text>(true).Single(t=>t.name=="txt_txtSpeedUp");
        var speed=Sdk.Singleton<SettingCtrl>.Ins.txtSpeedUp;
        RuntimeUiQA.Click("DialogueSave.ModeNext",check);yield return new WaitForSecondsRealtime(.3f);
        check(modeText.text=="ADV" && !owner.IsAdv && !owner.ModalOpen,"arrow changes draft without opening chooser or applying");
        check(Sdk.Singleton<SettingCtrl>.Ins.txtSpeedUp==speed,"mode arrows cannot change native speed");
        RuntimeUiQA.Click("DialogueSave.ModePrevious",check);yield return null;
        check(modeText.text=="原版" && !modeRow.Find("group/DialogueSave.ModePrevious").gameObject.activeSelf,
            "native arrow hides at Original endpoint");
        RuntimeUiQA.Click("DialogueSave.ModeNext",check);yield return new WaitForSecondsRealtime(.3f);
        ExecuteEvents.Execute(native.btn_cancel.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.pointerClickHandler);
        yield return new WaitForSecondsRealtime(.4f);
        check(!owner.IsAdv,"native cancel discards mode draft");
        UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
        yield return until(()=>UIMgr.GetView<SettingView>().isViewReady,15,"native settings reopens");
        yield return new WaitForSecondsRealtime(.4f);
        native=UIMgr.GetView<SettingView>() as SettingView;
        modeRow=native.line_txtSpeedUp.parent.Find("DialogueSave.ModeSetting");
        check(modeRow.GetComponentsInChildren<Text>(true).Single(t=>t.name=="txt_txtSpeedUp").text=="原版",
            "reopened row reflects persisted mode");
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/perf-original-settings.png"));
        RuntimeUiQA.Click("DialogueSave.ModeNext",check);yield return new WaitForSecondsRealtime(.3f);
        ExecuteEvents.Execute(native.btn_confirm.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.pointerClickHandler);
        yield return until(()=>owner.IsAdv && !UIMgr.IsViewOpened<SettingView>(),5,"native confirmation finishes its closing animation");
        check(!owner.ModalOpen,"native confirm applies ADV without a second chooser");
        yield return new WaitForSecondsRealtime(.4f);
        UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
        yield return until(()=>AdvSettings.Active!=null && AdvSettingsTransition.Active!=null,15,"performance settings transition starts");
        var frames=new List<double>();float last=Time.realtimeSinceStartup;
        bool moving=false;float firstDraw=last;
        while(AdvSettingsTransition.Active!=null){yield return null;float now=Time.realtimeSinceStartup;if(moving)frames.Add((now-last)*1000);else if(AdvSettingsTransition.Active?.Progress>0){moving=true;timing["openingPreparationMs"]=(now-firstDraw)*1000;}last=now;}
        timing["openingFramesMs"]=JArray.FromObject(frames);
        yield return until(()=>AdvSettings.Active.BuiltPages==4,10,"settings idle page warmup");
        for(int i=1;i<4;i++)
        {
            var button=AdvSettings.Active.GetComponentsInChildren<Button>().Single(b=>b.name=="Settings.Tab"+i);
            ExecuteEvents.Execute(button.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.pointerClickHandler);
            yield return until(()=>AdvSettingsTransition.Active!=null,5,"cached tab transition starts");
            yield return until(()=>AdvSettingsTransition.Active==null,5,"cached tab transition completes");
        }
        check(AdvSettings.Active.BuiltPages==4,"tab switches reuse all prebuilt pages");
        UIMgr.CloseView<SettingView>();yield return new WaitForSecondsRealtime(.4f);
        File.WriteAllText(Path.Combine(root,"results/performance.json"),timing.ToString());
        // Native state setup and EventSystem click; observe native Skip rather than replacing it.
        var take=adapter.CaptureAsync();yield return until(()=>take.IsCompleted,30,"boxing fixture checkpoint");var snapshot=take.GetAwaiter().GetResult();
        var probe=new Harmony("dialoguesave.qa.mouse-skip");probe.Patch(AccessTools.Method(typeof(FightMiniGameView),"Skip"),prefix:new HarmonyMethod(typeof(RuntimePerformanceQA),nameof(Skipped)));
        try
        {
            for(int i=0;i<2;i++)
            {
                UIMgr.OpenView<FightMiniGameView>(UILayerType.None,null,new object[]{default(MiniGameFromType),0,new List<double>{0},(Action)(()=>{}),(Action)(()=>{})});
                yield return until(()=>UIMgr.GetView<FightMiniGameView>()?.isViewReady==true,25,"native boxing ready");
                var fight=UIMgr.GetView<FightMiniGameView>() as FightMiniGameView;
                fight.btn_skip.gameObject.SetActive(false);int before=skips;
                fight.OnHotKeyInput(122);check(skips==before,"native hidden skip remains unavailable");
                fight.btn_skip.gameObject.SetActive(true);
                ExecuteEvents.Execute(fight.btn_skip.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
                check(skips==before+1,"mouse click enters native boxing skip exactly once on open "+i);
                check(AccessTools.Field(typeof(FightMiniGameView),"curState").GetValue(fight).ToString()=="Win","mouse skip reaches native victory state");
                UIMgr.CloseView<FightMiniGameView>();yield return new WaitForSecondsRealtime(.4f);
            }
        }
        finally{probe.UnpatchSelf();}
        var restore=adapter.RestoreAsync(snapshot);yield return until(()=>restore.IsCompleted,40,"restore after boxing");restore.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root,"results/performance.json"),timing.ToString());
    }
}
