using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;
using View.Main;
using View.Evt;
public static class RuntimeLifecycleQA
{
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var owner=AdvDialogueController.Active;
        yield return until(()=>adapter.CanCapture(out _),20,"unload fixture ready");
        owner.OpenHistory();yield return until(()=>AdvSettingsTransition.Active==null && owner.ModalOpen,10,"unload fixture history built");
        yield return new WaitForSecondsRealtime(.5f);owner.CloseModal();yield return until(()=>!owner.ModalOpen && AdvSettingsTransition.Active==null,10,"unload fixture cached history closed");
        ui.Open(true);yield return until(()=>AdvSavePage.Active!=null,15,"unload fixture archive opens");yield return new WaitForSecondsRealtime(.6f);
        var native=(SaveView)UIMgr.GetView<SaveView>();
        int deferredYes=0;ConfirmationOptions.Entry(owner.Configuration,"Action").Value=true;
        AdvConfirmation.Show(AdvWidgets.ReadingFont(TMPro.TMP_Settings.defaultFontAsset),"Action","隔离测试清理确认窗口。",()=>deferredYes++);
        yield return until(()=>AdvSettingsTransition.Active==null,10,"unload confirmation opened");
        RuntimeUiQA.Click("ADV.ActionConfirm",check);check(deferredYes==0,"unload fixture has deferred confirmation");
        var host=Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().Single();UnityEngine.Object.Destroy(host.gameObject);
        yield return null;yield return null;yield return new WaitForSecondsRealtime(.5f);
        check(deferredYes==0,"unload revokes pending confirmed action");
        check(AdvDialogueController.Active==null && AdvSavePage.Active==null && AdvSettings.Active==null && !AdvConfirmation.IsOpen,"unload clears all active owners");
        check(!Resources.FindObjectsOfTypeAll<Canvas>().Any(c=>c.gameObject.activeInHierarchy && (c.name.StartsWith("DialogueSave.") || c.name=="Settings diagonal transition")),"unload leaves no plugin canvas or input blocker");
        check(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m).Owners.Contains(DialogueSavePlugin.Id)),"unload removes only owned patches");
        check(native.gameObject.activeInHierarchy && native.transform.Cast<Transform>().Any(t=>t.gameObject.activeSelf),"unload restores original save window children");
        UIMgr.CloseView<SaveView>();yield return new WaitForSecondsRealtime(.4f);
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        check(talk.txtex_content!=null && talk.txtex_content.gameObject.activeInHierarchy && talk.txtex_content.font!=null && talk.txtex_content.fontSharedMaterial!=null,"unload returns live native dialogue font and text");
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/unload-native.png"));
        File.WriteAllText(Path.Combine(root,"results/lifecycle-success.txt"),"UNLOAD_RESTORES_NATIVE_UI_AND_REMOVES_MOD_OWNERS_PATCHES_OK");
    }
}
