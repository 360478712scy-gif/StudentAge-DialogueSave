using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Config;
using Sdk;
using View.Main;
using StudentAgeDialogueSave.Storage;

namespace StudentAgeDialogueSave.UI
{
    // Only presentation ordering/notes are exchanged for native saves. The game
    // continues to load their unchanged native files through Game.LoadGame.
    internal sealed class NativeArchiveBrowser
    {
        readonly string directory=Path.GetFullPath(PathDefine.SAVE_PATH).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        readonly Dictionary<string,SaveFileData> info=new Dictionary<string,SaveFileData>();
        internal readonly Dictionary<int,SaveFileData> Slots=new Dictionary<int,SaveFileData>();
        internal const int AutomaticBase=100000;
        JObject layout=new JObject();string stamp;bool headersComplete;
        string IndexPath=>Path.Combine(directory,"dialogue_native_layout.json");
        internal Action Changed;
        string Fingerprint()=>File.Exists(IndexPath)?SaveCodec.Hash(File.ReadAllBytes(IndexPath)):"";
        internal void Refresh()
        {
            stamp=Fingerprint();layout=stamp==""?new JObject():JObject.Parse(File.ReadAllText(IndexPath));
            var arranged=layout["slots"] as JObject??new JObject();layout["slots"]=arranged;
            var notes=layout["notes"] as JObject??new JObject();layout["notes"]=notes;
            Slots.Clear();if(!Directory.Exists(directory))return;
            var files=Directory.GetFiles(directory).Where(f=>f.EndsWith(".save")||f.EndsWith(".autosave")||f.EndsWith(".quicksave")).OrderBy(f=>f,StringComparer.Ordinal).ToArray();
            var available=new Dictionary<string,SaveFileData>();
            foreach(var file in files)
            {
                var data=new SaveFileData(file);if(!data.enable)continue;
                if(info.TryGetValue(data.fileName,out var cached) && cached.date==data.date)data=cached;else info[data.fileName]=data;
                if(!(layout["hidden"] as JArray??new JArray()).Values<string>().Contains(data.fileName))available[data.fileName]=data;
            }
            // Manual saves keep a stable number: the arranged slot, else their native position.
            // Automatic/quick saves live in their own range and never push manual saves along
            // (a new autosave every round used to renumber every unarranged manual save).
            foreach(var entry in arranged.Properties())if(int.TryParse(entry.Name,out int slot) && slot>0 && available.TryGetValue((string)entry.Value,out var data) && data.isManual==(slot<AutomaticBase))
            {Slots[slot]=data;available.Remove(data.fileName);}
            foreach(var data in available.Values.Where(d=>d.isManual).OrderBy(d=>d.pos).ThenBy(d=>d.fileName,StringComparer.Ordinal))
            {int slot=Math.Max(1,data.pos);while(Slots.ContainsKey(slot))slot++;Slots[slot]=data;}
            foreach(var data in available.Values.Where(d=>!d.isManual))
            {int slot=AutomaticBase;while(Slots.ContainsKey(slot))slot++;Slots[slot]=data;}
            headersComplete=Slots.Values.All(d=>d.previewState==2);
        }
        internal string Note(SaveFileData data)=>(string)layout["notes"]?[data.fileName];
        internal DialogueUiRecord Record(SaveFileData data,bool loadInfo=false)
        {
            if(loadInfo && data.previewState==0)
            {
                data.previewState=1;
                SaveMgrEx.LoadInfoAsync(directory+Path.DirectorySeparatorChar,data.fileName,16,text=>{try{data.InitInfo(text);}catch{data.enable=false;}finally{data.previewState=2;Changed?.Invoke();}});
            }
            Cfg.RoundCfgMap.TryGetValue(data.round,out var round);
            return new DialogueUiRecord{RevisionId=data.fileName,RunId=data.roleUid,RoleName=data.roleName,Comment=Note(data),Summary="",CreatedUtc=data.date,
                YearLabel=round?.year.ToString()??"",SeasonLabel=round!=null && Cfg.SeasonCfgMap.TryGetValue(round.season,out var season)?season.name:"",
                SeasonId=round?.season,Location=Cfg.MapCfgMap.TryGetValue(data.recordMapId,out var location)?location.name:"家",
                Gender=data.gender,GradeState=data.round>44?2:data.round>26?1:0,CanLoad=data.enable,StatusReason=data.enable?null:"原版存档信息无法读取，原文件已保留。",
                BackgroundId=Cfg.MapCfgMap.TryGetValue(data.recordMapId,out var map)?map.bg:0};
        }
        internal int FreeNativePosition()
        {
            int highest=info.Values.Where(d=>d.isManual).Select(d=>d.pos).DefaultIfEmpty(0).Max();
            // Deleted native names remain tombstoned. Reusing their physical
            // number after reopening would save successfully but hide the new file.
            foreach(string name in (layout["hidden"] as JArray??new JArray()).Values<string>())
            {
                if(string.IsNullOrEmpty(name) || !name.EndsWith(".save",StringComparison.Ordinal))continue;
                string stem=name.Substring(0,name.Length-5);int dot=stem.LastIndexOf('.');
                if(dot>=0 && int.TryParse(stem.Substring(dot+1),out int reserved))highest=Math.Max(highest,reserved);
            }
            return checked(highest+1);
        }
        internal void Request(int slot){if(Slots.TryGetValue(slot,out var data))Record(data,true);}
        internal void RequestGenderHeaders()
        {
            if(headersComplete)return;
            // Gender lives in the native 16-character header. Bound concurrent
            // header requests; do not deserialize save worlds to filter the list.
            int budget=Math.Max(0,4-info.Values.Count(d=>d.previewState==1));
            foreach(var data in Slots.Values.Where(d=>d.previewState==0).Take(budget).ToArray())Record(data,true);
        }
        internal async Task Edit(string operation,int source,int target,string note=null)
        {
            var current=(JObject)layout.DeepClone();var positions=new JObject();foreach(var pair in Slots)positions[pair.Key.ToString()]=pair.Value.fileName;current["slots"]=positions;
            string name=null;
            if(operation=="copy" && Slots.TryGetValue(source,out var from))
                name=new SaveFileData(CheckedPath(from.fileName)){isManual=true,isAuto=false,isQuick=false,pos=FreeNativePosition()}.ToPath();
            string expected=stamp,copyName=name;var selected=Slots[source];
            if(File.GetLastWriteTime(CheckedPath(selected.fileName))!=selected.date)throw new IOException("所选原版存档已变化，请刷新。");
            if(Slots.TryGetValue(target,out var destination) && File.GetLastWriteTime(CheckedPath(destination.fileName))!=destination.date)throw new IOException("目标原版存档已变化，请刷新。");
            await Task.Run(()=>NativeSlotEdits.Apply(directory,Path.Combine(Path.GetDirectoryName(directory),"DialogueSaveNativeBackups",Path.GetFileName(directory)),expected,current,operation,source,target,note,copyName));
            Refresh();Changed?.Invoke();
        }
        string CheckedPath(string file)
        {
            if(Path.GetFileName(file)!=file || !(file.EndsWith(".save")||file.EndsWith(".autosave")||file.EndsWith(".quicksave")))throw new IOException("无效原版文件名。");
            string path=Path.Combine(directory,file);if(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("不操作链接存档。");return path;
        }
    }
}
