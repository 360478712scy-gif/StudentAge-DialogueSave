using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Sdk.PlatformAPI;
using UnityEngine;
using StudentAgeDialogueSave.Storage;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

namespace StudentAgeDialogueSave
{
    // All public entry points run on Unity's main thread. Workers only receive detached DTOs.
    internal sealed partial class DialogueSaveService : IDialogueUiService, IDisposable
    {
        readonly DialogueCheckpointAdapter adapter;
        readonly Action<Action> post;
        readonly Action<string> log;
        readonly Func<Task> nextFrame;
        readonly CancellationToken shutdown;
        readonly bool autoEnabled;
        readonly int autoInterval;
        Repository repository;
        string repositoryPath, deviceId;
        List<SaveRecord> records = new List<SaveRecord>();
        IDisposable menuPause;
        IDisposable quickLoadPause;
        GameCheckpoint menuCheckpoint;
        Task<GameCheckpoint> menuCapture;
        bool captureInProgress;
        int captureRequests;
        CancellationTokenSource captureEpoch;
        int captureEpochNumber;
        Task loadTail = Task.CompletedTask;
        readonly Dictionary<string, List<Action<UiResult>>> pendingLoads = new Dictionary<string, List<Action<UiResult>>>();
        readonly Dictionary<string,List<Action<UiResult>>> pendingDeletes=new Dictionary<string,List<Action<UiResult>>>();
        bool quickLoadRequested;
        CancellationTokenSource menuCancellation;
        Exception menuCaptureError;
        string menuRunId;
        int menuGeneration;
        bool quickSaveRequested;
        bool autoSaveRequested;
        readonly Dictionary<string, List<Action<UiResult>>> pendingSaves = new Dictionary<string, List<Action<UiResult>>>();
        bool menuSaving, menuOpen, busy, initialized;
        volatile bool disposed;
        int generation;
        float lastScanTime = -100, nextAutoTime;
        string lastAutoKey;
        string lastAutoError;
        public event Action RecordsChanged;
        public string StatusMessage { get; private set; }
        public bool IsListing { get; private set; }
        public bool IsPreparingSave => !disposed && menuOpen && menuSaving && menuCheckpoint == null && menuCaptureError == null;
        public bool IsDialogueContext => !disposed && adapter.IsDialogueContext;
        public bool IsSupportedDialogueContext => !disposed && adapter.IsSupportedDialogueContext;

        public DialogueSaveService(DialogueCheckpointAdapter adapter, Action<Action> post, Action<string> log, Func<Task> nextFrame, CancellationToken shutdown, bool autoEnabled, int autoInterval)
        { this.adapter = adapter; this.post = post; this.log = log; this.nextFrame = nextFrame; this.shutdown = shutdown; this.autoEnabled = autoEnabled; this.autoInterval = autoInterval; captureEpoch = CancellationTokenSource.CreateLinkedTokenSource(shutdown); }

        bool EnsureRepository(out string reason)
        {
            reason = null;
            if (disposed) { reason = "对话存档已关闭。"; return false; }
            try
            {
                if (Platform.Current == null || string.IsNullOrEmpty(Platform.Current.GetUserId())) { reason = "游戏用户信息尚未准备好。"; return false; }
                string path = Path.GetFullPath(PathDefine.SAVE_PATH);
                if (repository != null && path == repositoryPath) return true;
                if (busy || IsListing) { reason = "正在处理上一账户的存档，请稍候。"; return false; }
                string user = Platform.Current.GetUserId();
                string basePath = Path.Combine(Application.persistentDataPath, "DialogueSaveLocal", user);
                var nextRepository = new Repository(path, Path.Combine(basePath, "Staging"), Path.Combine(basePath, "Backups"), retainLocalCopies: true, diagnostics: SafeLog);
                string idPath = Path.Combine(basePath, "device-id.txt");
                Directory.CreateDirectory(basePath);
                string nextDeviceId;
                if (File.Exists(idPath) && Guid.TryParse(File.ReadAllText(idPath).Trim(), out var existing)) nextDeviceId = existing.ToString("N");
                else { nextDeviceId = Guid.NewGuid().ToString("N"); File.WriteAllText(idPath, nextDeviceId); }
                EndMenu(); ReleaseQuickPause();
                repository = nextRepository; deviceId = nextDeviceId;
                repositoryPath = path; generation++; records.Clear(); initialized = false;
                return true;
            }
            catch (Exception ex) { reason = "无法访问对话存档目录：" + ex.Message; return false; }
        }

