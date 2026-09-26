using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using View.Evt;
using View.Main;

internal static class RuntimeArchiveRoutingQA
{
    static object Field(string name)=>AccessTools.Field(typeof(AdvSavePage),name).GetValue(AdvSavePage.Active);
    static Dictionary<int,DialogueUiRecord> Slots=>(Dictionary<int,DialogueUiRecord>)Field("slots");
    static bool Idle()=>AdvSavePage.Active!=null && AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1 && AdvSettingsTransition.Active==null && !(bool)Field("busy") && !AdvConfirmation.IsOpen;
    static IEnumerator Tap(string name,Action<bool,string> check)
    {yield return new WaitForSecondsRealtime(.15f);Canvas.ForceUpdateCanvases();RuntimeUiQA.Click(name,check);yield return null;}
    static void Page(int slot)=>AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{(slot-1)/12+1});
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _),30,"routing fixture ready");
        foreach(var key in new[]{"SaveEmpty","Overwrite","Load","NativeLoad"})ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,key).Value=true;
        ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,20,"save page ready");
        yield return Tap("Archive.PageNext",check);yield return null;
        int slot=((int)Field("pageCount")-1)*12+1;Page(slot);yield return null;
        yield return Tap("Archive.Slot"+slot,check);yield return until(()=>AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null,5,"save empty slot asks save");
        yield return Tap("ADV.SaveEmptyConfirm",check);yield return until(()=>Idle()&&Slots.ContainsKey(slot),40,"empty slot saves");
        string first=Slots[slot].RevisionId;int count=service.ArchiveList(DialogueUiCategory.Manual).Count;
        yield return Tap("Archive.Slot"+slot,check);yield return until(()=>AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null,5,"filled save slot asks overwrite");
        yield return Tap("ADV.OverwriteCancel",check);yield return until(Idle,10,"overwrite cancellation returns to save page");
        check(Slots[slot].RevisionId==first,"cancel overwrite preserves old revision");
        yield return Tap("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&AdvSettingsTransition.Active==null,10,"save page closes");
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk.talkState==TalkState.AnimEnd&&adapter.CanAdvancePresentation(talk),20,"first line completed");talk.OnClickNext();
        yield return until(()=>talk.talkState==TalkState.Option&&adapter.CanCapture(out _),25,"current world advances to next choices");
        ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,20,"reopen save page");Page(slot);yield return null;
        yield return Tap("Archive.Slot"+slot,check);yield return until(()=>AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null,5,"overwrite current progress confirmation");
        yield return Tap("ADV.OverwriteConfirm",check);yield return until(()=>Idle()&&Slots.ContainsKey(slot)&&Slots[slot].RevisionId!=first,40,"filled slot overwrites without loading");
        string replacement=Slots[slot].RevisionId;
        check(service.ArchiveList(DialogueUiCategory.Manual).Count==count,"overwrite adds no extra visible slot");
        check(talk.talkState==TalkState.Option,"overwrite does not restore earlier dialogue");
        check(Slots[slot].Summary.StartsWith("选择项："),"overwrite records current choice state");
        ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"Overwrite").Value=false;
        yield return Tap("Archive.Slot"+slot,check);yield return until(()=>Idle()&&Slots[slot].RevisionId!=replacement,40,"disabled overwrite confirmation still saves");
        replacement=Slots[slot].RevisionId;
        yield return Tap("Archive.Tab1",check);yield return until(Idle,10,"load page ready");Page(slot);yield return null;
        yield return Tap("Archive.Slot"+(slot+1),check);yield return new WaitForSecondsRealtime(.4f);
        check(!AdvConfirmation.IsOpen&&!Slots.ContainsKey(slot+1)&&service.ArchiveList(DialogueUiCategory.Manual).Count==count,"empty load slot never saves or asks to save");
        yield return Tap("Archive.Slot"+slot,check);yield return until(()=>AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null,5,"load page filled slot asks load");
        yield return Tap("ADV.LoadCancel",check);yield return until(Idle,10,"load cancel keeps page");check(Slots[slot].RevisionId==replacement,"load cancel leaves revision intact");
        yield return Tap("Archive.Tab2",check);yield return until(Idle,10,"quick load page ready");
        yield return Tap("Archive.Last",check);yield return null;int empty=((int)Field("page")-1)*12+12;
        if(!Slots.ContainsKey(empty)){yield return Tap("Archive.Slot"+empty,check);yield return new WaitForSecondsRealtime(.3f);check(!AdvConfirmation.IsOpen&&!Slots.ContainsKey(empty),"quick load empty slot never saves");}
        yield return Tap("Archive.Tab1",check);yield return until(Idle,10,"return load page");Page(slot);yield return null;
        yield return Tap("Archive.Slot"+slot,check);yield return until(()=>AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null,5,"restore overwritten record");yield return Tap("ADV.LoadConfirm",check);
        yield return until(()=>AdvSavePage.Active==null&&!adapter.IsRestoring&&adapter.CanCapture(out _),45,"load restores overwritten checkpoint");
        talk=(NewTalkView)UIMgr.GetView<NewTalkView>();check(talk.talkState==TalkState.Option,"loaded checkpoint is updated choice state");
        // Opening through LOAD must not inherit SAVE behavior in native archives either.
        ui.Open(false);yield return until(()=>Idle()&&!service.IsListing,20,"load entry ready");yield return Tap("Archive.Tab3",check);yield return until(Idle,10,"native load category ready");
        yield return Tap("Archive.PageNext",check);yield return null;empty=((int)Field("pageCount")-1)*12+12;Page(empty);yield return null;
        check(!Slots.ContainsKey(empty),"native load test slot empty");yield return Tap("Archive.Slot"+empty,check);yield return new WaitForSecondsRealtime(.4f);
        check(!AdvConfirmation.IsOpen&&!Slots.ContainsKey(empty),"native load empty slot never writes");
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/archive-routing-native-load.png"));
        yield return Tap("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&AdvSettingsTransition.Active==null,10,"native archive closes");
        ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,20,"native save entry ready");yield return Tap("Archive.Tab3",check);yield return until(Idle,10,"native save category ready");
        yield return Tap("Archive.PageNext",check);empty=((int)Field("pageCount")-1)*12+1;Page(empty);yield return null;
        yield return Tap("Archive.Slot"+empty,check);yield return until(()=>AdvConfirmation.IsOpen&&AdvSettingsTransition.Active==null,10,"native empty save asks save");
        yield return Tap("ADV.SaveEmptyConfirm",check);yield return until(()=>Idle()&&Slots.ContainsKey(empty),40,"native empty slot saved");
        string nativeRevision=Slots[empty].RevisionId;int nativeCount=Slots.Count;
        ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"Overwrite").Value=true;
        yield return Tap("Archive.Slot"+empty,check);yield return until(()=>AdvConfirmation.IsOpen&&AdvSettingsTransition.Active==null,10,"native filled save asks overwrite");
        yield return Tap("ADV.OverwriteConfirm",check);yield return until(()=>Idle()&&Slots.ContainsKey(empty)&&Slots[empty].RevisionId!=nativeRevision,40,"native existing slot overwritten");
        check(Slots.Count==nativeCount,"native overwrite does not move old save to another card");
        yield return Tap("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&AdvSettingsTransition.Active==null,10,"native save closes");
        File.WriteAllText(Path.Combine(root,"results/archive-routing-success.txt"),"SAVE_EMPTY_OVERWRITE_CANCEL_NO_PROMPT_LOAD_EMPTY_LOAD_FILLED_QUICK_NATIVE_OK");
    }
}
