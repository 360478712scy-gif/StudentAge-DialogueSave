using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using UnityEngine.UI;
using View.Main;
public static class RuntimeNativeModeQA
{
 public static IEnumerator Run(Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
 {
  var owner=AdvDialogueController.Active;
  check(owner.Mode=="Original","startup persisted Original mode");
  UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
  yield return until(()=>UIMgr.GetView<SettingView>().isViewReady,20,"native title settings ready");
  yield return new WaitForSecondsRealtime(1);
  var native=(SettingView)UIMgr.GetView<SettingView>();
  var row=native.line_txtSpeedUp.parent.Find("DialogueSave.ModeSetting");
  yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/original-startup-settings.png"));yield return null;
  check(row!=null,"persisted Original restart exposes style row");
  check(row.gameObject.activeInHierarchy,"native style row is visible");
  RuntimeUiQA.Click("DialogueSave.ModeNext",check);yield return null;
  RuntimeUiQA.Click(native.btn_confirm.gameObject.name,check);
  yield return until(()=>owner.IsAdv && !UIMgr.IsViewOpened<SettingView>(),15,"native setting applies ADV from title");
  UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
  yield return until(()=>AdvSettings.Active!=null && AdvSettingsTransition.Active==null,20,"ADV settings reopen");
  yield return new WaitForSecondsRealtime(1);
  AccessTools.Field(typeof(AdvSettings),"mode").SetValue(AdvSettings.Active,"Original");
  RuntimeUiQA.Click("Settings.Apply",check);
  yield return until(()=>!owner.IsAdv && !UIMgr.IsViewOpened<SettingView>(),15,"ADV applies Original");
  UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
  yield return until(()=>UIMgr.GetView<SettingView>().isViewReady,15,"Original after ADV ready");yield return new WaitForSecondsRealtime(1);
  native=(SettingView)UIMgr.GetView<SettingView>();row=native.line_txtSpeedUp.parent.Find("DialogueSave.ModeSetting");
  check(row!=null && row.gameObject.activeInHierarchy,"switching ADV to Original preserves visible return row");
  RuntimeUiQA.Click("DialogueSave.ModeNext",check);yield return null;
  RuntimeUiQA.Click(native.btn_cancel.gameObject.name,check);yield return new WaitForSecondsRealtime(.8f);
  check(owner.Mode=="Original","native cancel preserves Original");
  File.WriteAllText(Path.Combine(root,"results/native-mode-result.txt"),"NATIVE_STYLE_RETURN_PASS");
 }
}