        public bool CanSave(out string reason)
        {
            if (disposed) { reason = "对话存档已关闭。"; return false; }
            if (menuSaving && menuOpen) { reason = menuCaptureError?.Message; return true; }
            reason = adapter.IsDialogueContext ? null : "当前没有正在显示的对话。";
            return reason == null;
        }

        public bool BeginMenu(bool saving, out string reason)
        {
            reason = null;
            if (!EnsureRepository(out reason)) return false;
            EndMenu();
            try
            {
                StatusMessage = null;
                menuSaving = saving; menuOpen = true;
                if (saving)
                {
                    menuCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                    menuRunId = adapter.CurrentCheckpointBrief?.RunId;
                    // Freeze before returning to the UI, then let it actually render before
                    // the main-thread world capture. This is the same lease held until close.
                    menuPause = adapter.PauseForMenu();
                    menuCapture = CaptureMenuAsync(menuGeneration, menuCancellation.Token);
                    RaiseRecordsChanged();
                }
                else if (adapter.IsDialogueContext) menuPause = adapter.PauseForMenu();
                Refresh();
                return true;
            }
            catch (Exception ex) { EndMenu(); reason = ex.Message; return false; }
        }
        public void EndMenu()
        {
            menuGeneration++;
            var cancel = menuCancellation; menuCancellation = null;
            cancel?.Cancel(); cancel?.Dispose();
            menuCapture = null; menuCaptureError = null; menuRunId = null;
            var pause = menuPause; menuPause = null; menuCheckpoint = null; menuSaving = false; menuOpen = false;
            pause?.Dispose();
        }
        async Task<GameCheckpoint> CaptureMenuAsync(int token, CancellationToken cancellation)
        {
            try
            {
                while (adapter.IsRestoring) { cancellation.ThrowIfCancellationRequested(); await nextFrame(); }
                await WaitForSaveWindowPaintAsync(token, cancellation);
                var checkpoint = await CaptureRequestedAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (token != menuGeneration || !menuOpen) throw new OperationCanceledException();
                menuCheckpoint = checkpoint; menuRunId = checkpoint.Brief.RunId;
                RaiseRecordsChanged();
                return checkpoint;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (!disposed && token == menuGeneration) { menuCaptureError = ex; Notify("对话保存请求未完成：" + ex.Message); RaiseRecordsChanged(); }
                return null;
            }
        }
        async Task WaitForSaveWindowPaintAsync(int token, CancellationToken cancellation)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (disposed || token != menuGeneration || !menuOpen) throw new OperationCanceledException();
                var window = Sdk.UIMgr.GetView<View.Main.SaveView>(false);
                if (window != null && window.viewState == Sdk.ViewState.Opened && window.isViewReady &&
                    window.gameObject != null && window.gameObject.activeInHierarchy) break;
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("存档窗口未能显示，对话快照尚未创建。");
                await nextFrame();
            }
            // Readiness can become true before this frame's canvas render. Two frame boundaries
            // guarantee an intervening render opportunity, including reusing an already-open view.
            await nextFrame();
            cancellation.ThrowIfCancellationRequested();
            await nextFrame();
            cancellation.ThrowIfCancellationRequested();
            if (disposed || token != menuGeneration || !menuOpen) throw new OperationCanceledException();
            SafeLog("对话保存窗口已就绪，开始主线程快照；当前对白保持暂停。");
        }
        async Task<GameCheckpoint> CaptureRequestedAsync(CancellationToken cancellation, bool exiting = false)
        {
            // Requests have independent cancellation. Closing a menu must release its capture
            // without cancelling a quick-save request queued behind it.
            using (var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, captureEpoch.Token))
            {
            cancellation = captureCancellation.Token;
            while (pendingLoads.Count > 0 || adapter.IsRestoring)
            { cancellation.ThrowIfCancellationRequested(); await nextFrame(); }
            cancellation.ThrowIfCancellationRequested();
            captureRequests++;
            bool ownsCapture = false;
            try
            {
                while (captureInProgress || adapter.IsRestoring)
                { cancellation.ThrowIfCancellationRequested(); await nextFrame(); }
                cancellation.ThrowIfCancellationRequested();
                captureInProgress = true; ownsCapture = true;
                return exiting ? await adapter.CaptureExitAsync(cancellation) : await adapter.CaptureAsync(cancellation);
            }
            finally
            {
                if (ownsCapture) captureInProgress = false;
                captureRequests--;
            }
            }
        }
        void CancelUncommittedCaptures()
        {
            captureEpochNumber++;
            var old = captureEpoch;
            captureEpoch = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            old.Cancel(); old.Dispose();
        }
        public IReadOnlyList<DialogueUiRecord> List(DialogueUiCategory category)
        {
            if (!EnsureRepository(out _)) return new List<DialogueUiRecord>();
            if ((!initialized && Time.realtimeSinceStartup - lastScanTime > 2) || Time.realtimeSinceStartup - lastScanTime > 10) Refresh();
            string cat = Category(category); string user = Platform.Current.GetUserId();
            var heads = Repository.FindHeads(records);
            var display = heads.Concat(records.Where(r => r.Status == SaveStatus.UnsupportedVersion || r.Status == SaveStatus.Corrupt)).Where(r => r.Header != null && r.Header.SteamId == user && r.Header.Category == cat);
            if (menuSaving) display = display.Where(r => !string.IsNullOrEmpty(menuRunId) && r.Header.RunId == menuRunId);
            var result = display.OrderByDescending(r => r.Header.CreatedUtc, StringComparer.Ordinal).Select(ToUi).ToList();
            if (!menuSaving && category == DialogueUiCategory.Manual)
                result.AddRange(records.Where(r => r.Status == SaveStatus.Corrupt && r.Header == null).Select(r => new DialogueUiRecord
                {
                    RevisionId = "invalid:" + Path.GetFileName(r.FilePath),
                    Category = DialogueUiCategory.Manual,
                    Speaker = "无法读取的对话存档",
                    Summary = Path.GetFileName(r.FilePath),
                    CanLoad = false,
                    StatusReason = "文件损坏或格式无法识别；已保留原文件。" + r.Error
                }));
            return result;
        }
        DialogueUiRecord ToUi(SaveRecord r)
        {
            var h = r.Header;
            DateTime.TryParse(h.SavedUtc??h.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date);
            int.TryParse(h.LogicalSlot, out var slot);
            string summary=r.PreviewText??h.Summary??"";
            if(r.PreviewOptionIds?.Length>0 && !summary.StartsWith("选择项："))
                summary="选择项："+string.Join("/",r.PreviewOptionIds.Select(id=>Config.Cfg.OptionCfgMap.TryGetValue(id,out var option)?RecordMgr.Replace(option.content):"选项 "+id));
            return new DialogueUiRecord
            {
                RevisionId = h.RevisionId,
                RunId = h.RunId,
                Category = ParseCategory(h.Category),
                Slot = slot,
                Speaker = h.Speaker ?? "",
                Summary = System.Text.RegularExpressions.Regex.Replace(summary, "<[^>]*>", ""),Comment=h.Comment,PreviewImageUrl=h.PreviewImageUrl??r.PreviewImageUrl,BackgroundId=h.BackgroundId??r.PreviewBackgroundId,SpeakerId=h.SpeakerId??r.PreviewSpeakerId,
                CreatedUtc = date,
                RoleName = h.RoleName ?? "",
                YearLabel = h.YearLabel ?? "",
                SeasonLabel = h.SeasonLabel ?? "",
                SeasonId = h.SeasonId,
                Location = h.Location ?? "",
                Gender = h.Gender ?? 0,
                GradeState = h.GradeState ?? 0,
                CanLoad = r.Status == SaveStatus.Ready,
                IsConflict = r.IsConflict,
                StatusReason = r.Error ?? (r.IsConflict ? "存在另一设备保存的分支，请选择要加载的一份。" : "")
            };
        }
        void Refresh()
        {
            if (disposed || IsListing || repository == null || busy) return;
            IsListing = true; int token = generation; var repo = repository;
            Task.Run(() => repo.Scan(), shutdown).ContinueWith(task => post(() =>
            {
                if (disposed || token != generation) return;
                IsListing = false; lastScanTime = Time.realtimeSinceStartup;
                if (task.IsCanceled) return;
                if (task.IsFaulted) { SafeLog("读取对话档列表失败：" + task.Exception.GetBaseException().Message); Notify("读取存档列表失败，请检查目录访问权限。"); }
                else { records = task.Result; initialized = true; }
                RaiseRecordsChanged();
            }), TaskScheduler.Default);
        }
        public void Save(DialogueUiCategory category, int slot, string replacedRevisionId, Action<UiResult> done)
        {
            if (category == DialogueUiCategory.Auto) { InvokeResult(done, new UiResult(false, "自动和快速存档由对应功能创建。")); return; }
            if (slot < 1 || slot > 9999) { InvokeResult(done, new UiResult(false, "请选择1至9999号对话存档位。")); return; }
            if (!menuSaving || menuCapture == null) { InvokeResult(done, new UiResult(false, "当前没有打开对话保存界面。")); return; }
            StatusMessage = null;
            string key = menuGeneration + ":" + category + ":" + slot + ":" + replacedRevisionId;
            if (pendingSaves.TryGetValue(key, out var callbacks))
            {
                if (done != null) callbacks.Add(done);
                InvokeResult(done, new UiResult(false, "保存请求已受理，正在处理。", true));
                return;
            }
            int requestGeneration = menuGeneration;
            var requestCapture = menuCapture;
            var requestCancellation = menuCancellation.Token;
            pendingSaves[key] = new List<Action<UiResult>>();
            if (done != null) pendingSaves[key].Add(done);
            InvokeResult(done, new UiResult(false, "正在保存当前对话…", true));
            CompleteManualSaveAsync(key, requestGeneration, captureEpochNumber, requestCapture, requestCancellation, category, slot, replacedRevisionId);
        }
        async void CompleteManualSaveAsync(string key, int token, int epoch, Task<GameCheckpoint> capture, CancellationToken cancellation, DialogueUiCategory category, int slot, string replacement)
        {
            UiResult result;
            try
            {
                var snapshot = await capture;
                cancellation.ThrowIfCancellationRequested();
                if (snapshot == null) throw menuCaptureError ?? new InvalidOperationException("未能完成当前对话快照。");
                await WaitForStoreAsync(cancellation);
                if (token != menuGeneration || epoch != captureEpochNumber) throw new OperationCanceledException();
                result = await PublishAsync(snapshot, category, slot, replacement, true);
            }
            catch (OperationCanceledException) { result = new UiResult(false, "保存请求已取消。"); }
            catch (Exception ex) { result = new UiResult(false, "保存未完成：" + ex.Message); }
            if (disposed) return;
            if (!pendingSaves.TryGetValue(key, out var callbacks)) return;
            pendingSaves.Remove(key);
            foreach (var callback in callbacks.ToArray()) InvokeResult(callback, result);
        }
        async Task WaitForStoreAsync(CancellationToken cancellation)
        {
            while (busy || IsListing) { cancellation.ThrowIfCancellationRequested(); await nextFrame(); }
            cancellation.ThrowIfCancellationRequested();
            if (!EnsureRepository(out var reason)) throw new IOException(reason);
            if (!initialized)
            {
                Refresh();
                while (IsListing) { cancellation.ThrowIfCancellationRequested(); await nextFrame(); }
                if (!initialized) throw new IOException("存档列表读取失败。");
            }
        }
        sealed class CommitReceipt
        {
            internal UiResult Result;
            internal SaveRecord Record;
            internal List<SaveRecord> Records;
        }
        async Task<UiResult> PublishAsync(GameCheckpoint checkpoint, DialogueUiCategory category, int slot, string replacedRevisionId, bool requireEmpty=false)
        {
            if (busy) { return new UiResult(false, "正在存读档，请稍候。"); }
            if (!EnsureRepository(out var reason)) { return new UiResult(false, reason); }
            if (!initialized || IsListing) { Refresh(); return new UiResult(false, "正在读取已有存档，请稍后重试。"); }
            var brief = checkpoint.Brief; string cat = Category(category); string logical = slot.ToString(CultureInfo.InvariantCulture);
            if (brief.SteamId != Platform.Current.GetUserId()) { return new UiResult(false, "Steam 账户已改变，请重新进入对话并打开保存界面。"); }
            if (records.Any(r => r.Status == SaveStatus.UnsupportedVersion && r.Header != null && r.Header.SteamId == brief.SteamId && r.Header.RunId == brief.RunId && r.Header.Category == cat && r.Header.LogicalSlot == logical))
            { return new UiResult(false, "此存档位含有新版插件创建的存档，请更新插件后操作。"); }
            var allHeads = Repository.FindHeads(records);
            var replaced = string.IsNullOrEmpty(replacedRevisionId)?null:allHeads.SingleOrDefault(r=>r.Header.RevisionId==replacedRevisionId && r.Header.SteamId==brief.SteamId && r.Header.Category==cat);
            bool crossRun = replaced!=null && replaced.Header.RunId!=brief.RunId;
            var heads = allHeads.Where(r => r.Header.SteamId == brief.SteamId && r.Header.RunId == brief.RunId && r.Header.Category == cat && r.Header.LogicalSlot == logical).ToList();
            if (heads.Count > 1) { return new UiResult(false, "此存档位存在多个设备分支，请先在对话存档中处理冲突。"); }
            if ((category == DialogueUiCategory.Manual || requireEmpty) && string.IsNullOrEmpty(replacedRevisionId) && heads.Count > 0) { return new UiResult(false, "存档位已变化，请重新选择并确认覆盖。"); }
            if (!string.IsNullOrEmpty(replacedRevisionId) && !(crossRun && heads.Count==0) && !heads.Any(r => r.Header.RevisionId == replacedRevisionId)) { return new UiResult(false, "存档位已变化，请重新选择。"); }
            var header = new SaveHeader
            {
                SteamId = brief.SteamId,
                RunId = brief.RunId,
                Category = cat,
                LogicalSlot = logical,
                ParentRevisionIds = heads.Select(r => r.Header.RevisionId).ToArray(),
                DeviceId = deviceId,
                GameVersion = brief.GameVersion,
                PluginVersion = DialogueSavePlugin.Version,
                AdapterVersion = "round-dialogue-v1",
                Speaker = brief.Speaker,
                Summary = brief.Summary,
                BackgroundId=checkpoint.Dialogue.Value<int?>("background"),
                PreviewImageUrl=(string)(checkpoint.Dialogue["cg"] as JObject)?["url"],
                SpeakerId=checkpoint.Dialogue.Value<int?>("speakerId")??0,
                RoleName = brief.RoleName,
                YearLabel = brief.YearLabel,
                SeasonLabel = brief.SeasonLabel,
                SeasonId = brief.SeasonId,
                Location = brief.Location,
                Gender = brief.Gender,
                GradeState = brief.GradeState
            };
            var envelope = new SaveEnvelope { Header = header, World = (byte[])checkpoint.WorldBytes.Clone(), Dialogue = (JObject)checkpoint.Dialogue.DeepClone() };
            busy = true; int token = generation; var repo = repository;
            // The receipt finishes independently of the Unity dispatch queue. Once storage
            // starts, shutdown must not turn an actual commit into a cancellation result.
            Task<CommitReceipt> receipt = Task.Run(() => crossRun?repo.PublishReplacing(envelope,replacedRevisionId):repo.PublishChecked(envelope), shutdown).ContinueWith(task =>
            {
                var outcome = new CommitReceipt();
                if (task.IsCanceled) outcome.Result = new UiResult(false, "保存已取消。");
                else if (task.IsFaulted) outcome.Result = new UiResult(false, "保存失败：" + task.Exception.GetBaseException().Message + " 已有存档保持不变。");
                else { outcome.Record = task.Result; if(crossRun)outcome.Records=repo.Scan(); outcome.Result = new UiResult(true, "对话已保存到本机。"); }
                SafeLog(task.IsFaulted ? "保存对话档失败：" + task.Exception.GetBaseException() :
                    task.IsCanceled ? "保存尚未提交，已取消。" : "对话存档本地提交完成：" + outcome.Record.Header.RevisionId);
                return outcome;
            }, TaskScheduler.Default);
            try
            {
                while (!receipt.IsCompleted)
                {
                    if (disposed || shutdown.IsCancellationRequested)
                        return (await receipt.ConfigureAwait(false)).Result;
                    await nextFrame();
                }
            }
            catch (OperationCanceledException) when (disposed || shutdown.IsCancellationRequested)
            {
                // Only a detached result returns on this worker continuation. Every caller
                // checks disposed before touching state, invoking callbacks, or notifying UI.
                return (await receipt.ConfigureAwait(false)).Result;
            }
            CommitReceipt completed = receipt.GetAwaiter().GetResult();
            if (disposed || shutdown.IsCancellationRequested) return completed.Result;
            if (token == generation) busy = false;
            try
            {
                if (token == generation)
                {
                    if (completed.Result.Success) { if(completed.Records!=null)records=completed.Records;else records.Add(completed.Record); Repository.FindHeads(records); RaiseRecordsChanged(); }
                    else Refresh();
                }
            }
            catch (Exception ex) { SafeLog("存档已完成，界面刷新失败：" + ex.Message); }
            return completed.Result;
        }
        public void Load(string revisionId, Action<UiResult> done)
        {
            if (disposed) { InvokeResult(done, new UiResult(false, "对话存档已关闭。")); return; }
            if (string.IsNullOrEmpty(revisionId)) { InvokeResult(done, new UiResult(false, "请选择要读取的存档。")); return; }
            if (pendingLoads.TryGetValue(revisionId, out var existing))
            {
                if (done != null) existing.Add(done);
                InvokeResult(done, new UiResult(false, "正在读取存档…", true));
                return;
            }
            var callbacks = new List<Action<UiResult>>();
            if (done != null) callbacks.Add(done);
            pendingLoads.Add(revisionId, callbacks);
            var prior = loadTail;
            var completion = new TaskCompletionSource<bool>();
            loadTail = completion.Task;
            InvokeResult(done, new UiResult(false, "正在读取存档…", true));
            CompleteLoadAsync(revisionId, prior, completion);
        }
        async void CompleteLoadAsync(string revisionId, Task prior, TaskCompletionSource<bool> completion)
        {
            IDisposable readingPause = null;
            bool ownsStore = false;
            UiResult result;
            try
            {
                try
                {
                    await prior;
                    shutdown.ThrowIfCancellationRequested();
                    while (adapter.IsRestoring) { shutdown.ThrowIfCancellationRequested(); await nextFrame(); }
                    if (adapter.IsDialogueContext) readingPause = adapter.PauseForMenu();
                    // Loading abandons the source. Cancel only snapshots not yet published;
                    // a repository write already crossing its atomic commit must finish first.
                    CancelUncommittedCaptures();
                    while (captureRequests > 0)
                    { shutdown.ThrowIfCancellationRequested(); await nextFrame(); }
                    await WaitForStoreAsync(shutdown);
                    shutdown.ThrowIfCancellationRequested();
                    StatusMessage = null;
                    busy = true; ownsStore = true;
                    var repo = repository;
                    var read = Task.Run(() => repo.Load(revisionId), shutdown);
                    while (!read.IsCompleted) { shutdown.ThrowIfCancellationRequested(); await nextFrame(); }
                    var envelope = read.GetAwaiter().GetResult();
                    if (envelope.Header.SteamId != Platform.Current.GetUserId()) throw new InvalidOperationException("此对话存档属于其他 Steam 用户。");
                    var h = envelope.Header;
                    var checkpoint = new GameCheckpoint { WorldBytes = envelope.World, Dialogue = envelope.Dialogue,
                        Brief = new CheckpointBrief { SteamId = h.SteamId, RunId = h.RunId, GameVersion = h.GameVersion,
                            Speaker = h.Speaker, Summary = h.Summary, RoleName = h.RoleName } };
                    await adapter.RestoreAsync(checkpoint, shutdown);
                    EndMenu();
                    lastAutoKey = null; nextAutoTime = Time.realtimeSinceStartup + autoInterval;
                    result = new UiResult(true, "已回到保存的对话。");
                }
                catch (OperationCanceledException) { result = new UiResult(false, "读取请求已取消。"); }
                catch (Exception ex) { SafeLog("读取对话档失败：" + ex); result = new UiResult(false, "无法恢复此对话：" + ex.Message); }
                finally
                {
                    try { readingPause?.Dispose(); }
                    catch (Exception ex) { SafeLog("读档暂停租约释放失败：" + ex.Message); }
                    if (ownsStore) busy = false;
                }
                if (pendingLoads.TryGetValue(revisionId, out var callbacks))
                {
                    pendingLoads.Remove(revisionId);
                    foreach (var callback in callbacks.ToArray())
                        InvokeResult(callback, result);
                }
                if (!result.Success && !disposed && !Sdk.UIMgr.IsViewOpened<View.Main.SaveView>()) Notify(result.Message);
            }
            catch (Exception ex) { SafeLog("读档完成通知失败：" + ex.Message); }
            finally { completion.TrySetResult(true); }
        }
        public void Delete(string revisionId, Action<UiResult> done)
        {
            if (revisionId == null || revisionId.StartsWith("invalid:",StringComparison.Ordinal))
            {InvokeResult(done,new UiResult(false,"无法确认文件归属，已保留原文件。"));return;}
            if(!EnsureRepository(out var reason)){InvokeResult(done,new UiResult(false,reason));return;}
            if(pendingDeletes.TryGetValue(revisionId,out var callbacks)){callbacks.Add(done);return;}
            pendingDeletes.Add(revisionId,new List<Action<UiResult>>{done});
            InvokeResult(done,new UiResult(false,"",true));
            CompleteDeleteAsync(revisionId,generation,repository);
        }
        async void CompleteDeleteAsync(string revisionId,int token,Repository repo)
        {
            UiResult result;bool ownsStore=false;
            try
            {
                // Refreshing the page can start a list scan immediately before this
                // request. Queue that one click; never make the player retry it.
                await WaitForStoreAsync(shutdown);
                if(disposed || token!=generation || !ReferenceEquals(repo,repository))throw new OperationCanceledException();
                busy=true;ownsStore=true;
                var updated=await Task.Run(()=>{repo.Delete(revisionId);return repo.Scan();},shutdown);
                if(disposed || token!=generation)throw new OperationCanceledException();
                records=updated;lastScanTime=Time.realtimeSinceStartup;initialized=true;
                result=new UiResult(true,"");
            }
            catch(OperationCanceledException){result=new UiResult(false,"删除请求已取消。");}
            catch(Exception ex){SafeLog("删除对话存档失败："+ex);result=new UiResult(false,"删除未完成："+ex.Message);}
            finally{if(ownsStore)busy=false;}
            if(!pendingDeletes.TryGetValue(revisionId,out var callbacks))return;
            pendingDeletes.Remove(revisionId);
            if(!disposed){RaiseRecordsChanged();foreach(var callback in callbacks.ToArray())InvokeResult(callback,result);}
        }
        public void QuickSave()
        {
            if (quickSaveRequested) return;
            StatusMessage = null;
            quickSaveRequested = true;
            try{StudentAgeDialogueSave.UI.AdvConfirmation.Ask("保存当前对话进度到快速存档吗？",CompleteQuickSaveAsync,()=>quickSaveRequested=false,key:"QuickSave");}
            catch{quickSaveRequested=false;throw;}
        }
        async void CompleteQuickSaveAsync()
        {
            try
            {
                while (adapter.IsRestoring || pendingLoads.Count > 0) { shutdown.ThrowIfCancellationRequested(); await nextFrame(); }
                int epoch = captureEpochNumber;
                GameCheckpoint snapshot = menuSaving && menuCheckpoint != null ? menuCheckpoint : await CaptureRequestedAsync(shutdown);
                if (snapshot == null) throw menuCaptureError ?? new InvalidOperationException("未能完成当前对话快照。");
                await WaitForStoreAsync(shutdown);
                if (epoch != captureEpochNumber) throw new OperationCanceledException();
                var result = await PublishAsync(snapshot, DialogueUiCategory.Quick, NextQuickSlot(snapshot.Brief.RunId), null);
                if (disposed) return;
                if (!result.Success) Notify(result.Message);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Notify("快速保存未完成：" + ex.Message); }
            finally { quickSaveRequested = false; }
        }
        int NextQuickSlot(string run)
        {
            var latest=Repository.FindHeads(records).Where(r=>r.Header.Category=="quick" && r.Header.RunId==run)
                .OrderByDescending(r=>r.Header.SavedUtc??r.Header.CreatedUtc,StringComparer.Ordinal).FirstOrDefault();
            return latest!=null && int.TryParse(latest.Header.LogicalSlot,out int slot)?slot%12+1:1;
        }
        public void QuickLoad()
        {
            if (disposed || quickLoadRequested || quickLoadPause != null) return;
            quickLoadRequested = true;
            PrepareQuickLoadAsync();
        }
        async void PrepareQuickLoadAsync()
        {
            try
            {
                while (adapter.IsRestoring) { shutdown.ThrowIfCancellationRequested(); await nextFrame(); }
                if (adapter.IsDialogueContext) quickLoadPause = adapter.PauseForMenu();
                await WaitForStoreAsync(shutdown);
                var brief = adapter.CurrentCheckpointBrief;
                if (brief == null || string.IsNullOrEmpty(brief.RunId)) throw new InvalidOperationException("请在对话存档列表中选择要加载的周目。");
                if (records.Any(r => r.Status == SaveStatus.UnsupportedVersion && r.Header != null && r.Header.Category == "quick" && r.Header.RunId == brief.RunId && r.Header.SteamId == brief.SteamId))
                    throw new InvalidOperationException("快速档由较新版本创建，尚不能解析其格式。");
                var heads = Repository.FindHeads(records).Where(r => r.Header.Category == "quick" && r.Header.RunId == brief.RunId && r.Header.SteamId == brief.SteamId).ToList();
                if(heads.Count==0)throw new InvalidOperationException("当前周目没有对话快速存档。");
                if(heads.Any(r=>r.IsConflict))throw new InvalidOperationException("快速档存在设备分支，请从列表选择要读的一份。");
                string revision=heads.OrderByDescending(r=>r.Header.SavedUtc??r.Header.CreatedUtc,StringComparer.Ordinal).First().Header.RevisionId;
                StudentAgeDialogueSave.UI.AdvConfirmation.Ask("加载快速对话存档将离开当前进度，是否继续？",
                    () => Load(revision, r => { if (!r.IsPending) ReleaseQuickPause(); }), ReleaseQuickPause,key:"QuickLoad");
            }
            catch (OperationCanceledException) { ReleaseQuickPause(); }
            catch (Exception ex) { ReleaseQuickPause(); Notify(ex.Message); }
            finally { quickLoadRequested = false; }
        }
        void ReleaseQuickPause() { var pause = quickLoadPause; quickLoadPause = null; pause?.Dispose(); }
        internal async Task SaveBeforeExitAsync(CancellationToken cancellation)
        {
            if (!IsDialogueContext || adapter.IsRestoring) return;
            // A save-page snapshot is already frozen at the player's current position.
            var checkpoint = menuCheckpoint;
            EndMenu();
            if (!EnsureRepository(out string reason)) throw new IOException(reason);
            if (checkpoint == null) checkpoint = await CaptureRequestedAsync(cancellation, true);
            await WaitForStoreAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            // Slot 2 is separate from periodic autosave; PublishAsync still checks parents
            // under the repository lock and never overwrites a competing cloud branch.
            UiResult result = await PublishAsync(checkpoint, DialogueUiCategory.Auto, 2, null);
            if (!result.Success) throw new IOException(result.Message);
            SafeLog("退出对话自动存档已完成。");
        }

        public void TickAutoSave()
        {
            if (!autoEnabled || disposed || pendingLoads.Count > 0 || quickLoadRequested || busy || quickSaveRequested || autoSaveRequested || captureRequests > 0 || menuOpen || Time.realtimeSinceStartup < nextAutoTime) return;
            if (!adapter.CanCapture(out _)) return;
            if (!EnsureRepository(out _)) return;
            if (!initialized || IsListing) { Refresh(); return; }
            var brief = adapter.CurrentCheckpointBrief;
            if (brief == null || string.IsNullOrEmpty(brief.StableNodeKey) || brief.StableNodeKey == lastAutoKey) return;
            nextAutoTime = Time.realtimeSinceStartup + autoInterval;
            autoSaveRequested = true;
            CompleteAutoSaveAsync();
        }
        internal void WarmListing()
        {
            // Start the first verified index while the player is outside the save
            // page. Existing results remain visible during later background scans.
            if(disposed || initialized || IsListing || busy || Time.realtimeSinceStartup-lastScanTime<2 || !DialogueUiController.IsUiReady())return;
            if(EnsureRepository(out _))Refresh();
        }
        async void CompleteAutoSaveAsync()
        {
            try
            {
                int epoch = captureEpochNumber;
                GameCheckpoint snapshot = await CaptureRequestedAsync(shutdown);
                await WaitForStoreAsync(shutdown);
                if (epoch != captureEpochNumber) throw new OperationCanceledException();
                UiResult result = await PublishAsync(snapshot, DialogueUiCategory.Auto, 1, null);
                if (disposed) return;
                if (result.Success) { lastAutoKey = snapshot.Brief.StableNodeKey; lastAutoError = null; }
                else AutoFailure(result.Message);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed) AutoFailure(ex.Message); }
            finally { autoSaveRequested = false; }
        }
        void AutoFailure(string reason) { SafeLog("自动对话存档跳过：" + reason); if (reason != lastAutoError) { lastAutoError = reason; StatusMessage = "自动对话存档未完成：" + reason; RaiseRecordsChanged(); } }
        public void Notify(string message)
        {
            if (disposed || string.IsNullOrEmpty(message)) return;
            // Native save/load has no corner toast. Keep genuine errors visible in
            // the open page, or use the native error dialog when there is no page.
            SafeLog(message);
            StatusMessage = message;
            RaiseRecordsChanged();
            if (!Sdk.UIMgr.IsViewOpened<View.Main.SaveView>())
            {
                try { HintHelper.ShowHint("对话存档", message, null, false); }
                catch (Exception ex) { SafeLog("存档错误界面未能显示：" + ex.Message); }
            }
        }
        void SafeLog(string message) { try { log(message); } catch { } }
        void InvokeResult(Action<UiResult> callback, UiResult result)
        {
            if (callback == null) return;
            foreach (Action<UiResult> observer in callback.GetInvocationList())
                try { observer(result); }
                catch (Exception ex) { SafeLog("存读档结果回调失败：" + ex.Message); }
        }
        void RaiseRecordsChanged()
        {
            if (disposed) return;
            var observers = RecordsChanged;
            if (observers == null) return;
            foreach (Action observer in observers.GetInvocationList())
                try { observer(); }
                catch (Exception ex) { SafeLog("存档列表刷新回调失败：" + ex.Message); }
        }
        static string Category(DialogueUiCategory c) => c == DialogueUiCategory.Auto ? "auto" : c == DialogueUiCategory.Quick ? "quick" : "manual";
        static DialogueUiCategory ParseCategory(string c) => c == "auto" ? DialogueUiCategory.Auto : c == "quick" ? DialogueUiCategory.Quick : DialogueUiCategory.Manual;
        public void Dispose() { if (disposed) return; disposed = true; generation++; captureEpoch.Cancel(); captureEpoch.Dispose(); EndMenu(); ReleaseQuickPause(); }
    }
}
