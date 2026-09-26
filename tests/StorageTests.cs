using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StudentAgeDialogueSave.Storage;

internal static class StorageTests
{
    private static int _count;
    private static string _root;
    private static void Check(bool condition, string name)
    {
        _count++;
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS " + name);
    }
    private static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch { rejected = true; }
        Check(rejected, name);
    }
    private static Repository Repo(string name)
    {
        string root = Path.Combine(_root, name);
        return new Repository(Path.Combine(root, "Saves"), Path.Combine(root, "Staging"), Path.Combine(root, "Backups"));
    }
    private static SaveEnvelope Sample(string category = "manual", string run = "run-a", string slot = "slot-1")
    {
        return new SaveEnvelope {
            Header = new SaveHeader {
                SteamId = "76561198000000001", RunId = run, Category = category, LogicalSlot = slot,
                DeviceId = "device-a", GameVersion = "fixture", PluginVersion = "fixture", AdapterVersion = "fixture",
                Speaker = "测试角色", Summary = "仅为合成存储测试", SeasonId = 2
            },
            World = new byte[] {0, 1, 255, 17},
            Dialogue = JObject.Parse("{\"talkId\":123,\"phase\":\"afterEffect\",\"options\":[1,2]}")
        };
    }
    private static void VerifyLocalCopies()
    {
        string root=Path.Combine(_root,"local-copy"),cloud=Path.Combine(root,"Cloud"),backup=Path.Combine(root,"Backups");
        var repo=new Repository(cloud,Path.Combine(root,"Stage"),backup,retainLocalCopies:true);
        var saved=repo.Publish(Sample());
        string copy=Path.Combine(backup,"Retained",Path.GetFileName(saved.FilePath));
        Check(File.Exists(copy)&&File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(saved.FilePath)),"every save has a byte-identical independent local copy");
        File.Delete(saved.FilePath);
        Check(repo.Load(saved.Header.RevisionId).World.SequenceEqual(Sample().World),"missing cloud body restores from local copy before load");
        repo.Delete(saved.Header.RevisionId);
        foreach(var path in Directory.GetFiles(cloud,"dialogue_*.dsav"))File.Delete(path);
        var recovered=repo.Scan();
        Check(Repository.FindHeads(recovered).Count==0&&recovered.Any(r=>r.Status==SaveStatus.Deleted),"lost cloud directory restores tombstones without resurrecting deleted saves");
        Reject(()=>repo.Load(saved.Header.RevisionId),"local copy cannot bypass explicit deletion");
        var other=repo.Publish(Sample(slot:"other"));
        string otherCopy=Path.Combine(backup,"Retained",Path.GetFileName(other.FilePath));
        File.WriteAllText(other.FilePath,"broken-cloud-file");repo.Scan();
        Check(File.ReadAllText(other.FilePath)=="broken-cloud-file"&&File.Exists(otherCopy),"reconciliation preserves corrupt original and good backup independently");
        File.Delete(other.FilePath);File.WriteAllText(otherCopy,"broken-backup");
        repo.Scan();Check(!File.Exists(other.FilePath),"corrupt backup is not promoted into cloud directory");
        var legacyRoot=Path.Combine(_root,"local-legacy");
        var legacy=new Repository(Path.Combine(legacyRoot,"Cloud"),Path.Combine(legacyRoot,"Stage"),Path.Combine(legacyRoot,"Backups"));
        var old=legacy.Publish(Sample());
        var upgraded=new Repository(Path.Combine(legacyRoot,"Cloud"),Path.Combine(legacyRoot,"Stage"),Path.Combine(legacyRoot,"Backups"),retainLocalCopies:true);
        upgraded.Scan();File.Delete(old.FilePath);
        Check(upgraded.Load(old.Header.RevisionId).World.SequenceEqual(Sample().World),"first upgraded scan protects existing archives without rewriting them");
        var branchRoot=Path.Combine(_root,"local-branches");
        var branchRepo=new Repository(Path.Combine(branchRoot,"Cloud"),Path.Combine(branchRoot,"Stage"),Path.Combine(branchRoot,"Backups"),retainLocalCopies:true);
        var branchA=branchRepo.Publish(Sample());var branchB=branchRepo.Publish(Sample());
        File.Delete(branchA.FilePath);File.Delete(branchB.FilePath);
        var heads=Repository.FindHeads(branchRepo.Scan());
        Check(heads.Count==2&&heads.All(r=>r.IsConflict),"independent local recovery preserves both cloud branches without choosing by timestamp");
        var blockRoot=Path.Combine(_root,"local-blocked");Directory.CreateDirectory(blockRoot);
        File.WriteAllText(Path.Combine(blockRoot,"Backups"),"blocked");
        int warnings=0;
        var blocked=new Repository(Path.Combine(blockRoot,"Cloud"),Path.Combine(blockRoot,"Stage"),Path.Combine(blockRoot,"Backups"),retainLocalCopies:true,diagnostics:_=>warnings++);
        var durable=blocked.Publish(Sample());
        Check(File.Exists(durable.FilePath)&&warnings>0,"backup I/O failure does not misreport a durable primary commit as failed");
    }
    private static void VerifyScanCache()
    {
        var repo=Repo("scan-cache");var sample=Sample();
        sample.World=new byte[512*1024];new Random(29).NextBytes(sample.World);
        for(int i=0;i<12;i++){sample.Header.LogicalSlot="perf-"+i;repo.Publish(sample);}
        var clock=Stopwatch.StartNew();var cold=Repo("scan-cache");cold.Scan();double coldMs=clock.Elapsed.TotalMilliseconds;
        clock.Restart();var rows=cold.Scan();double warmMs=clock.Elapsed.TotalMilliseconds;
        Check(cold.LastScanDecodedFiles==0 && cold.LastScanReusedFiles==12,"warm listing hashes all files without decoding unchanged snapshots");
        string id=rows[0].Header.RevisionId,path=rows[0].FilePath;
        rows[0].Header.Summary="mutated external header";rows[0].Header.ParentRevisionIds=new[]{"fake"};
        Check(cold.Scan().Single(r=>r.Header.RevisionId==id).Header.Summary!="mutated external header","cached verified metadata does not escape by reference");
        var stamp=File.GetLastWriteTimeUtc(path);string contents=File.ReadAllText(path);
        int at=contents.IndexOf("测试角色",StringComparison.Ordinal);Check(at>=0,"same-length tamper fixture exists");
        File.WriteAllText(path,contents.Replace("测试角色","篡改角色"));File.SetLastWriteTimeUtc(path,stamp);
        Check(cold.Scan().Single(r=>r.FilePath==path).Status!=SaveStatus.Ready && cold.LastScanDecodedFiles>0,"same-size same-time external change cannot hit verified cache");
        Reject(()=>cold.Load(id),"load rejects tampered cached revision");
        Console.WriteLine($"SCAN_PERF files=12 cold_ms={coldMs:F1} warm_ms={warmMs:F1}");
    }
    private static void VerifyArchiveOverwrite()
    {
        var repo=Repo("archive-overwrite");var old=repo.PublishChecked(Sample(slot:"1"));
        var newer=Sample(slot:"1");newer.World=new byte[]{7,7,7};newer.Header.ParentRevisionIds=new[]{old.Header.RevisionId};
        var saved=repo.PublishReplacing(newer,old.Header.RevisionId);
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RevisionId==saved.Header.RevisionId,"overwrite same run keeps one visible slot");
        Reject(()=>repo.PublishReplacing(newer,old.Header.RevisionId),"stale overwrite cannot replace newer save");
        var other=Sample(run:"run-b",slot:"1");other.World=new byte[]{9,9,9};
        var cross=repo.PublishReplacing(other,saved.Header.RevisionId);
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RunId=="run-b" && repo.Load(cross.Header.RevisionId).World.SequenceEqual(other.World),"cross-run overwrite preserves new world and run identity with one visible slot");
        string dir=Path.GetDirectoryName(cross.FilePath),marker=Path.Combine(dir,"dialogue_tx_"+cross.Header.TransactionId+".commit");
        string receipt=File.ReadAllText(marker);File.Delete(marker);File.Delete(Path.Combine(_root,"archive-overwrite","Backups","Transactions",cross.Header.TransactionId+".commit"));
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RevisionId==saved.Header.RevisionId,"missing overwrite receipt leaves old save visible");
        File.WriteAllText(marker,receipt);File.Move(cross.FilePath,cross.FilePath+".offline");
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RevisionId==saved.Header.RevisionId,"missing new overwrite body cannot delete old save");
        File.Move(cross.FilePath+".offline",cross.FilePath);
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RevisionId==cross.Header.RevisionId,"complete overwrite transaction becomes visible again");
        var occupied=repo.PublishChecked(Sample(run:"run-c",slot:"1"));
        Reject(()=>repo.PublishReplacing(Sample(run:"run-c",slot:"1"),cross.Header.RevisionId),"cross-run overwrite cannot replace unrelated occupied destination");
        repo.Delete(cross.Header.RevisionId);
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RevisionId==occupied.Header.RevisionId,"deleting replacement never resurrects overwritten save");
    }
    private static void VerifyArchiveEdits()
    {
        var repo=Repo("archive-edits");var a=Sample(slot:"1");a.World=new byte[]{1,2,3};a.Dialogue["background"]=123;a.Dialogue["speakerId"]=456;
        var first=repo.Publish(a);var b=Sample(slot:"2");b.World=new byte[]{9,8,7};var second=repo.Publish(b);
        var legacy=SaveCodec.Token(repo.Load(first.Header.RevisionId));
        Check(legacy["Header"]["TransactionId"]==null && legacy["Header"]["Comment"]==null && legacy["Header"]["PreviewImageUrl"]==null,"optional metadata does not alter legacy null serialization");
        repo.EditSlots("note",first.Header.RevisionId,"1",first.Header.RevisionId,"我的备注");
        var note=Repository.FindHeads(repo.Scan()).Single(r=>r.Header.LogicalSlot=="1");var noted=repo.Load(note.Header.RevisionId);
        Check(noted.Header.Comment=="我的备注" && noted.Header.SavedUtc==first.Header.CreatedUtc && noted.World.SequenceEqual(a.World) && JToken.DeepEquals(noted.Dialogue,a.Dialogue),"note is a new revision retaining timestamp world and continuation");
        Reject(()=>repo.EditSlots("copy",first.Header.RevisionId,"3",null),"stale source selection cannot copy");
        repo.EditSlots("copy",note.Header.RevisionId,"3",null);var copy=Repository.FindHeads(repo.Scan()).Single(r=>r.Header.LogicalSlot=="3");
        Check(copy.Header.RevisionId!=note.Header.RevisionId && repo.Load(copy.Header.RevisionId).World.SequenceEqual(a.World),"copy is independent and complete");
        Reject(()=>repo.EditSlots("copy",note.Header.RevisionId,"3",null),"stale empty target cannot overwrite");
        repo.EditSlots("swap",note.Header.RevisionId,"2",second.Header.RevisionId);
        var heads=Repository.FindHeads(repo.Scan());var one=heads.Single(r=>r.Header.LogicalSlot=="1");var two=heads.Single(r=>r.Header.LogicalSlot=="2");
        Check(repo.Load(one.Header.RevisionId).World.SequenceEqual(b.World) && repo.Load(two.Header.RevisionId).World.SequenceEqual(a.World) && two.Header.Comment=="我的备注","exchange moves both complete bodies and notes atomically");
        string directory=Path.GetDirectoryName(one.FilePath),marker=Path.Combine(directory,"dialogue_tx_"+one.Header.TransactionId+".commit");
        string receipt=File.ReadAllText(marker);File.Delete(marker);File.Delete(Path.Combine(_root,"archive-edits","Backups","Transactions",one.Header.TransactionId+".commit"));
        heads=Repository.FindHeads(repo.Scan());
        Check(heads.Any(r=>r.Header.RevisionId==note.Header.RevisionId) && heads.Any(r=>r.Header.RevisionId==second.Header.RevisionId) && repo.Scan().Count(r=>r.Status==SaveStatus.Pending)==2,"missing exchange receipt leaves both old slots visible");
        File.WriteAllText(marker,receipt);string moved=two.FilePath+".offline";File.Move(two.FilePath,moved);
        Check(!Repository.FindHeads(repo.Scan()).Any(r=>r.Header.RevisionId==one.Header.RevisionId),"receipt before second cloud body never exposes half an exchange");
        File.Move(moved,two.FilePath);string bytes=File.ReadAllText(two.FilePath);File.WriteAllText(two.FilePath,bytes.Replace("我的备注","坏的备注"));
        Check(!Repository.FindHeads(repo.Scan()).Any(r=>r.Header.RevisionId==one.Header.RevisionId),"corrupt peer also prevents half exchange");File.WriteAllText(two.FilePath,bytes);
        repo.Delete(one.Header.RevisionId);
        Check(Repository.FindHeads(repo.Scan()).Any(r=>r.Header.RevisionId==two.Header.RevisionId) && !Repository.FindHeads(repo.Scan()).Any(r=>r.Header.LogicalSlot=="1"),"deleting exchanged slot neither invalidates its peer nor resurrects ancestors");
        repo.EditSlots("swap",copy.Header.RevisionId,"4",null);heads=Repository.FindHeads(repo.Scan());
        Check(!heads.Any(r=>r.Header.LogicalSlot=="3") && repo.Load(heads.Single(r=>r.Header.LogicalSlot=="4").Header.RevisionId).World.SequenceEqual(a.World),"exchange with empty slot atomically moves and tombstones source");
        var cold=Repo("archive-edits");var preview=cold.Scan().First(r=>r.Header.RevisionId==first.Header.RevisionId);
        Check(preview.PreviewBackgroundId==123 && preview.PreviewSpeakerId==456,"old-format preview metadata comes from verified body without rewriting it");
        var withoutCg=Sample(slot:"20");withoutCg.Dialogue["cg"]=JValue.CreateNull();var plain=repo.Publish(withoutCg);
        Check(repo.Load(plain.Header.RevisionId).Dialogue["cg"].Type==JTokenType.Null,"ordinary null CG snapshot publishes and loads");
        var withCg=Sample(slot:"21");withCg.Dialogue["cg"]=new JObject{["id"]=101,["url"]="cg/test",["mini"]=false,["scale"]=new JArray(1.05f,1.05f,1.05f)};withCg.Header.PreviewImageUrl="cg/test";var cgSave=repo.Publish(withCg);
        var scanCg=Repo("archive-edits").Scan().Single(r=>r.Header.RevisionId==cgSave.Header.RevisionId);
        Check(scanCg.PreviewImageUrl=="cg/test" && scanCg.Header.PreviewImageUrl=="cg/test","CG resource survives disk scan and header roundtrip");
        Check(Repo("archive-edits").Scan().Single(r=>r.Header.RevisionId==plain.Header.RevisionId).Status==SaveStatus.Ready,"null CG remains valid on cold scan");
        var conflicting=repo.Publish(Sample(slot:"2"));Reject(()=>repo.EditSlots("note",two.Header.RevisionId,"2",two.Header.RevisionId,"x"),"editing an unresolved device branch is rejected");
        var nativeDir=Path.Combine(_root,"native-edits");Directory.CreateDirectory(nativeDir);var backup=Path.Combine(_root,"native-backup");
        File.WriteAllText(Path.Combine(nativeDir,"a.save"),"original-a");File.WriteAllText(Path.Combine(nativeDir,"b.save"),"original-b");
        var layout=JObject.Parse("{slots:{'1':'a.save','2':'b.save'},notes:{'a.save':'note-a'}}");string index=Path.Combine(nativeDir,"dialogue_native_layout.json");
        NativeSlotEdits.Apply(nativeDir,backup,"",layout,"copy",1,2,null,"copy.save");layout=JObject.Parse(File.ReadAllText(index));
        Check(File.ReadAllText(Path.Combine(nativeDir,"copy.save"))=="original-a" && File.ReadAllText(Path.Combine(nativeDir,"a.save"))=="original-a" && Directory.GetFiles(backup).Any(p=>File.ReadAllText(p)=="original-b") && (string)layout["notes"]["copy.save"]=="note-a","native replacement retains old target and copies complete source plus note");
        Reject(()=>NativeSlotEdits.Apply(nativeDir,backup,"",layout,"delete",1,1,null),"native stale index rejected before deletion");
        NativeSlotEdits.Apply(nativeDir,backup,NativeSlotEdits.Fingerprint(index),layout,"swap",1,4,null);layout=JObject.Parse(File.ReadAllText(index));
        Check(layout["slots"]["1"]==null && (string)layout["slots"]["4"]=="a.save" && File.Exists(Path.Combine(nativeDir,"a.save")),"native empty exchange changes arrangement without touching game body");
        NativeSlotEdits.Apply(nativeDir,backup,NativeSlotEdits.Fingerprint(index),layout,"delete",4,4,null);layout=JObject.Parse(File.ReadAllText(index));
        Check(!File.Exists(Path.Combine(nativeDir,"a.save")) && layout["hidden"].Values<string>().Contains("a.save") && Directory.GetFiles(backup).Any(p=>File.ReadAllText(p)=="original-a"),"native deletion durable index and independent backup survive interrupted cleanup");
        Reject(()=>NativeSlotEdits.Apply(nativeDir,backup,NativeSlotEdits.Fingerprint(index),layout,"copy",2,5,null,"../escape.save"),"native filename traversal rejected");
        string linked=Path.Combine(_root,"native-linked");Directory.CreateSymbolicLink(linked,nativeDir);
        Reject(()=>NativeSlotEdits.Apply(linked,backup,NativeSlotEdits.Fingerprint(index),layout,"note",2,2,"bad"),"native linked directory below volume root rejected");
        File.CreateSymbolicLink(Path.Combine(nativeDir,"linked.save"),Path.Combine(nativeDir,"copy.save"));
        var linkedLayout=(JObject)layout.DeepClone();linkedLayout["slots"]["9"]="linked.save";
        Reject(()=>NativeSlotEdits.Apply(nativeDir,backup,NativeSlotEdits.Fingerprint(index),linkedLayout,"note",9,9,"bad"),"native linked file rejected");
    }
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "publish-worker") { PublishWorker(args); return; }
        _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StorageSandbox", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        VerifyArchiveOverwrite();
        VerifyArchiveEdits();
        if(args.Contains("--archive-edits")){Console.WriteLine("ARCHIVE_TESTS_OK "+_count);return;}
        VerifyLocalCopies();
        VerifyScanCache();
        var repo = Repo("basic");
        var original = Sample();
        var first = repo.Publish(original);
        Check(original.Header.RevisionId == null, "publish does not mutate caller snapshot");
        var loaded = repo.Load(first.Header.RevisionId);
        Check(loaded.World.SequenceEqual(original.World) && JToken.DeepEquals(loaded.Dialogue, original.Dialogue) && loaded.Header.SeasonId == 2, "complete roundtrip world continuation and season metadata");
        Check(first.FilePath.EndsWith("dialogue_" + first.Header.RevisionId + ".dsav"), "independent flat filename");
        Check(!Directory.GetFiles(Path.Combine(_root, "basic", "Staging")).Any(), "successful commit leaves no staged file");
        string normalPath = Path.Combine(_root, "basic", "Saves", "slot.save");
        File.WriteAllText(normalPath, "normal-save-sentinel");
        Check(repo.Scan().Count == 1, "ordinary save ignored");

        var secondSnapshot = Sample(); secondSnapshot.Header.ParentRevisionIds = new[] { first.Header.RevisionId };
        var second = repo.Publish(secondSnapshot);
        Check(Repository.FindHeads(repo.Scan()).Single().Header.RevisionId == second.Header.RevisionId, "linear history selects descendant without timestamp");
        var forkSnapshot = Sample(); forkSnapshot.Header.DeviceId = "device-b"; forkSnapshot.Header.ParentRevisionIds = new[] { first.Header.RevisionId };
        var fork = repo.Publish(forkSnapshot);
        var forkHeads = Repository.FindHeads(repo.Scan());
        Check(forkHeads.Count == 2 && forkHeads.All(x => x.IsConflict), "offline same-slot branches preserved");
        repo.Publish(Sample(run: "different-run"));
        repo.Publish(Sample(category: "quick"));
        repo.Publish(Sample(slot: "slot-2"));
        var unrelated = Repository.FindHeads(repo.Scan());
        Check(unrelated.Count == 5 && unrelated.Count(x => x.IsConflict) == 2, "run category and slot isolation");
        var merged = Sample(); merged.Header.ParentRevisionIds = new[] { second.Header.RevisionId, fork.Header.RevisionId };
        var mergeRecord = repo.Publish(merged);
        Check(!Repository.FindHeads(repo.Scan()).Any(x => x.IsConflict), "explicit merge resolves both parents");

        Reject(() => repo.Load("../slot.save"), "load path traversal rejected");
        Reject(() => repo.Delete("../slot.save"), "delete path traversal rejected");
        Reject(() => repo.Load(Guid.NewGuid().ToString("N")), "missing revision rejected");
        Reject(() => new Repository(Path.Combine(_root, "bad"), Path.Combine(_root, "bad", "stage"), Path.Combine(_root, "backup")), "stage inside cloud root rejected");
        Reject(() => new Repository("relative", Path.Combine(_root, "stage"), Path.Combine(_root, "backup")), "relative path rejected");
        var invalid = Sample(); invalid.Header.Category = "normal";
        Reject(() => repo.Publish(invalid), "unknown category rejected");
        invalid = Sample(); invalid.World = new byte[SaveCodec.MaximumWorldBytes + 1];
        Reject(() => repo.Publish(invalid), "oversized world rejected before publication");
        invalid = Sample(); invalid.Header.ParentRevisionIds = new[] { "../normal.save" };
        Reject(() => repo.Publish(invalid), "invalid parent id rejected");

        string blockedRoot = Path.Combine(_root, "blocked"); Directory.CreateDirectory(blockedRoot);
        string blockedStage = Path.Combine(blockedRoot, "StageIsAFile"); File.WriteAllText(blockedStage, "block");
        var blocked = new Repository(Path.Combine(blockedRoot, "Saves"), blockedStage, Path.Combine(blockedRoot, "Backups"));
        Reject(() => blocked.Publish(Sample()), "staging failure rejects save transaction");
        Check(!Directory.GetFiles(Path.Combine(blockedRoot, "Saves")).Any(), "failed transaction publishes no partial cloud file");
        string linkRoot = Path.Combine(_root, "links"); Directory.CreateDirectory(linkRoot);
        Directory.CreateSymbolicLink(Path.Combine(linkRoot, "Redirect"), Path.Combine(_root, "basic", "Saves"));
        Reject(() => new Repository(Path.Combine(linkRoot, "Redirect"), Path.Combine(linkRoot, "Stage"), Path.Combine(linkRoot, "Backup")), "symlink directory cannot escape repository ownership");
        Reject(() => new Repository(Path.Combine(linkRoot, "Redirect", "Descendant"), Path.Combine(linkRoot, "Stage"), Path.Combine(linkRoot, "Backup")), "linked ancestor below volume root still refused");
        try { new Repository(Path.Combine(linkRoot, "Redirect"), Path.Combine(linkRoot, "Stage"), Path.Combine(linkRoot, "Backup")); }
        catch (IOException ex) { Check(ex.Message.Contains(Path.Combine(linkRoot, "Redirect")), "link refusal identifies concrete offending directory"); }

        var corruptRepo = Repo("corrupt");
        var corrupted = corruptRepo.Publish(Sample());
        var bytes = JObject.Parse(File.ReadAllText(corrupted.FilePath)); bytes["Dialogue"]["talkId"] = 999;
        File.WriteAllText(corrupted.FilePath, bytes.ToString(Formatting.None));
        Reject(() => corruptRepo.Load(corrupted.Header.RevisionId), "changed dialogue checksum rejected");
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "corrupted card remains listed");
        string raw = File.ReadAllText(first.FilePath);
        string modifiedHeader = raw.Replace("仅为合成存储测试", "篡改摘要");
        File.WriteAllText(first.FilePath, modifiedHeader);
        Reject(() => repo.Load(first.Header.RevisionId), "header tampering is covered by envelope checksum");
        File.WriteAllText(first.FilePath, raw);
        File.WriteAllText(corrupted.FilePath, raw.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1"));
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "duplicate keys rejected");
        File.WriteAllText(corrupted.FilePath, "{\"SchemaVersion\":1,\"$type\":\"System.IO.FileInfo\"}");
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "type metadata rejected");
        File.WriteAllText(corrupted.FilePath, "{\"SchemaVersion\":1,\"Header\":");
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "interrupted file remains nonloadable");
        File.WriteAllText(corrupted.FilePath, "{\"a\":" + new string('[', 66) + "1" + new string(']', 66) + "}");
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "depth limit enforced");
        using (var huge = new FileStream(corrupted.FilePath, FileMode.Create)) huge.SetLength((long)SaveCodec.MaximumFileBytes + 1);
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "oversized sparse file refused");
        File.WriteAllText(corrupted.FilePath, raw);
        Check(corruptRepo.Scan().Single().Status == SaveStatus.Corrupt, "renamed revision is rejected");

        var futureRepo = Repo("future");
        var future = futureRepo.Publish(Sample());
        var futureJson = JObject.Parse(File.ReadAllText(future.FilePath));
        futureJson["SchemaVersion"] = 9; futureJson["FutureField"] = "unknown";
        File.WriteAllText(future.FilePath, futureJson.ToString(Formatting.None));
        var futureCard = futureRepo.Scan().Single();
        Check(futureCard.Status == SaveStatus.UnsupportedVersion && futureCard.SchemaVersion == 9 && futureCard.Header.Summary == "仅为合成存储测试", "future schema shown with summary but not loaded");
        Reject(() => futureRepo.Load(future.Header.RevisionId), "future schema load refused");
        futureRepo.Publish(Sample());
        Check(File.ReadAllText(future.FilePath).Contains("FutureField"), "publishing never overwrites future schema");

        string interrupted = Path.Combine(_root, "basic", "Staging", "unfinished.pending"); File.WriteAllText(interrupted, "partial");
        Check(repo.Scan().All(x => x.Status == SaveStatus.Ready), "uncommitted staging file never enters save list");
        int countBefore = Repository.FindHeads(repo.Scan()).Count;
        string deletedBody = File.ReadAllText(mergeRecord.FilePath);
        repo.Delete(mergeRecord.Header.RevisionId);
        Check(Repository.FindHeads(repo.Scan()).Count == countBefore - 1 && Directory.GetFiles(Path.Combine(_root, "basic", "Backups")).Length == 1, "explicit deletion creates local backup without resurrecting parents");
        File.WriteAllText(mergeRecord.FilePath, deletedBody);
        Check(Repository.FindHeads(repo.Scan()).Count == countBefore - 1, "offline reappearance remains deleted through tombstone");
        Reject(() => repo.Load(mergeRecord.Header.RevisionId), "deleted revision cannot be loaded directly");
        var tombstone = repo.Scan().Single(x => x.Status == SaveStatus.Deleted);
        Reject(() => repo.Load(tombstone.Header.RevisionId), "tombstone cannot be loaded as world");
        Check(File.ReadAllText(normalPath) == "normal-save-sentinel", "ordinary save bytes unchanged after all operations");
        var reopened = Repo("basic");
        Check(Repository.FindHeads(reopened.Scan()).Count == countBefore - 1, "repository recreation retains archives without shared index");

        string quotaRoot = Path.Combine(_root, "quota");
        string quotaSave = Path.Combine(quotaRoot, "Saves"), quotaStage = Path.Combine(quotaRoot, "Stage"), quotaBackup = Path.Combine(quotaRoot, "Backup");
        var unlimitedQuotaFixture = new Repository(quotaSave, quotaStage, quotaBackup);
        var quotaFirst = unlimitedQuotaFixture.Publish(Sample());
        string sentinel = File.ReadAllText(quotaFirst.FilePath);
        long fileLength = new FileInfo(quotaFirst.FilePath).Length;
        var exactQuota = new Repository(quotaSave, quotaStage, quotaBackup, fileLength * 2, 10);
        var quotaSecond = exactQuota.Publish(Sample());
        Check(Directory.GetFiles(quotaSave).Sum(p => new FileInfo(p).Length) == fileLength * 2, "publication at exact byte quota succeeds");
        Reject(() => exactQuota.Publish(Sample()), "next byte quota publication is rejected");
        Check(Directory.GetFiles(quotaSave).Length == 2 && Directory.GetFiles(quotaStage).Length == 0 && File.ReadAllText(quotaFirst.FilePath) == sentinel, "quota failure publishes nothing and keeps original bytes");
        var countQuota = new Repository(quotaSave, quotaStage, quotaBackup, long.MaxValue - 16L * 1024 * 1024, 2);
        Reject(() => countQuota.Publish(Sample()), "file count quota blocks a new revision");
        exactQuota.Delete(quotaSecond.Header.RevisionId);
        Check(exactQuota.Scan().Any(x => x.Status == SaveStatus.Deleted), "reserved tombstone quota permits deletion when data quota is full");

        var closureRepo = Repo("deletion-closure");
        var ancestorA = closureRepo.Publish(Sample());
        var sampleB = Sample(); sampleB.Header.ParentRevisionIds = new[] { ancestorA.Header.RevisionId };
        var ancestorB = closureRepo.Publish(sampleB);
        var sampleC = Sample(); sampleC.Header.ParentRevisionIds = new[] { ancestorB.Header.RevisionId };
        var descendantC = closureRepo.Publish(sampleC);
        var sampleD = Sample(); sampleD.Header.ParentRevisionIds = new[] { ancestorB.Header.RevisionId };
        var siblingD = closureRepo.Publish(sampleD);
        string siblingBody = File.ReadAllText(siblingD.FilePath);
        closureRepo.Delete(descendantC.Header.RevisionId);
        var fullMarker = closureRepo.Scan().Single(r => r.Status == SaveStatus.Deleted);
        Check(fullMarker.Header.ParentRevisionIds.OrderBy(x => x).SequenceEqual(
            new[] { ancestorA.Header.RevisionId, ancestorB.Header.RevisionId, descendantC.Header.RevisionId }.OrderBy(x => x)),
            "deletion marker contains complete ancestry and excludes live sibling");
        File.Delete(ancestorB.FilePath);
        File.Delete(siblingD.FilePath);
        Check(closureRepo.Scan().Count == 2 && !Repository.FindHeads(closureRepo.Scan()).Any(),
            "only oldest ancestor and tombstone arriving from cloud cannot resurrect ancestor");
        Reject(() => closureRepo.Load(ancestorA.Header.RevisionId), "oldest ancestor cannot load without intermediate cloud history");
        File.WriteAllText(siblingD.FilePath, siblingBody);
        Check(Repository.FindHeads(closureRepo.Scan()).Single().Header.RevisionId == siblingD.Header.RevisionId &&
            closureRepo.Load(siblingD.Header.RevisionId).World.SequenceEqual(Sample().World),
            "deleting one branch preserves another live branch sharing deleted ancestors");
        closureRepo.Delete(siblingD.Header.RevisionId);
        Check(!Repository.FindHeads(closureRepo.Scan()).Any(), "complete prior tombstone proves missing ancestry for later branch deletion");

        var incompleteRepo = Repo("incomplete-delete");
        var incomplete = Sample(); incomplete.Header.ParentRevisionIds = new[] { Guid.NewGuid().ToString("N") };
        var incompleteRecord = incompleteRepo.Publish(incomplete);
        string incompleteBody = File.ReadAllText(incompleteRecord.FilePath);
        Reject(() => incompleteRepo.Delete(incompleteRecord.Header.RevisionId), "missing unproven ancestor rejects deletion");
        Check(File.ReadAllText(incompleteRecord.FilePath) == incompleteBody && incompleteRepo.Scan().Count == 1 &&
            !Directory.Exists(Path.Combine(_root, "incomplete-delete", "Backups")),
            "incomplete history rejection creates neither backup nor tombstone and keeps source unchanged");
        var foreign = incompleteRepo.Publish(Sample(run: "different-run"));
        var crossRun = Sample(); crossRun.Header.ParentRevisionIds = new[] { foreign.Header.RevisionId };
        var crossRunRecord = incompleteRepo.Publish(crossRun);
        Reject(() => incompleteRepo.Delete(crossRunRecord.Header.RevisionId), "another run cannot supply proof of missing slot ancestry");

        var longRepo = Repo("long-delete");
        SaveRecord longHead = null;
        for (int i = 0; i < 35; i++)
        {
            var item = Sample();
            if (longHead != null) item.Header.ParentRevisionIds = new[] { longHead.Header.RevisionId };
            longHead = longRepo.Publish(item);
        }
        longRepo.Delete(longHead.Header.RevisionId);
        Check(longRepo.Scan().Single(r => r.Status == SaveStatus.Deleted).Header.ParentRevisionIds.Length == 35 &&
            !Repository.FindHeads(longRepo.Scan()).Any(), "complete tombstone supports more than legacy 32 ancestor IDs");
        var tooManyParents = Sample(); tooManyParents.Header.ParentRevisionIds = Enumerable.Range(0, 17).Select(i => Guid.NewGuid().ToString("N")).ToArray();
        Reject(() => longRepo.Publish(tooManyParents), "ordinary checkpoints retain 16 direct-parent limit");

        var checkedRepo = Repo("checked");
        var checkedParent = checkedRepo.PublishChecked(Sample());
        var competingRepo = Repo("checked");
        var results = new SaveRecord[2]; var errors = new Exception[2];
        using (var start = new Barrier(2))
        {
            var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
            {
                var snapshot = Sample(); snapshot.Header.ParentRevisionIds = new[] { checkedParent.Header.RevisionId };
                snapshot.Header.DeviceId = "competing-" + i;
                start.SignalAndWait();
                try { results[i] = (i == 0 ? checkedRepo : competingRepo).PublishChecked(snapshot); }
                catch (Exception ex) { errors[i] = ex; }
            })).ToArray();
            Check(Task.WaitAll(tasks, TimeSpan.FromSeconds(15)), "competing repository instances finish within bounded time");
        }
        Check(results.Count(r => r != null) == 1 && errors.Count(e => e is InvalidDataException) == 1,
            "two repository instances using one parent commit once and report the competing revision");
        var checkedRecords = checkedRepo.Scan();
        Check(checkedRecords.Count == 2 && checkedRecords.All(r => r.Status == SaveStatus.Ready) &&
            checkedRecords.All(r => checkedRepo.Load(r.Header.RevisionId).World.SequenceEqual(Sample().World)) &&
            Repository.FindHeads(checkedRecords).Count == 1,
            "concurrent checked publication keeps complete validated bytes and one head");
        Check(!Directory.GetFiles(Path.Combine(_root, "checked", "Staging")).Any(), "checked conflict removes temporary files outside lock directory");

        string processRoot = Path.Combine(_root, "processes");
        var processRepo = Repo("processes");
        var processParent = processRepo.PublishChecked(Sample());
        string signal = Path.Combine(processRoot, "start");
        var processes = Enumerable.Range(0, 2).Select(i => StartWorker(processRoot, processParent.Header.RevisionId,
            signal, Path.Combine(processRoot, "result-" + i))).ToArray();
        File.WriteAllText(signal, "go");
        foreach (var process in processes)
        {
            bool finished = process.WaitForExit(15000);
            if (!finished) process.Kill(true);
            Check(finished && process.ExitCode == 0, "isolated worker process terminates successfully");
            process.Dispose();
        }
        var processResults = Enumerable.Range(0, 2).Select(i => File.ReadAllText(Path.Combine(processRoot, "result-" + i))).ToArray();
        Check(processResults.Count(s => s.StartsWith("ok:")) == 1 && processResults.Count(s => s.StartsWith("conflict:")) == 1,
            "cross-process write lock serializes expected-head check and publication");
        Check(processRepo.Scan().Count == 2 && processRepo.Scan().All(r => r.Status == SaveStatus.Ready), "cross-process winner and parent both pass integrity validation");

        string lockPath = Path.Combine(_root, "checked", "Staging", "Locks", "publish.lock");
        var waitingSnapshot = Sample(); waitingSnapshot.Header.ParentRevisionIds = new[] { results.Single(r => r != null).Header.RevisionId };
        var waitWatch = Stopwatch.StartNew(); bool lockTimedOut = false;
        using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { competingRepo.PublishChecked(waitingSnapshot); }
            catch (IOException ex) { lockTimedOut = ex.Message.Contains("等待超时"); }
        }
        Check(lockTimedOut && waitWatch.Elapsed < TimeSpan.FromSeconds(10) && checkedRepo.Scan().Count == 2,
            "busy cross-process lock times out clearly without publishing a file");

        string noLimitRoot = Path.Combine(_root, "default-no-quota");
        var noLimit = Repo("default-no-quota"); noLimit.Publish(Sample());
        string noLimitSave = Path.Combine(noLimitRoot, "Saves");
        string largeForeign = Path.Combine(noLimitSave, "foreign-large.dsav");
        using (var sparse = new FileStream(largeForeign, FileMode.CreateNew)) sparse.SetLength(2L * 1024 * 1024 * 1024 + 1);
        var aboveOldBytes = noLimit.Publish(Sample(slot: "after-2gib"));
        Check(noLimit.Load(aboveOldBytes.Header.RevisionId).World.SequenceEqual(Sample().World), "default repository has no former 2 GiB total-history limit");
        for (int i = 0; i < 4000; i++) File.WriteAllBytes(Path.Combine(noLimitSave, "foreign-" + i + ".dsav"), Array.Empty<byte>());
        var aboveOldCount = noLimit.Publish(Sample(slot: "after-4000"));
        Check(noLimit.Load(aboveOldCount.Header.RevisionId).World.SequenceEqual(Sample().World) &&
            Directory.GetFiles(noLimitSave).Length == 4004 && new FileInfo(largeForeign).Length > 2L * 1024 * 1024 * 1024,
            "default repository passes 4000 files without deleting unknown files or save history");
        Console.WriteLine("STORAGE_TESTS_OK " + _count);
        Console.WriteLine("ISOLATED_DATA " + _root);
    }

    private static Process StartWorker(string root, string parent, string signal, string output)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(StorageTests).Assembly.Location);
        foreach (string arg in new[] { "publish-worker", root, parent, signal, output }) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }

    private static void PublishWorker(string[] args)
    {
        var repository = new Repository(Path.Combine(args[1], "Saves"), Path.Combine(args[1], "Staging"), Path.Combine(args[1], "Backups"));
        var snapshot = Sample(); snapshot.Header.ParentRevisionIds = new[] { args[2] };
        var timer = Stopwatch.StartNew();
        while (!File.Exists(args[3]) && timer.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        if (!File.Exists(args[3])) throw new TimeoutException("Isolated worker start signal missing");
        try { File.WriteAllText(args[4], "ok:" + repository.PublishChecked(snapshot).Header.RevisionId); }
        catch (InvalidDataException ex) { File.WriteAllText(args[4], "conflict:" + ex.Message); }
    }
}
