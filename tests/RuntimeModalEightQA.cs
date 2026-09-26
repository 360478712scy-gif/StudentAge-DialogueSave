using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Main;

public static class RuntimeModalEightQA
{
    static object Field(object obj,string key)=>AccessTools.Field(obj.GetType(),key).GetValue(obj);
    static bool Idle()=>AdvSettingsTransition.Active==null;
    static void Click(string name,Action<bool,string> check)=>RuntimeUiQA.Click(name,check);
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/modal-"+name+".png"));yield return null;}
    internal static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _),25,"modal batch dialogue ready");
        var adv=AdvDialogueController.Active;adv.SelectMode("ADV");yield return null;var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        check(!Resources.FindObjectsOfTypeAll<TextMeshProUGUI>().Any(t=>t.name=="Toolbar hint" && t.gameObject.activeInHierarchy),"toolbar black hover caption removed");
        var baseline=adapter.Capture();var cfg=(TalkCfg)Field(talk,"cfg");
        int cgId=Cfg.CGCfgMap.Keys.First(id=>id>0 && !(Cfg.CGCfgMap[id].comic?.Count>0));
        var oldEffect=cfg.screenEffect;cfg.screenEffect=new List<float>{4015,cgId};
        bool opened=false;
        AccessTools.Method(typeof(NewTalkView),"ShowCG").Invoke(talk,new object[]{cgId,(Action)(()=>{opened=true;talk.talk="CG 保存和恢复使用当前 CG 图像。";talk.tmpTalks=new List<string>{talk.talk};talk.tmpTalkIdx=0;talk.DoText(talk.talk);})});
        yield return until(()=>opened && talk.talkState==TalkState.AnimEnd && adapter.CanCapture(out _),30,"CG frame capturable: "+adapter.Reason);
        var captured=adapter.Capture();check((int)captured.Dialogue["cg"]["id"]==cgId,"captured CG identity");
        string url=(string)captured.Dialogue["cg"]["url"];
        ui.Open(true);yield return until(()=>AdvSavePage.Active!=null&&Idle()&&!service.IsListing,20,"CG save page ready");
        bool saved=false;UiResult savedResult=null;
        int saveSlot=Math.Max(911,service.ArchiveList(DialogueUiCategory.Manual).Select(r=>r.Slot).DefaultIfEmpty(0).Max()+1);
        service.ArchiveSave(saveSlot,DialogueUiCategory.Manual,service.ArchiveRun,result=>{if(!result.IsPending){saved=true;savedResult=result;}});
        yield return until(()=>saved,30,"CG archive written");check(savedResult.Success,savedResult.Message);
        yield return until(()=>!service.IsListing,20,"CG archive indexed");
        var row=service.ArchiveList(DialogueUiCategory.Manual).First(r=>r.Slot==saveSlot);check(row.PreviewImageUrl==url,"CG preview survives save header and list projection");
        Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&Idle(),10,"save page closed before restoration");
        var restore=adapter.RestoreAsync(captured);yield return until(()=>restore.IsCompleted,35,"CG checkpoint restored");if(restore.IsFaulted)throw restore.Exception;
        talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>adv.IsCgPresentation && adapter.CanCapture(out _),25,"restored CG remains capturable");
        check((bool)Field(talk,"isShowingCG"),"CG presentation restored");
        var text=(TextMeshProUGUI)AccessTools.Method(typeof(NewTalkView),"GetTalkTxt").Invoke(talk,null);
        check(text.text=="CG 保存和恢复使用当前 CG 图像。","CG text restored without advancing");
        check((string)adapter.Capture().Dialogue["cg"]["url"]==url,"CG resource preserved after restore");
        ui.Open(false);yield return until(()=>AdvSavePage.Active!=null&&Idle()&&!service.IsListing,20,"CG archive opens");
        check(!((SaveView)UIMgr.GetView<SaveView>()).isShowLoading,"ADV archive entrance suppresses native loading spinner");
        var page=AdvSavePage.Active;AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(page,new object[]{76});yield return null;
        var slots=(Dictionary<int,DialogueUiRecord>)Field(page,"slots");int display=slots.Single(p=>p.Value.RevisionId==row.RevisionId).Key;
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(page,new object[]{(display-1)/12+1});yield return null;
        var photo=page.transform.Find("Archive cards/Archive.Slot"+display+"/Saved background").GetComponent<Image>();
        yield return until(()=>photo.sprite!=null,20,"CG archive photo loaded");check((string)Field(photo.GetComponent<ArchivePreview>(),"current")==url,"card uses CG asset instead of scene background");
        yield return Shot(root,"cg-archive");
        Click("Archive.Tool2",check);yield return null;Click("Archive.Slot"+display,check);yield return until(Idle,10,"note opening horizontal blinds");yield return null;
        var input=Resources.FindObjectsOfTypeAll<TMP_InputField>().First(f=>f.gameObject.activeInHierarchy);
        yield return until(()=>input.isFocused,5,"note input focused");check(input.customCaretColor&&input.caretWidth>=2&&input.caretColor.a==1,"visible note caret configured");
        yield return until(()=>input.transform.Find("Caret")!=null,5,"TMP generated caret renderer");
        var caret=input.transform.Find("Caret").GetComponent<CanvasRenderer>();
        yield return until(()=>{var mesh=(Mesh)Field(input,"m_Mesh");return mesh!=null&&mesh.vertexCount>=4&&!caret.cull;},5,"caret has visible geometry");
        yield return Shot(root,"note-caret");Click("Note.Cancel",check);
        check(AdvSettingsTransition.Active.GetComponentsInChildren<SettingsSlats>().All(s=>s.Horizontal),"note closes with horizontal blinds");yield return until(Idle,10,"note closes");
        Click("Archive.Tab3",check);yield return until(Idle,10,"original archive tab");
        Click("Archive.Tool3",check);Click("Archive.Tool0",check);yield return null;
        check((bool)Field(page,"nativeAutomatic")&&(int)Field(page,"operation")==3,"automatic filter coexists with delete mode");
        var native=(NativeArchiveBrowser)Field(page,"native");var positions=(Dictionary<int,int>)Field(page,"nativePositions");
        check(positions.Values.All(id=>native.Slots[id].isAuto),"auto filter excludes manual and quick saves");
        Click("Archive.Tool0",check);yield return null;positions=(Dictionary<int,int>)Field(page,"nativePositions");check(positions.Values.All(id=>native.Slots[id].isManual),"manual filter excludes automatic and quick saves");
        Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&Idle(),10,"archive closes");
        ConfirmationOptions.Entry(adv.Configuration,"Action").Value=true;
        bool canceled=false;AdvConfirmation.Show(TMP_Settings.defaultFontAsset,"Action","弹窗测试",()=>{},()=>canceled=true);
        check(AdvSettingsTransition.Active.GetComponentsInChildren<SettingsSlats>().All(s=>s.Horizontal),"confirmation opens horizontally");yield return until(Idle,10,"confirmation opened");
        AdvConfirmation.Cancel();check(AdvSettingsTransition.Active.GetComponentsInChildren<SettingsSlats>().All(s=>s.Horizontal),"confirmation closes horizontally");yield return until(()=>Idle()&&!AdvConfirmation.IsOpen,10,"confirmation dismissed");check(canceled,"cancel action delivered once");
        cfg.screenEffect=oldEffect;restore=adapter.RestoreAsync(baseline);yield return until(()=>restore.IsCompleted,35,"return to normal snapshot");if(restore.IsFaulted)throw restore.Exception;
        check(!(bool)Field(UIMgr.GetView<NewTalkView>(),"isShowingCG"),"normal snapshot has no leftover CG");
        File.WriteAllText(Path.Combine(root,"results/modal-eight-success.txt"),"CG_SAVE_RESTORE_PREVIEW_MODAL_INPUT_FILTER_SPINNER_OK");
    }
}
