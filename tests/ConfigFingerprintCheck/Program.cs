using System; using System.Collections; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection; using System.Security.Cryptography; using System.Text;
using Newtonsoft.Json; using Newtonsoft.Json.Linq; using StudentAgeDialogueSave.GameIntegration;
namespace Config {
 public enum Kind { Foo=7 }
 public class TalkCfg { public int id; public string content; public List<List<float>> roles; public List<List<float>> effect; public List<double> doubles; public float scalar; public Kind kind; }
 public class Anime { public int type; }
 public class Misc { public byte[] bytes; public Dictionary<int,List<float>> nested; public string txt; public float f; public double d; public decimal dec; public long num; public float? optional; }
 public static class Cfg {
 public static Dictionary<int,TalkCfg> TalkCfgMap {get;} = new();
 public static Dictionary<int,Anime> TalkAnimeCfgMap {get;} = new();
 public static Dictionary<int,Misc> MiscCfgMap {get;} = new();
 }
}
class Program {
 static readonly JsonSerializer Serializer=JsonSerializer.Create(new JsonSerializerSettings {TypeNameHandling=TypeNameHandling.None,MetadataPropertyHandling=MetadataPropertyHandling.Ignore,MaxDepth=64});
 static string Legacy() {using var sha=SHA256.Create();using var crypto=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write);using var text=new StreamWriter(crypto,new UTF8Encoding(false),8192,true);using var writer=new JsonTextWriter(text){CloseOutput=false}; writer.WriteStartObject();
 foreach(var prop in typeof(Config.Cfg).GetProperties(BindingFlags.Public|BindingFlags.Static).OrderBy(p=>p.Name,StringComparer.Ordinal)) {writer.WritePropertyName(prop.Name);var map=(IDictionary)prop.GetValue(null);writer.WriteStartArray();foreach(object key in map.Keys.Cast<object>().OrderBy(Convert.ToString,StringComparer.Ordinal)){writer.WriteStartArray();Serializer.Serialize(writer,key);var value=map[key];if(value is Config.TalkCfg talk && talk.roles?.Count>0){var actions=new List<List<float>>(talk.roles);actions.Sort((a,b)=>{if((int)a[0]!=(int)b[0])return 0;if(Config.Cfg.TalkAnimeCfgMap[(int)a[1]].type==1)return -1;return Config.Cfg.TalkAnimeCfgMap[(int)b[1]].type==1?1:0;});var token=JObject.FromObject(talk,Serializer);token["roles"]=JArray.FromObject(actions,Serializer);token.WriteTo(writer);}else Serializer.Serialize(writer,value);writer.WriteEndArray();}writer.WriteEndArray();}writer.WriteEndObject();writer.Flush();text.Flush();crypto.FlushFinalBlock();return Convert.ToHexString(sha.Hash);}
 static int checkedCases;
 static void Case(string name, Action edit, Action undo, bool expectedMatch=false) {
   var saved=ConfigFingerprintSnapshot.Capture(Serializer);var digest=saved.ComputeDigest();
   if(!saved.MatchesCurrent())throw new Exception(name+": initial match false");
   edit();
   try {
     if(saved.MatchesCurrent()!=expectedMatch)throw new Exception(name+": change comparison wrong");
     if(!ReferenceEquals(digest,saved.ComputeDigest()))throw new Exception(name+": digest cache not retained");
     var changed=ConfigFingerprintSnapshot.Capture(Serializer);if(changed.ComputeDigest()!=Legacy())throw new Exception(name+": legacy mismatch");
   } finally {undo();}
   if(!saved.MatchesCurrent())throw new Exception(name+": reverted values not recognized");checkedCases++;
 }
 static void Main(){ Config.Cfg.TalkAnimeCfgMap[1]=new(){type=1};Config.Cfg.TalkAnimeCfgMap[2]=new(){type=2};
 for(int i=0;i<100;i++)Config.Cfg.TalkCfgMap[i]=new(){id=i,content="中文\\\"\n🙂 "+i,roles=i%2==0?new(){new(){1,2,.1f},new(){1,1,.33f}}:null,effect=new(){new(){.1f,float.NaN,float.PositiveInfinity,1e-20f,-0f}},doubles=new(){.1,double.NaN},scalar=.1234567f,kind=Config.Kind.Foo};
 Config.Cfg.MiscCfgMap[1]=new(){bytes=new byte[]{1,2,255},nested=new(){[2]=new(){.1f,.23f}},txt=null,f=.1f,d=.1,dec=.2m,num=long.MaxValue};
 var snap=ConfigFingerprintSnapshot.Capture(Serializer);var before=snap.ComputeDigest();if(before!=Legacy())throw new Exception("LEGACY_MISMATCH "+before+" "+Legacy());
 var t=Config.Cfg.TalkCfgMap[0];var m=Config.Cfg.MiscCfgMap[1];
 Case("int",()=>t.id++,()=>t.id--);
 var content=t.content;Case("string",()=>t.content+="x",()=>t.content=content);
 var nested=t.effect[0][0];Case("nested-list-in-place",()=>t.effect[0][0]=.9f,()=>t.effect[0][0]=nested);
 var scalar=t.scalar;Case("scalar-float",()=>t.scalar=.2f,()=>t.scalar=scalar);
 var kind=t.kind;Case("enum",()=>t.kind=(Config.Kind)8,()=>t.kind=kind);
 Case("list-count",()=>t.effect[0].Add(9),()=>t.effect[0].RemoveAt(t.effect[0].Count-1));
 Case("list-order",()=>t.effect[0].Reverse(),()=>t.effect[0].Reverse());
 Case("canonical-native-roles-order",()=>t.roles.Reverse(),()=>t.roles.Reverse(),true);
 var role=t.roles[0][2];Case("role-action-in-place",()=>t.roles[0][2]=77,()=>t.roles[0][2]=role);
 Case("animation-config",()=>Config.Cfg.TalkAnimeCfgMap[2].type=1,()=>Config.Cfg.TalkAnimeCfgMap[2].type=2);
 var oldEffects=t.effect;Case("null-versus-list",()=>t.effect=null,()=>t.effect=oldEffects);
 var oldInner=t.effect[0];Case("equal-container-replacement",()=>t.effect[0]=new List<float>(oldInner),()=>t.effect[0]=oldInner,true);
 Case("byte-array",()=>m.bytes[0]=22,()=>m.bytes[0]=1);
 var oldRow=Config.Cfg.TalkCfgMap[19];Case("row-null",()=>Config.Cfg.TalkCfgMap[19]=null,()=>Config.Cfg.TalkCfgMap[19]=oldRow);
 Case("map-remove",()=>Config.Cfg.TalkCfgMap.Remove(19),()=>Config.Cfg.TalkCfgMap[19]=oldRow);
 Case("map-key-replace",()=>{Config.Cfg.TalkCfgMap.Remove(19);Config.Cfg.TalkCfgMap[119]=oldRow;},()=>{Config.Cfg.TalkCfgMap.Remove(119);Config.Cfg.TalkCfgMap[19]=oldRow;});
 Case("top-map-reorder",()=>{Config.Cfg.TalkCfgMap.Remove(19);Config.Cfg.TalkCfgMap[19]=oldRow;},()=>{},true);
 var inner=m.nested[2][0];Case("dictionary-deep-value",()=>m.nested[2][0]=9,()=>m.nested[2][0]=inner);
 m.nested[9]=new(){5};var oldDict=m.nested;Case("inner-dictionary-order",()=>m.nested=oldDict.Reverse().ToDictionary(p=>p.Key,p=>p.Value),()=>m.nested=oldDict);
 var dec=m.dec;Case("decimal-scale",()=>m.dec=.20m,()=>m.dec=dec);
 var f=m.f;m.f=0f;Case("float-signed-zero",()=>m.f=BitConverter.Int32BitsToSingle(int.MinValue),()=>m.f=0f);m.f=f;
 var d=m.d;m.d=0d;Case("double-signed-zero",()=>m.d=BitConverter.Int64BitsToDouble(long.MinValue),()=>m.d=0d);m.d=d;
 m.optional=0f;Case("nullable-float-signed-zero",()=>m.optional=BitConverter.Int32BitsToSingle(int.MinValue),()=>m.optional=0f);
 Case("nullable-null",()=>m.optional=null,()=>m.optional=0f);
 var nan=t.effect[0][1];Case("NaN-payload",()=>t.effect[0][1]=BitConverter.Int32BitsToSingle(0x7fc01234),()=>t.effect[0][1]=nan);
 var workerSnapshot=ConfigFingerprintSnapshot.Capture(Serializer);var task=System.Threading.Tasks.Task.Run(workerSnapshot.ComputeDigest);t.content+="during hash";task.GetAwaiter().GetResult();if(workerSnapshot.MatchesCurrent())throw new Exception("background mutation was not detected before cache promotion");t.content=content;checkedCases++;
 Console.WriteLine("STRICT_CONFIG_COMPARISON_PASS "+checkedCases+" mutation/compatibility cases");
 Config.Cfg.TalkCfgMap[0].effect[0][0]=99;Config.Cfg.TalkCfgMap[0].roles[0][0]=9;Config.Cfg.MiscCfgMap[1].bytes[0]=22;Config.Cfg.MiscCfgMap[1].nested[2][0]=99;Config.Cfg.TalkAnimeCfgMap[1].type=9;
 if(snap.ComputeDigest()!=before)throw new Exception("MUTABLE_REFERENCE");if(Legacy()==before)throw new Exception("MUTATION_NOT_DETECTED");Console.WriteLine("CONFIG_FINGERPRINT_SYNTHETIC_PASS legacy bytes, normalized floats, nested collections, detached mutations"); }
}
