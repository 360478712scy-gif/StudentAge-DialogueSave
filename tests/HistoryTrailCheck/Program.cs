using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StudentAgeDialogueSave.GameIntegration;
namespace StudentAgeDialogueSave.GameIntegration {
 internal sealed class ConfigFingerprintSnapshot {internal string ComputeDigest()=>"exact-config-digest";}
 internal sealed class HistoryCheckpoint {internal GameCheckpoint State;internal ConfigFingerprintSnapshot Config;}
}
class Program {
 static int count;
 static void Check(bool value,string text){if(!value)throw new Exception(text);count++;}
 static void Reject(Action action,string name){try{action();}catch(Exception){count++;return;}throw new Exception(name);}
 static string Zip(string json){using var output=new MemoryStream();using(var z=new GZipStream(output,CompressionMode.Compress,true)){var b=Encoding.UTF8.GetBytes(json);z.Write(b);}return Convert.ToBase64String(output.ToArray());}
 static void Main(){
  var d=new JObject{["history"]=new JArray(new JObject{["content"]="实际对白"}),["segments"]=new JArray("实际对白"),["segmentIndex"]=0,["options"]=new JArray(),["configDigest"]=""};
  var original=new HistoryCheckpoint{Config=new ConfigFingerprintSnapshot(),State=new GameCheckpoint{WorldBytes=new byte[]{1,2,3},Dialogue=d,Brief=new CheckpointBrief{RunId="run",SteamId="123",Speaker="白雨"}}};
  string packed=HistoryTrailCodec.Encode(new[]{original});
  var decoded=HistoryTrailCodec.Decode(packed);
  Check(decoded.Length==1 && decoded[0].State.WorldBytes[2]==3,"world roundtrip");
  Check(decoded[0].State.Brief.RunId=="run" && decoded[0].State.Brief.Speaker=="白雨","identity roundtrip");
  Check(decoded[0].State.Dialogue.Value<string>("configDigest")=="exact-config-digest","detached config digest serialized");
  Check(d.Value<string>("configDigest")=="","source checkpoint unchanged");
  decoded[0].State.Dialogue["segments"][0]="changed";
  Check((string)d["segments"][0]=="实际对白","independent data on decode");
  Check(JToken.DeepEquals(decoded[0].State.MaterializeDialogue()["history"],d["history"]),"decoded history is materialized without loss");
  Check(HistoryTrailCodec.Encode(new HistoryCheckpoint[0])==null && HistoryTrailCodec.Decode(null).Length==0,"legacy archive without trail");
  Reject(()=>HistoryTrailCodec.Decode("invalid"),"invalid base64");
  Reject(()=>HistoryTrailCodec.Decode(Zip("{\"version\":99,\"entries\":[]}")),"unknown version");
  Reject(()=>HistoryTrailCodec.Decode(Zip("{\"version\":1,\"entries\":[{\"dialogue\":{\"history\":[],\"segments\":[],\"segmentIndex\":0,\"options\":[]}}]}")),"invalid segment");
  Reject(()=>HistoryTrailCodec.Decode(Zip("{\"version\":1,\"entries\":[{\"dialogue\":{\"historyTrail\":\"nested\"}}]}")),"recursive trail");
  var world=new byte[512*1024];new Random(19).NextBytes(world);
  var trail=Enumerable.Range(0,80).Select(i=>{
   var w=(byte[])world.Clone();w[i]=(byte)i;
   return new HistoryCheckpoint{State=new GameCheckpoint{WorldBytes=w,Dialogue=(JObject)d.DeepClone(),Brief=original.State.Brief}};
  }).ToArray();
  trail[2].State.WorldBytes=trail[1].State.WorldBytes;
  trail[3].State.WorldBytes=new byte[]{9,8,7};
  var oldEntries=new JArray(trail.Select(t=>new JObject{["world"]=Convert.ToBase64String(t.State.WorldBytes),["dialogue"]=t.State.Dialogue.DeepClone(),["brief"]=JObject.FromObject(t.State.Brief)}));
  var clock=Stopwatch.StartNew();var legacy=Zip(new JObject{["version"]=1,["entries"]=oldEntries}.ToString(Newtonsoft.Json.Formatting.None));var oldMs=clock.Elapsed.TotalMilliseconds;
  clock.Restart();var fast=HistoryTrailCodec.Encode(trail);var coldMs=clock.Elapsed.TotalMilliseconds;
  Check(HistoryTrailCodec.LastEncodedBlocks==80,"new history encodes all immutable blocks once");
  clock.Restart();var again=HistoryTrailCodec.Encode(trail);var warmMs=clock.Elapsed.TotalMilliseconds;
  Check(HistoryTrailCodec.LastEncodedBlocks==0 && fast==again,"unchanged history reuses all packed blocks");
  var restored=HistoryTrailCodec.Decode(fast);var restoredLegacy=HistoryTrailCodec.Decode(legacy);
  Check(restored.Length==80 && restoredLegacy.Length==80 && restored.Select((p,i)=>p.State.WorldBytes.SequenceEqual(trail[i].State.WorldBytes) && JToken.DeepEquals(p.State.MaterializeDialogue(),restoredLegacy[i].State.MaterializeDialogue())).All(v=>v),"legacy and delta blocks restore identical full states including size changes");
  var branch=trail.Take(40).Concat(new[]{original}).ToArray();HistoryTrailCodec.Encode(branch);
  Check(HistoryTrailCodec.LastEncodedBlocks==1,"new branch packs only its new checkpoint");
  var reordered=new[]{trail[0],trail[20]};var reorderedBytes=HistoryTrailCodec.Encode(reordered);
  Check(HistoryTrailCodec.LastEncodedBlocks==1 && HistoryTrailCodec.Decode(reorderedBytes)[1].State.WorldBytes.SequenceEqual(trail[20].State.WorldBytes),"changed predecessor rebuilds delta block");
  var badBlock=Zip(new JObject{["sameWorld"]=true,["dialogue"]=d,["brief"]=JObject.FromObject(original.State.Brief)}.ToString());
  Reject(()=>HistoryTrailCodec.Decode(Zip(new JObject{["version"]=2,["entries"]=new JArray(badBlock)}.ToString())),"first delta block cannot reference absent predecessor");
  CheckPersistentHistory(original.State.Brief);
  Console.WriteLine($"HISTORY_PERF legacy_ms={oldMs:F1} cold_ms={coldMs:F1} warm_ms={warmMs:F1} legacy_bytes={legacy.Length} packed_bytes={fast.Length}");
  Console.WriteLine("HISTORY_TRAIL_CODEC_PASS "+count);
 }
 static void CheckPersistentHistory(CheckpointBrief brief)
 {
  var rows=new JArray();CheckpointHistoryLog prefix=null;var points=new System.Collections.Generic.List<HistoryCheckpoint>();
  var expected=new System.Collections.Generic.List<JArray>();var worlds=new System.Collections.Generic.List<byte[]>();
  for(int i=0;i<120;i++)
  {
   rows.Add(new JObject{["talkId"]=i,["roleName"]="角色"+i,["content"]="历史对白"+i,["talkRoleIds"]=new JArray(i),["cloth"]=new JObject{["1"]=i}});
   prefix=CheckpointHistoryLog.Capture(rows,prefix);expected.Add((JArray)rows.DeepClone());
   var world=Encoding.UTF8.GetBytes(string.Join("|",Enumerable.Repeat("完整世界"+i,500)));worlds.Add(world);
   var state=new GameCheckpoint{NativeHistory=prefix,Dialogue=new JObject{["history"]=new JArray(),["segments"]=new JArray("正文"+i),["segmentIndex"]=0,["options"]=new JArray()},WorldBytes=world,Brief=brief};
   state.PackHistoryWorld();points.Add(new HistoryCheckpoint{State=state});
  }
  Check(points.All(p=>p.State.WorldLength>0 && p.State.WorldIdentity is HistoryWorldBuffer),"history checkpoints use lossless compact worlds");
  Check(points.Select((p,i)=>p.State.HistoryCount==i+1 && p.State.WorldBytes.SequenceEqual(worlds[i]) && JToken.DeepEquals(p.State.MaterializeDialogue()["history"],expected[i])).All(v=>v),"all growing prefixes and changed worlds remain exact");
  var detached=points[0].State.MaterializeDialogue();detached["history"][0]["content"]="修改恢复副本";
  Check((string)points[0].State.MaterializeDialogue()["history"][0]["content"]=="历史对白0","materializing never mutates a retained checkpoint");
  rows[0]["content"]="第三方改变早期内容";rows[0]["talkRoleIds"][0]=900;rows[0]["cloth"]["1"]=901;
  var branch=CheckpointHistoryLog.Capture(rows,prefix);
  Check(!prefix.MatchesPrefix(rows) && branch.MatchesPrefix(rows) && (string)prefix.Materialize()[0]["content"]=="历史对白0","earlier edits fork immutable prefixes and fail old-branch validation");
  while(rows.Count>7)rows.RemoveAt(rows.Count-1);
  rows.Add(new JObject{["content"]="截断后的新分支"});branch=CheckpointHistoryLog.Capture(rows,branch);
  Check(branch.Count==8 && JToken.DeepEquals(branch.Materialize(),rows) && prefix.Count==120,"truncation and append preserve the abandoned full branch");
  var encoded=HistoryTrailCodec.Encode(points.ToArray());var decoded=HistoryTrailCodec.Decode(encoded);
  Check(decoded.Select((p,i)=>p.State.WorldBytes.SequenceEqual(worlds[i]) && JToken.DeepEquals(p.State.MaterializeDialogue()["history"],expected[i])).All(v=>v),"compact worlds and growing logs survive archive encoding and decoding");
  var repeated=HistoryTrailCodec.Encode(points.ToArray());
  Check(encoded==repeated && HistoryTrailCodec.LastEncodedBlocks==0,"compact checkpoints preserve packed block reuse");
  var shared=new GameCheckpoint{Dialogue=(JObject)points[119].State.Dialogue.DeepClone(),NativeHistory=prefix,Brief=brief};shared.ShareHistoryWorld(points[119].State);
  var same=HistoryTrailCodec.Decode(HistoryTrailCodec.Encode(new[]{points[119],new HistoryCheckpoint{State=shared}}));
  Check(ReferenceEquals(same[0].State.WorldIdentity,same[1].State.WorldIdentity) && same[1].State.WorldBytes.SequenceEqual(worlds[119]),"same-world compression preserves equality and storage sharing");
 }
}
