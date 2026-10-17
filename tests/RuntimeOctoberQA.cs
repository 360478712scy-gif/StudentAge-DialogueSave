using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Config;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using View.Evt;
using View.Main;
using View.Hint;
public static class RuntimeOctoberQA
{
 static readonly List<string> sounds=new List<string>();
 static void Sound(int _id){sounds.Add(_id+" "+Environment.StackTrace);}
 static void ClickSound(UIButton __instance){sounds.Add("button "+__instance.gameObject.name+" "+Environment.StackTrace);}
 static NewTalkView Talk=>(NewTalkView)UIMgr.GetView<NewTalkView>(false);
 static TalkCfg CfgNow=>Talk==null?null:(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk);
 static void Invoke(string method)=>AccessTools.Method(typeof(AdvDialogueController),method).Invoke(AdvDialogueController.Active,null);
 static bool Settled=>AdvSettingsTransition.Active==null && !AdvConfirmation.IsOpen;
 public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
 {
  var adv=AdvDialogueController.Active;
  yield return until(()=>adapter.CanCapture(out _),25,"October fixture stable");
  var checkpoint=adapter.CaptureHistory();
  AccessTools.Method(typeof(DialogueCheckpointAdapter),"Validate").Invoke(adapter,new object[]{checkpoint.State.Dialogue,false});
  check(checkpoint.State.Dialogue["backgroundSource"].Type==JTokenType.Null,"in-memory absent background is JSON null, not a null string");
  var jump=adapter.RestoreHistoryAsync(checkpoint,System.Threading.CancellationToken.None);
  yield return until(()=>jump.IsCompleted,30,"direct in-memory jump completes");jump.GetAwaiter().GetResult();
  check(CfgNow.id==checkpoint.State.Dialogue.Value<int>("talkId"),"in-memory jump restores same line without disk JSON roundtrip");
  yield return until(()=>adapter.CanCapture(out _),20,"restored fixture stable");
  yield return RuntimeHistoryRollbackQA.Run(adapter,check,until,root);
  // Defaults only affect new entries. Loading an existing explicit preference must preserve it.
  string prefPath=Path.Combine(root,"results/new-preferences.cfg");File.Delete(prefPath);
  var config=new ConfigFile(prefPath,true);var defaults=new AdvPreferences(config);
  check(!defaults.SkipUnread.Value,"new installation defaults to no unread skip");
  defaults.SkipUnread.Value=true;config.Save();
  check(new AdvPreferences(new ConfigFile(prefPath,false)).SkipUnread.Value,"existing unread skip preference survives loading");
  yield return until(()=>adapter.CanCapture(out _),20,"fixture ready for skip checks");
  var source=CfgNow;
  const int first=1900040001,last=1900040005;
  for(int id=first;id<=last;id++)Cfg.TalkCfgMap[id]=new TalkCfg{id=id,bg=source.bg,roleIds=source.roleIds,roleName=source.roleName,content="跳过边界验证 "+id,nextTalk=id==last?null:new List<int>{id+1},option=id==last?new List<int>{1900000001,1900000002}:null};
  bool prior=adv.Preferences.SkipUnread.Value;
  var confirm=ConfirmationOptions.Entry(adv.Configuration,"Skip");bool priorConfirm=confirm.Value;confirm.Value=false;
  var global=AccessTools.Field(typeof(GlobalMgr),"model").GetValue(Singleton<GlobalMgr>.Ins);
  var seen=(List<int>)AccessTools.Field(global.GetType(),"historyTalks").GetValue(global);
  seen.RemoveAll(id=>id>=first && id<=last);
  adv.Preferences.SkipUnread.Value=false;
  yield return until(()=>adapter.CanAdvancePresentation(Talk),15,"presentation accepts fixture refresh");
  Talk.RefreshTalk(first);
  yield return until(()=>CfgNow.id==first && Talk.talkState==TalkState.Anim,20,"unread line starts typing");
  Invoke("RequestSceneSkip");yield return null;
  check(CfgNow.id==first && !adv.ModalOpen && !(bool)AccessTools.Field(typeof(AdvDialogueController),"sceneSkipPending").GetValue(adv),"unread scene skip has no advance, modal or pending skip");
  yield return until(()=>Talk.talkState==TalkState.AnimEnd,20,"first unread line finishes");
  int clicksBefore=AdvSkin.PlayedClicks;
  RuntimeUiQA.Click("ADV.跳过剧情",check);yield return new WaitForSecondsRealtime(.4f);
  check(AdvSkin.PlayedClicks==clicksBefore+1,"blocked unread skip plays exactly one click sound");
  check(CfgNow.id==first,"first-visit completed text does not allow skip to next unread line");
  check(!Singleton<GlobalMgr>.Ins.HasSeenTalk(first+1),"blocked skip does not mark unseen next line read");
  adv.Preferences.SkipUnread.Value=true;
  Invoke("RequestSceneSkip");
  yield return until(()=>CfgNow.id==last && Talk.talkState==TalkState.Option,20,"actual scene skip reaches choices");
  for(int id=first;id<last;id++)check(Singleton<GlobalMgr>.Ins.HasSeenTalk(id),"actually skipped line is read: "+id);
  adv.Preferences.SkipUnread.Value=false;
  yield return until(()=>adapter.CanAdvancePresentation(Talk),15,"presentation accepts fixture refresh");
  Talk.RefreshTalk(first);
  yield return until(()=>Talk.talkState==TalkState.AnimEnd,20,"revisit read line");
  check(AdvReadPolicy.CanSkip(Talk),"read revisit remains skippable");
  adv.Preferences.SkipUnread.Value=prior;confirm.Value=priorConfirm;
  // An ordinary native archive opened from outside dialogue stays native in ADV mode.
  var h=new Harmony("qa.october.sounds");
  h.Patch(AccessTools.Method(typeof(AudioMgrEx),"PlayUISound",new[]{typeof(int),typeof(float)}),prefix:new HarmonyMethod(typeof(RuntimeOctoberQA),nameof(Sound)));
  h.Patch(AccessTools.Method(typeof(UIButton),"PlayClickSound"),prefix:new HarmonyMethod(typeof(RuntimeOctoberQA),nameof(ClickSound)));
  var host=UnityEngine.Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().First();
  var ui=(DialogueUiController)AccessTools.Field(typeof(DialogueRuntimeHost),"ui").GetValue(host);
  // Native confirmations used while an unrelated game page owns the foreground must stay untouched.
  UIMgr.CloseView<NewTalkView>();yield return new WaitForSecondsRealtime(.8f);
  bool accepted=false;
  HintHelper.ShowConfirm("QA unrelated main-screen prompt",()=>accepted=true);
  yield return until(()=>UIMgr.IsViewOpened<CommonComfirmView>(),15,"out-of-scope prompt uses native view");
  check(!AdvConfirmation.IsOpen && !accepted,"out-of-scope prompt is not reskinned or automatically accepted");
  check(UIMgr.GetView<CommonComfirmView>().transform.Find("DialogueSave.Remember")==null,"out-of-scope prompt gets no remember checkbox");
  UIMgr.CloseView<CommonComfirmView>();yield return new WaitForSecondsRealtime(.5f);
  foreach(bool saving in new[]{false,true})
  {
   sounds.Clear();ui.Open(saving);
   yield return until(()=>UIMgr.IsViewOpened<SaveView>(),20,"non-dialogue native archive opens");
   yield return new WaitForSecondsRealtime(1f);
   var save=(SaveView)UIMgr.GetView<SaveView>();
   check(AdvSavePage.Active==null && save.isSaveMode==saving,"non-dialogue save/load routes to correct native page");
   int beforePage=(int)AccessTools.Field(typeof(SaveView),"page").GetValue(save);
   string buttonName=save.btn_next.gameObject.name;save.btn_next.gameObject.name="QA.NativeNext";
   RuntimeUiQA.Click("QA.NativeNext",check);save.btn_next.gameObject.name=buttonName;
   check((int)AccessTools.Field(typeof(SaveView),"page").GetValue(save)==beforePage+1,"native archive responds to actual page button click");
   File.WriteAllLines(Path.Combine(root,"results/october-native-sounds-"+saving+".txt"),sounds);
   UIMgr.CloseView<SaveView>();yield return new WaitForSecondsRealtime(.6f);
  }
  h.UnpatchSelf();
  // Re-open dialogue to validate scoped prompts and explicit native escape from ADV archive.
  Singleton<CommonEvtMgr>.Ins.EnqueueEvt(0,1);Singleton<CommonEvtMgr>.Ins.ShowNewRoundEvent();
  yield return until(()=>adapter.CanCapture(out _),20,"new dialogue context ready");
  ui.Open(false);yield return until(()=>AdvSavePage.Active!=null && Settled,15,"dialogue still opens ADV archive");
  // Hold the same native card while header completions arrive. Its identity must
  // survive, including when background metadata is refreshed mid-pointer sequence.
  var page=AdvSavePage.Active;
  AccessTools.Method(typeof(AdvSavePage),"Switch").Invoke(page,new object[]{3});
  yield return until(()=>Settled,15,"native category transition settles");
  var card=page.transform.Find("Archive grid/Archive.Slot1");
  if(card==null)card=page.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b=>b.name=="Archive.Slot1").transform;
  int cardId=card.gameObject.GetInstanceID();
  var native=(NativeArchiveBrowser)AccessTools.Field(typeof(AdvSavePage),"native").GetValue(page);
  for(int i=0;i<5;i++){native.Changed?.Invoke();yield return null;check(card!=null && card.gameObject.GetInstanceID()==cardId && card.gameObject.activeInHierarchy,"native header refresh retains interactive card "+i);}
  HintHelper.ShowConfirm("QA archive scoped prompt",()=>{});
  yield return until(()=>AdvConfirmation.IsOpen,10,"archive prompt uses ADV style");
  yield return until(()=>AdvSettingsTransition.Active==null,10,"confirmation opening settles");
  AdvConfirmation.Cancel();yield return until(()=>Settled,10,"confirmation closes");
  DialogueUiController.OpenOriginalArchive((SaveView)UIMgr.GetView<SaveView>());
  yield return new WaitForSecondsRealtime(1f);
  check(AdvSavePage.Active==null,"explicit original archive remains native inside dialogue");
  UIMgr.CloseView<SaveView>();yield return until(()=>Settled && !adapter.IsPausedForMenu,15,"archive close releases all pauses");
  File.WriteAllText(Path.Combine(root,"results/october.txt"),"HISTORY_SKIP_ROUTING_SCOPE_OK");
 }
}
