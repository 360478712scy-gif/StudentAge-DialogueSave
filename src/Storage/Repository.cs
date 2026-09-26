using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;

namespace StudentAgeDialogueSave.Storage
{
    /// <summary>Owns only dialogue_*.dsav files. There is intentionally no uninstall cleanup.</summary>
    public sealed partial class Repository
    {
        private readonly string _saveDirectory;
        private readonly string _stagingDirectory;
        private readonly string _backupDirectory;
        private readonly bool _retainLocalCopies;
        private readonly Action<string> _diagnostics;
        private readonly object _gate = new object();
        sealed class VerifiedRow { internal string Hash; internal SaveRecord Record; }
        readonly Dictionary<string,VerifiedRow> verifiedRows=new Dictionary<string,VerifiedRow>(StringComparer.Ordinal);
        internal int LastScanDecodedFiles {get;private set;}
        internal int LastScanReusedFiles {get;private set;}
        static SaveRecord CopyRecord(SaveRecord row)=>new SaveRecord{SchemaVersion=row.SchemaVersion,Header=row.Header?.DetachedCopy(),FilePath=row.FilePath,Status=row.Status,Error=row.Error,PreviewBackgroundId=row.PreviewBackgroundId,PreviewSpeakerId=row.PreviewSpeakerId,PreviewImageUrl=row.PreviewImageUrl,PreviewText=row.PreviewText,PreviewOptionIds=row.PreviewOptionIds};
        private readonly long? _maximumTotalBytes;
        private readonly int? _maximumFiles;
        private const long TombstoneReserveBytes = 16L * 1024 * 1024;
        private const int TombstoneReserveFiles = 256;

