using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Config;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

// Probe: for sampled events of every subscribed story mod, which stable lines refuse a save and why,
// plus one real quick save + load per event with timings. Records IDs, reasons and milliseconds only.
internal static class RuntimeModSweepQA
{
    static TalkCfg Current(NewTalkView t){try{return t==null?null:(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(t);}catch{return null;}}
    static NewTalkView Talk(){try{var v=UIMgr.GetView<NewTalkView>(false) as NewTalkView;return v!=null && v.gameObject!=null && v.gameObject.activeInHierarchy?v:null;}catch{return null;}}
    static string Error(Exception ex){var b=ex.GetBaseException();return b.GetType().Name+": "+b.Message.Replace('\t',' ').Replace('\n',' ');}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var plan=JArray.Parse(File.ReadAllText(Path.Combine(root,"mod-sweep-plan.json")));
        string output=Path.Combine(root,"results/mod-sweep.tsv");
        var rows=new List<string>{"mod\tevent\tline\tcommand\tresult\tdetail"};
        rows.Add("#active="+string.Join(",",Singleton<ModCtrl>.Ins.activeMods)+" plugins="+string.Join(",",Chainloader.PluginInfos.Keys));
        var quickConfirm=ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"QuickSave");bool previousConfirm=quickConfirm.Value;quickConfirm.Value=false;
        foreach(JObject mod in plan)
        foreach(JObject ev in (JArray)mod["events"])
        {
            string modId=(string)mod["mod"];int id=(int)ev["id"];
            Action<int,string,string,string> add=(line,command,result,detail)=>rows.Add(modId+"\t"+id+"\t"+line+"\t"+command+"\t"+result+"\t"+detail);
            File.WriteAllLines(output,rows);
            if(!Cfg.EvtCfgMap.ContainsKey(id)){add(0,"0","NOT_LOADED","");continue;}
            try
            {
                UIMgr.CloseAllView();
                var model=(CommonEvtModel)AccessTools.Field(typeof(CommonEvtMgr),"model").GetValue(Singleton<CommonEvtMgr>.Ins);model.evtQueues.Clear();
                ((Queue<int>)AccessTools.Field(typeof(CommonEvtMgr),"roundEndEventQueue").GetValue(Singleton<CommonEvtMgr>.Ins)).Clear();
                UIMgr.OpenView<MainView>();UIMgr.OpenView<TopView>();
            }
            catch(Exception ex){add(0,"0","RESET_FAILED",Error(ex));}
            yield return new WaitForSecondsRealtime(1f);
            try{Singleton<CommonEvtMgr>.Ins.ShowEvent(id);}catch(Exception ex){add(0,"0","SHOW_FAILED",Error(ex));continue;}
            float opened=Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup-opened<12 && Talk()==null){yield return Popups();yield return null;}
            if(Talk()==null){add(0,"0","NO_DIALOGUE","");continue;}
            int lines=0,ok=0,refused=0,previous=0;float lastChange=Time.realtimeSinceStartup;bool roundTrip=false;var probed=new HashSet<string>();
            while(lines<=40)
            {
                yield return Popups();
                var talk=Talk();if(talk==null)break;
                var cfg=Current(talk);if(cfg==null){yield return null;continue;}
                string command=cfg.screenEffect!=null && cfg.screenEffect.Count>0?((int)cfg.screenEffect[0]).ToString():"0";
                if(cfg.id!=previous){previous=cfg.id;lastChange=Time.realtimeSinceStartup;lines++;}
                if(Time.realtimeSinceStartup-lastChange>15){add(cfg.id,command,"STALL","state="+talk.talkState);break;}
                bool stable=talk.talkState==TalkState.AnimEnd || talk.talkState==TalkState.Option;
                if(stable && probed.Add(cfg.id+":"+talk.tmpTalkIdx))
                {
                    string why=null;bool can=false;
                    try{can=adapter.CanCapture(out why);}catch(Exception ex){why="CanCapture threw "+Error(ex);}
                    float waited=Time.realtimeSinceStartup;
                    while(!can && Time.realtimeSinceStartup-waited<2.5f)
                    {
                        yield return null;
                        try{can=adapter.CanCapture(out why);}catch(Exception ex){why="CanCapture threw "+Error(ex);break;}
                    }
                    if(can)ok++;
                    else
                    {
                        refused++;string extra="";
                        if(why!=null && why.Contains("背景"))
                            try{var sprites=(UISprite[])AccessTools.Field(typeof(NewTalkView),"bgs").GetValue(talk);
                                extra=" curBgId="+AccessTools.Field(typeof(NewTalkView),"curBgId").GetValue(talk)+" shown="+string.Join("|",sprites.Where(b=>b!=null && b.gameObject.activeSelf).Select(b=>b.url));}catch{}
                        add(cfg.id,command,"REFUSED",why+extra);
                    }
                    if(can && !roundTrip)
                    {
                        // One real quick save and load per event, timed end to end.
                        roundTrip=true;string[] before=null;
                        try{before=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).ToArray();}catch(Exception ex){add(cfg.id,command,"LIST_FAILED",Error(ex));}
                        var clock=Stopwatch.StartNew();string saved=null;
                        try{service.QuickSave();}catch(Exception ex){add(cfg.id,command,"SAVE_FAILED",Error(ex));}
                        while(before!=null && saved==null && clock.Elapsed.TotalSeconds<30)
                        {
                            yield return Popups();
                            try{saved=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).FirstOrDefault(r=>!before.Contains(r));}catch{}
                            if(saved==null)yield return null;
                        }
                        long saveMs=clock.ElapsedMilliseconds;
                        if(saved==null)add(cfg.id,command,"SAVE_FAILED","ms="+saveMs+" status="+service.StatusMessage);
                        else
                        {
                            UiResult loaded=null;clock.Restart();
                            try{service.Load(saved,r=>{if(!r.IsPending)loaded=r;});}catch(Exception ex){add(cfg.id,command,"LOAD_FAILED",Error(ex));}
                            while(loaded==null && clock.Elapsed.TotalSeconds<60){yield return Popups();yield return null;}
                            long loadMs=clock.ElapsedMilliseconds;
                            add(cfg.id,command,loaded!=null && loaded.Success?"SAVE_LOAD_OK":"LOAD_FAILED","saveMs="+saveMs+" loadMs="+loadMs+(loaded!=null && !loaded.Success?" message="+loaded.Message:loaded==null?" message=timeout":""));
                            yield return new WaitForSecondsRealtime(.5f);
                            lastChange=Time.realtimeSinceStartup;
                        }
                        continue;
                    }
                }
                talk=Talk();if(talk==null)break;
                if(talk.talkState==TalkState.Option)
                {
                    yield return null;
                    Button choice=null;
                    try{choice=Resources.FindObjectsOfTypeAll<Button>().FirstOrDefault(b=>b!=null && b.name=="ADV.Choice.0" && b.gameObject.activeInHierarchy);}catch{}
                    if(choice==null){add(cfg.id,command,"NO_CHOICE","");break;}
                    try{choice.onClick.Invoke();}catch(Exception ex){add(cfg.id,command,"CHOICE_THREW",Error(ex));}
                }
                else
                {
                    try{talk.OnClickNext();}catch(Exception ex){add(cfg.id,command,"NEXT_THREW",Error(ex));}
                }
                yield return new WaitForSecondsRealtime(.07f);
            }
            add(0,"-","SUMMARY","lines="+lines+" ok="+ok+" refused="+refused+" roundTrip="+roundTrip);
        }
        quickConfirm.Value=previousConfirm;
        File.WriteAllLines(output,rows);
        check(true,"mod sweep recorded");
    }
    // Acknowledge authored reward/notice popups; anything else is left alone.
    static IEnumerator Popups()
    {
        for(int i=0;i<4;i++)
        {
            bool clicked=false;
            try
            {
                var top=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
                if(top!=null && !(top is NewTalkView) && top.gameObject!=null)
                {
                    var words=new[]{"确定","确认","关闭","继续"};
                    var button=top.gameObject.GetComponentsInChildren<Button>(false).FirstOrDefault(b=>b!=null && b.IsInteractable() &&
                        (b.GetComponentsInChildren<TextMeshProUGUI>(false).Any(t=>words.Contains((t.text??"").Trim())) ||
                         b.GetComponentsInChildren<Text>(false).Any(t=>words.Contains((t.text??"").Trim()))));
                    if(button!=null){button.onClick.Invoke();clicked=true;}
                }
            }
            catch(Exception){}
            if(!clicked)yield break;
            yield return new WaitForSecondsRealtime(.3f);
        }
    }
}
