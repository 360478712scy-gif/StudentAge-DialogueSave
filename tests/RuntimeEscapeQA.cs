using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using View.Main;
using View.Evt;
public static class RuntimeEscapeQA
{
 static IEnumerator Press(Keyboard keyboard)
 {
  InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Escape));yield return null;yield return null;
  InputSystem.QueueStateEvent(keyboard,new KeyboardState());yield return null;yield return null;
  yield return new WaitForSecondsRealtime(.8f);
 }
 public static IEnumerator Run(DialogueUiController ui,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
 {
  var adv=AdvDialogueController.Active;
  var keyboard=InputSystem.AddDevice<Keyboard>("ADVQAEscapeKeyboard");
  var talk=(NewTalkView)UIMgr.GetView<NewTalkView>(false);
  yield return until(()=>talk.talkState==TalkState.AnimEnd,20,"escape fixture stable");
  try
  {
   foreach(var scope in (AdvArchiveScope[])Enum.GetValues(typeof(AdvArchiveScope)))
   foreach(bool saving in new[]{true,false})
   {
    adv.Preferences.ArchiveScope.Value=scope;ui.Open(saving);
    yield return until(()=>UIMgr.IsViewOpened<SaveView>() && AdvSettingsTransition.Active==null,15,"archive ready");
    yield return new WaitForSecondsRealtime(.5f);
    if(AdvSavePage.Active!=null)
    {
     bool accepted=false,cancelled=false;
     AdvConfirmation.Show(TMP_Settings.defaultFontAsset,"EscapeQA","QA cancel",()=>accepted=true,()=>cancelled=true);
     yield return until(()=>AdvSettingsTransition.Active==null,10,"confirmation settled");
     yield return Press(keyboard);
     check(!AdvConfirmation.IsOpen && cancelled && !accepted && AdvSavePage.Active!=null,"Esc cancels only confirmation: "+scope+" / "+saving);
     AccessTools.Method(typeof(AdvSavePage),"EditNote").Invoke(AdvSavePage.Active,new object[]{1,new DialogueUiRecord{Comment="unchanged"}});
     yield return until(()=>AdvSettingsTransition.Active==null,10,"note editor settled");
     yield return Press(keyboard);
     check(AdvSavePage.Active!=null && !AdvSavePage.Active.EditorOpen,"Esc cancels note without closing archive: "+scope+" / "+saving);
    }
    yield return Press(keyboard);
    check(!UIMgr.IsViewOpened<SaveView>() && AdvSavePage.Active==null,"Esc closes archive: "+scope+" / "+saving);
    check(UIMgr.GetTopView(ViewType.Guide,ViewType.Side)==talk && !global::Game.IsMenuOpened(),"Esc release does not open menu: "+scope+" / "+saving);
   }
   bool originalFont=adv.Preferences.OriginalDialogueFont.Value;
   UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
   yield return until(()=>AdvSettings.Active!=null && AdvSettingsTransition.Active==null && !(bool)AccessTools.Field(typeof(AdvSettings),"transitionBusy").GetValue(AdvSettings.Active),25,"settings ready");
   yield return new WaitForSecondsRealtime(.5f);
   AccessTools.Field(typeof(AdvSettings),"originalDialogueFont").SetValue(AdvSettings.Active,!originalFont);
   yield return Press(keyboard);
   check(AdvSettings.Active==null && !UIMgr.IsViewOpened<SettingView>() && adv.Preferences.OriginalDialogueFont.Value==originalFont,"Esc settings cancels uncommitted draft: active="+(AdvSettings.Active!=null)+" native="+UIMgr.IsViewOpened<SettingView>()+" font="+adv.Preferences.OriginalDialogueFont.Value+" expected="+originalFont);
   check(UIMgr.GetTopView(ViewType.Guide,ViewType.Side)==talk && !global::Game.IsMenuOpened(),"settings Esc release stays in dialogue");
   adv.OpenHistory();yield return new WaitForSecondsRealtime(1);yield return until(()=>AdvSettingsTransition.Active==null,10,"history settled");
   yield return Press(keyboard);
   check(!adv.ModalOpen && UIMgr.GetTopView(ViewType.Guide,ViewType.Side)==talk,"Esc closes backlog without opening menu");
   UIMgr.CloseView<NewTalkView>();yield return new WaitForSecondsRealtime(.8f);
   foreach(var scope in (AdvArchiveScope[])Enum.GetValues(typeof(AdvArchiveScope)))
   foreach(bool saving in new[]{true,false})
   {
    adv.Preferences.ArchiveScope.Value=scope;ui.Open(saving);
    yield return until(()=>UIMgr.IsViewOpened<SaveView>(),15,"outside dialogue archive opens");
    yield return new WaitForSecondsRealtime(1);
    yield return until(()=>AdvSettingsTransition.Active==null,15,"outside dialogue archive settled");
    yield return Press(keyboard);
    check(!UIMgr.IsViewOpened<SaveView>() && AdvSavePage.Active==null,"Esc closes outside-dialogue archive: "+scope+" / "+saving);
   }
   File.WriteAllText(Path.Combine(root,"results/escape-result.txt"),"ESCAPE_ALL_SCOPES_SAVE_LOAD_MODAL_EDITOR_SETTINGS_BACKLOG_PASS");
  }
  finally{InputSystem.RemoveDevice(keyboard);}
 }
}