        public Repository(string saveDirectory, string stagingDirectory, string backupDirectory,
            long? maximumTotalBytes = null, int? maximumFiles = null, bool retainLocalCopies = false, Action<string> diagnostics = null)
        {
            if ((maximumTotalBytes.HasValue && (maximumTotalBytes.Value < 1 || maximumTotalBytes.Value > long.MaxValue - TombstoneReserveBytes)) ||
                (maximumFiles.HasValue && (maximumFiles.Value < 1 || maximumFiles.Value > int.MaxValue - TombstoneReserveFiles)))
                throw new ArgumentOutOfRangeException("存档限额必须为有效正数。");
            _retainLocalCopies = retainLocalCopies;
            _diagnostics = diagnostics;
            _maximumTotalBytes = maximumTotalBytes;
            _maximumFiles = maximumFiles;
            _saveDirectory = FullDirectory(saveDirectory);
            _stagingDirectory = FullDirectory(stagingDirectory);
            _backupDirectory = FullDirectory(backupDirectory);
            if (Within(_stagingDirectory, _saveDirectory) || Within(_backupDirectory, _saveDirectory) ||
                Within(_saveDirectory, _stagingDirectory) || Within(_saveDirectory, _backupDirectory) ||
                Within(_stagingDirectory, _backupDirectory) || Within(_backupDirectory, _stagingDirectory))
                throw new ArgumentException("暂存、备份和存档目录必须相互独立。");
            if (!string.Equals(Path.GetPathRoot(_saveDirectory), Path.GetPathRoot(_stagingDirectory), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("暂存目录必须与存档位于同卷。");
            CheckDirectoryLinks(_saveDirectory); CheckDirectoryLinks(_stagingDirectory); CheckDirectoryLinks(_backupDirectory);
        }

        public SaveRecord Publish(SaveEnvelope snapshot)
        {
            return PublishCheckpoint(snapshot, false);
        }

        /// <summary>Compare the complete expected head set and publish under one local cross-process write lock.</summary>
        public SaveRecord PublishChecked(SaveEnvelope snapshot)
        {
            return PublishCheckpoint(snapshot, true);
        }

        private SaveRecord PublishCheckpoint(SaveEnvelope snapshot, bool checkParents)
        {
            if (snapshot == null || snapshot.Kind != "checkpoint") throw new InvalidDataException("只能提交对话检查点。");
            if (snapshot.Header != null && snapshot.Header.ParentRevisionIds != null && snapshot.Header.ParentRevisionIds.Length > 16)
                throw new InvalidDataException("合并父版本数量过多。");
            lock (_gate)
            using (AcquireWriteLock())
                return PublishInternal(snapshot, checkParents);
        }

        private SaveRecord PublishInternal(SaveEnvelope snapshot, bool checkParents = false)
        {
            lock (_gate)
            {
                if (snapshot == null || snapshot.SchemaVersion != 1) throw new InvalidDataException("不支持的存档格式。");
                if (snapshot.World == null || snapshot.World.Length > SaveCodec.MaximumWorldBytes || snapshot.Dialogue == null)
                    throw new InvalidDataException("存档数据缺失或超限。");
                // Detach all user-owned objects before assigning repository metadata.
                var value = new SaveEnvelope{SchemaVersion=snapshot.SchemaVersion,Kind=snapshot.Kind,Header=snapshot.Header?.DetachedCopy(),
                    World=(byte[])snapshot.World.Clone(),Dialogue=(Newtonsoft.Json.Linq.JObject)snapshot.Dialogue.DeepClone()};
                if (value.Header == null) throw new InvalidDataException("存档头部缺失。");
                value.Header.RevisionId = Guid.NewGuid().ToString("N");
                value.Header.CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveCodec.Validate(value);
                value.Header.PayloadSha256 = SaveCodec.PayloadHash(value);
                var token = SaveCodec.Token(value);
                value.EnvelopeSha256 = SaveCodec.EnvelopeHash(token);
                token["EnvelopeSha256"] = value.EnvelopeSha256;
                byte[] bytes = SaveCodec.Utf8.GetBytes(token.ToString(Formatting.None));
                if (bytes.Length > SaveCodec.MaximumFileBytes) throw new InvalidDataException("存档大小超出限制。");
                EnsureDirectories();
                if (checkParents) CheckExpectedHeads(value.Header);
                CheckQuota(bytes.Length, value.Kind == "tombstone");
                string stage = Path.Combine(_stagingDirectory, value.Header.RevisionId + ".pending");
                string target = PathFor(value.Header.RevisionId);
                try
                {
                    using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        output.Write(bytes, 0, bytes.Length);
                        output.Flush(true);
                    }
                    SaveCodec.Decode(SaveCodec.Read(stage));
                    // Account for cloud files arriving while the temporary file was written.
                    CheckQuota(bytes.Length, value.Kind == "tombstone");
                    // Cooperative local writers cannot enter until publication completes.
                    // A cloud client does not honor this lock, so also recheck immediately
                    // before publication and preserve immutable branches arriving afterward.
                    if (checkParents) CheckExpectedHeads(value.Header);
                    // No copy fallback: a cross-device publish must fail, not expose a partial file.
                    AtomicPublish(stage, target);
                    PreserveLocalCopy(target);
                    var published=new SaveRecord { PreviewImageUrl=(string)(value.Dialogue?["cg"] as Newtonsoft.Json.Linq.JObject)?["url"],PreviewBackgroundId=(int?)value.Dialogue?["background"]??0,PreviewSpeakerId=(int?)value.Dialogue?["speakerId"]??0, Header = value.Header, FilePath = target, SchemaVersion = 1, Status = value.Kind == "tombstone" ? SaveStatus.Deleted : SaveStatus.Ready };
                    verifiedRows[target]=new VerifiedRow{Hash=SaveCodec.Hash(bytes),Record=CopyRecord(published)};
                    return published;
                }
                finally
                {
                    // A leftover non-cloud staging file is harmless; cleanup failure must not
                    // report a committed checkpoint as failed and invite a duplicate retry.
                    try { if (File.Exists(stage)) File.Delete(stage); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public SaveEnvelope Load(string revisionId)
        {
            lock (_gate)
            {
                SaveCodec.Revision(revisionId);
                var records = Scan(); // Recover missing bodies before opening; respect deletion markers.
                string path = PathFor(revisionId);
                CheckDirectoryLinks(_saveDirectory); CheckFileLink(path);
                SaveEnvelope value = SaveCodec.Decode(SaveCodec.Read(path));
                if (value.Header.RevisionId != revisionId) throw new InvalidDataException("文件名与存档版本不一致。");
                if(!Committed(value.Header))throw new InvalidDataException("交换事务尚未完整同步，请稍后再读。");
                if (value.Kind != "checkpoint") throw new InvalidDataException("此版本是删除标记，不能加载。");
                if (records.Any(x => x.Status == SaveStatus.Deleted && SlotKey(x.Header) == SlotKey(value.Header) && x.Header.ParentRevisionIds.Contains(revisionId)))
                    throw new InvalidDataException("此存档已删除。");
                return value;
            }
        }

        public List<SaveRecord> Scan()
        {
            lock (_gate)
            {
                CheckDirectoryLinks(_saveDirectory);
                RestoreMissingLocalCopies();
                var retained=RetainedNames();
                LastScanDecodedFiles=LastScanReusedFiles=0;
                var records = new List<SaveRecord>();
                if (!Directory.Exists(_saveDirectory)) return records;
                foreach (string path in Directory.GetFiles(_saveDirectory, "dialogue_*.dsav", SearchOption.TopDirectoryOnly))
                {
                    var record = new SaveRecord { FilePath = path, Status = SaveStatus.Corrupt };
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(path).Substring("dialogue_".Length);
                        SaveCodec.Revision(name);
                        CheckFileLink(path);
                        // Always read/hash current bytes, including conflict checks. Size,
                        // mtime and TTL cannot prove that a cloud/external edit is unchanged.
                        var bytes=SaveCodec.ReadBytes(path);string hash=SaveCodec.Hash(bytes);
                        if(verifiedRows.TryGetValue(path,out var cached) && cached.Hash==hash)
                        {
                            records.Add(CopyRecord(cached.Record));LastScanReusedFiles++;
                            if(cached.Record.Status==SaveStatus.Ready || cached.Record.Status==SaveStatus.Deleted)PreserveLocalCopy(path,retained);
                            continue;
                        }
                        LastScanDecodedFiles++;
                        var root = SaveCodec.Read(bytes);
                        record.SchemaVersion = (int?)root["SchemaVersion"] ?? 0;
                        if (record.SchemaVersion > 1)
                        {
                            record.Header = SaveCodec.FutureHeader(root);
                            if (record.Header.RevisionId != name) throw new InvalidDataException("文件名与存档版本不一致。");
                            record.Status = SaveStatus.UnsupportedVersion;
                            record.Error = "此存档由更高版本插件创建，仅可查看信息。";
                        }
                        else
                        {
                            SaveEnvelope value = SaveCodec.Decode(root);
                            if (value.Header.RevisionId != name) throw new InvalidDataException("文件名与存档版本不一致。");
                            record.PreviewImageUrl=(string)(value.Dialogue?["cg"] as Newtonsoft.Json.Linq.JObject)?["url"];
                            record.PreviewBackgroundId=(int?)value.Dialogue?["background"]??0;
                            record.PreviewSpeakerId=(int?)value.Dialogue?["speakerId"]??0;
                            var segments=value.Dialogue?["segments"] as Newtonsoft.Json.Linq.JArray;int index=(int?)value.Dialogue?["segmentIndex"]??-1;
                            record.PreviewText=(string)value.Dialogue?["choiceSummary"] ?? (string)(segments!=null && index>=0 && index<segments.Count?segments[index]:value.Dialogue?["talk"]);
                            if((string)value.Dialogue?["phase"]=="Option" && value.Dialogue?["options"] is Newtonsoft.Json.Linq.JArray opts)
                                record.PreviewOptionIds=opts.Select(o=>(int)o["id"]).ToArray();
                            record.Header = value.Header;
                            record.Status = value.Kind == "tombstone" ? SaveStatus.Deleted : SaveStatus.Ready;
                            PreserveLocalCopy(path,retained);
                        }
                        verifiedRows[path]=new VerifiedRow{Hash=hash,Record=CopyRecord(record)};
                    }
                    catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is JsonException || error is ArgumentException || error is OverflowException || error is FormatException)
                    {
                        record.Status = SaveStatus.Corrupt;
                        record.Error = error.Message;
                        verifiedRows.Remove(path);
                    }
                    records.Add(record);
                }
                foreach(var row in records)if((row.Status==SaveStatus.Ready || row.Status==SaveStatus.Deleted) && !Committed(row.Header))row.Status=SaveStatus.Pending;
                FindHeads(records);
                var live=new HashSet<string>(records.Select(r=>r.FilePath),StringComparer.Ordinal);
                foreach(string missing in verifiedRows.Keys.Where(p=>!live.Contains(p)).ToArray())verifiedRows.Remove(missing);
                return records;
            }
        }

        // Retained revisions live outside the game's cloud save directory and plugin install.
        // The worker running publication/listing owns this I/O; no main-thread copying.
        private string LocalCopies => Path.Combine(_backupDirectory, "Retained");
        private HashSet<string> RetainedNames()
        {
            if(!_retainLocalCopies)return null;
            try
            {
                CheckDirectoryLinks(LocalCopies);
                return new HashSet<string>(Directory.Exists(LocalCopies)?Directory.GetFiles(LocalCopies,"dialogue_*.dsav").Select(Path.GetFileName):Enumerable.Empty<string>(),StringComparer.Ordinal);
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException){return null;}
        }
        private void PreserveLocalCopy(string source,HashSet<string> existing=null)
        {
            if (!_retainLocalCopies || existing?.Contains(Path.GetFileName(source))==true) return;
            try
            {
                CheckDirectoryLinks(LocalCopies);
                Directory.CreateDirectory(LocalCopies);
                string target = Path.Combine(LocalCopies, Path.GetFileName(source));
                if (!File.Exists(target)) CopyVerifiedRevision(source, target, LocalCopies);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Win32Exception || ex is JsonException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { LocalCopyWarning("保留本地副本失败，主存档未改动：" + ex.Message); }
        }
        private void RestoreMissingLocalCopies()
        {
            if (!_retainLocalCopies) return;
            try
            {
                CheckDirectoryLinks(LocalCopies);
                if (!Directory.Exists(LocalCopies)) return;
                // Include tombstones: cloud disappearance must not resurrect deliberately
                // deleted saves, nor select one branch over another by timestamp.
                Directory.CreateDirectory(_saveDirectory);
                var present=new HashSet<string>(Directory.GetFiles(_saveDirectory,"dialogue_*.dsav").Select(Path.GetFileName),StringComparer.Ordinal);
                foreach (string source in Directory.GetFiles(LocalCopies, "dialogue_*.dsav"))
                {
                    string target = Path.Combine(_saveDirectory, Path.GetFileName(source));
                    if (present.Contains(Path.GetFileName(source))) continue; // Atomic publication still never overwrites a newly arriving file.
                    try { CopyVerifiedRevision(source, target, _stagingDirectory); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Win32Exception || ex is JsonException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
                    { LocalCopyWarning("本地副本恢复未完成，原文件保留：" + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { LocalCopyWarning("本地副本目录不可用：" + ex.Message); }
        }
        private static void CopyVerifiedRevision(string source, string target, string stagingDirectory)
        {
            CheckFileLink(source); CheckFileLink(target);
            var sourceBytes=SaveCodec.ReadBytes(source);
            var value = SaveCodec.Decode(SaveCodec.Read(sourceBytes));
            string expected = "dialogue_" + value.Header.RevisionId + ".dsav";
            if (Path.GetFileName(source) != expected || Path.GetFileName(target) != expected)
                throw new InvalidDataException("本地副本文件名与存档版本不一致。");
            CheckDirectoryLinks(stagingDirectory);
            Directory.CreateDirectory(stagingDirectory);
            string temp = Path.Combine(stagingDirectory, "." + Guid.NewGuid().ToString("N") + ".pending");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(sourceBytes,0,sourceBytes.Length); output.Flush(true); }
                // Copy exactly the fully validated source snapshot, then verify actual
                // disk bytes. No second JSON/base64 expansion is necessary.
                if (SaveCodec.Hash(SaveCodec.ReadBytes(temp)) != SaveCodec.Hash(sourceBytes))
                    throw new InvalidDataException("复制时存档内容发生变化。");
                try { AtomicPublish(temp, target); }
                catch (Exception ex) when ((ex is IOException || ex is Win32Exception) && File.Exists(target)) { }
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private void LocalCopyWarning(string message) { try { _diagnostics?.Invoke(message); } catch { } }

        /// <summary>Return every unresolved head; never pick a winner using a machine clock.</summary>
        public static List<SaveRecord> FindHeads(IEnumerable<SaveRecord> records)
        {
            var all = records.ToList();
            foreach (var row in all) row.IsConflict = false;
            var heads = new List<SaveRecord>();
            foreach (var group in all.Where(x => (x.Status == SaveStatus.Ready || x.Status == SaveStatus.Deleted) && x.Header != null).GroupBy(x => SlotKey(x.Header)))
            {
                var revisions = new HashSet<string>(group.Select(x => x.Header.RevisionId), StringComparer.Ordinal);
                var parents = new HashSet<string>(group.SelectMany(x => x.Header.ParentRevisionIds).Where(revisions.Contains), StringComparer.Ordinal);
                var current = group.Where(x => x.Status == SaveStatus.Ready && !parents.Contains(x.Header.RevisionId)).ToList();
                foreach (var row in current) row.IsConflict = current.Count > 1;
                heads.AddRange(current);
            }
            return heads;
        }

        /// <summary>Explicit logical deletion. A cloud tombstone prevents older revisions from resurfacing.</summary>
        public void Delete(string revisionId)
        {
            lock (_gate)
            using (AcquireWriteLock())
            {
                SaveCodec.Revision(revisionId);
                string path = PathFor(revisionId);
                CheckDirectoryLinks(_saveDirectory); CheckFileLink(path);
                if (!File.Exists(path)) throw new FileNotFoundException("对话存档不存在。", path);
                SaveEnvelope original = Load(revisionId);
                // Schema 1 has not shipped: every tombstone now contains a complete ancestor
                // closure, so any subset arriving from cloud still suppresses old checkpoints.
                // Compute this before creating a backup or writing any deletion marker.
                string[] deletedAncestors = CollectDeletionClosure(original.Header, Scan());
                if (new FileInfo(path).Length > SaveCodec.MaximumFileBytes) throw new InvalidDataException("存档大小超出限制。");
                CheckDirectoryLinks(_backupDirectory);
                Directory.CreateDirectory(_backupDirectory);
                string backup = Path.Combine(_backupDirectory, "deleted_" + revisionId + "_" + Guid.NewGuid().ToString("N") + ".dsav");
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                bool transactionBody=!string.IsNullOrEmpty(original.Header.TransactionId);
                original.Header.TransactionId=null;
                original.Header.ParentRevisionIds = deletedAncestors;
                original.Kind = "tombstone";
                original.World = new byte[0];
                original.Dialogue = new Newtonsoft.Json.Linq.JObject();
                PublishInternal(original);
                // The durable marker is the logical commit. A cloud client may still hold
                // the old body open; failure to reclaim it cannot undo that deletion.
                try { if(!transactionBody)File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string[] CollectDeletionClosure(SaveHeader target, List<SaveRecord> records)
        {
            var sameSlot = records.Where(r => r.Header != null && SlotKey(r.Header) == SlotKey(target) &&
                (r.Status == SaveStatus.Ready || r.Status == SaveStatus.Deleted)).ToList();
            var byId = sameSlot.ToDictionary(r => r.Header.RevisionId, StringComparer.Ordinal);
            var proof = new Dictionary<string, SaveRecord>(StringComparer.Ordinal);
            foreach (var marker in sameSlot.Where(r => r.Status == SaveStatus.Deleted))
                foreach (string covered in marker.Header.ParentRevisionIds)
                    if (!proof.ContainsKey(covered)) proof.Add(covered, marker);
            var closure = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(target.RevisionId);
            while (pending.Count > 0)
            {
                string revision = pending.Pop();
                if (!closure.Add(revision)) continue;
                if (byId.TryGetValue(revision, out var known))
                {
                    foreach (string parent in known.Header.ParentRevisionIds) pending.Push(parent);
                }
                else if (proof.TryGetValue(revision, out var completeMarker))
                {
                    // These IDs are already logically deleted. The validated marker carries
                    // their entire ancestry even when their checkpoint bodies are absent.
                    closure.UnionWith(completeMarker.Header.ParentRevisionIds);
                }
                else throw new InvalidDataException("历史存档尚未同步完整，请完成同步后再删除。缺少版本：" + revision);
                if (closure.Count > 8192) throw new InvalidDataException("历史版本数量过多，不能完整记录删除范围。");
            }
            return closure.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        private static string SlotKey(SaveHeader h)
        {
            // Length-prefixing avoids collisions even if a label includes punctuation or newlines.
            return Part(h.SteamId) + Part(h.RunId) + Part(h.Category) + Part(h.LogicalSlot);
        }

        private static string Part(string value) { return value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value; }
        private void CheckQuota(long incomingBytes, bool tombstone)
        {
            // No arbitrary total-history limit by default. Explicit caller quotas retain
            // their previous behavior, including the deletion-marker reserve.
            if (!_maximumTotalBytes.HasValue && !_maximumFiles.HasValue) return;
            long maximumBytes = _maximumTotalBytes.HasValue ? _maximumTotalBytes.Value + (tombstone ? TombstoneReserveBytes : 0) : long.MaxValue;
            int maximumCount = _maximumFiles.HasValue ? _maximumFiles.Value + (tombstone ? TombstoneReserveFiles : 0) : int.MaxValue;
            // Unknown/future .dsav files consume space too; never rewrite or remove them.
            string[] files = Directory.GetFiles(_saveDirectory, "*.dsav", SearchOption.TopDirectoryOnly);
            if (files.Length >= maximumCount) throw new IOException("对话存档文件数量已达本地上限；请先备份并清理不需要的历史文件。");
            long total = incomingBytes;
            if (total > maximumBytes) throw new IOException("对话存档空间已达本地上限。");
            foreach (string path in files)
            {
                long length;
                try { length = new FileInfo(path).Length; }
                catch (FileNotFoundException) { continue; }
                if (length > maximumBytes - total) throw new IOException("对话存档空间已达本地上限；未改动已有存档。");
                total += length;
            }
        }

        private void CheckExpectedHeads(SaveHeader header)
        {
            var records = Scan();
            Func<SaveRecord, bool> sameSlot = r => r.Header != null && r.Header.SteamId == header.SteamId &&
                r.Header.RunId == header.RunId && r.Header.Category == header.Category && r.Header.LogicalSlot == header.LogicalSlot;
            if (records.Any(r => sameSlot(r) && r.Status == SaveStatus.UnsupportedVersion))
                throw new InvalidDataException("此存档位已有更高版本存档；已保留原文件。");
            var expected = header.ParentRevisionIds.OrderBy(x => x, StringComparer.Ordinal);
            var actual = FindHeads(records).Where(sameSlot).Select(r => r.Header.RevisionId).OrderBy(x => x, StringComparer.Ordinal);
            if (!expected.SequenceEqual(actual))
                throw new InvalidDataException("存档位已被其他进程或设备更新，请重新选择；已有版本均已保留。");
        }

        private FileStream AcquireWriteLock()
        {
            EnsureDirectories();
            string directory = Path.Combine(_stagingDirectory, "Locks");
            CheckDirectoryLinks(directory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "publish.lock");
            CheckFileLink(path);
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error)
                {
                    if (elapsed.ElapsedMilliseconds >= 5000)
                        throw new IOException("存档写入锁等待超时，其他进程可能正在提交存档；本次尚未提交。", error);
                    Thread.Sleep(25);
                }
            }
            // Keep the lock file after releasing its handle: deleting it could let
            // another process lock a new inode while a previous waiter owns the old one.
        }
        private string PathFor(string revision) { SaveCodec.Revision(revision); return Path.Combine(_saveDirectory, "dialogue_" + revision + ".dsav"); }
        private void EnsureDirectories()
        {
            CheckDirectoryLinks(_saveDirectory); CheckDirectoryLinks(_stagingDirectory);
            Directory.CreateDirectory(_saveDirectory); Directory.CreateDirectory(_stagingDirectory);
        }

        private static string FullDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("必须使用绝对目录。");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        private static bool Within(string child, string parent) { return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase); }
        private static void CheckDirectoryLinks(string path)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                DirectoryInfo parent = current.Parent;
                // Wine exposes a mapped drive root as a reparse point. The volume root
                // is our trust boundary; every descendant still must be a real directory.
                if (parent != null && current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("存档目录不能经过符号链接：" + current.FullName);
                current = parent;
            }
        }
        private static void CheckFileLink(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("存档不能是符号链接。");
        }

        private static void AtomicPublish(string source, string target)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (!MoveFileEx(source, target, 8)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法原子提交对话存档。");
            }
            else
            {
                // link is an atomic, no-overwrite operation and fails across volumes.
                if (Link(source, target) != 0) throw new IOException("无法在同卷原子发布存档。", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFile, string newFile, int flags);
        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link(string oldPath, string newPath);
    }
}
