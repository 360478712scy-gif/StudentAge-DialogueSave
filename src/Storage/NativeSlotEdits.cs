using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.Storage
{
    // An atomic presentation index precedes removal of native files. Replaced
    // bodies are retained independently; interrupted cleanup remains hidden.
    public static class NativeSlotEdits
    {
        public static string Fingerprint(string path)=>File.Exists(path)?SaveCodec.Hash(File.ReadAllBytes(path)):"";
        public static void Apply(string directory,string backup,string expected,JObject current,string operation,int source,int target,string note,string copyName=null)
        {
            Directory.CreateDirectory(directory);CheckLink(directory);
            string index=Path.Combine(directory,"dialogue_native_layout.json");CheckLink(index);CheckLink(index+".lock");
            using(var gate=new FileStream(index+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                if(Fingerprint(index)!=expected)throw new IOException("原版存档排列已变化，请刷新。");
                var next=(JObject)current.DeepClone();var slots=(JObject)next["slots"];if(!(next["notes"] is JObject))next["notes"]=new JObject();var notes=(JObject)next["notes"];
                if(!(next["hidden"] is JArray))next["hidden"]=new JArray();var hidden=(JArray)next["hidden"];
                string from=(string)slots[source.ToString()],to=(string)slots[target.ToString()];
                string original=Checked(directory,from);if(!File.Exists(original))throw new IOException("来源存档已变化。");
                string sourceHash=Fingerprint(original),targetPath=to==null?null:Checked(directory,to),targetHash=targetPath==null?null:Fingerprint(targetPath);
                string created=null,remove=null,stage=index+"."+Guid.NewGuid().ToString("N")+".pending";bool committed=false;
                try
                {
                    if(operation=="note") {if(note?.Length>4096)throw new IOException("备注不能超过4096字。");notes[from]=note??"";}
                    else if(operation=="swap") {if(to==null)slots.Remove(source.ToString());else slots[source.ToString()]=to;slots[target.ToString()]=from;}
                    else if(operation=="copy")
                    {
                        created=Checked(directory,copyName);if(File.Exists(created))throw new IOException("复制目标文件已存在。");
                        byte[] body=File.ReadAllBytes(original);WriteNew(created+".pending",body);
                        if(Fingerprint(created+".pending")!=sourceHash)throw new IOException("来源存档变化，复制已取消。");
                        File.Move(created+".pending",created);File.SetLastWriteTimeUtc(created,File.GetLastWriteTimeUtc(original));slots[target.ToString()]=copyName;notes[copyName]=notes[from]??JValue.CreateNull();
                        if(to!=null){hidden.Add(to);remove=targetPath;}
                    }
                    else if(operation=="delete"){slots.Remove(source.ToString());hidden.Add(from);remove=original;}
                    else throw new IOException("未知存档操作。");
                    if(remove!=null)
                    {
                        Directory.CreateDirectory(backup);CheckLink(backup);
                        string retained=Path.Combine(backup,Guid.NewGuid().ToString("N")+"-"+Path.GetFileName(remove));
                        WriteNew(retained,File.ReadAllBytes(remove));
                        if(Fingerprint(retained)!=Fingerprint(remove))throw new IOException("备份校验失败。");
                    }
                    WriteNew(stage,SaveCodec.Utf8.GetBytes(next.ToString()));
                    if(Fingerprint(index)!=expected || Fingerprint(original)!=sourceHash || (targetPath!=null && Fingerprint(targetPath)!=targetHash))throw new IOException("原版存档发生变化，请刷新后重试。");
                    if(File.Exists(index))File.Replace(stage,index,null);else File.Move(stage,index);committed=true;
                    // Failure here must not misreport the durable index commit.
                    if(remove!=null)try{File.Delete(remove);}catch(IOException){}catch(UnauthorizedAccessException){}
                }
                finally
                {
                    Cleanup(stage);
                    if(created!=null){Cleanup(created+".pending");if(!committed)Cleanup(created);}
                }
            }
        }
        static void Cleanup(string path){try{if(File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
        static void WriteNew(string path,byte[] data){using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(data,0,data.Length);f.Flush(true);}}
        static string Checked(string directory,string name)
        {if(string.IsNullOrEmpty(name)||Path.GetFileName(name)!=name||!(name.EndsWith(".save")||name.EndsWith(".autosave")||name.EndsWith(".quicksave")))throw new IOException("无效原版文件名。");string result=Path.Combine(directory,name);CheckLink(result);return result;}
        static void CheckLink(string path)
        {
            // As in Repository, Wine's mapped volume root is the trust boundary.
            // Reject file/directory links at every level below that root.
            for(var p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if(!string.IsNullOrEmpty(Path.GetDirectoryName(p)) && (File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("不操作链接路径："+p);
        }
    }
}
