using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;

// Focused end-to-end regression for the September 21 reports.
public static class RuntimeFeedbackQA
{
    static NewTalkView Talk=>(NewTalkView)UIMgr.GetView<NewTalkView>(false);
    static int Id=>((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk)).id;
    static readonly System.Reflection.FieldInfo Auto=AccessTools.Field(typeof(NewTalkView),"enableAutoTalk");
    static Button Button(string name)=>Resources.FindObjectsOfTypeAll<Button>().Single(b=>b.name==name&&b.gameObject.activeInHierarchy);
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetView<View.Hint.CommonComfirmView>(false);
        if(notice!=null && notice.GetType().Name=="CommonComfirmView")UIMgr.CloseView(notice);
        yield return until(()=>adapter.CanCapture(out _)&&Talk.talkState==TalkState.AnimEnd,25,"feedback fixture stable");
        var adv=AdvDialogueController.Active;var history=adv.History;var baseline=adapter.Capture();
        Cfg.OptionCfgMap[1900000001].content="<color=#ff78cb>我好像……明白你的意思了。</color>";
        Cfg.OptionCfgMap[1900000002].content="我也把你当最好的朋友啊。";
        Cfg.OptionCfgMap[1900000001].precondition=new List<List<double>>{new List<double>{4,1,101,-999999}};
        Cfg.OptionCfgMap[1900000002].precondition=new List<List<double>>{new List<double>{4,1,101,999999},new List<double>{4,1,102,-999999}};
        Talk.NextTalk();
        yield return until(()=>Talk.talkState==TalkState.Option&&adapter.CanCapture(out _)&&Resources.FindObjectsOfTypeAll<Button>().Any(b=>b.name=="ADV.Choice.0"&&b.gameObject.activeInHierarchy),25,"formatted options visible");
        yield return null;
        var pink=Button("ADV.Choice.0").GetComponentInChildren<TextMeshProUGUI>();
        var plain=Button("ADV.Choice.1").GetComponentInChildren<TextMeshProUGUI>();
        pink.ForceMeshUpdate();plain.ForceMeshUpdate();
        check(pink.GetParsedText()=="我好像……明白你的意思了。","color tags are parsed, not displayed as text");
        check(plain.GetParsedText()=="我也把你当最好的朋友啊。","plain option remains unchanged");
        var colors=pink.textInfo.characterInfo.Take(pink.textInfo.characterCount).Where(c=>c.isVisible).Select(c=>c.color).ToArray();
        check(colors.Length>0&&colors.All(c=>c.r==255&&c.g==120&&c.b==203),"rendered option glyphs retain author color #ff78cb");
        check(pink.textInfo.lineCount==1&&!pink.isTextOverflowing,"formatted caption fits without tag-induced overflow");
        var locked=Button("ADV.Choice.1");
        var native=(GenUI.Common.Cell_CommonOptionItemUI)Talk.itemgroup_options.GetCells()[1];
        check(!native.btn_click.interactable&&!locked.interactable,"unmet native attribute condition disables ADV option");
        check(!plain.overrideColorTags&&plain.color==pink.color,"disabled choice preserves original caption color");
        check(locked.colors.disabledColor!=locked.colors.normalColor,"disabled box stays gray before hover");
        check(!UIMgr.IsViewOpened<View.Common.DescriptionView>(),"conditions are hidden until hover");
        locked.onClick.Invoke();yield return null;
        check(Id==1900000002&&Talk.talkState==TalkState.Option,"disabled option cannot invoke its branch even through direct callback");
        var desc=locked.GetComponent<Components.Description>();
        var expected=EvtDescHelper.Option2(0,(CommonEvtOptionData)native.data);
        check(desc!=null&&expected.HasValue&&desc.getDescription(0)?.title==expected.Value.title,"hover reuses the exact native failure reason");
        check(expected.Value.title.Contains("999999")&&expected.Value.title.Contains("-999999"),"unavailable choice tooltip includes both unmet and fulfilled conditions");
        UnityEngine.EventSystems.ExecuteEvents.Execute(locked.gameObject,new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current),UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
        yield return until(()=>UIMgr.IsViewOpened<View.Common.DescriptionView>(),10,"disabled option hover shows native condition tooltip");
        yield return new WaitForSecondsRealtime(.6f);
        check(locked.colors.disabledColor.r==locked.colors.disabledColor.g&&locked.colors.disabledColor.g==locked.colors.disabledColor.b&&locked.colors.disabledColor!=locked.colors.normalColor,"disabled box remains gray during hover");
        check(!plain.overrideColorTags&&plain.color==pink.color,"hover does not gray caption");
        var tooltip=(View.Common.DescriptionView)UIMgr.GetView<View.Common.DescriptionView>();
        check((bool)AccessTools.Field(typeof(View.Common.DescriptionView),"isResizeFinish").GetValue(tooltip),"native hover tooltip finished layout before screenshot");
        tooltip.SetPos(new Vector2(250,80));
        check(tooltip.gameObject.GetComponentInParent<Canvas>().renderMode==RenderMode.ScreenSpaceOverlay&&tooltip.gameObject.GetComponentInParent<Canvas>().sortingOrder>locked.GetComponentInParent<Canvas>().sortingOrder,"condition tooltip renders above all ADV choices");
        File.WriteAllLines(Path.Combine(root,"results/feedback-tooltip-canvas.txt"),tooltip.gameObject.GetComponentsInParent<Canvas>(true).Select(c=>c.name+" "+c.sortingOrder+" "+c.overrideSorting+" "+c.renderMode));
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/feedback-rich-options.png"));yield return null;
        UnityEngine.EventSystems.ExecuteEvents.Execute(locked.gameObject,new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current),UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler);
        yield return until(()=>!UIMgr.IsViewOpened<View.Common.DescriptionView>(),10,"condition tooltip closes on pointer exit");
        yield return null;yield return null;
        check(tooltip.gameObject.GetComponentInParent<Canvas>().renderMode==RenderMode.ScreenSpaceCamera,"native tooltip parent is restored after hover ends");
        check(locked.colors.disabledColor!=locked.colors.normalColor,"disabled box stays gray after pointer exit");
        var unlocked=Button("ADV.Choice.0");
        var unlockedDesc=unlocked.GetComponent<Components.Description>();
        check(unlocked.interactable&&unlockedDesc.getDescription(0).HasValue,"available option also exposes fulfilled condition");
        UnityEngine.EventSystems.ExecuteEvents.Execute(unlocked.gameObject,new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current),UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
        yield return until(()=>UIMgr.IsViewOpened<View.Common.DescriptionView>(),10,"available option fulfilled condition appears on hover");
        UnityEngine.EventSystems.ExecuteEvents.Execute(unlocked.gameObject,new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current),UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler);
        yield return until(()=>!UIMgr.IsViewOpened<View.Common.DescriptionView>(),10,"available option condition hides on exit");
        // Native refresh can change eligibility without changing the cell or option id.
        ((CommonEvtOptionData)native.data).conditioner=null;
        View.Common.CommonOptionItem.OnCellRender(native);yield return null;yield return null;
        check(locked.interactable&&!plain.overrideColorTags,"same native cell becoming available restores caption and click state");
        ((CommonEvtOptionData)native.data).conditioner=CommonEvtMgr.GenConditioner(Cfg.OptionCfgMap[1900000002].precondition);
        View.Common.CommonOptionItem.OnCellRender(native);yield return null;yield return null;
        check(!locked.interactable,"same native cell becoming unavailable remains blocked");
        Button("ADV.Choice.0").onClick.Invoke();
        yield return until(()=>Id==1900000003&&adapter.CanCapture(out _),20,"colored option still invokes the native branch once");
        var restore=adapter.RestoreAsync(baseline);yield return until(()=>restore.IsCompleted,30,"return to playback fixture");restore.GetAwaiter().GetResult();
        yield return until(()=>adapter.CanAdvancePresentation(Talk),15,"playback fixture accepts input");
        foreach(var mode in new[]{new {Auto=true,Scale=1f},new {Auto=false,Scale=4f},new {Auto=true,Scale=4f}})
        {
            // Capture real native playback flags under a menu lease, then jump using
            // the same backlog button and confirmation path as the player.
            Auto.SetValue(Talk,mode.Auto);global::Game.TimeChange(mode.Scale);
            HistoryCheckpoint point;
            using(adapter.PauseForMenu())
            {
                point=adapter.CaptureHistory();adv.OpenHistory();
            }
            check(point.State.Dialogue["playback"].Value<bool>("auto")==mode.Auto&&point.State.Dialogue["playback"].Value<float>("timeScale")==mode.Scale,"fixture records original auto/fast playback");
            int index=history.Entries.FindLastIndex(e=>e.TalkId==1900000001&&!e.IsOption);
            check(index>=0,"first dialogue has a usable history entry");
            history.Entries[index].Checkpoint=point;
            yield return null;
            Button("ADV.Rollback."+index).onClick.Invoke();yield return null;
            Button("ADV.JumpConfirm").onClick.Invoke();
            yield return until(()=>!history.Restoring&&!adv.ModalOpen&&adapter.CanAdvancePresentation(Talk),30,"history jump completed through confirmation");
            yield return new WaitForSecondsRealtime(2);
            check(Id==1900000001&&Talk.talkState==TalkState.AnimEnd,"history return stays on requested line without advancing");
            check(!(bool)Auto.GetValue(Talk)&&Math.Abs(Time.timeScale-1f)<.001f&&Talk.btn_click.interactable,"history return is normal speed, manual and clickable");
            check(point.State.Dialogue["playback"].Value<bool>("auto")==mode.Auto&&point.State.Dialogue["playback"].Value<float>("timeScale")==mode.Scale,"history checkpoint playback metadata was not mutated");
            check(!global::Game.IsMenuOpened(),"history return does not open the ESC menu");
        }
        // Closing history without jumping must still resume the user's current mode.
        Auto.SetValue(Talk,true);adv.OpenHistory();adv.CloseModal();
        yield return until(()=>adapter.CanAdvancePresentation(Talk),10,"history close resumes current playback lease");
        check((bool)Auto.GetValue(Talk),"closing without rollback preserves auto playback");
        Talk.AutoTalk(false);Singleton<TimerMgr>.Ins.Remove(Talk.OnClickNext);global::Game.TimeChange(1f);
        restore=adapter.RestoreAsync(baseline);yield return until(()=>restore.IsCompleted,30,"restore baseline for effect regression");restore.GetAwaiter().GetResult();
        yield return until(()=>adapter.CanCapture(out _)&&adapter.CanAdvancePresentation(Talk),15,"baseline ready for effect regression");
        yield return RuntimeHistoryRollbackQA.Run(adapter,check,until,root);
        File.WriteAllText(Path.Combine(root,"results/feedback-fixes.txt"),"Rich choice rendering, native selection, auto/fast history return, cancel/close behavior and state-effect rollback passed.");
    }
}
