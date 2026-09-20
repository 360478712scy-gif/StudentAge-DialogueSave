using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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
  Check(HistoryTrailCodec.Encode(new HistoryCheckpoint[0])==null && HistoryTrailCodec.Decode(null).Length==0,"legacy archive without trail");
  Reject(()=>HistoryTrailCodec.Decode("invalid"),"invalid base64");
  Reject(()=>HistoryTrailCodec.Decode(Zip("{\"version\":2,\"entries\":[]}")),"unknown version");
  Reject(()=>HistoryTrailCodec.Decode(Zip("{\"version\":1,\"entries\":[{\"dialogue\":{\"history\":[],\"segments\":[],\"segmentIndex\":0,\"options\":[]}}]}")),"invalid segment");
  Reject(()=>HistoryTrailCodec.Decode(Zip("{\"version\":1,\"entries\":[{\"dialogue\":{\"historyTrail\":\"nested\"}}]}")),"recursive trail");
  Console.WriteLine("HISTORY_TRAIL_CODEC_PASS "+count);
 }
}
