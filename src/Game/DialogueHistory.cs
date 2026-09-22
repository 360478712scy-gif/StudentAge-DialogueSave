using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using View.Evt;

namespace StudentAgeDialogueSave.GameIntegration
{
    internal sealed class HistoryCheckpoint
    {
        internal GameCheckpoint State;
        internal ConfigFingerprintSnapshot Config;
        internal double CaptureMilliseconds;
    }
    internal sealed class DialogueHistory
    {
        internal sealed class Entry
        {
            internal string Speaker, Text;
            internal HistoryCheckpoint Checkpoint;
            internal int TalkId, Segment, NativeCount;
            internal bool IsOption;
        }
        readonly DialogueCheckpointAdapter adapter;
        readonly CancellationToken shutdown;
        readonly Action<string> log;
        internal readonly List<Entry> Entries=new List<Entry>();
        NewTalkView owner;
        int lastTalk=-1,lastSegment=-1,lastCount=-1;
        string lastPhase,lastFailure;
        long bytes;
        internal bool Restoring {get;private set;}
        internal double LastCaptureMilliseconds {get;private set;}
        internal event Action Changed;
        internal DialogueHistory(DialogueCheckpointAdapter adapter,CancellationToken shutdown,Action<string> log)
        {
            this.adapter=adapter;this.shutdown=shutdown;this.log=log;
            adapter.CaptureHistoryTrail=()=>Entries.Select(e=>e.Checkpoint).ToArray();
            adapter.RestoreHistoryTrail=Import;
        }
        void Import(HistoryCheckpoint[] trail)
        {
            Clear();owner=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            foreach(var checkpoint in trail)
            {
                var data=checkpoint.State.Dialogue;
                bool option=data.Value<string>("phase")=="Option";
                int segment=data.Value<int>("segmentIndex");
                string text=option?string.Join("\n",((Newtonsoft.Json.Linq.JArray)data["options"]).Select(o=>
                    Config.Cfg.OptionCfgMap.TryGetValue(o.Value<int>("id"),out var cfg)?cfg.content:"")):
                    (string)data["segments"][segment];
                Entries.Add(new Entry{Checkpoint=checkpoint,Speaker=option?"选项":checkpoint.State.Brief.Speaker,
                    Text=text,TalkId=data.Value<int>("talkId"),Segment=segment,
                    NativeCount=((Newtonsoft.Json.Linq.JArray)data["history"]).Count,IsOption=option});
                bytes+=checkpoint.State.WorldBytes.Length;
            }
            Changed?.Invoke();
        }

        internal void Tick(NewTalkView view)
        {
            if(Restoring || adapter.IsRestoring || adapter.IsPausedForMenu || view==null ||
                !ReferenceEquals(UIMgr.GetView<NewTalkView>(false),view))return;
            if(!ReferenceEquals(owner,view)) {Clear();owner=view;}
            var cfg=AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(view) as Config.TalkCfg;
            if(cfg==null)return;
            var history=AccessTools.Field(typeof(NewTalkView),"historys").GetValue(view) as List<TalkData>;
            int count=history?.Count??0;
            string phase=view.talkState.ToString();
            // One world snapshot per displayed line, plus a separate choice snapshot.
            // Typing/completed/countdown are stages of that same line, not new history.
            if(cfg.id==lastTalk && view.tmpTalkIdx==lastSegment && count==lastCount &&
                (phase==lastPhase || (phase!="Option" && lastPhase!="Option")))return;
            if(!adapter.CanCaptureHistory(out var reason))
            {
                string failure=cfg.id+":"+view.tmpTalkIdx+":"+reason;
                if(failure!=lastFailure){lastFailure=failure;log("回看等待记录 ["+cfg.id+"/"+phase+"]："+reason);}
                return;
            }
            try
            {
                var checkpoint=adapter.CaptureHistory();
                // Reuse an immutable byte buffer only after full byte equality. Effects,
                // random state and presentation are still captured at every boundary.
                var previous=Entries.Count==0?null:Entries[Entries.Count-1].Checkpoint.State.WorldBytes;
                if(previous!=null && previous.Length==checkpoint.State.WorldBytes.Length &&
                    Enumerable.SequenceEqual(previous,checkpoint.State.WorldBytes))checkpoint.State.WorldBytes=previous;
                lastTalk=cfg.id;lastSegment=view.tmpTalkIdx;lastCount=count;lastPhase=phase;
                LastCaptureMilliseconds=checkpoint.CaptureMilliseconds;
                lastFailure=null;
                bool option=view.talkState==TalkState.Option;
                // Preserve the spoken line and its subsequent choices as separate
                // destinations. Phase updates may replace only the same destination.
                if(Entries.Count>0 && Entries[Entries.Count-1].NativeCount==count && Entries[Entries.Count-1].IsOption==option)
                {bytes-=Entries[Entries.Count-1].Checkpoint.State.WorldBytes.Length;Entries.RemoveAt(Entries.Count-1);}
                string text=option?string.Join("\n",view.itemgroup_options.GetCells().OrderBy(c=>c.cellIdx)
                    .Select(c=>c.data as CommonEvtOptionData).Where(o=>o!=null)
                    .Select(o=>Config.Cfg.OptionCfgMap.TryGetValue(o.id,out var choice)?choice.content:"")):view.tmpTalks[view.tmpTalkIdx];
                Entries.Add(new Entry { Speaker=option?"选项":checkpoint.State.Brief.Speaker,Text=text,IsOption=option,
                    TalkId=cfg.id,Segment=view.tmpTalkIdx,NativeCount=count,Checkpoint=checkpoint });
                bytes+=checkpoint.State.WorldBytes.Length;
                // No age/count/byte eviction: visible earlier records keep their complete state.
                Changed?.Invoke();
            }
            catch(Exception ex){log("回看检查点未建立："+ex.Message);}
        }
        internal async Task Restore(int index)
        {
            if(Restoring || index<0 || index>=Entries.Count)throw new InvalidOperationException("回看位置已变化。");
            var selected=Entries[index];Restoring=true;
            try
            {
                await adapter.RestoreHistoryAsync(selected.Checkpoint,shutdown);
                while(Entries.Count>index+1){bytes-=Entries[Entries.Count-1].Checkpoint.State.WorldBytes.Length;Entries.RemoveAt(Entries.Count-1);}
                owner=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                lastTalk=selected.TalkId;lastSegment=selected.Segment;lastPhase=owner.talkState.ToString();
                lastCount=(AccessTools.Field(typeof(NewTalkView),"historys").GetValue(owner) as List<TalkData>)?.Count??0;
                Changed?.Invoke();
            }
            finally{Restoring=false;}
        }
        internal void Clear(){Entries.Clear();bytes=0;lastTalk=lastSegment=lastCount=-1;lastPhase=null;owner=null;}
    }
}
