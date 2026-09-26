using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public static class RuntimeInteractionQA
{
    static bool Saving(DialogueSaveService service)=>(bool)AccessTools.Field(typeof(DialogueSaveService),"quickSaveRequested").GetValue(service);
    static bool ButtonShown(string name)=>Resources.FindObjectsOfTypeAll<Button>().Any(b=>b.name==name && b.gameObject.activeInHierarchy);
    internal static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var owner=AdvDialogueController.Active;
        ConfirmationOptions.Entry(owner.Configuration,"QuickSave").Value=true;
        ConfirmationOptions.Entry(owner.Configuration,"QuickLoad").Value=true;
        yield return until(()=>adapter.CanCapture(out _) && !AdvConfirmation.IsOpen && AdvSettingsTransition.Active==null,15,"quick-action fixture ready");
        service.QuickSave();service.QuickSave();yield return null;
        check(Resources.FindObjectsOfTypeAll<Canvas>().Count(c=>c.name=="DialogueSave.Confirmation" && c.gameObject.activeInHierarchy)==1,"double quick-save creates one confirmation");
        RuntimeUiQA.Click("ADV.QuickSaveRemember",check);yield return null;
        AdvConfirmation.Cancel();yield return until(()=>!AdvConfirmation.IsOpen && !Saving(service),10,"cancel releases quick-save request");
        check(ConfirmationOptions.Enabled(owner.Configuration,"QuickSave"),"cancel does not persist do-not-ask");
        service.QuickSave();yield return null;RuntimeUiQA.Click("ADV.QuickSaveRemember",check);yield return null;
        RuntimeUiQA.Click("ADV.QuickSaveConfirm",check);
        yield return until(()=>!Saving(service) && !AdvConfirmation.IsOpen,30,"confirmed quick-save completes");
        check(!ConfirmationOptions.Enabled(owner.Configuration,"QuickSave") && !adapter.IsPausedForMenu,"remembered quick-save completes and releases pause");
        // The first plus twelve additional writes exercise the wrap without suppressing capture/store.
        for(int i=0;i<12;i++){service.QuickSave();yield return until(()=>!Saving(service),30,"quick-save rotation "+i);}
        var records=service.ArchiveList(DialogueUiCategory.Quick).Where(r=>r.RunId==service.ArchiveRun).ToArray();
        check(records.Length==12 && records.Select(r=>r.Slot).Distinct().Count()==12 && records.All(r=>r.Slot>=1 && r.Slot<=12),"thirteen saves retain twelve distinct rotating slots");
        ConfirmationOptions.Entry(owner.Configuration,"Action").Value=true;
        AdvConfirmation.Show(AdvWidgets.ReadingFont(TMP_Settings.defaultFontAsset),"Action","隔离测试：先取消此提示，再查看排队的快速读取确认。",()=>{});
        service.QuickLoad();service.QuickLoad();yield return new WaitForSecondsRealtime(.3f);
        check(ButtonShown("ADV.ActionCancel") && !ButtonShown("ADV.QuickLoadConfirm"),"quick-load waits behind existing confirmation");
        RuntimeUiQA.Click("ADV.ActionCancel",check);
        yield return until(()=>ButtonShown("ADV.QuickLoadCancel") && AdvSettingsTransition.Active==null,10,"queued quick-load appears after first prompt closes");
        RuntimeUiQA.Click("ADV.QuickLoadCancel",check);
        yield return until(()=>!AdvConfirmation.IsOpen && !adapter.IsPausedForMenu,10,"queued quick-load cancel releases all pause leases");
        service.QuickLoad();yield return until(()=>ButtonShown("ADV.QuickLoadConfirm"),10,"quick-load can immediately be requested again");yield return null;
        RuntimeUiQA.Click("ADV.QuickLoadConfirm",check);
        yield return until(()=>!AdvConfirmation.IsOpen && !adapter.IsRestoring && !adapter.IsPausedForMenu && adapter.CanCapture(out _),40,"quick-load confirmation restores latest checkpoint");
        owner.ToggleToolbar();yield return null;check(owner.IsFolded,"unlock enables toolbar auto-hide mode");
        var lockIcon=Resources.FindObjectsOfTypeAll<AdvSkinButton>().Single(b=>b.name=="ADV.ToolbarToggle" && b.gameObject.activeInHierarchy);
        var pointer=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
        ExecuteEvents.Execute(lockIcon.gameObject,pointer,ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.Execute(lockIcon.gameObject,pointer,ExecuteEvents.pointerDownHandler);
        var colors=lockIcon.GetComponentsInChildren<Graphic>().Select(g=>g.color).ToArray();
        ExecuteEvents.Execute(lockIcon.gameObject,pointer,ExecuteEvents.pointerExitHandler);
        check(colors.SequenceEqual(lockIcon.GetComponentsInChildren<Graphic>().Select(g=>g.color)),"held toolbar button keeps pressed colors after pointer exit");
        ExecuteEvents.Execute(lockIcon.gameObject,pointer,ExecuteEvents.pointerUpHandler);
        owner.ToggleToolbar();yield return null;check(!owner.IsFolded,"second lock click fixes toolbar again");
        check(!adapter.IsPausedForMenu,"interaction suite leaves no pause lease");
        File.WriteAllText(Path.Combine(root,"results/interaction-success.txt"),"QUICK_SAVE_ROTATION_CONFIRM_QUEUE_CANCEL_RETRY_LOAD_PRESSED_OK");
    }
}
