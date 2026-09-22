using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using View.Evt;

// Exercise the actual backlog checkpoints and native effect runner, including
// choosing a different branch after a jump. No production world/saves are used.
public static class RuntimeHistoryRollbackQA
{
    const int First=1901000201, Choice=1901000202, Final=1901000203, A=1901000211, B=1901000212;
    const int Probe=16000001;
    static NewTalkView Talk=>(NewTalkView)UIMgr.GetView<NewTalkView>(false);
    static float Value=>Singleton<CommonEvtMgr>.Ins.GetEvtSaveData(1,Probe);
    static List<List<float>> Effect(int delta)=>new List<List<float>>{new List<float>{50,2,1,Probe,delta}};
    static bool At(int id)=>Talk!=null && ((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk)).id==id;
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var history=AdvDialogueController.Active.History;
        var snapshot=adapter.Capture();var original=Cfg.EvtCfgMap[1];
        var source=(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk);
        float baseline=Value;
        var files=Directory.GetFiles(PathDefine.SAVE_PATH,"dialogue_*.dsav").OrderBy(x=>x).ToArray();
        check(new[]{First,Choice,Final}.All(id=>!Cfg.TalkCfgMap.ContainsKey(id)) && new[]{A,B}.All(id=>!Cfg.OptionCfgMap.ContainsKey(id)),"rollback fixture does not replace existing nodes");
        try
        {
            Cfg.TalkCfgMap[First]=new TalkCfg{id=First,bg=source.bg,roleIds=source.roleIds,roleName=source.roleName,content="这是选项之前的对白，本句结束时增加三点。",effect=Effect(3),nextTalk=new List<int>{Choice}};
            Cfg.TalkCfgMap[Choice]=new TalkCfg{id=Choice,bg=source.bg,roleIds=source.roleIds,roleName=source.roleName,content="下一句增加五点，选项应当分别记录。",effect=Effect(5),option=new List<int>{A,B}};
            Cfg.TalkCfgMap[Final]=new TalkCfg{id=Final,bg=source.bg,roleIds=source.roleIds,roleName=source.roleName,content="选项效果已经执行。"};
            Cfg.OptionCfgMap[A]=new OptionCfg{id=A,content="分支 A 增加七点",effect=Effect(7),talkId=new List<int>{Final}};
            Cfg.OptionCfgMap[B]=new OptionCfg{id=B,content="分支 B 增加十一点",effect=Effect(11),talkId=new List<int>{Final}};
            Cfg.EvtCfgMap[1]=new EvtCfg{id=1,type=1,title="回溯效果隔离验证",talkId=new List<int>{First}};
            yield return until(()=>adapter.CanAdvancePresentation(Talk),15,"fixture accepts native dialogue transition");
            Talk.RefreshTalk(First);
            yield return until(()=>At(First) && Talk.talkState==TalkState.AnimEnd && adapter.CanCapture(out _),25,"first effect completes on original branch");
            check(Value==baseline+3,"native line effect applied once");
            int first=history.Entries.FindIndex(e=>e.TalkId==First && !e.IsOption);
            check(first>=0 && history.Entries[first].Checkpoint.State.Dialogue.Value<string>("phase")=="Anim","history owns the before-effect typing state");
            Talk.NextTalk();
            yield return until(()=>At(Choice) && Talk.talkState==TalkState.Option && adapter.CanCapture(out _),25,"native options have separate snapshot");
            int choice=history.Entries.FindLastIndex(e=>e.TalkId==Choice && e.IsOption);
            check(choice>first && Value==baseline+8,"choice snapshot includes line effects but not a selection");
            Singleton<CommonEvtMgr>.Ins.SelectOption(Talk.itemgroup_options.GetCells().Select(c=>(CommonEvtOptionData)c.data).Single(c=>c.id==A));
            yield return until(()=>At(Final) && adapter.CanCapture(out _),25,"branch A finished");
            check(Value==baseline+15,"branch A effect applied once");
            var clock=System.Diagnostics.Stopwatch.StartNew();var jump=history.Restore(choice);
            yield return until(()=>jump.IsCompleted,25,"backlog restores choice world");jump.GetAwaiter().GetResult();clock.Stop();
            double choiceMs=clock.Elapsed.TotalMilliseconds;
            check(Value==baseline+8 && Talk.talkState==TalkState.Option,"jump removes branch A effect and restores unselected options");
            yield return until(()=>adapter.CanAdvancePresentation(Talk),10,"restored choices accept selection");
            Singleton<CommonEvtMgr>.Ins.SelectOption(Talk.itemgroup_options.GetCells().Select(c=>(CommonEvtOptionData)c.data).Single(c=>c.id==B));
            yield return until(()=>At(Final) && adapter.CanCapture(out _),25,"branch B finished after rollback");
            check(Value==baseline+19,"new branch applies only B, never A plus B");
            clock.Restart();jump=history.Restore(first);
            yield return until(()=>jump.IsCompleted,25,"backlog restores before both line and option effects");jump.GetAwaiter().GetResult();clock.Stop();
            double firstMs=clock.Elapsed.TotalMilliseconds;
            check(Value==baseline,"jump to typing rewinds all subsequent line and option effects");
            yield return until(()=>At(First) && Talk.talkState==TalkState.AnimEnd && adapter.CanCapture(out _),25,"restored typing finishes normally");
            check(Value==baseline+3,"restored pending effect runs exactly once");
            Talk.NextTalk();
            yield return until(()=>At(Choice) && Talk.talkState==TalkState.Option && adapter.CanCapture(out _),25,"restored choice reached again");
            check(Value==baseline+8,"continuing the rewound timeline has no doubled line effect");
            // A hundred in-memory checkpoints: measure capture, not frame-rate waits.
            var view=Talk;view.tmpTalks=Enumerable.Range(0,101).Select(i=>"回溯性能验证第 "+i+" 句，记录真实世界，不执行文件保存。").ToList();
            int start=history.Entries.Count;
            for(int i=0;i<100;i++)
            {
                view.txtex_content.DOKill(false);view.tmpTalkIdx=i;
                AccessTools.Field(typeof(NewTalkView),"waitFrame").SetValue(view,0);
                view.DoText(view.tmpTalks[i]);yield return null;
            }
            var entries=history.Entries.Skip(start).ToArray();
            check(entries.Length==100,"all 100 typing positions have immutable world checkpoints");
            check(entries.All(e=>e.Checkpoint.Config==null),"history does not traverse or retain full game configuration");
            check(files.SequenceEqual(Directory.GetFiles(PathDefine.SAVE_PATH,"dialogue_*.dsav").OrderBy(x=>x)),"history capture and jumps create no disk saves");
            var samples=entries.Select(e=>e.Checkpoint.CaptureMilliseconds).OrderBy(x=>x).ToArray();
            File.WriteAllText(Path.Combine(root,"results/history-rollback-effects.json"),new JObject{
                ["choiceJumpMs"]=choiceMs,["firstJumpMs"]=firstMs,["captureMedianMs"]=samples[50],
                ["captureP95Ms"]=samples[95],["captureMaxMs"]=samples[99],["entries"]=entries.Length,
                ["distinctWorldBuffers"]=entries.Select(e=>e.Checkpoint.State.WorldBytes).Distinct().Count(),
                ["effectsRewound"]=true,["noDiskSaves"]=true}.ToString());
        }
        finally
        {
            foreach(int id in new[]{First,Choice,Final})Cfg.TalkCfgMap.Remove(id);
            foreach(int id in new[]{A,B})Cfg.OptionCfgMap.Remove(id);
            Cfg.EvtCfgMap[1]=original;
        }
        var restore=adapter.RestoreAsync(snapshot);yield return until(()=>restore.IsCompleted,35,"restore fixture after rollback effect tests");restore.GetAwaiter().GetResult();
        check(Value==baseline,"effect probes are removed by world restore");
    }
}
