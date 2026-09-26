using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Config;
using Sdk;
using UnityEngine;
using View.Evt;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

// Lines with native one-shot screen effects save and load; a line and its choices form one backlog entry.
internal static class RuntimeEffectsQA
{
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var baseTalk=Cfg.TalkCfgMap[1900000001];
        var chain=new[]{(1900000201,new List<float>{4012f,1f},"闪屏演出后的对白。"),(1900000202,new List<float>{4017f},"收起画面后的对白。"),(1900000203,new List<float>{4005f,1f},"姿势演出后的对白。")};
        for(int i=0;i<chain.Length;i++)
            Cfg.TalkCfgMap[chain[i].Item1]=new TalkCfg{id=chain[i].Item1,bg=baseTalk.bg,roleIds=baseTalk.roleIds,roleName=baseTalk.roleName,content=chain[i].Item3,screenEffect=chain[i].Item2,
                nextTalk=new List<int>{i+1<chain.Length?chain[i+1].Item1:1900000204}};
        Cfg.TalkCfgMap[1900000204]=new TalkCfg{id=1900000204,bg=baseTalk.bg,roleIds=baseTalk.roleIds,roleName=baseTalk.roleName,content="要慎重对待这份感情。（你只能拥有一个恋人，请谨慎选择）",option=new List<int>{1900000001,1900000002}};
        baseTalk.nextTalk=new List<int>{1900000201};
        var quickConfirm=ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"QuickSave");bool previous=quickConfirm.Value;quickConfirm.Value=false;
        var log=new List<string>();string lastSave=null;
        Func<NewTalkView> talk=()=>UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        Func<int> current=()=>talk()==null?0:((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk())).id;
        try
        {
            foreach(var line in chain)
            {
                yield return until(()=>talk()!=null && (talk().talkState==TalkState.AnimEnd),20,"line before "+line.Item1+" stable");
                talk().OnClickNext();
                yield return until(()=>current()==line.Item1 && talk().talkState==TalkState.AnimEnd,20,"effect line "+line.Item1+" shown");
                string why=null;
                yield return until(()=>adapter.CanCapture(out why),8,"effect line "+line.Item1+" becomes saveable");
                var before=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).ToArray();
                service.QuickSave();
                yield return until(()=>service.List(DialogueUiCategory.Quick).Any(r=>!before.Contains(r.RevisionId)),30,"effect line "+line.Item1+" quick save commits");
                lastSave=service.List(DialogueUiCategory.Quick).First(r=>!before.Contains(r.RevisionId)).RevisionId;
                log.Add(line.Item1+" effect="+line.Item2[0]+" saved "+lastSave);
                yield return new WaitForSecondsRealtime(.5f);
            }
            UiResult loaded=null;service.Load(lastSave,r=>{if(!r.IsPending)loaded=r;});
            yield return until(()=>loaded!=null,60,"effect save loads");
            check(loaded.Success,"effect line save loads: "+loaded.Message);
            yield return until(()=>current()==1900000203,20,"restored at the pose effect line");
            check(true,"restored to effect line 1900000203");
            yield return until(()=>talk().talkState==TalkState.AnimEnd,20,"restored line stable");
            yield return new WaitForSecondsRealtime(.5f);
            talk().OnClickNext();
            yield return until(()=>current()==1900000204 && talk().talkState==TalkState.Option,20,"choice line shown");
            yield return new WaitForSecondsRealtime(1f);
            var adv=AdvDialogueController.Active;adv.OpenHistory();
            yield return until(()=>Resources.FindObjectsOfTypeAll<AdvBacklog>().Any(r=>r.gameObject.activeInHierarchy) && AdvSettingsTransition.Active==null,10,"backlog open");
            yield return new WaitForSecondsRealtime(.6f);
            var reader=Resources.FindObjectsOfTypeAll<AdvBacklog>().Single(r=>r.gameObject.activeInHierarchy);
            var records=(List<TalkData>)AccessTools.Field(typeof(AdvBacklog),"records").GetValue(reader);
            var checkpoints=(Dictionary<int,int>)AccessTools.Field(typeof(AdvBacklog),"checkpoints").GetValue(reader);
            int row=records.FindLastIndex(r=>r.content!=null && r.content.StartsWith("要慎重对待这份感情"));
            log.Add("row="+row+" content="+(row>=0?records[row].content.Replace("\n","\\n"):"")+" rows="+records.Count+" optionRows="+records.Count(r=>r.roleName=="选项"));
            check(row>=0 && records[row].content.EndsWith("\n选择项："+Cfg.OptionCfgMap[1900000001].content+"/"+Cfg.OptionCfgMap[1900000002].content),"line and its choices merged into one backlog entry");
            check(!records.Any(r=>r.roleName=="选项"),"no separate choice rows remain");
            check(checkpoints.ContainsKey(row) && adv.History.Entries[checkpoints[row]].IsOption,"merged entry jumps to the choice state");
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/effects-backlog.png"));yield return new WaitForSecondsRealtime(.5f);
        }
        finally{quickConfirm.Value=previous;File.WriteAllLines(Path.Combine(root,"results/effects.txt"),log);}
    }
}
