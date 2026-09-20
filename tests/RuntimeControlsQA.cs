using System;
using System.IO;
using System.Linq;
using System.Collections;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.Storage;

// Tests real input-system key events and native EventSystem pointer dispatch in the isolated player.
public static class RuntimeControlsQA
{
    public static bool MeasuringSaveOpen;
    public static float LargestSaveOpenFrame;
    public static bool MeasuringSavePreparation;
    public static float LargestSavePreparationFrame;
    public static void ObserveFrame(float seconds)
    {
        if(MeasuringSaveOpen)LargestSaveOpenFrame=Mathf.Max(LargestSaveOpenFrame,seconds);
        if(MeasuringSavePreparation)LargestSavePreparationFrame=Mathf.Max(LargestSavePreparationFrame,seconds);
    }
    public static IEnumerator Run(DialogueCheckpointAdapter adapter, DialogueUiController ui,
        IDialogueUiService service, Action<bool,string> check,
        Func<Func<bool>,float,string,IEnumerator> until, Func<string,IEnumerator> shot, string root)
    {
        // Select before this run creates any archive. Preserve its original bytes; a
        // freshly captured checkpoint cannot prove compatibility with a prior build.
        string oldPath=null;
        SaveEnvelope oldSave=null;
        var oldVerifier=new Repository(PathDefine.SAVE_PATH,Path.Combine(root,"data/old-verify-stage"),Path.Combine(root,"data/old-verify-backup"));
        foreach(var record in oldVerifier.Scan().Where(r=>r.Status==SaveStatus.Ready)
            .OrderByDescending(r=>r.Header.CreatedUtc,StringComparer.Ordinal))
        {
            var candidate=oldVerifier.Load(record.Header.RevisionId);
            if(candidate.Dialogue.Value<int>("talkId")!=1900000001 ||
                candidate.Dialogue.Value<string>("configDigest")!="712AC7722CA6A34190D398BEA9DD531E403E897B1C95C49E9033FECA84617920" ||
                !(candidate.Dialogue["activeMods"] is JArray))continue;
            oldPath=record.FilePath;oldSave=candidate;break;
        }
        check(oldSave!=null,"prior-run real dialogue checkpoint exists before controls create any save");
        string oldHash=FileHash(oldPath);
        File.WriteAllText(Path.Combine(root,"results/old-save-before-run.json"),new JObject
        {
            ["path"]=oldPath,["revision"]=oldSave.Header.RevisionId,["sha256"]=oldHash,
            ["createdUtc"]=oldSave.Header.CreatedUtc,["talkId"]=oldSave.Dialogue["talkId"],
            ["configDigest"]=oldSave.Dialogue["configDigest"],["activeMods"]=oldSave.Dialogue["activeMods"],
            ["plugins"]=oldSave.Dialogue["plugins"]
        }.ToString());
        var comparePlugins=AccessTools.Method(typeof(DialogueCheckpointAdapter),"PluginFingerprintsMatch");
        var currentPlugins=new JArray(
            new JObject{["guid"]="local.studentage.dialoguesave",["version"]="0.1.0",["module"]="new",["sha256"]="new"},
            new JObject{["guid"]="qa.thirdparty",["version"]="1.0",["module"]="same",["sha256"]="same"});
        Func<JArray,bool> compatible=p=>(bool)comparePlugins.Invoke(null,new object[]{p,currentPlugins});
        var priorPlugins=(JArray)currentPlugins.DeepClone();priorPlugins[0]["module"]="old";priorPlugins[0]["sha256"]="old";
        check(compatible(priorPlugins),"own UI-only rebuild retains archive compatibility");
        priorPlugins[1]["sha256"]="changed";
        check(!compatible(priorPlugins),"third-party binary change still rejects archive");
        priorPlugins=(JArray)currentPlugins.DeepClone();priorPlugins[0]["version"]="other";
        check(!compatible(priorPlugins),"own version mismatch still rejects archive");
        priorPlugins=(JArray)currentPlugins.DeepClone();priorPlugins.RemoveAt(0);
        check(!compatible(priorPlugins),"missing own fingerprint rejects archive");
        priorPlugins=(JArray)currentPlugins.DeepClone();priorPlugins.Add(priorPlugins[0].DeepClone());
        check(!compatible(priorPlugins),"duplicated own fingerprint rejects archive");
        // This old QA fixture can trigger a delayed missing-Mod notice. Record and dismiss only
        // that isolated setup modal; no user save or production dialog is touched.
        yield return new WaitForSecondsRealtime(1);
        var setupModal=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        if(setupModal!=null && setupModal.GetType().Name=="CommonComfirmView")
        {
            var texts=setupModal.gameObject.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Select(t=>t.text)
                .Concat(setupModal.gameObject.GetComponentsInChildren<UnityEngine.UI.Text>(true).Select(t=>t.text));
            File.WriteAllLines(Path.Combine(root,"results/isolated-fixture-notice.txt"),texts);
            UIMgr.CloseView(setupModal);
            yield return new WaitForSecondsRealtime(.5f);
        }
        yield return until(()=>FullToolbarReady(ui),20,"native three and added four dialogue controls appear without forced Refresh event");
        yield return new WaitForSecondsRealtime(1);
        yield return shot("controls-01-dialogue-toolbar");
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        string text=talk.txtex_content.text;
        LargestSaveOpenFrame=0;MeasuringSaveOpen=true;
        LargestSavePreparationFrame=0;MeasuringSavePreparation=true;
        var opening=System.Diagnostics.Stopwatch.StartNew();
        yield return Press(Key.K);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"K opens dialogue save");
        long registered=opening.ElapsedMilliseconds;
        yield return until(()=>((SaveView)UIMgr.GetView<SaveView>()).isViewReady,15,"save view resources ready");
        yield return new WaitForEndOfFrame();
        File.WriteAllText(Path.Combine(root,"results/save-open-timing.json"),new JObject
        {
            ["inputToOpenedMs"]=registered,["inputToReadyFrameMs"]=opening.ElapsedMilliseconds,
            ["largestFrameMs"]=LargestSaveOpenFrame*1000f
        }.ToString());
        MeasuringSaveOpen=false;
        check(((SaveView)UIMgr.GetView<SaveView>()).isSaveMode,"K selects save mode");
        yield return new WaitForSecondsRealtime(1);
        yield return shot("controls-02-keyboard-save");
        var manualBefore=service.List(DialogueUiCategory.Manual).Select(r=>r.RevisionId).ToArray();
        var empty=Resources.FindObjectsOfTypeAll<RectTransform>().First(r=>r.gameObject.activeInHierarchy &&
            r.name.StartsWith("DialogueSave.Card.") && new GenUI.Main.Cell_SaveItemUI(r.gameObject).btn_add.gameObject.activeInHierarchy);
        var add=new GenUI.Main.Cell_SaveItemUI(empty.gameObject).btn_add.gameObject;
        var addName=add.name;add.name="QA.NewDialogueSave";
        File.WriteAllText(Path.Combine(root,"results/new-save-pointer.json"),RuntimeUiQA.PointerReport(add).ToString());
        yield return RuntimeUiQA.CheckExclusiveHover("QA.NewDialogueSave",check);
        RuntimeUiQA.Click("QA.NewDialogueSave",check);
        if(add!=null)add.name=addName;
        yield return until(()=>service.List(DialogueUiCategory.Manual).Any(r=>!manualBefore.Contains(r.RevisionId)),40,"one click on new save publishes manual checkpoint");
        var created=service.List(DialogueUiCategory.Manual).First(r=>!manualBefore.Contains(r.RevisionId));
        var verifier=new StudentAgeDialogueSave.Storage.Repository(PathDefine.SAVE_PATH,
            Path.Combine(root,"data/verify-stage"),Path.Combine(root,"data/verify-backup"));
        var saved=verifier.Load(created.RevisionId);
        check(saved.Header.Category=="manual" && saved.World.Length>0 && saved.Dialogue["segments"]!=null,
            "manual save file passes repository integrity checks with world and dialogue payloads");
        MeasuringSavePreparation=false;
        File.WriteAllText(Path.Combine(root,"results/save-preparation-timing.json"),new JObject
        {
            ["inputToManualCommitMs"]=opening.ElapsedMilliseconds,
            ["largestFrameMs"]=LargestSavePreparationFrame*1000f
        }.ToString());
        check(UIMgr.IsViewOpened<SaveView>(),"successful new save keeps save window open");
        check(adapter.IsPausedForMenu,"successful new save retains dialogue pause for another save");
        yield return new WaitForSecondsRealtime(.4f);
        check(UIMgr.IsViewOpened<SaveView>() && adapter.IsPausedForMenu,"save completion does not close or resume on a delayed callback");
        yield return shot("controls-02a-save-complete-window-retained");
        UIMgr.CloseView<SaveView>();
        yield return until(()=>!adapter.IsPausedForMenu,5,"explicit save-window close releases dialogue pause");
        // Compare only after timing the first cold capture; this expensive QA-only legacy
        // calculation must not warm the implementation before its responsiveness measurement.
        check(adapter.VerifyDetachedConfigFingerprint(),"detached config digest equals legacy format for loaded game config");
        yield return new WaitForSecondsRealtime(.5f);
        yield return Press(Key.K);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"reopen save to verify overwrite confirmation layering");
        yield return new WaitForSecondsRealtime(1);
        var occupied=Resources.FindObjectsOfTypeAll<RectTransform>().Where(r=>r.gameObject.activeInHierarchy &&
            r.name.StartsWith("DialogueSave.Card.")).Select(r=>new GenUI.Main.Cell_SaveItemUI(r.gameObject))
            .First(c=>c.txt_idx.text==created.Slot.ToString() && c.btn_click.gameObject.activeInHierarchy);
        ClickNamed(occupied.btn_click.gameObject,"QA.ExistingDialogueCard",check);
        RuntimeUiQA.Click("DialogueSave.Commit",check);
        yield return until(()=>UIMgr.GetTopView(ViewType.Guide,ViewType.Side) is View.Hint.CommonComfirmView,10,
            "overwrite confirmation is above save window");
        yield return new WaitForSecondsRealtime(.5f);
        var overwrite=(View.Hint.CommonComfirmView)UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        ClickNamed(overwrite.btn_cancel.gameObject,"QA.OverwriteCancel",check);
        check(UIMgr.IsViewOpened<SaveView>(),"cancel overwrite returns to save window");
        check(service.List(DialogueUiCategory.Manual).Any(r=>r.RevisionId==created.RevisionId),"cancel overwrite preserves original revision");
        UIMgr.CloseView<SaveView>();
        yield return until(()=>!adapter.IsPausedForMenu,5,"cancel pending save preparation releases pause");
        yield return until(()=>adapter.CanCapture(out _),20,"dialogue settles for read menu");
        yield return Press(Key.L);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"L opens dialogue load");
        check(!((SaveView)UIMgr.GetView<SaveView>()).isSaveMode,"L selects load mode");
        yield return new WaitForSecondsRealtime(1);
        yield return shot("controls-02b-created-manual-card");
        check(service.List(DialogueUiCategory.Manual).Any(r=>!manualBefore.Contains(r.RevisionId) && r.CanLoad && !string.IsNullOrEmpty(r.Summary)),
            "new manual save is readable with dialogue summary");
        for(int tab=0;tab<3;tab++)
        {
            ClickNativeTab(tab,check);
            yield return new WaitForSecondsRealtime(.3f);
            check(UIMgr.IsViewOpened<SaveView>(),"ordinary category remains open: "+tab);
            check(adapter.IsPausedForMenu,"ordinary category keeps dialogue paused: "+tab);
            var page=(JObject)((JArray)JObject.Parse(ui.CaptureLayoutReport())["saveWindows"])[0];
            check(!(bool)page["dialoguePageOpen"],"ordinary category shows native grid: "+tab);
            RuntimeUiQA.Click("DialogueSave.Entry",check);
            yield return new WaitForSecondsRealtime(.3f);
            check(adapter.IsPausedForMenu,"returning to dialogue category retains pause");
        }
        UIMgr.CloseView<SaveView>();
        yield return until(()=>!adapter.IsPausedForMenu,5,"cancel read browser releases retained pause");
        yield return new WaitForSecondsRealtime(.5f);
        yield return Press(Key.Escape);
        yield return until(()=>global::Game.IsMenuOpened(),10,"Escape opens native in-game menu");
        yield return new WaitForSecondsRealtime(1);
        yield return shot("controls-03-escape-menu");
        File.WriteAllText(Path.Combine(root,"results/controls-menu-view.txt"),UIMgr.GetTopView()?.GetType().FullName ?? "none");
        RuntimeUiQA.Click("DialogueSave.Menu.Save",check);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"Escape menu save opens dialogue slots");
        check(!global::Game.IsMenuOpened(),"Escape menu closes before save capture");
        check(((SaveView)UIMgr.GetView<SaveView>()).isSaveMode,"Escape save mode correct");
        UIMgr.CloseView<SaveView>();
        yield return new WaitForSecondsRealtime(.5f);
        yield return Press(Key.Escape);
        yield return until(()=>global::Game.IsMenuOpened(),10,"Escape menu can reopen");
        yield return new WaitForSecondsRealtime(.5f);
        RuntimeUiQA.Click("DialogueSave.Menu.Load",check);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"Escape menu load opens dialogue slots");
        check(!((SaveView)UIMgr.GetView<SaveView>()).isSaveMode,"Escape load mode correct");
        UIMgr.CloseView<SaveView>();
        yield return until(()=>HasFour(ui),10,"dialogue controls return after closing Escape menu");
        var previous=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).ToArray();
        yield return Press(Key.H);
        yield return until(()=>service.List(DialogueUiCategory.Quick).Any(r=>!previous.Contains(r.RevisionId)),40,"H publishes an actual quick checkpoint");
        yield return new WaitForSecondsRealtime(.5f);
        yield return Press(Key.J);
        yield return until(()=> { var top=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);return top!=null && !(top is NewTalkView); },10,"J opens native quick-load confirmation");
        var confirmation=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        File.WriteAllText(Path.Combine(root,"results/controls-confirmation-type.txt"),confirmation.GetType().FullName);
        // Use the actual native Cancel button; closing BaseView programmatically bypasses
        // the game's cancellation callback and is not a user-operable path for this modal.
        var cancel=((View.Hint.CommonComfirmView)confirmation).btn_cancel.gameObject;
        var oldName=cancel.name;cancel.name="QA.QuickLoadCancel";
        RuntimeUiQA.Click("QA.QuickLoadCancel",check);
        cancel.name=oldName;
        yield return new WaitForSecondsRealtime(.5f);
        yield return until(()=>!adapter.IsPausedForMenu,5,"cancelling quick load releases pause");
        check(SaveMgr.GetPref("LatestSaveKey","")=="QA_SENTINEL","controls do not change normal LatestSaveKey");
        yield return Press(Key.L);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"open read browser for ordinary load");
        yield return new WaitForSecondsRealtime(.6f);
        ClickNativeTab(0,check);
        yield return new WaitForSecondsRealtime(.6f);
        string ordinary=File.ReadAllText(Path.Combine(root,"results/ordinary-fixture-name.txt"));
        var loadView=(SaveView)UIMgr.GetView<SaveView>();
        var nativeCard=loadView.itemgroup_save.GetCells().OfType<GenUI.Main.Cell_SaveItemUI>()
            .First(c=>(c.data as SaveFileData)?.fileName==ordinary);
        // The native center is the editable note button. Select through the portrait area.
        ClickNamed(nativeCard.btn_click.gameObject,"QA.OrdinarySaveCard",check,new Vector2(.18f,.5f));
        yield return new WaitForSecondsRealtime(.2f);
        yield return shot("controls-04-ordinary-load-selection");
        ClickNamed(loadView.btn_load.gameObject,"QA.OrdinaryLoad",check);
        yield return until(()=>SaveMgr.GetPref("LatestSaveKey","")==ordinary && !UIMgr.IsViewOpened<SaveView>(),40,
            "native ordinary save loads from dialogue and updates native LatestSaveKey");
        yield return until(()=>UIMgr.IsViewOpened<TopView>() && ((TopView)UIMgr.GetView<TopView>()).isViewReady,30,
            "ordinary game toolbar starts after native load");
        yield return until(()=>!UIMgr.IsViewOpened<LoadingView>(),30,"native load transition mask has finished");
        yield return new WaitForSecondsRealtime(2);
        check(!adapter.IsPausedForMenu,"native load releases old dialogue pause before new world starts");
        check(!adapter.IsDialogueContext,"ordinary load does not resurrect abandoned dialogue");
        var warning=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        if(warning!=null && warning.GetType().Name=="CommonComfirmView")UIMgr.CloseView(warning);
        // Exercise the actual dialogue-load UI after native loading changed LatestSaveKey.
        // The abandoned dialogue is deliberately not capturable: its callback belongs to this
        // QA assembly and is outside the continuation allowlist. Loading must discard it quietly.
        var evt=Singleton<CommonEvtMgr>.Ins;
        evt.EnqueueEvt(0,1);evt.ShowNewRoundEvent();
        yield return until(()=>adapter.IsDialogueContext && HasFour(ui) && adapter.CanCapture(out _),20,"dialogue reopens after ordinary-load test");
        var abandoned=(NewTalkView)UIMgr.GetView<NewTalkView>();
        abandoned.NextTalk();
        yield return until(()=>abandoned.talkState==TalkState.Option && adapter.CanCapture(out _),20,"abandoned dialogue reaches choices before unknown callback is installed");
        int abandonedCallbackCount=0;
        abandoned.callback=()=>abandonedCallbackCount++;
        check(!adapter.CanCapture(out var blockedReason),"unknown callback makes current dialogue non-capturable");
        File.WriteAllText(Path.Combine(root,"results/abandoned-capture-reason.txt"),blockedReason ?? "");
        string latestBeforeDialogueLoad=SaveMgr.GetPref("LatestSaveKey","");
        yield return Press(Key.L);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>() && ((SaveView)UIMgr.GetView<SaveView>()).isViewReady,20,"load browser opens from non-capturable dialogue");
        yield return new WaitForSecondsRealtime(.6f);
        var orderedManual=service.List(DialogueUiCategory.Manual).OrderByDescending(r=>r.CreatedUtc).ToArray();
        check(orderedManual.Length>0 && orderedManual[0].RevisionId==created.RevisionId,"new manual checkpoint is the first visible load record");
        var firstCard=Resources.FindObjectsOfTypeAll<RectTransform>().Single(r=>r.gameObject.activeInHierarchy && r.name=="DialogueSave.Card.0");
        ClickNamed(new GenUI.Main.Cell_SaveItemUI(firstCard.gameObject).btn_click.gameObject,"QA.CreatedDialogueLoadCard",check,new Vector2(.18f,.5f));
        RuntimeUiQA.Click("DialogueSave.Commit",check);
        yield return until(()=>UIMgr.GetTopView(ViewType.Guide,ViewType.Side) is View.Hint.CommonComfirmView,10,"created dialogue checkpoint asks to discard current progress");
        yield return new WaitForSecondsRealtime(.4f);
        yield return until(()=>!UIMgr.IsViewOpened<LoadingView>(),15,"native transition does not cover dialogue load confirmation");
        var readConfirmation=(View.Hint.CommonComfirmView)UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        ClickNamed(readConfirmation.btn_ok.gameObject,"QA.ConfirmCreatedDialogueLoad",check);
        yield return until(()=>!adapter.IsRestoring && !UIMgr.IsViewOpened<SaveView>() &&
            UIMgr.IsViewOpened<NewTalkView>() && !ReferenceEquals(UIMgr.GetView<NewTalkView>(),abandoned) &&
            ((NewTalkView)UIMgr.GetView<NewTalkView>()).isViewReady,45,"actual dialogue restore replaces non-capturable source view");
        var restored=(NewTalkView)UIMgr.GetView<NewTalkView>();
        int savedSegment=saved.Dialogue.Value<int>("segmentIndex");
        check(restored.tmpTalkIdx==savedSegment && restored.tmpTalks[savedSegment]==(string)saved.Dialogue["segments"][savedSegment],
            "loaded dialogue resumes the stored segment instead of abandoned choices");
        check(((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(restored)).id==saved.Dialogue.Value<int>("talkId"),
            "loaded view uses saved dialogue node");
        check(adapter.CurrentCheckpointBrief.RunId==saved.Header.RunId,"loaded world belongs to checkpoint run");
        yield return new WaitForSecondsRealtime(1);
        check(abandonedCallbackCount==0,"discarding unsupported source never executes its old callback");
        check(!adapter.IsPausedForMenu && UIMgr.IsViewOpened<TopView>(),"dialogue restore releases menu pause and restores game toolbar");
        check(SaveMgr.GetPref("LatestSaveKey","")==latestBeforeDialogueLoad && latestBeforeDialogueLoad==ordinary,
            "dialogue restore preserves LatestSaveKey established by ordinary load");
        yield return shot("controls-05-actual-dialogue-restore");

        // Both reads target the unchanged prior-run file. The first completion callback
        // deliberately fails after recording the real result; later loads must still run.
        var warmReads=new JArray();
        bool threwCompletion=false;
        for(int attempt=0;attempt<2;attempt++)
        {
            UiResult completed=null;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            bool throwThisCallback=attempt==0;
            service.Load(oldSave.Header.RevisionId,result=>
            {
                if(result.IsPending)return;
                completed=result;
                clock.Stop();
                if(throwThisCallback)
                {
                    threwCompletion=true;
                    throw new InvalidOperationException("Intentional QA completion callback failure; storage/restore already finished.");
                }
            });
            yield return until(()=>completed!=null,60,"prior-run checkpoint load finishes: "+attempt);
            check(completed.Success,"prior-run checkpoint load succeeds: "+attempt+" "+completed.Message);
            yield return until(()=>!adapter.IsRestoring && !adapter.IsPausedForMenu && FullToolbarReady(ui),30,
                "restored old checkpoint has complete native dialogue toolbar: "+attempt);
            var oldTalk=(NewTalkView)UIMgr.GetView<NewTalkView>();
            int segment=oldSave.Dialogue.Value<int>("segmentIndex");
            check(oldTalk.tmpTalkIdx==segment && oldTalk.tmpTalks[segment]==(string)oldSave.Dialogue["segments"][segment] &&
                ((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(oldTalk)).id==oldSave.Dialogue.Value<int>("talkId"),
                "prior-run restore matches saved dialogue node and segment: "+attempt);
            check(adapter.CurrentCheckpointBrief.RunId==oldSave.Header.RunId,"prior-run restore matches saved world run: "+attempt);
            check(FileHash(oldPath)==oldHash,"prior-run archive bytes unchanged after actual restore: "+attempt);
            check(SaveMgr.GetPref("LatestSaveKey","")==latestBeforeDialogueLoad,"old dialogue restore preserves ordinary LatestSaveKey: "+attempt);
            warmReads.Add(new JObject{["attempt"]=attempt+1,["loadCompletionMs"]=clock.ElapsedMilliseconds,["revision"]=oldSave.Header.RevisionId});
        }
        check(threwCompletion,"later actual load completes after previous result callback throws");

        // A third manual checkpoint measures the warm path from the same real key
        // press to durable completion; selecting an empty slot still uses the UI.
        var warmSaveClock=System.Diagnostics.Stopwatch.StartNew();
        yield return Press(Key.K);
        yield return until(()=>UIMgr.IsViewOpened<SaveView>() && ((SaveView)UIMgr.GetView<SaveView>()).isViewReady,20,
            "warm save window ready");
        yield return until(()=>!service.IsListing,20,"warm save list ready");
        var beforeWarm=service.List(DialogueUiCategory.Manual).Select(r=>r.RevisionId).ToArray();
        var warmEmpty=Resources.FindObjectsOfTypeAll<RectTransform>().First(r=>r.gameObject.activeInHierarchy &&
            r.name.StartsWith("DialogueSave.Card.") && new GenUI.Main.Cell_SaveItemUI(r.gameObject).btn_add.gameObject.activeInHierarchy);
        ClickNamed(new GenUI.Main.Cell_SaveItemUI(warmEmpty.gameObject).btn_add.gameObject,"QA.WarmNewDialogueSave",check);
        yield return until(()=>service.List(DialogueUiCategory.Manual).Any(r=>!beforeWarm.Contains(r.RevisionId)),40,"warm save publishes a new complete checkpoint");
        warmSaveClock.Stop();
        var warmRecord=service.List(DialogueUiCategory.Manual).First(r=>!beforeWarm.Contains(r.RevisionId));
        check(oldVerifier.Load(warmRecord.RevisionId).World.Length>0,"warm manual checkpoint passes repository integrity validation");
        check(FileHash(oldPath)==oldHash,"warm save also preserves the prior-run archive bytes");
        File.WriteAllText(Path.Combine(root,"results/warm-save-load-timing.json"),new JObject
        {
            ["reads"]=warmReads,["warmSaveKeyToCommitMs"]=warmSaveClock.ElapsedMilliseconds,
            ["warmSavedRevision"]=warmRecord.RevisionId,["oldRevision"]=oldSave.Header.RevisionId,
            ["oldSha256After"]=FileHash(oldPath),["throwingCallbackRecovered"]=threwCompletion
        }.ToString());
        UIMgr.CloseView<SaveView>();
        yield return until(()=>!adapter.IsPausedForMenu && !adapter.IsRestoring && FullToolbarReady(ui),30,
            "final screenshot contains complete native dialogue and all seven toolbar actions");
        yield return new WaitForSecondsRealtime(.5f);
        yield return new WaitForEndOfFrame();
        RuntimeFontQA.Capture(Path.Combine(root,"results"));
        yield return shot("controls-06-final-native-dialogue-toolbar");
    }
    static string FileHash(string path)
    {
        using(var digest=System.Security.Cryptography.SHA256.Create())
        using(var input=File.OpenRead(path))return BitConverter.ToString(digest.ComputeHash(input)).Replace("-","").ToLowerInvariant();
    }
    static bool FullToolbarReady(DialogueUiController ui)
    {
        var talk=UIMgr.GetView<NewTalkView>() as NewTalkView;
        var top=UIMgr.GetView<TopView>() as TopView;
        return talk!=null && talk.isViewReady && talk.gameObject.activeInHierarchy && top!=null && top.isViewReady &&
            top.gameObject.activeInHierarchy && !UIMgr.IsViewOpened<LoadingView>() &&
            UIMgr.GetTopView(ViewType.Guide,ViewType.Side) is NewTalkView &&
            top.itemgroup_key.GetCells().Count(c=>c.gameObject.activeInHierarchy)>=3 && HasFour(ui);
    }
    static void ClickNativeTab(int index,Action<bool,string> check)
    {
        var view=(SaveView)UIMgr.GetView<SaveView>();
        ClickNamed(view.tabgroup_top.GetCells()[index].gameObject,"QA.OrdinaryTab"+index,check);
    }
    static void ClickNamed(GameObject obj,string name,Action<bool,string> check,Vector2? point=null)
    {
        string original=obj.name;obj.name=name;
        try{RuntimeUiQA.Click(name,check,point);}finally{if(obj!=null)obj.name=original;}
    }
    public static bool HasFour(DialogueUiController ui)
    {
        var rows=(JArray)JObject.Parse(ui.CaptureLayoutReport())["dialogueActionRows"];
        return rows.Any(r=>((JArray)r["labels"]).Count==4);
    }
    public static IEnumerator Press(Key key)
    {
        if(Keyboard.current==null)throw new InvalidOperationException("QA keyboard unavailable");
        InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(key));
        yield return null;yield return null;
        InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());
        yield return null;yield return null;
    }
}
