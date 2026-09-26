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
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

// Find grade-rank native events and record where the mod's scene skip stops, and why.
internal static class RuntimeSkipProbeQA
{
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var log=new List<string>();string output=Path.Combine(root,"results/skip-probe.txt");
        var events=Cfg.EvtCfgMap.Values.Where(e=>e.title!=null && (e.title.Contains("段位")||e.title.Contains("成绩")) && e.talkId!=null && e.talkId.Count>0).Take(6).ToList();
        foreach(var e in events)log.Add("event "+e.id+" "+e.title+" type="+e.type+" talk="+string.Join(",",e.talkId));
        File.WriteAllLines(output,log);
        var adv=AdvDialogueController.Active;
        Func<NewTalkView> talk=()=>UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        Func<int> current=()=>talk()==null?0:((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk())).id;
        foreach(var e in events.Take(2))
        {
            // Close whatever dialogue is open, then enter the event natively.
            if(talk()!=null){UIMgr.CloseView(talk());yield return new WaitForSecondsRealtime(1f);}
            // Treat the event as never read, as for a player meeting it the first time.
            var global=AccessTools.Field(typeof(GlobalMgr),"model").GetValue(Singleton<GlobalMgr>.Ins);
            var seen=(List<int>)AccessTools.Field(global.GetType(),"historyTalks").GetValue(global);
            seen?.RemoveAll(id=>id/1000==e.id || e.talkId.Contains(id));
            Singleton<CommonEvtMgr>.Ins.ShowEvent(e.id);
            float t0=Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup-t0<15 && !(talk()!=null && talk().talkState==TalkState.AnimEnd))yield return null;
            if(talk()==null){log.Add(e.id+": no dialogue opened");File.WriteAllLines(output,log);continue;}
            int startLine=current();var cfg=(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk());
            log.Add(e.id+": start line "+startLine+" seen="+Singleton<GlobalMgr>.Ins.HasSeenTalk(startLine)+" effect="+string.Join(",",cfg.screenEffect??new List<float>())+" paper="+talk().paperId+" option="+(cfg.option?.Count??0)+" minigame="+(cfg.miniGame?.Count??0)+" next="+cfg.GetNextTalk());
            AccessTools.Method(typeof(AdvDialogueController),"BeginSceneSkip").Invoke(adv,null);
            yield return new WaitForSecondsRealtime(3f);
            log.Add(e.id+": after skip line "+current()+" state="+talk()?.talkState+" open="+(talk()!=null)+" pending="+AccessTools.Field(typeof(AdvDialogueController),"sceneSkipPending").GetValue(adv));
            File.WriteAllLines(output,log);
        }
    }
}
