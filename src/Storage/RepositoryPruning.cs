using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace StudentAgeDialogueSave.Storage
{
    // Reclaims checkpoint bodies that a newer, fully verified checkpoint in the same slot
    // explicitly replaced. Such a body is never listed or loadable (it is not a head), so
    // removing it changes no visible save. Heads, conflicts, tombstones, transaction members,
    // corrupt, pending and future-format files are never touched.
    public sealed partial class Repository
    {
        private readonly bool _pruneSuperseded;
        private bool _holdingWriteLock;
        private Dictionary<string, string[]> _prunedLedger;
        internal int LastPrunedFiles { get; private set; }
        internal long LastPrunedBytes { get; private set; }

        private const int TrashMaximumFiles = 40;
        private const long TrashMaximumBytes = 64L * 1024 * 1024;
        private static readonly TimeSpan TrashMaximumAge = TimeSpan.FromDays(7);
        private const int LedgerMaximumEntries = 20000;

        private string TrashDirectory => Path.Combine(_backupDirectory, "Pruned");
        private string LedgerPath => Path.Combine(_backupDirectory, "pruned-revisions.txt");

        private void PruneSuperseded(List<SaveRecord> records)
        {
            LastPrunedFiles = 0; LastPrunedBytes = 0;
            IDisposable acquired = null;
            if (!_holdingWriteLock && (acquired = TryAcquireWriteLock()) == null) return; // Another writer is active; next scan retries.
            try
            {
                var covers = new Dictionary<string, SaveRecord>(StringComparer.Ordinal);
                foreach (var row in records)
                {
                    if (!Plain(row)) continue;
                    foreach (string parent in row.Header.ParentRevisionIds ?? new string[0])
                        if (!covers.ContainsKey(parent)) covers[parent] = row;
                }
                if (covers.Count == 0) return;
                // Phase 1 re-reads every covering descendant while all files are still present;
                // phase 2 only moves bodies proven replaced. A chain collapses to its newest head.
                var verifiedCovers = new Dictionary<string, bool>(StringComparer.Ordinal);
                var plan = new List<SaveRecord>();
                foreach (var row in records)
                {
                    if (!Plain(row) || !covers.TryGetValue(row.Header.RevisionId, out var cover)) continue;
                    if (ReferenceEquals(cover, row) || SlotKey(cover.Header) != SlotKey(row.Header)) continue;
                    if (!verifiedCovers.TryGetValue(cover.Header.RevisionId, out bool valid))
                        verifiedCovers[cover.Header.RevisionId] = valid = VerifyCover(cover);
                    // The descendant must still name this parent after a full re-read.
                    if (valid && ParentsOnDisk(cover).Contains(row.Header.RevisionId)) plan.Add(row);
                }
                var removed = new List<SaveRecord>();
                foreach (var row in plan)
                {
                    long length = new FileInfo(row.FilePath).Length;
                    if (!Reclaim(row)) continue;
                    removed.Add(row); LastPrunedFiles++; LastPrunedBytes += length;
                }
                if (removed.Count == 0) return;
                foreach (var row in removed) { records.Remove(row); verifiedRows.Remove(row.FilePath); }
                LocalCopyWarning("已回收被覆盖的旧对话存档 " + removed.Count + " 个，释放 " +
                    (LastPrunedBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB；可见存档未改变。");
                TrimTrash();
            }
            finally { acquired?.Dispose(); }
        }

        // Only ordinary, verified, committed checkpoints participate on either side.
        private static bool Plain(SaveRecord row) =>
            row != null && row.Status == SaveStatus.Ready && row.Header != null && row.SchemaVersion == 1 &&
            string.IsNullOrEmpty(row.Header.TransactionId) && !string.IsNullOrEmpty(row.Header.RevisionId);

        private readonly Dictionary<string, string[]> _coverParents = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private bool VerifyCover(SaveRecord cover)
        {
            try
            {
                CheckFileLink(cover.FilePath);
                var value = SaveCodec.Decode(SaveCodec.Read(cover.FilePath));
                if (value.Kind != "checkpoint" || value.Header.RevisionId != cover.Header.RevisionId ||
                    SlotKey(value.Header) != SlotKey(cover.Header) || !string.IsNullOrEmpty(value.Header.TransactionId)) return false;
                _coverParents[cover.Header.RevisionId] = value.Header.ParentRevisionIds ?? new string[0];
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { return false; }
        }
        private string[] ParentsOnDisk(SaveRecord cover) =>
            _coverParents.TryGetValue(cover.Header.RevisionId, out var parents) ? parents : new string[0];

        // Record first, then move the body to a bounded local trash, then drop its local copy.
        // An interruption at any point leaves either the body or a recoverable trash copy.
        private bool Reclaim(SaveRecord row)
        {
            string id = row.Header.RevisionId, name = Path.GetFileName(row.FilePath);
            try
            {
                if (name != "dialogue_" + id + ".dsav") return false;
                CheckFileLink(row.FilePath);
                RecordPruned(id, row.Header.ParentRevisionIds ?? new string[0]);
                CheckDirectoryLinks(TrashDirectory);
                Directory.CreateDirectory(TrashDirectory);
                string trash = Path.Combine(TrashDirectory, DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "_" + name);
                File.Move(row.FilePath, trash);
                string copy = Path.Combine(LocalCopies, name);
                try { if (File.Exists(copy)) { CheckFileLink(copy); File.Delete(copy); } }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { LocalCopyWarning("旧版本的本地副本暂未删除：" + ex.Message); }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is ArgumentException)
            {
                LocalCopyWarning("旧版本回收跳过，文件保留：" + ex.Message);
                return false;
            }
        }

        private void TrimTrash()
        {
            try
            {
                if (!Directory.Exists(TrashDirectory)) return;
                var files = Directory.GetFiles(TrashDirectory, "*_dialogue_*.dsav").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
                long total = 0; int kept = 0; long now = DateTime.UtcNow.Ticks;
                foreach (string path in files)
                {
                    long length = new FileInfo(path).Length;
                    long.TryParse(Path.GetFileName(path).Split('_')[0], NumberStyles.None, CultureInfo.InvariantCulture, out long stamp);
                    bool keep = kept < TrashMaximumFiles && total + length <= TrashMaximumBytes && now - stamp <= TrashMaximumAge.Ticks;
                    if (keep) { kept++; total += length; continue; }
                    try { CheckFileLink(path); File.Delete(path); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { LocalCopyWarning("旧版本回收站整理未完成：" + ex.Message); }
        }

        // Local ledger: revision -> its direct parents. It keeps deletion markers complete
        // after reclamation and stops a stale local copy from restoring a reclaimed body.
        private Dictionary<string, string[]> Ledger()
        {
            if (_prunedLedger != null) return _prunedLedger;
            var ledger = new Dictionary<string, string[]>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(LedgerPath))
                    foreach (string line in File.ReadAllLines(LedgerPath))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length != 2 || !IsRevision(parts[0])) continue; // A torn final line is ignored.
                        var parents = parts[1].Length == 0 ? new string[0] : parts[1].Split(',');
                        if (parents.All(IsRevision)) ledger[parts[0]] = parents;
                    }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { LocalCopyWarning("旧版本记录读取失败：" + ex.Message); }
            return _prunedLedger = ledger;
        }
        private void RecordPruned(string id, string[] parents)
        {
            var ledger = Ledger();
            if (ledger.ContainsKey(id)) return;
            CheckDirectoryLinks(_backupDirectory);
            Directory.CreateDirectory(_backupDirectory);
            if (ledger.Count >= LedgerMaximumEntries)
            {
                // Keep the newest half; entries only extend ancestry beyond the retained history.
                var lines = File.ReadAllLines(LedgerPath);
                var keep = lines.Skip(lines.Length - LedgerMaximumEntries / 2).ToArray();
                string temp = LedgerPath + ".pending";
                File.WriteAllLines(temp, keep);
                File.Copy(temp, LedgerPath, true); File.Delete(temp);
                _prunedLedger = null; ledger = Ledger();
            }
            using (var stream = new FileStream(LedgerPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, SaveCodec.Utf8))
            {
                writer.Write(id + "\t" + string.Join(",", parents) + "\n");
                writer.Flush(); stream.Flush(true);
            }
            ledger[id] = parents;
        }
        private string[] PrunedParents(string revision) =>
            _pruneSuperseded && Ledger().TryGetValue(revision, out var parents) ? parents : null;
        private bool IsPrunedCopy(string path)
        {
            if (!_pruneSuperseded) return false;
            string name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("dialogue_", StringComparison.Ordinal) && Ledger().ContainsKey(name.Substring("dialogue_".Length));
        }
        private static bool IsRevision(string value) =>
            value != null && value.Length == 32 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private IDisposable TryAcquireWriteLock()
        {
            try
            {
                EnsureDirectories();
                string directory = Path.Combine(_stagingDirectory, "Locks");
                CheckDirectoryLinks(directory);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "publish.lock");
                CheckFileLink(path);
                return new WriteLock(this, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // The just-published bytes were fully validated; copy them directly instead of
        // re-reading and re-decoding the body, then verify what reached the disk.
        private void PreserveLocalCopy(string source, byte[] bytes)
        {
            if (!_retainLocalCopies) return;
            try
            {
                CheckDirectoryLinks(LocalCopies);
                Directory.CreateDirectory(LocalCopies);
                string target = Path.Combine(LocalCopies, Path.GetFileName(source));
                if (File.Exists(target)) return;
                string temp = Path.Combine(LocalCopies, "." + Guid.NewGuid().ToString("N") + ".pending");
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                    if (SaveCodec.Hash(SaveCodec.ReadBytes(temp)) != SaveCodec.Hash(bytes)) throw new InvalidDataException("复制时存档内容发生变化。");
                    try { AtomicPublish(temp, target); }
                    catch (Exception ex) when ((ex is IOException || ex is Win32Exception) && File.Exists(target)) { }
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Win32Exception || ex is ArgumentException)
            { LocalCopyWarning("保留本地副本失败，主存档未改动：" + ex.Message); }
        }
    }
}
