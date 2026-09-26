using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Optional archive payload. Only detached values enter the worker; no game objects,
    // type-name deserialization, or callbacks are reconstructed here.
    internal static class HistoryTrailCodec
    {
        const int MaximumExpandedBytes=384*1024*1024;
        // HistoryCheckpoint is sealed once published by DialogueHistory. Weak keys let
        // abandoned branches release their packed blocks; no Unity object enters this cache.
        sealed class Packed { internal byte[] Previous; internal ConfigFingerprintSnapshot Config; internal string Value; }
        static readonly ConditionalWeakTable<HistoryCheckpoint,Packed> packed=new ConditionalWeakTable<HistoryCheckpoint,Packed>();
        internal static int LastEncodedBlocks {get;private set;}
        internal static string Encode(HistoryCheckpoint[] trail)
        {
            LastEncodedBlocks=0;
            if(trail==null || trail.Length==0)return null;
            return Compress(writer=>{
                writer.WriteStartObject();writer.WritePropertyName("version");writer.WriteValue(2);
                writer.WritePropertyName("entries");writer.WriteStartArray();
                byte[] previous=null;long expanded=0;
                foreach(var checkpoint in trail)
                {
                    var state=checkpoint.State;
                    expanded+=state.WorldBytes.Length;
                    if(expanded>MaximumExpandedBytes)throw new InvalidDataException("对话回看数据超出单文件安全解析范围。");
                    var block=packed.GetValue(checkpoint,_=>new Packed());
                    lock(block)
                    {
                        if(block.Value==null || !ReferenceEquals(previous,block.Previous) || !ReferenceEquals(checkpoint.Config,block.Config))
                        {
                            var dialogue=(JObject)state.Dialogue.DeepClone();dialogue.Remove("historyTrail");
                            if(checkpoint.Config!=null)dialogue["configDigest"]=checkpoint.Config.ComputeDigest();
                            bool same=ReferenceEquals(previous,state.WorldBytes);
                            byte[] delta=null;
                            if(!same)
                            {
                                delta=(byte[])state.WorldBytes.Clone();
                                if(previous!=null)for(int i=0;i<Math.Min(delta.Length,previous.Length);i++)delta[i]^=previous[i];
                            }
                            block.Value=Compress(w=>{
                                w.WriteStartObject();w.WritePropertyName("dialogue");dialogue.WriteTo(w);
                                w.WritePropertyName("brief");JObject.FromObject(state.Brief).WriteTo(w);
                                w.WritePropertyName("sameWorld");w.WriteValue(same);
                                if(!same){w.WritePropertyName("world");w.WriteValue(delta);}
                                w.WriteEndObject();
                            });
                            block.Previous=previous;block.Config=checkpoint.Config;LastEncodedBlocks++;
                        }
                        writer.WriteValue(block.Value);
                    }
                    previous=state.WorldBytes;
                }
                writer.WriteEndArray();writer.WriteEndObject();
            });
        }
        static string Compress(Action<JsonTextWriter> write)
        {
            using(var output=new MemoryStream())
            {
                using(var zip=new GZipStream(output,CompressionLevel.Fastest,true))
                using(var text=new StreamWriter(zip,new UTF8Encoding(false),32768))
                using(var writer=new JsonTextWriter(text)){write(writer);writer.Flush();}
                return Convert.ToBase64String(output.ToArray());
            }
        }
        static JObject Expand(string encoded,ref long remaining)
        {
            using(var input=new MemoryStream(Convert.FromBase64String(encoded)))
            using(var zip=new GZipStream(input,CompressionMode.Decompress))
            using(var output=new MemoryStream())
            {
                var buffer=new byte[32768];int count;
                while((count=zip.Read(buffer,0,buffer.Length))>0)
                {
                    remaining-=count;if(remaining<0)throw new InvalidDataException("回看数据解压大小异常。");
                    output.Write(buffer,0,count);
                }
                output.Position=0;
                using(var text=new StreamReader(output,Encoding.UTF8))
                using(var reader=new JsonTextReader(text){MaxDepth=64,DateParseHandling=DateParseHandling.None})
                {
                    var data=JObject.Load(reader,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
                    if(reader.Read())throw new InvalidDataException("回看数据尾部异常。");return data;
                }
            }
        }
        internal static HistoryCheckpoint[] Decode(string encoded)
        {
            if(string.IsNullOrEmpty(encoded))return new HistoryCheckpoint[0];
            long budget=MaximumExpandedBytes;
            var data=Expand(encoded,ref budget);
            int version=data.Value<int>("version");
            if((version!=1 && version!=2) || !(data["entries"] is JArray entries))throw new InvalidDataException("未知回看数据格式。");
            var result=new List<HistoryCheckpoint>();byte[] previous=null;long worldBytes=0;
            foreach(var token in entries)
            {
                var item=version==1?token as JObject:Expand((string)token,ref budget);
                var dialogue=item?["dialogue"] as JObject;
                if(dialogue==null || dialogue["historyTrail"]!=null || !(dialogue["history"] is JArray) || !(dialogue["segments"] is JArray segments) ||
                    dialogue.Value<int>("segmentIndex")<0 || dialogue.Value<int>("segmentIndex")>=segments.Count || !(dialogue["options"] is JArray))throw new InvalidDataException("回看记录不完整。");
                bool same=version==2 && item.Value<bool>("sameWorld");
                var world=same?previous:Convert.FromBase64String(item.Value<string>("world"));
                if(world==null || world.Length==0 || world.Length>32*1024*1024)throw new InvalidDataException("回看世界数据大小异常。");
                worldBytes+=world.Length;if(worldBytes>MaximumExpandedBytes)throw new InvalidDataException("回看世界数据总量异常。");
                if(version==2 && !same && previous!=null)for(int i=0;i<Math.Min(world.Length,previous.Length);i++)world[i]^=previous[i];
                var brief=item["brief"]?.ToObject<CheckpointBrief>();
                if(brief==null)throw new InvalidDataException("回看周目信息缺失。");
                result.Add(new HistoryCheckpoint{State=new GameCheckpoint{Dialogue=dialogue,WorldBytes=world,Brief=brief}});previous=world;
            }
            return result.ToArray();
        }
    }
}
