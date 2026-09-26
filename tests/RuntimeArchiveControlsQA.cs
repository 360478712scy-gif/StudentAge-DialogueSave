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
using UnityEngine.EventSystems;
using UnityEngine.UI;
using View.Main;
using View.Evt;

public static class RuntimeArchiveControlsQA
{
    static object Field(string n)=>AccessTools.Field(typeof(AdvSavePage),n).GetValue(AdvSavePage.Active);
    static bool Idle()=>AdvSavePage.Active!=null && AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1 && AdvSettingsTransition.Active==null && !(bool)Field("busy") && !AdvConfirmation.IsOpen;
    static void Click(string n,Action<bool,string> check)=>RuntimeUiQA.Click(n,check);
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/controls-"+name+".png"));yield return null;}
    static IEnumerator Wheel(float delta,Action<bool,string> check)
    {
        yield return new WaitForSecondsRealtime(.2f);
        var cards=AdvSavePage.Active.GetComponentsInChildren<Button>().Where(b=>b.name.StartsWith("Archive.Slot")).ToArray();
        var report=RuntimeUiQA.PointerReport(cards[0].gameObject);var point=report["point"];
        var ev=new PointerEventData(EventSystem.current){position=new Vector2((float)point[0],(float)point[1]),scrollDelta=new Vector2(0,delta)};
        var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(ev,hits);check(hits.Count>0,"wheel raycast finds archive");
        ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,ev,ExecuteEvents.scrollHandler);yield return null;
        check(AdvSavePage.Active.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Archive.Slot"))==12,"wheel leaves 12 full cards");
    }
    static IEnumerator SaveAt(int slot,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until)
    {
        ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"SaveEmpty").Value=true;
        Click("Archive.Slot"+slot,check);yield return until(()=>AdvConfirmation.IsOpen,5,"save confirmation");yield return null;
        Click("ADV.SaveEmptyConfirm",check);yield return until(()=>Idle()&&!service.IsListing,30,"fixture persisted");
    }
    internal static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _),25,"controls dialogue ready");
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        var name=AdvDialogueController.Active.GetType();
        var speaker=(TextMeshProUGUI)AccessTools.Field(name,"nameLabel").GetValue(AdvDialogueController.Active);
        check(speaker.fontStyle==FontStyles.Normal,"name and brackets are not bold");
        check(adapter.CurrentCheckpointBrief.Speaker.Contains("肖清雅"),"fixture actual speaker Xiao Qingya");
        ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,25,"archive ready");
        check(AdvSavePage.Active.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Archive.Slot"))==12,"four by three cards");
        Click("Archive.First",check);yield return Wheel(-100,check);check((int)Field("page")==2,"large wheel delta advances one page");
        yield return Wheel(1,check);check((int)Field("page")==1,"wheel up returns one page");
        Click("Archive.PageNext",check);yield return null;Click("Archive.Last",check);yield return null;
        int slot=((int)Field("page")-1)*12+1;yield return SaveAt(slot,service,check,until);yield return null;
        var card=AdvSavePage.Active.transform.Find("Archive cards/Archive.Slot"+slot);var portrait=card.Find("Speaker chibi").GetComponent<Image>();
        int id=Cfg.PersonCfgMap.Values.Single(p=>p.name=="肖清雅").id;Sprite expected=null;
        AtlasMgr.GetSpriteAsync(Cfg.PersonCfgMap[id].GetComicIcon(true,false),s=>expected=s);
        yield return until(()=>portrait.sprite!=null&&expected!=null,15,"speaker Q portrait loaded");
        check(portrait.sprite.texture==expected.texture && portrait.sprite.textureRect==expected.textureRect,"saved Xiao Qingya uses her Q portrait not protagonist");
        var all=AdvSavePage.Active.GetComponentsInChildren<Button>().Where(b=>b.name.StartsWith("Archive.Slot")).Select(b=>(RectTransform)b.transform).ToArray();
        check(all.All(r=>-r.anchoredPosition.y+r.rect.height<=756),"all card bottoms inside grid");
        var ticket=AdvSavePage.Active.transform.Find("Archive.Return").GetComponent<Image>();
        check(ticket.type==Image.Type.Sliced && ticket.sprite.border.x>0,"ticket keeps plane and endcap unsqueezed");
        check(Mathf.Abs(ticket.pixelsPerUnitMultiplier-ticket.sprite.rect.height/ticket.rectTransform.rect.height)<.001f,"ticket endcap horizontal scale matches vertical scale");
        yield return Shot(root,"12-speaker");
        Click("Archive.Tab3",check);yield return until(Idle,10,"native category");
        check(AdvSavePage.Active.transform.Find("Archive.Tool1").GetComponentInChildren<TextMeshProUGUI>().text=="原版界面","short original label");
        Click("Archive.Tool1",check);yield return until(()=>AdvSavePage.Active==null,5,"native page opens");yield return new WaitForSecondsRealtime(.7f);
        var native=(SaveView)UIMgr.GetView<SaveView>();check(native.itemgroup_save.gameObject.activeInHierarchy,"original nine-slot grid visible");
        check(AdvSavePage.Active==null,"reconciliation does not recapture original page");yield return Shot(root,"original");
        native.CloseView();yield return until(()=>!UIMgr.IsViewOpened<SaveView>(),10,"original closes");
        // Advance only the isolated two-line fixture to its authored choice state.
        yield return until(()=>adapter.CanAdvancePresentation(talk)&&talk.talkState==TalkState.AnimEnd,15,"native close releases dialogue input");
        talk.OnClickNext();
        yield return until(()=>talk.talkState==TalkState.Option&&adapter.CanCapture(out _),25,"choice fixture ready");
        string choices=DialogueCheckpointAdapter.ChoiceSummary(talk);check(choices=="选择项：继续第一条分支/继续第二条分支","actual option labels summarized");
        ui.Open(true);yield return until(()=>Idle()&&!service.IsListing,20,"ADV returns after native close");
        Click("Archive.Last",check);yield return null;int next=((int)Field("page")-1)*12+2;
        yield return SaveAt(next,service,check,until);
        var slots=(Dictionary<int,DialogueUiRecord>)Field("slots");check(slots[next].Summary==choices,"choice save list preserves summary");
        yield return Shot(root,"choice");Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&AdvSettingsTransition.Active==null,10,"archive closes");
        talk.CloseView();yield return null;
        Singleton<GlobalMgr>.Ins.isStartScreenShowed=true;
        UIMgr.OpenView<EntryView>(UILayerType.None,null,new object[]{false,true});
        yield return until(()=>UIMgr.GetView<EntryView>(false)?.isViewReady==true,15,"title entry ready");yield return new WaitForSecondsRealtime(1);
        var entry=(EntryView)UIMgr.GetView<EntryView>();check(!entry.btn_save.gameObject.activeInHierarchy,"title has no save entry");
        ExecuteEvents.Execute(entry.btn_load.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);yield return until(Idle,20,"title load entry opens archive");
        check(!AdvSavePage.Active.transform.Find("Archive.Tab0").GetComponent<Button>().interactable,"title save category disabled");
        RuntimeUiQA.Click("Archive.Tab1",check);yield return null;check(AdvSettingsTransition.Active==null,"clicking current load tab has no transition");
        yield return Shot(root,"title-archive");
        File.WriteAllText(Path.Combine(root,"results/archive-controls-success.txt"),"12_FULL_SLOTS_WHEEL_SPEAKER_CHOICES_NATIVE_ESCAPE_TITLE_ENTRY_OK");
    }
}
