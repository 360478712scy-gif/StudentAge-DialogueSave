using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace StudentAgeDialogueSave.Storage
{
    public sealed partial class Repository
    {
        // Immutable revisions: a two-slot exchange is visible only after its single
        // commit marker and both verified bodies arrive. Old heads remain recoverable.
        public void EditSlots(string operation,string sourceRevision,string targetSlot,string targetRevision,string note=null)
        {
            lock(_gate)using(AcquireWriteLock())
            {
                var source=Load(sourceRevision);var heads=FindHeads(Scan());
                RequireHead(heads,source.Header,sourceRevision);
                source.Header.SavedUtc=source.Header.SavedUtc??source.Header.CreatedUtc;
                if(operation=="note")
                {
                    if(note!=null && note.Length>4096)throw new InvalidDataException("备注不能超过4096字。");
                    source.Header.Comment=note??"";source.Header.ParentRevisionIds=new[]{sourceRevision};source.Header.TransactionId=null;
                    PublishInternal(source,true);return;
                }
                if(operation!="copy" && operation!="swap")throw new InvalidDataException("未知存档操作。");
                if(source.Header.LogicalSlot==targetSlot)throw new InvalidDataException("请选择另一个存档位。");
                var destination=source.Header.DetachedCopy();destination.LogicalSlot=targetSlot;
                RequireHead(heads,destination,targetRevision);
                var target=string.IsNullOrEmpty(targetRevision)?null:Load(targetRevision);
                if(target!=null)target.Header.SavedUtc=target.Header.SavedUtc??target.Header.CreatedUtc;
                var copy=new SaveEnvelope{World=source.World,Dialogue=source.Dialogue,Header=source.Header.DetachedCopy()};
                copy.Header.LogicalSlot=targetSlot;copy.Header.ParentRevisionIds=target==null?new string[0]:new[]{targetRevision};copy.Header.TransactionId=null;
                if(operation=="copy"){PublishInternal(copy,true);return;}
                string transaction=Guid.NewGuid().ToString("N");copy.Header.TransactionId=transaction;
                var other=target==null?new SaveEnvelope{Kind="tombstone",World=new byte[0],Dialogue=new JObject(),Header=source.Header.DetachedCopy()}:
                    new SaveEnvelope{World=target.World,Dialogue=target.Dialogue,Header=target.Header.DetachedCopy()};
                other.Header.LogicalSlot=source.Header.LogicalSlot;other.Header.ParentRevisionIds=target==null?CollectDeletionClosure(source.Header,Scan()):new[]{sourceRevision};other.Header.TransactionId=transaction;
                // All expensive serialization/validation still precedes the visibility point.
                var first=PublishInternal(copy,true);var second=PublishInternal(other,target!=null);
                var members=new JObject{[first.Header.RevisionId]=SaveCodec.Hash(File.ReadAllBytes(first.FilePath)),[second.Header.RevisionId]=SaveCodec.Hash(File.ReadAllBytes(second.FilePath))};
                var marker=new JObject{["id"]=transaction,["members"]=members};
                string stage=Path.Combine(_stagingDirectory,transaction+".commit.pending");
                using(var stream=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {var bytes=SaveCodec.Utf8.GetBytes(marker.ToString());stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                // A concurrent cloud head must not silently win during the exchange.
                CheckExpectedHeads(copy.Header);RequireHead(FindHeads(Scan()),source.Header,sourceRevision);
                AtomicPublish(stage,TransactionPath(transaction));
                try{Directory.CreateDirectory(Path.Combine(_backupDirectory,"Transactions"));File.Copy(TransactionPath(transaction),Path.Combine(_backupDirectory,"Transactions",transaction+".commit"),false);}catch(Exception e) when(e is IOException || e is UnauthorizedAccessException){_diagnostics?.Invoke(e.Message);}
            }
        }
        // Replacing a card from another run keeps the new world's run identity.
        // The new body and old-slot tombstone become visible together.
        public SaveRecord PublishReplacing(SaveEnvelope snapshot,string replacedRevision)
        {
            lock(_gate)using(AcquireWriteLock())
            {
                var old=Load(replacedRevision);var heads=FindHeads(Scan());
                RequireHead(heads,old.Header,replacedRevision);
                if(snapshot.Kind!="checkpoint" || snapshot.Header.SteamId!=old.Header.SteamId || snapshot.Header.Category!=old.Header.Category)
                    throw new InvalidDataException("只能覆盖同一账户和分类的存档。");
                if(SlotKey(snapshot.Header)==SlotKey(old.Header))
                {
                    if(!snapshot.Header.ParentRevisionIds.SequenceEqual(new[]{replacedRevision}))throw new InvalidDataException("覆盖版本已变化。");
                    return PublishInternal(snapshot,true);
                }
                RequireHead(heads,snapshot.Header,null);
                string transaction=Guid.NewGuid().ToString("N");
                var copy=new SaveEnvelope{World=snapshot.World,Dialogue=snapshot.Dialogue,Header=snapshot.Header.DetachedCopy()};
                copy.Header.TransactionId=transaction;copy.Header.ParentRevisionIds=new string[0];
                var deleted=new SaveEnvelope{Kind="tombstone",World=new byte[0],Dialogue=new JObject(),Header=old.Header.DetachedCopy()};
                deleted.Header.ParentRevisionIds=CollectDeletionClosure(old.Header,Scan());deleted.Header.TransactionId=transaction;
                var first=PublishInternal(copy,true);var second=PublishInternal(deleted);
                var members=new JObject{[first.Header.RevisionId]=SaveCodec.Hash(File.ReadAllBytes(first.FilePath)),[second.Header.RevisionId]=SaveCodec.Hash(File.ReadAllBytes(second.FilePath))};
                var marker=new JObject{["id"]=transaction,["operation"]="replace",["members"]=members};
                string stage=Path.Combine(_stagingDirectory,transaction+".commit.pending");
                using(var stream=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {var bytes=SaveCodec.Utf8.GetBytes(marker.ToString());stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                CheckExpectedHeads(copy.Header);RequireHead(FindHeads(Scan()),old.Header,replacedRevision);
                AtomicPublish(stage,TransactionPath(transaction));
                try{Directory.CreateDirectory(Path.Combine(_backupDirectory,"Transactions"));File.Copy(TransactionPath(transaction),Path.Combine(_backupDirectory,"Transactions",transaction+".commit"),false);}catch(Exception e) when(e is IOException || e is UnauthorizedAccessException){_diagnostics?.Invoke(e.Message);}
                return first;
            }
        }
        static void RequireHead(System.Collections.Generic.List<SaveRecord> heads,SaveHeader slot,string expected)
        {
            var actual=heads.Where(x=>SlotKey(x.Header)==SlotKey(slot)).Select(x=>x.Header.RevisionId).ToArray();
            if(expected==null?actual.Length!=0:actual.Length!=1 || actual[0]!=expected)
                throw new InvalidDataException("存档位已变化或存在分支，请刷新后重新选择。");
        }
        string TransactionPath(string id){SaveCodec.Revision(id);return Path.Combine(_saveDirectory,"dialogue_tx_"+id+".commit");}
        bool Committed(SaveHeader header)
        {try{return CheckCommitted(header);}catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException || e is ArgumentException){return false;}}
        bool CheckCommitted(SaveHeader header)
        {
            if(string.IsNullOrEmpty(header.TransactionId))return true;
            string path=TransactionPath(header.TransactionId);
            if(!File.Exists(path))
            {
                string backup=Path.Combine(_backupDirectory,"Transactions",header.TransactionId+".commit");
                if(!File.Exists(backup))return false;
                CheckDirectoryLinks(Path.GetDirectoryName(backup));CheckFileLink(backup);try{File.Copy(backup,path,false);}catch(IOException){if(!File.Exists(path))return false;}
            }
            CheckFileLink(path);var marker=SaveCodec.Read(path);
            if((string)marker["id"]!=header.TransactionId || !(marker["members"] is JObject members) || members.Count!=2 || members[header.RevisionId]==null)return false;
            bool replacement=(string)marker["operation"]=="replace";
            int checkpoints=0,tombstones=0;
            foreach(var member in members.Properties())
            {
                SaveCodec.Revision(member.Name);string body=PathFor(member.Name);if(!File.Exists(body))return false;CheckFileLink(body);
                var bytes=SaveCodec.ReadBytes(body);string hash=SaveCodec.Hash(bytes);if(hash!=(string)member.Value)return false;
                SaveHeader peer;
                if(verifiedRows.TryGetValue(body,out var verified) && verified.Hash==hash && (verified.Record.Status==SaveStatus.Ready || verified.Record.Status==SaveStatus.Deleted))peer=verified.Record.Header;
                else peer=SaveCodec.Decode(SaveCodec.Read(bytes)).Header;
                if(peer.TransactionId!=header.TransactionId || peer.RevisionId!=member.Name || peer.SteamId!=header.SteamId || (!replacement && peer.RunId!=header.RunId) || peer.Category!=header.Category)return false;
                if(peer.RevisionId!=header.RevisionId && SlotKey(peer)==SlotKey(header))return false;
                if(replacement){var kind=(string)SaveCodec.Read(bytes)["Kind"];if(kind=="checkpoint")checkpoints++;else if(kind=="tombstone")tombstones++;else return false;}
            }
            return !replacement || (checkpoints==1 && tombstones==1);
        }
    }
}
