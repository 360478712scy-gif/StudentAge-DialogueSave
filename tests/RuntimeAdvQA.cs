using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using DG.Tweening;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using View.Main;
using View.Evt;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

public static class RuntimeAdvQA
{
    static bool isolated;
    public static void IsolateInput(bool enabled)
    {
        if(enabled==isolated)return;isolated=enabled;
        if(enabled)InputSystem.onEvent+=FilterInput;else InputSystem.onEvent-=FilterInput;
    }
    static void FilterInput(InputEventPtr e,InputDevice device)
    {
        // Isolated game only; OS input remains untouched. Restore physical input
        // before leaving the completed preview for manual inspection.
        if((device is Mouse || device is Keyboard) && !device.name.StartsWith("ADVQA"))e.handled=true;
    }
    public static IEnumerator Preview(DialogueCheckpointAdapter adapter,IDialogueUiService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;
        check(adv.IsAdv,"preview starts in ADV without ever selecting original dialogue mode");
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        if(notice!=null && notice.GetType().Name=="CommonComfirmView")UIMgr.CloseView(notice);
        yield return until(()=>adv.History.Entries.Count>0,35,"preview first complete dialogue checkpoint");
        var probe=Resources.FindObjectsOfTypeAll<AdvFirstFrameQA>().Single(p=>p.gameObject.activeInHierarchy);
        check(probe.Frames>0 && probe.UnshieldedFrames==0,"all observed render frames suppress original dialogue graphics ("+probe.Frames+" checked)");
        check(adv.LastAttachedBeforeReady,"ADV attaches before the first dialogue becomes ready");
        yield return Shot(root,"adv-preview-first-dialogue");
        var fold=Resources.FindObjectsOfTypeAll<AdvSkinButton>().Single(i=>i.gameObject.activeInHierarchy && i.name=="ADV.ToolbarToggle");
        Vector3 foldCenter=fold.IconRect.TransformPoint(fold.IconRect.rect.center);
        RuntimeUiQA.Click("ADV.ToolbarToggle",check);yield return null;
        check(adv.IsFolded && Vector3.Distance(foldCenter,fold.IconRect.TransformPoint(fold.IconRect.rect.center))<.01f,
            "fold arrow remains at the same screen center when collapsed");
        yield return Shot(root,"adv-preview-folded");
        RuntimeUiQA.Click("ADV.ToolbarToggle",check);yield return null;
        check(!adv.IsFolded && Vector3.Distance(foldCenter,fold.IconRect.TransformPoint(fold.IconRect.rect.center))<.01f,
            "horizontal unfold arrow returns without position drift");
        RuntimeUiQA.Click("ADV.跳过剧情",check);yield return null;
        yield return Shot(root,"adv-preview-skip-confirmation");
        RuntimeUiQA.Click("ADV.SkipConfirm",check);
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk.talkState==TalkState.Option && adv.History.Entries.Count>=2,30,"direct skip arrives at the next choice");
        yield return ReadingInput(talk,adv,check);
        yield return Shot(root,"adv-preview-history");
        var reader=Resources.FindObjectsOfTypeAll<AdvBacklog>().Single(p=>p.gameObject.activeInHierarchy);
        var jump=reader.GetComponentsInChildren<UnityEngine.UI.Button>().First(b=>b.name=="ADV.Rollback.0");
        check(jump.GetComponentInChildren<TMPro.TextMeshProUGUI>()==null && jump.transform.Find("Source icon")!=null,
            "rollback is an arrow button with no text");
        check(reader.GetComponentsInChildren<UnityEngine.UI.Image>().Where(i=>i.name=="Divider").All(i=>i.rectTransform.sizeDelta.y==2 && i.color.a>=.5f),
            "dialogue row dividers have visible weight and contrast");
        check(reader.transform.Find("History layout/History status")==null,"history footer contains no status or diagnostic text");
        check(adv.History.Entries.Any(e=>e.IsOption) && adv.History.Entries.Any(e=>!e.IsOption && e.NativeCount==adv.History.Entries.Last().NativeCount),
            "choice checkpoint is separate from its preceding spoken line");
        int choiceIndex=adv.History.Entries.FindLastIndex(e=>e.IsOption);
        RuntimeUiQA.Click("ADV.Rollback."+choiceIndex,check);yield return null;
        check(adv.ModalOpen && !adv.History.Restoring,"jump waits for confirmation before changing the world");
        yield return Shot(root,"adv-preview-jump-confirmation");
        RuntimeUiQA.Click("ADV.JumpCancel",check);yield return null;
        check(adv.ModalOpen && UIMgr.GetView<NewTalkView>(false)==talk,"cancel returns to the same log and dialogue");
        RuntimeUiQA.Click("ADV.Rollback."+choiceIndex,check);yield return null;
        RuntimeUiQA.Click("ADV.JumpConfirm",check);
        yield return until(()=>!adv.ModalOpen && !adapter.IsRestoring,60,"choice-row jump restores actual options");
        talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        check(talk.talkState==TalkState.Option && talk.itemgroup_options.GetCells().Count==2,"restored choice keeps both original branches");
        adv.OpenHistory();yield return null;
        var cfg=(Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk);
        string originalContent=cfg.content;
        try
        {
            cfg.content=originalContent+"配置变更验证";
            var updated=adv.History.Restore(0);yield return until(()=>updated.IsCompleted,40,"history remains loadable after mod text update");
            updated.GetAwaiter().GetResult();
            check(!adapter.IsRestoring,"changed configuration does not invalidate existing history");
        }
        finally{cfg.content=originalContent;}
        if(!adv.ModalOpen)adv.OpenHistory();
        yield return null;
        RuntimeUiQA.Click("ADV.Rollback.0",check);
        yield return null;
        RuntimeUiQA.Click("ADV.JumpRemember",check);
        var jumpClock=System.Diagnostics.Stopwatch.StartNew();
        RuntimeUiQA.Click("ADV.JumpConfirm",check);
        yield return until(()=>!adv.ModalOpen && !adv.History.Restoring && !adapter.IsRestoring,60,"icon button restores the actual dialogue checkpoint");
        jumpClock.Stop();
        File.WriteAllText(Path.Combine(root,"results/history-jump-timing.json"),new JObject{["elapsedMs"]=jumpClock.Elapsed.TotalMilliseconds}.ToString());
        yield return new WaitForSecondsRealtime(.5f);
        File.WriteAllText(Path.Combine(root,"results/first-frame-check.json"),new JObject{["observedFrames"]=probe.Frames,["unshieldedOriginalFrames"]=probe.UnshieldedFrames,["invalidFontFrames"]=probe.InvalidFontFrames,["restoreStatusFrames"]=probe.RestoreStatusFrames,["observations"]=new JArray(probe.UnshieldedObservations)}.ToString());
        check(probe.UnshieldedFrames==0,"no original dialogue frame during restore either");
        check(probe.InvalidFontFrames==0,"visible ADV text keeps valid shader and atlas throughout restore");
        check(probe.RestoreStatusFrames==0,"restore keeps input guarded without an onscreen status label");
        probe.enabled=false;
        check(File.ReadAllText(Path.Combine(root,"BepInEx/config/local.studentage.dialoguesave.cfg")).Contains("ConfirmHistoryJump = false"),"jump confirmation preference is persisted separately from skip");
        adv.OpenHistory();yield return null;
        RuntimeUiQA.Click("ADV.Rollback.0",check);
        yield return until(()=>!adv.ModalOpen && !adapter.IsRestoring,60,"remembered jump skips confirmation on the next request");
        yield return HistoryRetention(adapter,adv,check,until,root);
        yield return NativeHistoryCases(adapter,adv,service,check,until,root);
        yield return DeleteDuringScan(adapter,service,check,until,root);
        yield return Shot(root,"adv-preview-ready");
        adv.OpenPicker(false);yield return null;
        var previews=Resources.FindObjectsOfTypeAll<AdvModePreview>().Where(p=>p.gameObject.activeInHierarchy).ToArray();
        check(previews.Length==2 && previews.All(p=>p.GetComponent<UnityEngine.UI.RawImage>().texture.width==960),"chooser displays both bundled interface screenshots");
        var chooser=GameObject.Find("Campus mode chooser");
        check(chooser!=null && chooser.GetComponentInParent<Canvas>().transform.Find("Redrawn campus backdrop")!=null,
            "chooser reuses campus skin backdrop");
        check(chooser!=null && chooser.GetComponentsInChildren<AdvPaper>().Length==0,
            "chooser cards no longer use the old beige paper skin");
        var recommended=GameObject.Find("ADV.Recommended");
        check(recommended!=null && recommended.transform.parent.name=="ADV.ChooseAdv" &&
            recommended.GetComponentInChildren<TMPro.TextMeshProUGUI>().text=="推荐",
            "recommendation badge belongs only to the ADV card");
        yield return Shot(root,"adv-preview-chooser");
        RuntimeUiQA.Click("ADV.ChooseAdv",check);yield return null;
        check(adv.IsAdv && !adv.ModalOpen,"preview card remains clickable and applies ADV mode");
        var jumpPreference=(BepInEx.Configuration.ConfigEntry<bool>)AccessTools.Field(typeof(AdvDialogueController),"rollbackConfirmation").GetValue(adv);
        jumpPreference.Value=true;jumpPreference.ConfigFile.Save();
        adv.OpenHistory();yield return null;
    }
    static IEnumerator HistoryRetention(DialogueCheckpointAdapter adapter,AdvDialogueController adv,Action<bool,string> check,
        Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>adapter.CanCapture(out _) && !adapter.IsPausedForMenu,20,"long-history fixture is stable");
        var first=adv.History.Entries[0].Checkpoint;
        int initial=adv.History.Entries.Count;
        var native=(List<TalkData>)AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk);
        int nativeStart=native.Count;
        talk.tmpTalks=Enumerable.Range(0,106).Select(i=>"连续对白记录 "+i+"：逐字显示期间也保留完整跳转位置。").ToList();
        var timer=System.Diagnostics.Stopwatch.StartNew();
        for(int i=0;i<105;i++)
        {
            talk.txtex_content.DOKill(false);talk.tmpTalkIdx=i;
            talk.DoText(talk.tmpTalks[i]);
            yield return null;
        }
        timer.Stop();
        check(native.Count==nativeStart+105 && adv.History.Entries.Count>=initial+105 && ReferenceEquals(first,adv.History.Entries[0].Checkpoint),
            "105 rapidly presented lines retain every checkpoint beyond the old 96-entry cap");
        check(adv.History.Entries.Skip(initial).All(e=>e.Checkpoint.State.Dialogue.Value<string>("phase")=="Anim"),
            "typing lines are captured before completion rather than waiting for polling");
        File.WriteAllText(Path.Combine(root,"results/history-retention.json"),new JObject{["entries"]=adv.History.Entries.Count,
            ["worldBytes"]=adv.History.Entries.Sum(e=>(long)e.Checkpoint.State.WorldBytes.Length),["elapsedMs"]=timer.Elapsed.TotalMilliseconds}.ToString());
        int middle=initial+50;
        var middleRestore=adv.History.Restore(middle);
        yield return until(()=>middleRestore.IsCompleted,60,"middle retained line restores");
        if(middleRestore.IsFaulted)throw middleRestore.Exception;
        talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        check(talk.tmpTalkIdx==50 && talk.tmpTalks[50].Contains("连续对白记录 50"),"middle jump restores its exact segment and typing state");
        var firstRestore=adv.History.Restore(0);
        yield return until(()=>firstRestore.IsCompleted,60,"oldest retained line restores after passing 96 records");
        if(firstRestore.IsFaulted)throw firstRestore.Exception;
    }
    static IEnumerator NativeHistoryCases(DialogueCheckpointAdapter adapter,AdvDialogueController adv,IDialogueUiService service,Action<bool,string> check,
        Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        string[] texts={"秋老虎仍在肆虐","你坐了我位置","位子还你","位置还你"};
        var targets=Config.Cfg.TalkCfgMap.Values.Where(c=>c.content!=null && texts.Any(t=>c.content.Contains(t))).ToArray();
        File.WriteAllText(Path.Combine(root,"results/native-history-targets.json"),JArray.FromObject(targets).ToString());
        check(targets.Length>=3,"real screenshot dialogue nodes located in current game configuration");
        var results=new JArray();
        foreach(var cfg in targets)
        {
            var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
            yield return until(()=>adapter.CanAdvancePresentation(talk),10,"previous restoration has released native presentation");
            talk.RefreshTalk(cfg.id);
            float deadline=Time.realtimeSinceStartup+15;
            while(Time.realtimeSinceStartup<deadline && !adv.History.Entries.Any(e=>e.TalkId==cfg.id))yield return null;
            bool captured=adv.History.Entries.Any(e=>e.TalkId==cfg.id);
            adapter.CanCaptureHistory(out var reason);
            results.Add(new JObject{["id"]=cfg.id,["text"]=cfg.content,["captured"]=captured,["reason"]=reason});
            File.WriteAllText(Path.Combine(root,"results/native-history-capture.json"),results.ToString());
            check(captured,"native screenshot dialogue receives checkpoint: "+cfg.id+" / "+reason);
        }
        adv.OpenHistory();yield return new WaitForSecondsRealtime(1);yield return Shot(root,"adv-real-dialogue-history");
        File.WriteAllText(Path.Combine(root,"results/portrait-alignment.json"),new JObject{
            ["samples"]=AdvPortraitCrop.Samples,["worstSampleMilliseconds"]=AdvPortraitCrop.WorstSampleMilliseconds,
            ["portraits"]=new JArray(Resources.FindObjectsOfTypeAll<AdvPortraitCrop>().Where(p=>p.gameObject.activeInHierarchy).Select(p=>{
                var rect=(RectTransform)p.transform;return new JObject{["parent"]=p.transform.parent.parent.name,["x"]=rect.anchoredPosition.x,["width"]=rect.sizeDelta.x,["ready"]=!p.enabled};
            }))}.ToString());
        adv.CloseModal();
        RuntimeUiQA.Click("ADV.保存",check);
        yield return until(()=>UIMgr.GetView<SaveView>(false)?.isViewReady==true && !service.IsListing,20,"open real-dialogue archive window");
        var existing=service.List(DialogueUiCategory.Manual).Select(r=>r.RevisionId).ToArray();
        int slot=Enumerable.Range(1,99).First(n=>service.List(DialogueUiCategory.Manual).All(r=>r.Slot!=n));
        UiResult published=null;
        service.Save(DialogueUiCategory.Manual,slot,null,r=>{if(!r.IsPending)published=r;});
        yield return until(()=>published!=null,60,"archive includes full rollback trail");
        check(published.Success,"real-dialogue archive published: "+published.Message);
        var archive=service.List(DialogueUiCategory.Manual).Single(r=>!existing.Contains(r.RevisionId));
        UIMgr.CloseView<SaveView>();adv.History.Clear();
        check(adv.History.Entries.Count==0,"no in-memory checkpoint survives the simulated restart");
        UiResult loaded=null;
        service.Load(archive.RevisionId,r=>{if(!r.IsPending)loaded=r;});
        yield return until(()=>loaded!=null,60,"load archive and recover earlier rollback trail");
        check(loaded.Success && targets.All(t=>adv.History.Entries.Any(e=>e.TalkId==t.id)),
            "disk archive restores all three earlier dialogue destinations after memory clear: "+loaded.Message);
        File.WriteAllText(Path.Combine(root,"results/history-disk-roundtrip.json"),new JObject{
            ["revision"]=archive.RevisionId,["restoredTalkIds"]=new JArray(adv.History.Entries.Select(e=>e.TalkId))}.ToString());
        foreach(var target in Enumerable.Reverse(targets))
        {
            int index=adv.History.Entries.FindLastIndex(e=>e.TalkId==target.id);
            var jump=adv.History.Restore(index);
            yield return until(()=>jump.IsCompleted,60,"actual screenshot dialogue restore "+target.id);
            if(jump.IsFaulted)throw jump.Exception;
            var live=(NewTalkView)UIMgr.GetView<NewTalkView>();
            check(((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(live)).id==target.id,
                "restored the real screenshot dialogue node "+target.id);
        }
        var restored=adv.History.Restore(0);yield return until(()=>restored.IsCompleted,60,"return from real-dialogue regression");
        if(restored.IsFaulted)throw restored.Exception;
    }
    static IEnumerator DeleteDuringScan(DialogueCheckpointAdapter adapter,IDialogueUiService service,Action<bool,string> check,
        Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _) && Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>()
            .Any(b=>b.name=="ADV.保存" && b.gameObject.activeInHierarchy),20,"deletion fixture source and toolbar stable");
        RuntimeUiQA.Click("ADV.保存",check);
        yield return until(()=>UIMgr.GetView<SaveView>(false)?.isViewReady==true && !service.IsListing,20,"open deletion fixture save window");
        var before=service.List(DialogueUiCategory.Manual).Select(r=>r.RevisionId).ToArray();
        int slot=Enumerable.Range(1,99).First(n=>service.List(DialogueUiCategory.Manual).All(r=>r.Slot!=n));
        UiResult saved=null;
        service.Save(DialogueUiCategory.Manual,slot,null,r=>{if(!r.IsPending)saved=r;});
        yield return until(()=>saved!=null,60,"deletion fixture committed");
        check(saved.Success,"deletion fixture is a real valid archive: "+saved.Message);
        var target=service.List(DialogueUiCategory.Manual).Single(r=>!before.Contains(r.RevisionId));
        UIMgr.CloseView<SaveView>();
        AccessTools.Field(service.GetType(),"lastScanTime").SetValue(service,-100f);
        service.List(DialogueUiCategory.Manual);
        check(service.IsListing,"deletion regression overlaps the page-triggered list refresh");
        UiResult deleted=null;int pending=0,completed=0;
        service.Delete(target.RevisionId,r=>{if(r.IsPending)pending++;else{completed++;deleted=r;}});
        yield return until(()=>deleted!=null,30,"single delete request completes after scan");
        var after=service.List(DialogueUiCategory.Manual).Select(r=>r.RevisionId).ToArray();
        check(deleted.Success && pending==1 && completed==1 && !after.Contains(target.RevisionId) && before.All(after.Contains),
            "one delete removes only the selected archive despite concurrent list scan");
        File.WriteAllText(Path.Combine(root,"results/delete-during-scan.json"),new JObject{
            ["revision"]=target.RevisionId,["pendingReceipts"]=pending,["completionReceipts"]=completed,
            ["success"]=deleted.Success,["remainingOriginals"]=before.All(after.Contains)}.ToString());
    }
    public static IEnumerator FirstRun(Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>AdvDialogueController.Active?.ModalOpen==true,30,"first launch presents UI choice");
        check(GameObject.Find("ADV.Recommended")?.transform.parent.name=="ADV.ChooseAdv","first-run campus chooser recommends ADV");
        check(Resources.FindObjectsOfTypeAll<AdvModePreview>().Count(p=>p.gameObject.activeInHierarchy)==2,"first-run chooser keeps both real previews");
        check(GameObject.Find("ADV.ClosePicker")==null,"first-run choice cannot be cancelled without a preference");
        yield return Shot(root,"adv-00-first-choice");
        RuntimeUiQA.Click("ADV.ChooseOriginal",check);
        yield return until(()=>!AdvDialogueController.Active.ModalOpen,5,"first-run chooser finishes closing shutter");
        check(AdvDialogueController.Active.Mode=="Original","original selection applies and closes chooser");
        check(File.ReadAllText(Path.Combine(root,"BepInEx/config/local.studentage.dialoguesave.cfg")).Contains("DialogueStyle = Original"),"choice persists to QA plugin config");
        AdvDialogueController.Active.OpenPicker(false);yield return null;
        check(GameObject.Find("ADV.ClosePicker")!=null,"later chooser includes cancel ticket");
        RuntimeUiQA.Click("ADV.ChooseAdv",check);
        yield return until(()=>!AdvDialogueController.Active.ModalOpen,5,"ADV chooser closes");
        check(AdvDialogueController.Active.IsAdv && File.ReadAllText(Path.Combine(root,"BepInEx/config/local.studentage.dialoguesave.cfg")).Contains("DialogueStyle = ADV"),"ADV selection persists independently");
    }
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController saves,IDialogueUiService service,
        Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        if(notice!=null && notice.GetType().Name=="CommonComfirmView")UIMgr.CloseView(notice);
        yield return until(()=>adapter.CanCapture(out _),30,"native comparison dialogue ready");
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();var originalParent=talk.txtex_content.transform.parent;
        check(((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000001,"fixture starts on first dialogue node");
        var originalPosition=talk.txtex_content.rectTransform.anchoredPosition;var originalSize=talk.txtex_content.rectTransform.sizeDelta;
        string latest=SaveMgr.GetPref("LatestSaveKey","");
        var performance=new JObject();var baseline=new List<float>();
        for(int i=0;i<120;i++){yield return null;baseline.Add(Time.unscaledDeltaTime*1000);}
        performance["native"]=Stats(baseline);
        var adv=AdvDialogueController.Active;
        adv.OpenPicker(false);yield return null;
        RuntimeUiQA.Click("ADV.ChooseAdv",check);
        yield return until(()=>adv.IsAdv && adv.History.Entries.Count>0,45,"ADV creates first complete history checkpoint");
        yield return new WaitForSecondsRealtime(1);
        check(talk.txtex_content.transform.parent!=originalParent,"ADV uses the actual native typing text once");
        check(talk.txtex_content.font.faceInfo.familyName=="Source Han Sans CN","ADV uses native Source Han Sans Chinese font");
        check(talk.txtex_content.color.r>.9f && talk.txtex_content.fontSharedMaterial.GetFloat("_OutlineWidth")>0,
            "normal ADV dialogue uses outlined light text on the extracted dark gradient");
        string[] saveNames={"保存","读取","快存","快读"};string[] saveWords={"SAVE","LOAD","Q.SAVE","Q.LOAD"};
        for(int i=0;i<4;i++)
        {
            var button=Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>().Single(b=>b.name=="ADV."+saveNames[i] && b.gameObject.activeInHierarchy);
            check(button.GetComponent<AdvSkinButton>()!=null && button.GetComponentInChildren<UnityEngine.UI.RawImage>().texture!=null,"save toolbar uses extracted English wordmark artwork");
        }
        RuntimeUiQA.Click("ADV.跳过剧情",check);yield return null;
        check(adv.ModalOpen,"story skip presents confirmation");
        var overlay=Resources.FindObjectsOfTypeAll<Canvas>().Single(c=>c.name=="DialogueSave.ADV");
        check(overlay.gameObject.activeInHierarchy,"skip confirmation keeps the dialogue and toolbar visible underneath");
        var modal=Resources.FindObjectsOfTypeAll<Canvas>().Single(c=>c.name=="DialogueSave.ADV.Modal");
        var frame=modal.transform.Find("Skip confirmation") as RectTransform;
        check(frame.sizeDelta.x==600 && frame.Find("Detail")==null,"skip confirmation is narrower and omits explanatory small print");
        yield return Shot(root,"adv-01-skip-confirmation");
        RuntimeUiQA.Click("ADV.SkipCancel",check);yield return null;
        check(!adv.ModalOpen && ((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000001,"canceling skip preserves current dialogue");
        yield return Shot(root,"adv-01-dialogue");
        var frames=new List<float>();var controller=new List<float>();
        for(int i=0;i<120;i++){yield return null;frames.Add(Time.unscaledDeltaTime*1000);controller.Add((float)adv.TickMilliseconds);}
        performance["adv"]=Stats(frames);performance["advController"]=Stats(controller);
        performance["firstHistoryCaptureMs"]=adv.History.LastCaptureMilliseconds;
        File.WriteAllText(Path.Combine(root,"results/adv-performance.json"),performance.ToString());
        RuntimeUiQA.Click("ADV.ToolbarToggle",check);check(adv.IsFolded,"toolbar collapses");
        yield return Shot(root,"adv-02-folded");
        RuntimeUiQA.Click("ADV.ToolbarToggle",check);check(!adv.IsFolded,"toolbar expands");
        int segment=talk.tmpTalkIdx;
        RuntimeUiQA.Click("ADV.隐藏",check);check(adv.IsHidden,"hide enters background appreciation");
        yield return Shot(root,"adv-03-hidden");
        AccessTools.Method(typeof(NewTalkView),"OnClickNext").Invoke(talk,null);
        check(!adv.IsHidden && talk.tmpTalkIdx==segment,"first advance while hidden restores without advancing");
        yield return null;
        // Repeated mode switches restore the same native component and geometry.
        adv.SelectMode("Original");yield return new WaitForSecondsRealtime(.3f);
        check(talk.txtex_content.transform.parent==originalParent && talk.txtex_content.rectTransform.anchoredPosition==originalPosition &&
            talk.txtex_content.rectTransform.sizeDelta==originalSize,"native mode restores original parent position and dimensions");
        adv.SelectMode("ADV");yield return new WaitForSecondsRealtime(.4f);
        AccessTools.Method(typeof(NewTalkView),"OnClickNext").Invoke(talk,null);
        yield return new WaitForSecondsRealtime(.16f);
        check(talk.talkState==TalkState.Anim && talk.txtex_content.text.Length>0 && talk.txtex_content.text.Length<talk.tmpTalks[talk.tmpTalkIdx].Length,
            "next sentence starts with progressive reveal rather than instant text");
        AccessTools.Method(typeof(NewTalkView),"OnClickNext").Invoke(talk,null);
        check(((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000002,
            "click during reveal completes the same sentence without advancing its node");
        yield return until(()=>talk.talkState==TalkState.Option && adv.History.Entries.Count>=2,35,"native options and rollback checkpoint ready");
        yield return Shot(root,"adv-04-choices");
        var shields=talk.group_talk.GetComponentsInParent<CanvasGroup>(true);
        check(shields.Any(g=>g.name=="ADV.NativeShield" && g.alpha==0 && !g.blocksRaycasts),"native dialogue is masked by independent shield");
        var choice=Resources.FindObjectsOfTypeAll<AdvChoiceGraphic>().First(g=>g.gameObject.activeInHierarchy);
        check(choice.rectTransform.anchoredPosition.y<-300 && choice.rectTransform.anchoredPosition.y>-600,"choices sit centered above the bottom controls");
        check(choice.raycastTarget,"source choice retains full button hit area");
        check(choice.mainTexture!=null,"choice uses cached source capsule artwork");
        yield return ReadingInput(talk,adv,check);
        if(!adv.ModalOpen)RuntimeUiQA.Click("ADV.回看",check);
        check(adv.ModalOpen && adapter.IsPausedForMenu,"history pauses the actual dialogue");
        var reader=Resources.FindObjectsOfTypeAll<AdvBacklog>().Single(r=>r.gameObject.activeInHierarchy);
        var footer=reader.GetComponentsInChildren<UnityEngine.UI.Button>().Where(b=>-((RectTransform)b.transform).anchoredPosition.y>=979).ToArray();
        check(footer.Length==1 && footer[0].name=="ADV.CloseHistory" && footer[0].GetComponentInChildren<TMPro.TextMeshProUGUI>().text=="返回对话",
            "fullscreen history has only Return to dialogue outside per-line jump buttons");
        var background=reader.transform.Find("History fullscreen") as RectTransform;
        check(background.anchorMin==Vector2.zero && background.anchorMax==Vector2.one,"history paper fills the entire screen");
        foreach(var button in reader.GetComponentsInChildren<UnityEngine.UI.Button>().Where(b=>b.name.StartsWith("ADV.Rollback.")))
        {
            var line=button.transform.parent.Find("Line") as RectTransform;
            check(-((RectTransform)button.transform).anchoredPosition.y>=-line.anchoredPosition.y+line.sizeDelta.y,
                "jump button sits below its own complete dialogue text");
        }
        yield return Shot(root,"adv-05-history");
        RuntimeUiQA.Click("ADV.Rollback.0",check);
        yield return null;RuntimeUiQA.Click("ADV.JumpConfirm",check);
        yield return until(()=>!adv.ModalOpen && !adv.History.Restoring && !adapter.IsRestoring,60,"history click restores real checkpoint");
        talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        check(adv.LastAttachedBeforeReady,"restored dialogue is taken over before native view readiness, without waiting for async art");
        check(((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000001,
            "rollback returns to actual first dialogue node");
        check(SaveMgr.GetPref("LatestSaveKey","")==latest,"history rollback never changes ordinary LatestSaveKey");
        yield return Shot(root,"adv-06-restored");
        // Continue again, choose through the newly drawn button, and require the
        // original option callback to progress the real game exactly once.
        AccessTools.Method(typeof(NewTalkView),"OnClickNext").Invoke(talk,null);
        yield return until(()=>talk.talkState==TalkState.Option,25,"choices available after rollback");
        yield return new WaitForSecondsRealtime(.4f);
        RuntimeUiQA.Click("ADV.Choice.0",check);
        yield return until(()=>((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000003,20,
            "ADV choice invokes native option continuation");
        RuntimeUiQA.Click("ADV.设置",check);
        yield return until(()=>UIMgr.GetView<SettingView>(false)?.isViewReady==true,25,"ADV opens native settings");
        yield return new WaitForSecondsRealtime(.4f);
        RuntimeUiQA.Click("DialogueSave.UiPreference",check);
        yield return null;
        RuntimeUiQA.Click("ADV.ChooseOriginal",check);check(!adv.IsAdv,"native settings switches back to original");
        adv.SelectMode("ADV");UIMgr.CloseView<SettingView>();
        yield return new WaitForSecondsRealtime(.6f);
        performance["lastHistoryCaptureMs"]=adv.History.LastCaptureMilliseconds;
        performance["steadyFrameComparisonWithinBudget"]=Percentile(frames,.95)<=Percentile(baseline,.95)+2;
        performance["historyCaptureWithin16ms"]=adv.History.LastCaptureMilliseconds<=16;
        File.WriteAllText(Path.Combine(root,"results/adv-performance.json"),performance.ToString());
        yield return Shot(root,"adv-07-final");
        // Real native CG view and actual native typing text, not a recoloured mockup.
        int cgId=Config.Cfg.CGCfgMap.Keys.First(id=>id>0);
        bool cgOpened=false;
        AccessTools.Method(typeof(NewTalkView),"ShowCG").Invoke(talk,new object[]{cgId,(Action)(()=>{
            cgOpened=true;talk.DoText("这是 CG 模式的第一行对白。\n第二行仍然保留清晰、舒适的阅读空间。\n第三行不会与底部操作图标挤在一起。");})});
        yield return until(()=>cgOpened && adv.IsCgPresentation && talk.talkState==TalkState.AnimEnd,35,"native CG opens with compact dark ADV surface");
        yield return new WaitForSecondsRealtime(.5f);
        var cgText=(TMPro.TextMeshProUGUI)AccessTools.Method(typeof(NewTalkView),"GetTalkTxt").Invoke(talk,null);
        check(cgText.transform.parent.name=="Reading" && cgText.color.r>.9f,"CG uses its actual native text in light ink");
        check(cgText.fontSize==38 && cgText.alignment==TMPro.TextAlignmentOptions.Top,"CG larger text is horizontally centered");
        File.WriteAllText(Path.Combine(root,"results/adv-font.json"),new JObject{["font"]=cgText.font.name,["family"]=cgText.font.faceInfo.familyName,
            ["cgSize"]=cgText.fontSize,["outline"]=cgText.fontSharedMaterial.GetFloat("_OutlineWidth")}.ToString());
        CheckThreeLines(cgText,check);
        check(Resources.FindObjectsOfTypeAll<AdvVeil>().Single(v=>v.gameObject.activeInHierarchy).rectTransform.rect.height<385,
            "CG gradient remains lower than normal dialogue while larger text retains three lines");
        yield return Shot(root,"adv-08-cg");
        yield return Names(talk,adapter,check,root,"adv-08-cg");
        AccessTools.Method(typeof(NewTalkView),"HideCGComic").Invoke(talk,null);
        talk.DoText(talk.tmpTalks[talk.tmpTalkIdx]);
        yield return until(()=>!adv.IsCgPresentation && talk.talkState==TalkState.AnimEnd,20,"leaving CG restores normal cream presentation");
        yield return new WaitForSecondsRealtime(.6f);
        yield return Shot(root,"adv-09-returned");
        talk.DoText("普通对白保持当前的对话框高度。\n米色的渐隐底增加通透感，仍能清楚阅读。\n这里是第三行，文字与操作图标之间需要留有间距。");
        yield return until(()=>talk.talkState==TalkState.AnimEnd,15,"three-line normal dialogue ready");
        CheckThreeLines(talk.txtex_content,check);
        check(Mathf.Abs(Resources.FindObjectsOfTypeAll<AdvPaperPlane>().Single(p=>p.gameObject.activeInHierarchy).rectTransform.anchoredPosition.x-1644)<1,
            "hollow paper plane occupies the fixed right-hand indicator position");
        yield return Shot(root,"adv-10-three-lines");
        yield return Names(talk,adapter,check,root,"adv-11-normal");
        var plane=Resources.FindObjectsOfTypeAll<AdvPaperPlane>().Single(p=>p.gameObject.activeInHierarchy);
        var firstPlane=plane.rectTransform.anchoredPosition;
        var firstPhase=plane.AnimationTime;
        yield return new WaitForSecondsRealtime(.24f);
        check(plane.rectTransform.anchoredPosition==firstPlane && plane.AnimationTime!=firstPhase,"paper plane animates strokes without moving its anchor");
        yield return LongHistory(talk,adv,check,root);
        var skipSetting=(BepInEx.Configuration.ConfigEntry<bool>)AccessTools.Field(typeof(AdvDialogueController),"skipConfirmation").GetValue(adv);
        bool previous=skipSetting.Value;
        try
        {
            var restore=adv.History.Restore(0);
            yield return until(()=>restore.IsCompleted,60,"prepare real story-skip checkpoint");
            restore.GetAwaiter().GetResult();yield return new WaitForSecondsRealtime(.4f);
            talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
            RuntimeUiQA.Click("ADV.跳过剧情",check);yield return null;
            RuntimeUiQA.Click("ADV.SkipRemember",check);
            RuntimeUiQA.Click("ADV.SkipConfirm",check);
            yield return until(()=>talk.talkState==TalkState.Option,15,"direct skip reaches the next actual choice");
            check(((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000002 && Time.timeScale==1,
                "story skip jumps directly to choice without enabling fast-forward");
            check(!skipSetting.Value && File.ReadAllText(Path.Combine(root,"BepInEx/config/local.studentage.dialoguesave.cfg")).Contains("ConfirmStorySkip = false"),
                "do-not-show-again preference is persisted");
            RuntimeUiQA.Click("ADV.跳过剧情",check);yield return null;
            check(!adv.ModalOpen && talk.talkState==TalkState.Option,"remembered skip omits confirmation without choosing for the player");
            yield return Shot(root,"adv-13-skip-at-choice");
        }
        finally{skipSetting.Value=previous;skipSetting.ConfigFile.Save();}
    }
    static IEnumerator ReadingInput(NewTalkView talk,AdvDialogueController adv,Action<bool,string> check)
    {
        var keyboard=InputSystem.AddDevice<Keyboard>("ADVQAKeyboard");
        var mouse=InputSystem.AddDevice<Mouse>("ADVQAMouse");
        int node=((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id;
        try
        {
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Space));yield return null;yield return null;
            check(adv.IsHidden,"Space hides the dialogue UI");
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());yield return null;
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Space));yield return null;yield return null;
            check(!adv.IsHidden && ((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==node,
                "second Space restores UI without selecting a choice or advancing (hidden="+adv.IsHidden+", node="+((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id+")");
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());yield return null;
            InputSystem.QueueDeltaStateEvent(mouse.scroll,new Vector2(0,120));yield return null;yield return null;
            check(adv.ModalOpen,"scrolling upward opens the dialogue log");
            InputSystem.QueueDeltaStateEvent(mouse.scroll,new Vector2(0,-120));yield return null;
            check(adv.ModalOpen,"scrolling within the log does not close it or advance dialogue");
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Escape));yield return null;yield return null;
            check(!adv.ModalOpen,"Escape closes the fullscreen dialogue log");
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());yield return null;yield return null;
            check(UIMgr.GetTopView(ViewType.Guide,ViewType.Side)==talk && !global::Game.IsMenuOpened(),"Escape release does not open the native pause menu after closing the log");
            adv.OpenHistory();yield return null;
        }
        finally{InputSystem.RemoveDevice(keyboard);InputSystem.RemoveDevice(mouse);}
    }
    static IEnumerator LongHistory(NewTalkView talk,AdvDialogueController adv,Action<bool,string> check,string root)
    {
        var data=(List<TalkData>)AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk);int originalCount=data.Count;
        var person=Config.Cfg.PersonCfgMap.Values.First(p=>p.id>0 && p.gender==2);
        try
        {
            for(int i=0;i<120;i++)data.Add(new TalkData{roleName=i%2==0?"夏日里的长名字":"林川",talkRoleIds=new List<int>{person.id},
                content=i==119?"这是日志中的第一行对白。\n第二行保留足够的阅读空间。\n第三行仍然完整显示，跳转按钮应当位于文字下方。":"走廊里的阳光渐渐暖了起来，放学之后的故事还在继续。"});
            var sw=System.Diagnostics.Stopwatch.StartNew();adv.OpenHistory();sw.Stop();
            check(sw.Elapsed.TotalMilliseconds<150,"120-line history opens without instantiating every row ("+sw.Elapsed.TotalMilliseconds+" ms)");
            yield return new WaitForSecondsRealtime(1);
            var reader=Resources.FindObjectsOfTypeAll<AdvBacklog>().Single(r=>r.gameObject.activeInHierarchy);
            check(reader.GetComponentsInChildren<TMPro.TextMeshProUGUI>().Count(t=>t.name=="Line")<12,"long history instantiates only nearby rows");
            check(reader.GetComponentsInChildren<UnityEngine.UI.Image>().Any(i=>i.name=="Portrait" && i.sprite!=null),"historical native character portraits load at the left");
            yield return Shot(root,"adv-12-fullscreen-history");
            RuntimeUiQA.Click("ADV.HistoryTop",check);
            yield return null;yield return null;
            check(reader.transform.GetComponentsInChildren<RectTransform>().Any(r=>r.name=="History row 0"),"scrollbar reaches earliest history without page buttons");
            RuntimeUiQA.Click("ADV.HistoryBottom",check);yield return null;yield return null;
            int logCount=data.Count+adv.History.Entries.Count(e=>e.IsOption);
            check(reader.transform.GetComponentsInChildren<RectTransform>().Any(r=>r.name=="History row "+(logCount-1)),"bottom arrow reaches newest history");
            check(reader.transform.Find("History layout/Hint")==null,"history has no unsolicited header slogan");
            check(reader.transform.Find("History fullscreen").GetComponent<UnityEngine.UI.Image>().color.a<1,"history paper lets the dialogue scene show through");
            RuntimeUiQA.Click("ADV.CloseHistory",check);check(!adv.ModalOpen,"bottom Return to dialogue closes history");
        }
        finally{adv.CloseModal();if(data.Count>originalCount)data.RemoveRange(originalCount,data.Count-originalCount);}
    }
    static IEnumerator Names(NewTalkView talk,DialogueCheckpointAdapter adapter,Action<bool,string> check,string root,string prefix)
    {
        var cfg=(Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk);
        var originalIds=cfg.roleIds;var originalName=cfg.roleName;
        var refresh=AccessTools.Method(typeof(NewTalkView),"RefreshTalkingRole",new[]{typeof(List<int>),typeof(string)});
        using(adapter.PauseForMenu())
        try
        {
            var ids=new[]{Config.Cfg.PersonCfgMap.Values.First(p=>p.gender==1).id,Config.Cfg.PersonCfgMap.Values.First(p=>p.gender==2).id};
            string[] names={"林川","夏日里的长名字"};float firstWidth=0;
            var veil=Resources.FindObjectsOfTypeAll<AdvVeil>().Single(v=>v.gameObject.activeInHierarchy);Color veilColor=veil.color;
            for(int i=0;i<2;i++)
            {
                cfg.roleIds=new List<int>{ids[i]};cfg.roleName=names[i];refresh.Invoke(talk,new object[]{cfg.roleIds,cfg.roleName});
                yield return null;yield return new WaitForEndOfFrame();
                var paper=Resources.FindObjectsOfTypeAll<TMPro.TextMeshProUGUI>().Single(p=>p.name=="Speaker" && p.gameObject.activeInHierarchy);
                if(i==0)firstWidth=paper.rectTransform.rect.width;
                else check(paper.rectTransform.rect.width>firstWidth,"speaker text area grows with name length");
                check(veil.color==veilColor,"speaker gender does not change the dialogue background");
                if(AdvDialogueController.Active.IsCgPresentation)
                    check(Mathf.Abs(paper.rectTransform.anchoredPosition.x+paper.rectTransform.rect.width/2-960)<1,"CG speaker nameplate is centered");
                yield return Shot(root,prefix+(i==0?"-male":"-female"));
            }
            cfg.roleIds=new List<int>{-1};cfg.roleName=null;refresh.Invoke(talk,new object[]{cfg.roleIds,cfg.roleName});
            yield return null;
            check(!Resources.FindObjectsOfTypeAll<TMPro.TextMeshProUGUI>().Any(p=>p.name=="Speaker" && p.gameObject.activeInHierarchy),"narration hides the speaker label");
            yield return Shot(root,prefix+"-narration");
        }
        finally{cfg.roleIds=originalIds;cfg.roleName=originalName;refresh.Invoke(talk,new object[]{originalIds,originalName});}
    }
    static void CheckThreeLines(TMPro.TextMeshProUGUI text,Action<bool,string> check)
    {
        text.ForceMeshUpdate();check(text.textInfo.lineCount==3,"three lines are rendered by actual native TMP");
        float bottom=RectTransformUtility.WorldToScreenPoint(null,text.transform.TransformPoint(new Vector3(0,text.textInfo.lineInfo[2].descender,0))).y;
        var button=Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>().Single(b=>b.name=="ADV.回看" && b.gameObject.activeInHierarchy);
        var corners=new Vector3[4];((RectTransform)button.transform).GetWorldCorners(corners);
        float top=RectTransformUtility.WorldToScreenPoint(null,corners[1]).y;
        check(bottom>=top+4,"third text line clears icon hit area by at least four screen pixels ("+bottom+" / "+top+")");
    }
    static JObject Stats(List<float> x)=>new JObject{["medianMs"]=Percentile(x,.5),["p95Ms"]=Percentile(x,.95),["maxMs"]=x.Max()};
    static float Percentile(List<float> x,double p){var a=x.OrderBy(v=>v).ToArray();return a[Math.Min(a.Length-1,(int)(a.Length*p))];}
    static IEnumerator Shot(string root,string name)
    {yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results",name+".png"));yield return new WaitForSecondsRealtime(.25f);}
}

public sealed class AdvFirstFrameQA : MonoBehaviour
{
    public int Frames,UnshieldedFrames,InvalidFontFrames,RestoreStatusFrames;
    public readonly List<string> UnshieldedObservations=new List<string>();
    void OnEnable(){Canvas.willRenderCanvases+=Observe;}
    void OnDisable(){Canvas.willRenderCanvases-=Observe;}
    void Observe()
    {
        if(AdvDialogueController.Active?.IsAdv!=true)return;
        var blocker=GameObject.Find("DialogueSaveRestoreBlocker");
        if(blocker!=null && blocker.GetComponentsInChildren<UnityEngine.UI.Text>().Any(t=>!string.IsNullOrEmpty(t.text)))RestoreStatusFrames++;
        foreach(var text in Resources.FindObjectsOfTypeAll<TMPro.TextMeshProUGUI>())
        {
            if(!text.gameObject.activeInHierarchy || string.IsNullOrEmpty(text.text))continue;
            var canvas=text.GetComponentInParent<Canvas>();
            if(canvas==null || !canvas.name.StartsWith("DialogueSave.ADV"))continue;
            var material=text.fontSharedMaterial;
            if(text.font==null || material==null || material.shader==null || material.shader.name=="Hidden/InternalErrorShader" || material.mainTexture==null)
            {InvalidFontFrames++;UnshieldedObservations.Add("invalid font: "+text.name+" frame="+Time.frameCount);}
        }
        var view=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        if(view?.gameObject==null || view.viewState!=ViewState.Opened || !view.gameObject.activeInHierarchy)return;
        Frames++;
        if(view.group_talk!=null && view.group_talk.gameObject.activeInHierarchy &&
            !view.group_talk.GetComponentsInParent<CanvasGroup>(true).Any(g=>g.name=="ADV.NativeShield" && g.alpha==0))
        {
            UnshieldedFrames++;UnshieldedObservations.Add("frame="+Time.frameCount+" ready="+view.isViewReady+" parent="+view.group_talk.parent.name);
        }
    }
}
