using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using View.Main;

public static class RuntimeArchiveQA
{
    static int Int(string name)=>(int)AccessTools.Field(typeof(AdvSavePage),name).GetValue(AdvSavePage.Active);
    static bool Idle()=>AdvSavePage.Active!=null && AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1 && AdvSettingsTransition.Active==null && !AdvConfirmation.IsOpen && !(bool)AccessTools.Field(typeof(AdvSavePage),"busy").GetValue(AdvSavePage.Active);
    static void Click(string name,Action<bool,string> check)=>RuntimeUiQA.Click(name,check);
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/archive-"+name+".png"));yield return null;}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,IDialogueUiService api,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var service=(DialogueSaveService)api;var owner=AdvDialogueController.Active;
        yield return until(()=>adapter.CanCapture(out _),25,"archive fixture ready");
        foreach(var key in new[]{"SaveEmpty","Load","CopySave","SwapSave","Delete"})ConfirmationOptions.Entry(owner.Configuration,key).Value=true;
        if(File.Exists(Path.Combine(root,"native-archive-only.txt"))){yield return NativeOperations(ui,check,until,root);yield break;}
        ui.Open(true);yield return until(()=>Idle() && !service.IsListing,25,"12-slot archive opens and animation finishes");
        check(Int("page")==1,"page rail does not jump on open");
        var rail=AdvSavePage.Active.transform.Find("Archive page rail").GetComponent<RectTransform>();
        var canvas=rail.GetComponentInParent<Canvas>();var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
        var point=RectTransformUtility.WorldToScreenPoint(camera,rail.TransformPoint(new Vector3(rail.rect.width/2,-rail.rect.height*.75f,0)));
        var e=new PointerEventData(EventSystem.current){position=point,pressPosition=point,button=PointerEventData.InputButton.Left};var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(e,hits);
        check(hits.Count>0 && ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject)==rail.gameObject,"scroll rail has a working pointer hit area");
        e.pointerPressRaycast=hits[0];e.pointerCurrentRaycast=hits[0];ExecuteEvents.Execute(rail.gameObject,e,ExecuteEvents.pointerDownHandler);yield return null;ExecuteEvents.Execute(rail.gameObject,e,ExecuteEvents.pointerUpHandler);
        check(Int("page")>1,"clicking gradient rail changes current page");
        Click("Archive.First",check);yield return null;check(Int("page")==1,"right top arrow returns to first page");
        Click("Archive.DownTen",check);yield return null;check(Int("page")>1,"grey right arrow is clickable");
        Click("Archive.First",check);yield return null;
        for(int mode=0;mode<4;mode++)
        {
            Click("Archive.Tab"+mode,check);yield return null;yield return until(Idle,10,"archive mode ready "+mode);
            check(AdvSavePage.Active.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Archive.Slot"))==12,"mode has exactly 12 visible slots "+mode);
            check(Int("mode")==mode,"mode selection is applied "+mode);
            yield return Shot(root,"mode-"+mode);
        }
        Click("Archive.Tab0",check);yield return null;yield return until(Idle,10,"save mode ready");
        // Reserve an empty page in this isolated fixture, never touch a player save.
        Click("Archive.PageNext",check);yield return null;
        int page=Int("pageCount");
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{page});yield return null;
        int slot=(page-1)*12+1;
        check(!service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.RunId==service.ArchiveRun && r.Slot>=slot && r.Slot<slot+12),"new final page is empty");
        Click("Archive.Slot"+slot,check);yield return null;yield return until(()=>AdvConfirmation.IsOpen,5,"empty-slot save asks confirmation");
        Click("ADV.SaveEmptyConfirm",check);yield return null;yield return until(()=>Idle() && service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot && r.RunId==service.ArchiveRun),40,"clicked empty slot commits save");
        var original=service.ArchiveList(DialogueUiCategory.Manual).Single(r=>r.Slot==slot && r.RunId==service.ArchiveRun);
        yield return until(()=>AdvSavePage.Active.GetComponentsInChildren<Image>().Any(i=>i.name=="Saved background" && i.sprite!=null),10,"saved slot background has loaded");
        yield return Shot(root,"saved");
        Click("Archive.Tool2",check);yield return null;Click("Archive.Slot"+slot,check);yield return null;
        yield return until(()=>AdvSavePage.Active.EditorOpen,5,"note editor opens");
        const string note="隔离测试备注：放学后的约定";var input=UnityEngine.Object.FindObjectOfType<TMP_InputField>();input.text=note;
        Click("Note.Apply",check);yield return null;yield return until(Idle,20,"note committed");
        check(service.ArchiveList(DialogueUiCategory.Manual).Single(r=>r.Slot==slot && r.RunId==service.ArchiveRun).Comment==note,"edited note replaces dialogue caption");
        Click("Archive.Detail",check);yield return null;yield return Shot(root,"notes");
        Click("Archive.Tool0",check);yield return null;check(Int("operation")==0,"copy replaces edit selection");
        Click("Archive.Slot"+slot,check);yield return null;Click("Archive.Slot"+(slot+1),check);yield return null;Click("ADV.CopySaveConfirm",check);yield return null;
        yield return until(()=>Idle() && service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot+1 && r.RunId==service.ArchiveRun),25,"copy creates complete target");
        Click("Archive.Tool1",check);yield return null;Click("Archive.Slot"+(slot+1),check);yield return null;Click("Archive.Slot"+(slot+2),check);yield return null;Click("ADV.SwapSaveConfirm",check);yield return null;
        yield return until(()=>Idle() && service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot+2 && r.RunId==service.ArchiveRun),25,"swap into empty slot moves archive");
        check(!service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot+1 && r.RunId==service.ArchiveRun),"swap leaves source empty");
        Click("Archive.Tool3",check);yield return null;Click("Archive.Slot"+(slot+2),check);yield return null;Click("ADV.DeleteConfirm",check);yield return null;
        yield return until(()=>Idle() && !service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot+2 && r.RunId==service.ArchiveRun),25,"delete removes only selected copy");
        Click("Archive.Tool3",check);yield return null;check(Int("operation")==-1,"click selected operation cancels it");
        Click("Archive.Tab1",check);yield return until(Idle,10,"load page selected");
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{page});yield return new WaitForSecondsRealtime(.2f);
        Click("Archive.Slot"+slot,check);yield return null;yield return until(()=>AdvConfirmation.IsOpen,5,"filled slot asks load confirmation");
        Click("ADV.LoadCancel",check);yield return null;yield return until(Idle,10,"load cancel leaves page usable");
        Click("Archive.Slot"+slot,check);yield return null;Click("ADV.LoadConfirm",check);yield return null;
        yield return until(()=>AdvSavePage.Active==null && !adapter.IsRestoring && adapter.CanCapture(out _),45,"filled-slot click restores saved dialogue");
        check(((View.Evt.NewTalkView)UIMgr.GetView<View.Evt.NewTalkView>()).talk.Contains("隔离测试对白"),"archive load restores original dialogue text");
        yield return Shot(root,"loaded");
        var loading=UIMgr.GetView<LoadingView>(false);
        File.WriteAllText(Path.Combine(root,"results/loading-state.txt"),"view="+loading+"; state="+loading?.viewState+"; ready="+loading?.isViewReady+"; active="+loading?.gameObject?.activeInHierarchy+"; objects="+string.Join("|",Resources.FindObjectsOfTypeAll<Image>().Where(i=>i.transform.root.name=="Canvas" && i.transform.parent!=null && i.transform.parent.name.Contains("LoadingView")).Select(i=>i.name+":"+i.gameObject.activeInHierarchy)));

        yield return NativeOperations(ui,check,until,root);
    }
    static IEnumerator NativeOperations(DialogueUiController ui,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>UIMgr.GetView<LoadingView>(false)?.gameObject?.activeInHierarchy!=true,15,"native loading overlay has closed");
        ui.Open(true);yield return until(()=>Idle() && UIMgr.GetView<LoadingView>(false)?.gameObject?.activeInHierarchy!=true,20,"native archive fixture opens without loading blocker");
        Click("Archive.Tab3",check);yield return new WaitForSecondsRealtime(.15f);yield return until(Idle,10,"yellow native page opens");
        var browser=(NativeArchiveBrowser)AccessTools.Field(typeof(AdvSavePage),"native").GetValue(AdvSavePage.Active);
        int slot=Enumerable.Range(1,118).First(n=>(n-1)/12==(n+1)/12 && !browser.Slots.ContainsKey(n) && !browser.Slots.ContainsKey(n+1) && !browser.Slots.ContainsKey(n+2));
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{(slot-1)/12+1});yield return new WaitForSecondsRealtime(.15f);
        Click("Archive.Slot"+slot,check);yield return new WaitForSecondsRealtime(.15f);Click("ADV.SaveEmptyConfirm",check);
        yield return until(()=>Idle() && browser.Slots.ContainsKey(slot),40,"yellow empty slot creates native save at selected position");
        yield return new WaitForSecondsRealtime(.15f);
        var saved=browser.Slots[slot];string path=Path.Combine(PathDefine.SAVE_PATH,saved.fileName);byte[] body=File.ReadAllBytes(path);
        check(body.Length>1000,"native save contains actual game body");
        Click("Archive.Tool0",check);yield return new WaitForSecondsRealtime(.15f);Click("Archive.Slot"+slot,check);yield return new WaitForSecondsRealtime(.15f);Click("Archive.Slot"+(slot+1),check);yield return new WaitForSecondsRealtime(.15f);Click("ADV.CopySaveConfirm",check);
        yield return until(Idle,30,"native UI copy finishes");
        check(browser.Slots.ContainsKey(slot+1),"native copy result: "+((AdvSettingsHelp)AccessTools.Field(typeof(AdvSavePage),"help").GetValue(AdvSavePage.Active)).Caption);
        check(Enumerable.SequenceEqual(body,File.ReadAllBytes(Path.Combine(PathDefine.SAVE_PATH,browser.Slots[slot+1].fileName))),"native copy preserves complete body bytes");
        int deletedPhysicalPosition=browser.Slots[slot+1].pos;
        yield return new WaitForSecondsRealtime(.15f);
        Click("Archive.Tool1",check);yield return new WaitForSecondsRealtime(.15f);Click("Archive.Slot"+(slot+1),check);yield return new WaitForSecondsRealtime(.15f);Click("Archive.Slot"+(slot+2),check);yield return new WaitForSecondsRealtime(.15f);Click("ADV.SwapSaveConfirm",check);
        yield return until(()=>Idle() && browser.Slots.ContainsKey(slot+2),30,"native UI swap to empty completes");
        check(!browser.Slots.ContainsKey(slot+1),"native swap clears old visual slot");
        yield return new WaitForSecondsRealtime(.15f);
        Click("Archive.Tool3",check);yield return new WaitForSecondsRealtime(.15f);Click("Archive.Slot"+(slot+2),check);yield return new WaitForSecondsRealtime(.15f);Click("ADV.DeleteConfirm",check);
        yield return until(()=>Idle() && !browser.Slots.ContainsKey(slot+2),30,"native delete removes copy");
        check(Enumerable.SequenceEqual(body,File.ReadAllBytes(path)),"native edit operations preserve source file");
        var reopenedBrowser=new NativeArchiveBrowser();reopenedBrowser.Refresh();
        check(reopenedBrowser.FreeNativePosition()>deletedPhysicalPosition,"fresh browser reserves deleted native filenames before saving again");
        yield return Shot(root,"native-edited");Click("Archive.Return",check);
        yield return until(()=>AdvSavePage.Active==null && AdvSettingsTransition.Active==null,10,"native archive closes through shutter");
    }

}
