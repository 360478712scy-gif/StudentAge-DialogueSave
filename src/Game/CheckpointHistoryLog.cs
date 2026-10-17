using System;
using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Persistent immutable prefixes: one detached row per append, never one full
    // native log per checkpoint. Materialize only for a restore or disk worker.
    internal sealed class CheckpointHistoryLog
    {
        readonly CheckpointHistoryLog previous;
        readonly JToken row;
        internal readonly int Count;
        internal CheckpointHistoryLog(CheckpointHistoryLog previous,JToken detachedRow)
        {this.previous=previous;row=detachedRow;Count=(previous?.Count??0)+1;}
        internal JArray Materialize()
        {
            var rows=new JToken[Count];var node=this;
            for(int i=Count-1;i>=0;i--){rows[i]=node.row;node=node.previous;}
            // JArray attaches cloned rows, never reparents the immutable prefix.
            var result=new JArray();foreach(var item in rows)result.Add(item.DeepClone());return result;
        }
        internal bool MatchesPrefix(JArray full)
        {
            if(full==null || Count>full.Count)return false;
            for(var node=this;node!=null;node=node.previous)
                if(!JToken.DeepEquals(node.row,full[node.Count-1]))return false;
            return true;
        }
        internal static CheckpointHistoryLog Capture(JArray rows,CheckpointHistoryLog prior)
        {
            var nodes=new CheckpointHistoryLog[prior?.Count??0];
            for(var node=prior;node!=null;node=node.previous)nodes[node.Count-1]=node;
            int shared=0;
            while(shared<rows.Count && shared<nodes.Length && JToken.DeepEquals(rows[shared],nodes[shared].row))shared++;
            var last=shared==0?null:nodes[shared-1];
            for(int i=shared;i<rows.Count;i++)last=new CheckpointHistoryLog(last,rows[i].DeepClone());
            return last;
        }
    }
}
