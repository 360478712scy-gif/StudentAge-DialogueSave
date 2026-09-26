using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

public static class RuntimeBacklogQA
{
    static AdvBacklog Reader()=>Resources.FindObjectsOfTypeAll<AdvBacklog>().Single(r=>r.gameObject.activeInHierarchy);
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);if(notice!=null&&notice.GetType().Name=="CommonComfirmView")UIMgr.CloseView(notice);
        var adv=AdvDialogueController.Active;var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk.talkState==TalkState.AnimEnd&&adv.History.Entries.Count>0,30,"backlog stable dialogue and checkpoint");
        var native=(List<TalkData>)AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk);int count=native.Count;
        var person=Cfg.PersonCfgMap.Values.First(p=>p.id>0&&p.gender==2);
        try
        {
            for(int i=0;i<40;i++)native.Add(new TalkData{roleName=i%4==0?person.name:"",talkRoleIds=i%4==0?new List<int>{person.id}:null,
                content=i%4==0?"放学后的阳光落在课桌上。\n你还记得那年春天，我们说过的话吗？":i%4==1?"总算回到了教室，窗外的风也安静了下来。":i%4==2?"那时没有说完的故事，似乎还停留在记忆里。":"我翻过笔记本的最后一页，明天又会是新的一天。"});
            var watch=System.Diagnostics.Stopwatch.StartNew();adv.OpenHistory();watch.Stop();
            yield return until(()=>AdvSettingsTransition.Active!=null&&AdvSettingsTransition.Active.Progress>.08f,3,"backlog opening uses settings shutter");
            check(AdvSettingsTransition.Active.GetComponent<Canvas>().sortingOrder>Reader().GetComponent<Canvas>().sortingOrder,"shutter and input guard render above backlog modal");
            yield return Shot(root,"backlog-opening-blinds");
            yield return until(()=>AdvSettingsTransition.Active==null&&!Reader().Opening,3,"shutter completes and releases input");
            yield return new WaitForSecondsRealtime(.5f);
            var reader=Reader();var frame=reader.transform.Find("History layout");
            var color=reader.transform.Find("History fullscreen").GetComponent<Image>().color;
            check(Mathf.Abs(color.a-230f/255)<.001f,"backlog opacity matches extracted source 230/255");
            var overlay=AdvSkin.Texture("backlog-frame.png");
            check(overlay.GetPixel(overlay.width/2,overlay.height/2).a==0,"decorative frame opening is transparent rather than baked black");
            var cfg=Cfg.RoundCfgMap[Singleton<RoundMgr>.Ins.GetRound()];
            string season=Cfg.SeasonCfgMap[cfg.season].name;char c="春夏秋冬".FirstOrDefault(s=>season.Contains(s.ToString()));
            string expected="DATE "+cfg.year+"-"+(c=='\0'?season:c.ToString());
            check(frame.Find("Game date").GetComponent<TextMeshProUGUI>().text==expected,"DATE label reads native year and season");
            check(reader.GetComponentsInChildren<TextMeshProUGUI>().Count(t=>t.name=="Line")<12,"history retains visible-row virtualization");
            check(reader.GetComponentsInChildren<Image>().Any(i=>i.name=="Portrait"&&i.sprite!=null),"historical portrait remains visible at left");
            foreach(var row in reader.GetComponentsInChildren<RectTransform>().Where(r=>r.name.StartsWith("History row ")))
            {
                var line=row.Find("Line").GetComponent<TextMeshProUGUI>();var button=row.GetComponentInChildren<Button>();
                check(!line.isTextOverflowing,"history dialogue fits its row");
                check(-button.GetComponent<RectTransform>().anchoredPosition.y>=-line.rectTransform.anchoredPosition.y+line.rectTransform.sizeDelta.y,"rollback button sits below complete text");
            }
            yield return Shot(root,"backlog-layout");
            RuntimeUiQA.Click("ADV.HistoryTop",check);yield return null;yield return null;
            check(reader.transform.GetComponentsInChildren<RectTransform>().Any(r=>r.name=="History row 0"),"top arrow reaches earliest record");
            RuntimeUiQA.Click("ADV.HistoryPageDown",check);yield return null;
            var scroll=reader.GetComponentInChildren<ScrollRect>();check(scroll.content.anchoredPosition.y>500,"page-down moves one reading page");
            var slider=reader.GetComponentInChildren<Slider>();slider.value=.5f;yield return null;
            check(Math.Abs(slider.handleRect.rect.width-slider.handleRect.rect.height)<.01f,"paper-plane scroll handle stays circular");
            check(Math.Abs(scroll.verticalNormalizedPosition-.5f)<.02f,"paper-plane slider continuously follows history position");
            RuntimeUiQA.Click("ADV.HistoryBottom",check);yield return null;yield return null;
            check(Math.Abs(scroll.verticalNormalizedPosition)<.01f,"bottom arrow reaches newest record");
            RuntimeUiQA.Click("ADV.CloseHistory",check);yield return null;
            check(AdvSettingsTransition.Active!=null,"backlog footer starts shared closing shutter");
            yield return until(()=>!adv.ModalOpen&&!adapter.IsPausedForMenu,4,"footer returns after exit animation without leaked menu pause");
            adv.OpenHistory();yield return until(()=>AdvSettingsTransition.Active==null&&!Reader().Opening,4,"cached backlog reopens");
            check(ReferenceEquals(reader,Reader()),"unchanged log reuses rendered rows and portraits");
            adv.CloseModal();yield return until(()=>!adv.ModalOpen,4,"cached backlog also closes with shutter");
            File.WriteAllText(Path.Combine(root,"results/backlog-open-timing.txt"),"warm_assets_open_ms="+watch.Elapsed.TotalMilliseconds);
        }
        finally{adv.CloseModal();if(native.Count>count)native.RemoveRange(count,native.Count-count);}
        adv.OpenHistory();yield return until(()=>!Reader().Opening&&AdvSettingsTransition.Active==null,3,"second opening finishes");
        var rollback=Reader().GetComponentsInChildren<Button>().First(b=>b.name.StartsWith("ADV.Rollback."));
        RuntimeUiQA.Click(rollback.name,check);yield return null;
        check(adv.ModalOpen&&!adv.History.Restoring,"rollback still asks for confirmation");
        RuntimeUiQA.Click("ADV.JumpConfirm",check);
        yield return until(()=>!adv.ModalOpen&&!adv.History.Restoring&&adapter.CanCapture(out _),40,"native history rollback restores dialogue");
        talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        check(talk.talkState==TalkState.AnimEnd,"return from history stays on a complete manual dialogue");
        yield return Shot(root,"backlog-returned-dialogue");
        File.WriteAllText(Path.Combine(root,"results/backlog-visual-success.txt"),"BACKLOG_LAYOUT_DATE_SHUTTER_SCROLL_ROLLBACK_OK");
    }
    static IEnumerator Shot(string root,string name)
    {yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/"+name+".png"));yield return null;}
}
