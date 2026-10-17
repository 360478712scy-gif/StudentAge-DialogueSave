using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave.GameIntegration;
using UnityEngine;
using View.Evt;
public static class RuntimeLongHistoryQA
{
 public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
 {
  yield return until(()=>adapter.CanCaptureHistory(out _),25,"long history stable");
  var talk=(NewTalkView)UIMgr.GetView<NewTalkView>(false);
  var native=(List<TalkData>)AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk);
  var startPoint=adapter.CaptureHistory();
  var initial=new GameCheckpoint{Dialogue=startPoint.State.MaterializeDialogue(),WorldBytes=startPoint.State.WorldBytes,Brief=startPoint.State.Brief};
  var original=native.Count;var snapshots=new List<HistoryCheckpoint>();var times=new List<double>();
  long before=GC.GetTotalMemory(true);var record=new JObject();
  using(adapter.PauseForMenu())
  {
   for(int i=0;i<350;i++)
   {
    native.Add(new TalkData{talkId=1900000001,content="长期字幕性能验证 "+i+"：放学后的阳光透过走廊的窗户，落在课桌与书本上；完整保留每一句历史，不省略回跳状态。",roleName="肖清雅",talkRoleIds=new List<int>{1},cloth=new Dictionary<int,int>{{1,101}}});
    var point=adapter.CaptureHistory();snapshots.Add(point);times.Add(point.CaptureMilliseconds);
    if(i%20==0)yield return null;
   }
  }
  record["lines"]=snapshots.Count;record["first50Ms"]=JArray.FromObject(times.Take(50));record["last50Ms"]=JArray.FromObject(times.Skip(times.Count-50));
  record["managedGrowthBytes"]=GC.GetTotalMemory(true)-before;record["worldBytes"]=snapshots[0].State.WorldBytes.Length;
  File.WriteAllText(Path.Combine(root,"results/long-history.json"),record.ToString());
  check(snapshots.Count==350 && snapshots[0].State.Dialogue!=null,"all long-session snapshots retained");
  check(snapshots[0].State.HistoryCount==original+1 && snapshots[349].State.HistoryCount==original+350,"shared histories retain every native row at each destination");
  using(adapter.PauseForMenu())
  {
   for(int i=350;i<2000;i++)
   {
    native.Add(new TalkData{talkId=1900000001,content="长期字幕性能验证 "+i+"：所有历史仍然完整保留，验证长时间累积后的内存和字幕捕获。",roleName="肖清雅",talkRoleIds=new List<int>{1},cloth=new Dictionary<int,int>{{1,101}}});
    var point=adapter.CaptureHistory();snapshots.Add(point);times.Add(point.CaptureMilliseconds);
    if(i%20==0)yield return null;
   }
  }
  record["stressLines"]=snapshots.Count;record["stressLast50Ms"]=JArray.FromObject(times.Skip(times.Count-50));
  record["stressManagedGrowthBytes"]=GC.GetTotalMemory(true)-before;
  File.WriteAllText(Path.Combine(root,"results/long-history.json"),record.ToString());
  check(snapshots[1999].State.HistoryCount==original+2000 && snapshots[0].State.HistoryCount==original+1,"2000 snapshots remain complete including the first line");
  var first=(JArray)snapshots[0].State.MaterializeDialogue()["history"];
  var oldText=native[original].content;native[original].content="第三方修改早期日志";
  using(adapter.PauseForMenu())
  {
   var branch=adapter.CaptureHistory();
   check((string)branch.State.MaterializeDialogue()["history"][original]["content"]==native[original].content && (string)first[original]["content"]==oldText,"earlier native edits do not corrupt retained history");
  }
  native[original].content=oldText;
  var pack=Task.Run(()=>HistoryTrailCodec.Encode(snapshots.Take(350).ToArray()));
  yield return until(()=>pack.IsCompleted,45,"long history archive encoding");
  var unpack=Task.Run(()=>HistoryTrailCodec.Decode(pack.GetAwaiter().GetResult()));
  yield return until(()=>unpack.IsCompleted,45,"long history archive decoding");
  var decoded=unpack.GetAwaiter().GetResult();
  var roundtrip=new JArray(new[]{0,174,349}.Select(i=>new JObject{
   ["index"]=i,["worldEqual"]=Enumerable.SequenceEqual(decoded[i].State.WorldBytes,snapshots[i].State.WorldBytes),
   ["dialogueEqual"]=JToken.DeepEquals(decoded[i].State.MaterializeDialogue(),JObject.Parse(snapshots[i].State.MaterializeDialogue().ToString())),
   ["differentFields"]=new JArray(snapshots[i].State.MaterializeDialogue().Properties().Where(p=>!JToken.DeepEquals(decoded[i].State.MaterializeDialogue()[p.Name],p.Value)).Select(p=>p.Name))}));
  File.WriteAllText(Path.Combine(root,"results/long-history-roundtrip.json"),roundtrip.ToString());
  check(decoded.Length==350 && roundtrip.All(r=>r.Value<bool>("worldEqual") && r.Value<bool>("dialogueEqual")),"compressed archive restores full worlds and serialized presentation without loss");
  record["archiveCharacters"]=pack.GetAwaiter().GetResult().Length;
  var saved=new GameCheckpoint{Dialogue=snapshots[349].State.MaterializeDialogue(),WorldBytes=snapshots[349].State.WorldBytes,Brief=snapshots[349].State.Brief};
  saved.Dialogue["historyTrail"]=pack.GetAwaiter().GetResult();
  var load=adapter.RestoreAsync(saved);
  yield return until(()=>load.IsCompleted,40,"embedded archive history native restore");load.GetAwaiter().GetResult();
  var imported=StudentAgeDialogueSave.UI.AdvDialogueController.Active.History;
  check(imported.Entries.Count==350 && imported.Entries[0].NativeCount==original+1 && imported.Entries[349].NativeCount==original+350,"archive load imports every compact jump destination after branch validation");
  // Restore actual native views from the earliest, middle and latest compact snapshots.
  foreach(int index in new[]{0,999,1999})
  {
   var jump=adapter.RestoreHistoryAsync(snapshots[index],CancellationToken.None);
   yield return until(()=>jump.IsCompleted,35,"long history native jump "+index);jump.GetAwaiter().GetResult();
   var restored=(List<TalkData>)AccessTools.Field(typeof(NewTalkView),"historys").GetValue(UIMgr.GetView<NewTalkView>(false));
   check(restored.Count==original+index+1 && restored[original].content==oldText,"native jump restores exact log prefix at "+index);
  }
  record["nativeJumpsRestored"]=true;File.WriteAllText(Path.Combine(root,"results/long-history.json"),record.ToString());
  GC.KeepAlive(snapshots);
  var restore=adapter.RestoreAsync(initial);
  yield return until(()=>restore.IsCompleted,35,"restore original fixture after long history");restore.GetAwaiter().GetResult();
  yield return until(()=>((NewTalkView)UIMgr.GetView<NewTalkView>(false)).talkState==TalkState.AnimEnd && adapter.CanCapture(out _),20,"rollback effect fixture text finished");
  yield return RuntimeHistoryRollbackQA.Run(adapter,check,until,root);
 }
}
