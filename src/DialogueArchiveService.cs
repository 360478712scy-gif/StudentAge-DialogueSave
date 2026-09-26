using Sdk.PlatformAPI;

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using StudentAgeDialogueSave.Storage;
using StudentAgeDialogueSave.UI;

namespace StudentAgeDialogueSave
{
    internal sealed partial class DialogueSaveService
    {
        internal string ArchiveRun=>adapter.CurrentCheckpointBrief?.RunId;
        internal int ArchiveGender=>adapter.CurrentCheckpointBrief?.Gender??0;
        internal IReadOnlyList<DialogueUiRecord> ArchiveList(DialogueUiCategory category)
        {
            if(!EnsureRepository(out _))return new List<DialogueUiRecord>();
            if(!initialized)Refresh();
            string user=Platform.Current.GetUserId(),cat=Category(category);
            return Repository.FindHeads(records).Concat(records.Where(r=>r.Header!=null && (r.Status==SaveStatus.UnsupportedVersion || r.Status==SaveStatus.Corrupt))).Where(r=>r.Header.SteamId==user && r.Header.Category==cat).Select(ToUi).ToList();
        }
        internal void ArchiveSave(int slot,DialogueUiCategory category,string run,Action<UiResult> done,string replacedRevisionId=null)
        {
            if(!string.IsNullOrEmpty(run) && run!=ArchiveRun){done(new UiResult(false,"请切换到当前周目后保存，其他周目的空位不会写入当前进度。"));return;}
            if(!menuSaving && !BeginMenu(true,out string reason)){done(new UiResult(false,reason));return;}
            Save(category,slot,replacedRevisionId,done);
        }
        internal async void EditArchive(string operation,DialogueUiRecord source,int destination,DialogueUiRecord target,string note,Action<UiResult> done)
        {
            bool owns=false;
            try
            {
                await WaitForStoreAsync(shutdown);
                if(source==null || !records.Any(r=>r.Header?.RevisionId==source.RevisionId && r.Header.SteamId==Platform.Current.GetUserId()))throw new InvalidOperationException("存档已变化。");
                if(target!=null && (source.RunId!=target.RunId || source.Category!=target.Category))throw new InvalidOperationException("请选择同一周目和分类的存档位。");
                busy=true;owns=true;InvokeResult(done,new UiResult(false,"正在处理存档…",true));
                var repo=repository;int token=generation;
                var updated=await Task.Run(()=>{repo.EditSlots(operation,source.RevisionId,destination.ToString(System.Globalization.CultureInfo.InvariantCulture),target?.RevisionId,note);return repo.Scan();},shutdown);
                if(disposed || token!=generation)return;
                records=updated;initialized=true;lastScanTime=UnityEngine.Time.realtimeSinceStartup;
                RaiseRecordsChanged();InvokeResult(done,new UiResult(true,"操作完成。"));
            }
            catch(Exception ex){if(!disposed)InvokeResult(done,new UiResult(false,ex.Message));}
            finally{if(owns)busy=false;}
        }
    }
}
