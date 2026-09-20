using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        internal static string Encode(HistoryCheckpoint[] trail)
        {
            if(trail==null || trail.Length==0)return null;
            var entries=new JArray();
            foreach(var checkpoint in trail)
            {
                var state=checkpoint.State;
                var dialogue=(JObject)state.Dialogue.DeepClone();
                dialogue.Remove("historyTrail");
                if(checkpoint.Config!=null)dialogue["configDigest"]=checkpoint.Config.ComputeDigest();
                entries.Add(new JObject{["dialogue"]=dialogue,["world"]=Convert.ToBase64String(state.WorldBytes),
                    ["brief"]=JObject.FromObject(state.Brief)});
            }
            byte[] plain=Encoding.UTF8.GetBytes(new JObject{["version"]=1,["entries"]=entries}.ToString(Formatting.None));
            if(plain.Length>MaximumExpandedBytes)throw new InvalidDataException("对话回看数据超出单文件安全解析范围。");
            using(var stream=new MemoryStream())
            {
                using(var zip=new GZipStream(stream,CompressionMode.Compress,true))zip.Write(plain,0,plain.Length);
                return Convert.ToBase64String(stream.ToArray());
            }
        }
        internal static HistoryCheckpoint[] Decode(string encoded)
        {
            if(string.IsNullOrEmpty(encoded))return new HistoryCheckpoint[0];
            using(var input=new MemoryStream(Convert.FromBase64String(encoded)))
            using(var zip=new GZipStream(input,CompressionMode.Decompress))
            using(var output=new MemoryStream())
            {
                var buffer=new byte[32768];int count;
                while((count=zip.Read(buffer,0,buffer.Length))>0)
                {
                    if(output.Length+count>MaximumExpandedBytes)throw new InvalidDataException("回看数据解压大小异常。");
                    output.Write(buffer,0,count);
                }
                output.Position=0;
                using(var text=new StreamReader(output,Encoding.UTF8))
                using(var reader=new JsonTextReader(text){MaxDepth=64,DateParseHandling=DateParseHandling.None})
                {
                    var data=JObject.Load(reader);
                    if(data.Value<int>("version")!=1 || !(data["entries"] is JArray entries))throw new InvalidDataException("未知回看数据格式。");
                    return entries.Select(item=>{
                        var dialogue=item["dialogue"] as JObject;
                        if(dialogue==null || dialogue["historyTrail"]!=null || !(dialogue["history"] is JArray) || !(dialogue["segments"] is JArray segments) ||
                            dialogue.Value<int>("segmentIndex")<0 || dialogue.Value<int>("segmentIndex")>=segments.Count || !(dialogue["options"] is JArray))throw new InvalidDataException("回看记录不完整。");
                        var world=Convert.FromBase64String(item.Value<string>("world"));
                        if(world.Length==0 || world.Length>32*1024*1024)throw new InvalidDataException("回看世界数据大小异常。");
                        var brief=item["brief"]?.ToObject<CheckpointBrief>();
                        if(brief==null)throw new InvalidDataException("回看周目信息缺失。");
                        return new HistoryCheckpoint{State=new GameCheckpoint{Dialogue=dialogue,WorldBytes=world,Brief=brief}};
                    }).ToArray();
                }
            }
        }
    }
}
