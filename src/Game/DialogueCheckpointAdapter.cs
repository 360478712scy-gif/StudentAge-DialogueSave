using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Config;
using BepInEx.Bootstrap;
using DG.Tweening;
using HarmonyLib;
using MessagePack;
using MessagePack.Resolvers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using View.Evt;
using View.Main;

namespace StudentAgeDialogueSave.GameIntegration
{
    /// <summary>
    /// Version-specific native adapter. Supports ordinary, settled dialogue entered by
    /// CommonEvtMgr.ShowNewRoundEvent over MainView. It never replays RefreshTalk to restore.
    /// All live game objects and MessagePack serialization remain on the Unity thread.
    /// </summary>
    public sealed class DialogueCheckpointAdapter : IDisposable
    {
        // Optional presentation policy; storage/restoration does not own a UI mode.
        internal Func<float,float> PresentationTextSpeed;
        internal Action BeforePresentationRebuild;
        internal Func<HistoryCheckpoint[]> CaptureHistoryTrail;
        internal Action<HistoryCheckpoint[]> RestoreHistoryTrail;
        internal Action<NewTalkView> PreparingPresentation;
        private const string Continuation = "main-round-event-v1";
        private static DialogueCheckpointAdapter current;
        private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
        private readonly Func<Task> nextFrame;
        private readonly Action<string> diagnostics;
        private readonly ConditionalWeakTable<BaseView, object> retired = new ConditionalWeakTable<BaseView, object>();
        private readonly List<MethodBase> patches = new List<MethodBase>();
        private Harmony harmony;
        private bool disposed;
        private ConfigFingerprintSnapshot cachedConfig;
        private readonly ConditionalWeakTable<ConfigFingerprintSnapshot,Task<string>> configHashJobs=new ConditionalWeakTable<ConfigFingerprintSnapshot,Task<string>>();
        private ConfigFingerprintSnapshot idleConfig;
        private Task<string> idleConfigHash;
        private bool idleConfigStarted;
        private System.Diagnostics.Stopwatch idleConfigTime;
        private bool configPlansReady;
        private bool configWarmFailed;
        private double configWarmMilliseconds;
        private bool capturing;
        private bool captureRequested;
        private bool exitCapture;
        private NewTalkView captureView;
        private NewTalkView playbackResumeView;
        private bool playbackResumeAuto;
        private float playbackResumeScale = 1f;
        private float playbackResumeDelay = -1f;
        private bool restoring;
        private int pauseCount;
        private int sourceDepth;
        private int optionDepth;
        private int eventDepth;
        private int rootEvent;
        private int pendingTalk;
        private NewTalkView trackedView;
        private string sessionId;
        private long visit;
        private JObject pendingRestore;
        private NewTalkView pendingRestoreView;
        private int pendingRestoreGeneration;
        private TaskCompletionSource<NewTalkView> restoredView;
        private int generation;

        private static readonly MessagePackSerializerOptions SaveOptions =
            ContractlessStandardResolverAllowPrivate.Options.WithSecurity(MessagePackSecurity.UntrustedData);
        private static readonly JsonSerializer DataJson = JsonSerializer.Create(new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 64
        });

        public DialogueCheckpointAdapter(Func<Task> nextFrame, Action<string> diagnostics = null)
        {
            this.nextFrame = nextFrame ?? throw new ArgumentNullException(nameof(nextFrame));
            this.diagnostics = diagnostics;
        }

        public bool IsRestoring => restoring;
        public bool IsPausedForMenu => pauseCount > 0;
        public bool IsDialogueContext => !disposed && IsUiReady() && UIMgr.GetView<NewTalkView>(false) is NewTalkView view && view.viewState == ViewState.Opened;
        public bool IsSupportedDialogueContext => IsDialogueContext;
        public string Reason { get { CanCapture(out string reason); return reason; } }
        public CheckpointBrief CurrentCheckpointBrief => IsDialogueContext && UIMgr.GetView<NewTalkView>(false) is NewTalkView view &&
            Get<TalkCfg>(view, "cfg") != null ? BuildBrief(view) : null;

        public void WarmConfigurationPlansTick()
        {
            AssertThread();
            if (disposed || configWarmFailed || restoring || capturing || captureRequested || pauseCount > 0 ||
                !IsUiReady() || Cfg.TextCfgMap == null || Cfg.TextCfgMap.Count == 0 || IsDialogueContext || UIMgr.IsViewOpened<SaveView>()) return;
            var title = UIMgr.GetView<EntryView>(false) as EntryView;
            var main = UIMgr.GetView<MainView>(false) as MainView;
            bool titleReady = title != null && title.viewState == ViewState.Opened && title.isViewReady &&
                title.gameObject.activeInHierarchy && !Get<bool>(title, "isPause");
            bool mainReady = main != null && main.viewState == ViewState.Opened && main.isViewReady && main.gameObject.activeInHierarchy;
            // These views open only after config, Mod and DescCtrl initialization. No early Install work.
            if ((!titleReady && !mainReady) || (title != null && title.viewState == ViewState.Opened && Get<bool>(title, "isPause"))) return;
            if (!configPlansReady)
            {
                var time = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    configPlansReady = ConfigFingerprintSnapshot.WarmPlansTick(DataJson);
                    configWarmMilliseconds += time.Elapsed.TotalMilliseconds;
                    if (time.Elapsed.TotalMilliseconds >= 3) Trace("config.plans.prewarm.tick", time.Elapsed.TotalMilliseconds);
                    if (configPlansReady) Trace("config.plans.prewarm.total", configWarmMilliseconds);
                }
                catch (Exception error)
                {
                    configWarmFailed = true;
                    try { diagnostics?.Invoke("对话配置类型预热未完成，保存时将重新校验：" + error.Message); } catch { }
                }
                return; // Never combine the last schema compilation and full copy in one tick.
            }
            // At most one speculative idle snapshot per plugin lifetime. A failed comparison
            // does not retry every frame; actual save/load retains full change detection.
            if (!idleConfigStarted && titleReady && ReferenceEquals(UIMgr.GetTopView(), title))
            {
                idleConfigStarted = true;
                if (cachedConfig != null) return;
                try
                {
                    idleConfig = Measure("config.idle.detach", () => ConfigFingerprintSnapshot.Capture(DataJson, (stage, ms) => Trace(stage, ms)));
                    idleConfigTime = System.Diagnostics.Stopwatch.StartNew();
                    idleConfigHash = Task.Run(idleConfig.ComputeDigest);
                    // Observe faults even if shutdown leaves no main-thread tick to consume them.
                    idleConfigHash.ContinueWith(failed => { var observed = failed.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception error)
                {
                    idleConfig = null; idleConfigHash = null;
                    try { diagnostics?.Invoke("对话配置空闲预校验未完成，实际存读档时重新检查：" + error.Message); } catch { }
                }
            }
            if (idleConfigHash == null || !idleConfigHash.IsCompleted) return;
            try
            {
                idleConfigHash.GetAwaiter().GetResult();
                Trace("config.idle.hash", idleConfigTime.Elapsed.TotalMilliseconds);
                if (Measure("config.idle.compare", idleConfig.MatchesCurrent)) cachedConfig = idleConfig;
            }
            catch (Exception error)
            {
                try { diagnostics?.Invoke("对话配置空闲预校验未完成，实际存读档时重新检查：" + error.Message); } catch { }
            }
            finally { idleConfig = null; idleConfigHash = null; idleConfigTime = null; }
        }

        public void Install(Harmony owner)
        {
            AssertThread();
            if (current != null) throw new InvalidOperationException("Dialogue adapter is already installed.");
            harmony = owner ?? throw new ArgumentNullException(nameof(owner));
            // Validate every private field used in save/restore before allowing the UI to offer a save.
            foreach (string name in new[] { "cfg", "talkType", "evtId", "talkId", "roles", "posRoles", "roleCloths", "historys", "curBgId", "lastEffectCfgId", "waitFrame", "enableAutoTalk", "isDelaying", "isPhoneing", "isShowingCG", "isShowingComic", "countdown", "talkingPos", "talkingRoles", "playEvtGroupBgm", "enableSceneSound", "topSeq", "topSeq2", "topWaitSeq", "curVFX" }) Field(typeof(NewTalkView), name);
            Field(typeof(SaveMgrEx), "gameSaveMgr");
            Field(typeof(SaveMgr), "saveDict");
            Field(typeof(SaveMgr), "saveObjList");
            Field(typeof(CommonEvtMgr), "roundEndEventQueue");
            current = this;
            try
            {
                Patch(typeof(CommonEvtMgr), "ShowNewRoundEvent", nameof(SourcePrefix), null, nameof(SourceFinalizer));
                Patch(typeof(CommonEvtMgr), "ShowEvent", nameof(EventPrefix), null, nameof(EventFinalizer));
                Patch(typeof(CommonEvtMgr), "ShowTalk", nameof(TalkEntryPrefix), null, null,
                    new[] { typeof(int), typeof(Action), typeof(int), typeof(bool), typeof(bool), typeof(string) });
                Patch(typeof(CommonEvtMgr), "SelectOption", nameof(OptionPrefix), null, nameof(OptionFinalizer));
                Patch(typeof(NewTalkView), "OnOpen", nameof(OpenPrefix), null, null);
                Patch(typeof(BaseView), "LoadComp", nameof(LoadedPrefix), null, null);
                Patch(typeof(NewTalkView), "RefreshTalk", nameof(RefreshPrefix), null, null);
                foreach (string name in new[] { "OnClickNext", "NextTalk", "OnClickSkip", "CloseView", "DoTextEnd", "ShowOption", "RefreshEvt" })
                    Patch(typeof(NewTalkView), name, nameof(TransitionPrefix), null, null);
                Patch(typeof(NewTalkView), "AutoTalk", nameof(AutoPrefix), null, null);
                Patch(typeof(NewTalkView), "SpeedUp", nameof(SpeedPrefix), null, null);
                Patch(typeof(NewTalkView), "Update", nameof(DialogueUpdatePrefix), null, null);
                Patch(typeof(MainView), "CheckGuide", nameof(GuidePrefix), null, null);
                Patch(typeof(MapSceneView), "CheckGuide", nameof(GuidePrefix), null, null);
                Patch(typeof(MapRoleView), "CheckGuide", nameof(GuidePrefix), null, null);
                Patch(typeof(MapRoleView), "CloseView2", nameof(QuietPrefix), null, null);
                Patch(typeof(PhoneData), "AddPhoto", nameof(QuietPrefix), null, null);
                Patch(typeof(AudioMgrEx), "PlayNpcSoundOneShot", nameof(QuietPrefix), null, null);
                Patch(typeof(MainView), "OnTelephoneRing", nameof(QuietPrefix), null, null);
                Patch(typeof(GuideData), "SaveGlobalGuide", nameof(GlobalGuidePrefix), null, null);
                Patch(typeof(Control), "Update", nameof(QuietPrefix), null, null);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool CanCapture(out string reason) => CanCaptureCore(false,out reason);
        internal bool CanCaptureHistory(out string reason) => CanCaptureCore(true,out reason);
        private bool CanCaptureCore(bool history,out string reason)
        {
            AssertThread();
            reason = null;
            if (disposed || current != this) return Refuse(out reason, "对话存档适配器未就绪");
            if (!IsUiReady()) return Refuse(out reason, "游戏界面尚未准备好");
            if (restoring || capturing) return Refuse(out reason, "正在处理另一个存读档操作");
            var view = UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            if (view == null || view.viewState != ViewState.Opened || !view.gameObject.activeInHierarchy)
                return Refuse(out reason, "当前没有正在显示的普通对话");
            if (Singleton<FuncMgr>.Ins.GetGuideData().showingGuide != 0)
                return Refuse(out reason, "正在进行引导操作，请完成引导后保存");
            bool pausedTyping = view.talkState == TalkState.Anim &&
                (history || (captureRequested && view == captureView) || playbackResumeView == view);
            if (!pausedTyping && view.talkState != TalkState.AnimEnd && view.talkState != TalkState.Option && view.talkState != TalkState.Countdown)
                return Refuse(out reason, "请等待这段文字显示完整或选项稳定后保存");
            int waitFrame = Get<int>(view, "waitFrame");
            // Native DoTextEnd returns directly after ShowOption and leaves waitFrame nonzero.
            // For an option page, one completed frame is the stability boundary, not a zero flag.
            bool textTransitionPending = view.talkState == TalkState.Option ? !history && waitFrame >= Time.frameCount : waitFrame != 0;
            if (Get<NewTalkType>(view, "talkType") != NewTalkType.Talk || textTransitionPending ||
                Get<bool>(view, "isDelaying") || optionDepth != 0)
                return Refuse(out reason, "请暂停自动播放或快进并等待对话停稳");
            if (Get<bool>(view, "isPhoneing") || Get<bool>(view, "isShowingCG") || Get<bool>(view, "isShowingComic") ||
                view.paperId != 0 || view.group_foreground.gameObject.activeInHierarchy ||
                view.group_item.gameObject.activeInHierarchy || view.group_msg.gameObject.activeInHierarchy ||
                view.img_screen_effect.gameObject.activeInHierarchy || Get<object>(view, "curVFX") != null)
                return Refuse(out reason, "此特殊演出或限时操作尚不支持对话存档");
            TalkCfg cfg = Get<TalkCfg>(view, "cfg");
            if (cfg == null || (cfg.screenEffect != null && cfg.screenEffect.Count > 0))
                return Refuse(out reason, "此对白的特殊演出状态尚未完成适配");
            if (view.tmpTalks == null || view.tmpTalkIdx < 0 || view.tmpTalkIdx >= view.tmpTalks.Count ||
                (!pausedTyping && view.txtex_content.text != view.tmpTalks[view.tmpTalkIdx]))
                return Refuse(out reason, "对白仍在显示过程中");
            if (Get<int>(view, "curBgId") <= 0 || !Cfg.BgCfgMap.ContainsKey(Get<int>(view, "curBgId")))
                return Refuse(out reason, "当前背景状态尚不能完整恢复");
            try { DialogueContinuation.Capture(view); }
            catch (InvalidOperationException error) { return Refuse(out reason, error.Message); }
            catch (InvalidDataException error) { return Refuse(out reason, error.Message); }
            try { foreach (var option in ReadOptions(view)) DialogueContinuation.CaptureCallback(option.callback); }
            catch (InvalidOperationException error) { return Refuse(out reason, error.Message); }
            catch (InvalidDataException error) { return Refuse(out reason, error.Message); }
            // A third-party view may hold an untracked continuation even with a native talk window visible.
            if (!OnlyExpectedViews()) return Refuse(out reason, "存在其他活动窗口，请关闭后再保存");
            if (!DialogueAudioAdapter.CanCapture(out reason)) return false;
            return true;
        }

        public IDisposable PauseForMenu()
        {
            AssertThread();
            if (disposed || current != this) throw new InvalidOperationException("对话存档适配器未就绪");
            if (restoring) throw new InvalidOperationException("正在恢复对话");
            // A browsing/confirmation lease freezes the current conversation; it does not
            // certify that its complete world/continuation can already be serialized.
            // In particular, opening Load while text is typing must remain possible.
            if (pauseCount == 0 && IsDialogueContext)
            {
                var view = UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                EnsureTracked(view);
                bool automatic = playbackResumeView == view ? playbackResumeAuto : Get<bool>(view, "enableAutoTalk");
                float scale = playbackResumeView == view ? playbackResumeScale : Time.timeScale;
                if (view.talkState == TalkState.Anim && Get<int>(view, "waitFrame") == 0)
                    ((TMPro.TextMeshProUGUI)Call(view, "GetTalkTxt"))?.DOKill(false);
                QueuePlaybackResume(view, automatic, scale, PlaybackDelay(view));
            }
            pauseCount++;
            return new PauseLease(this);
        }

        public GameCheckpoint Capture() => CaptureCore(false, out _);

        // History owns detached world bytes and presentation state, entirely in memory.
        // Global configuration and plugin binaries are diagnostics, not world state:
        // do not traverse/hash them for each line or replay effects when jumping.
        internal HistoryCheckpoint CaptureHistory()
        {
            var time=System.Diagnostics.Stopwatch.StartNew();
            GameCheckpoint state=CaptureCore(true,out ConfigFingerprintSnapshot config,true);
            return new HistoryCheckpoint { State=state, CaptureMilliseconds=time.Elapsed.TotalMilliseconds };
        }
        internal Task RestoreHistoryAsync(HistoryCheckpoint history, CancellationToken cancellation)
        {
            AssertThread();
            if(history?.State==null)throw new InvalidDataException("缺少完整历史快照");
            var state=new GameCheckpoint { WorldBytes=history.State.WorldBytes,
                Dialogue=(JObject)history.State.Dialogue.DeepClone(),Brief=history.State.Brief };
            // Returning to a line is a manual reading action, not a request to resume
            // the auto/fast-forward mode used when its checkpoint was recorded.
            // Only change the detached history copy; disk saves keep their playback policy.
            state.Dialogue["playback"]=new JObject { ["auto"]=false, ["timeScale"]=1f, ["autoDelayRemaining"]=-1f };
            // Use the same world restore path as a disk archive, without disk reads
            // or replaying the intervening dialogue/choice effects.
            return RestoreCheckedAsync(state,cancellation,history.Config);
        }

        bool capturingHistory;
        private GameCheckpoint CaptureCore(bool detachConfig, out ConfigFingerprintSnapshot config,bool history=false)
        {
            config = null;
            AssertThread();
            if (!CanCaptureCore(history,out string reason)) throw new InvalidOperationException(reason);
            NewTalkView view = UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            EnsureTracked(view);
            capturing = true; capturingHistory = history;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                JObject dialogue = Measure("dialogue", () => CaptureDialogue(view, detachConfig, history));
                byte[] bytes = CaptureWorld();
                return new GameCheckpoint { Dialogue = dialogue, WorldBytes = bytes, Brief = BuildBrief(view), HistoryTrail=history?null:CaptureHistoryTrail?.Invoke() };
            }
            finally { capturing = false; Trace("capture.total", elapsed.Elapsed.TotalMilliseconds); capturingHistory = false; }
        }

        /// <summary>Accept once, pause text before its completion effect and settle presentation resources,
        /// and detach a complete snapshot on the main thread. Playback resumes before history
        /// compression and disk writes; callers keeping a menu open hold their own pause lease.</summary>
        internal async Task<GameCheckpoint> CaptureExitAsync(CancellationToken token)
        {
            exitCapture = true;
            try { return await CaptureAsync(token); }
            finally { exitCapture = false; }
        }

        public async Task<GameCheckpoint> CaptureAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            AssertThread();
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || captureRequested || restoring || capturing)
                throw new InvalidOperationException("正在处理另一个存读档操作");
            if (!IsDialogueContext) throw new InvalidOperationException("当前没有正在显示的对话");
            NewTalkView view = UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            bool wasAuto = playbackResumeView == view ? playbackResumeAuto : Get<bool>(view, "enableAutoTalk");
            float wasScale = playbackResumeView == view ? playbackResumeScale : Time.timeScale;
            float wasDelay = PlaybackDelay(view);
            captureRequested = true;
            captureView = view;
            bool captureReleased = false;
            Action releaseCapture = () =>
            {
                if (captureReleased) return;
                captureReleased = true;
                captureRequested = false;
                captureView = null;
                if (!disposed && view != null && view.viewState == ViewState.Opened)
                    QueuePlaybackResume(view, wasAuto, wasScale, wasDelay);
            };
            try
            {
                Set(view, "enableAutoTalk", false);
                Singleton<TimerMgr>.Ins.Remove(view.OnClickNext);
                global::Game.TimeChange(1f);
                float deadline = Time.realtimeSinceStartup + 20f;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (disposed || view == null || view.viewState != ViewState.Opened)
                        throw new OperationCanceledException("对话存档请求已取消");
                    // Pause without completing. Saving must not run DoTextEnd's side effects or
                    // discard a transition launched by one of them. Resume the tween after the lease.
                    if (view.talkState == TalkState.Anim && Get<int>(view, "waitFrame") == 0 &&
                        view.tmpTalks != null && view.tmpTalkIdx >= 0 && view.tmpTalkIdx < view.tmpTalks.Count)
                    {
                        var text = (TMPro.TextMeshProUGUI)Call(view, "GetTalkTxt");
                        text.DOKill(false);
                    }
                    if (CanCapture(out string reason))
                    {
                        GameCheckpoint checkpoint = CaptureCore(true, out ConfigFingerprintSnapshot config);
                        // The complete world and dialogue are detached on the main thread.
                        // Global config and DLL hashes are not restore prerequisites. Computing
                        // them here stalls typing and adds no protection to this snapshot.
                        releaseCapture();
                        cancellationToken.ThrowIfCancellationRequested();
                        checkpoint.Dialogue["playback"] = new JObject { ["auto"] = wasAuto, ["timeScale"] = wasScale, ["autoDelayRemaining"] = wasDelay };
                        if(checkpoint.HistoryTrail?.Length>0)
                        {
                            var pack=Task.Run(()=>HistoryTrailCodec.Encode(checkpoint.HistoryTrail));
                            while(!pack.IsCompleted){cancellationToken.ThrowIfCancellationRequested();await nextFrame();AssertThread();}
                            checkpoint.Dialogue["historyTrail"]=pack.GetAwaiter().GetResult();
                            checkpoint.HistoryTrail=null;
                        }
                        return checkpoint;
                    }
                    if (Time.realtimeSinceStartup > deadline)
                        throw new TimeoutException("对话保存未完成：" + reason);
                    await nextFrame();
                    AssertThread();
                }
            }
            finally
            {
                releaseCapture();
            }
        }

        public bool CanRestore(out string reason)
        {
            AssertThread();
            reason = null;
            if (disposed || current != this) return Refuse(out reason, "对话存档适配器未就绪");
            if (restoring || capturing || captureRequested) return Refuse(out reason, "正在处理另一个存读档操作");
            if (!IsUiReady()) return Refuse(out reason, "游戏界面尚未准备好");
            // Loading abandons the current flow. Its callback, visual phase and saveability
            // are not requirements for restoring an independently validated checkpoint.
            return true;
        }

        public Task RestoreAsync(GameCheckpoint snapshot, CancellationToken cancellationToken = default(CancellationToken))
            => RestoreCheckedAsync(snapshot,cancellationToken,null);

        private async Task RestoreCheckedAsync(GameCheckpoint snapshot,CancellationToken cancellationToken,ConfigFingerprintSnapshot historyConfig)
        {
            AssertThread();
            if (!CanRestore(out string reason)) throw new InvalidOperationException(reason);
            if (snapshot == null || snapshot.WorldBytes == null || snapshot.Dialogue == null) throw new InvalidDataException("缺少完整对话存档");
            HistoryCheckpoint[] trail=null;
            if(snapshot.Dialogue["historyTrail"]!=null)
            {
                string encoded=snapshot.Dialogue.Value<string>("historyTrail");
                var unpack=Task.Run(()=>HistoryTrailCodec.Decode(encoded));
                while(!unpack.IsCompleted){cancellationToken.ThrowIfCancellationRequested();await nextFrame();AssertThread();}
                trail=unpack.GetAwaiter().GetResult();
            }
            var restoreClock = System.Diagnostics.Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            // Fingerprints describe the capture environment, not the archive format.
            // Restore validates the actual saved nodes, world schema and continuation below.
            // A mod update or native in-place animation sorting must not invalidate old saves.
            Trace("restore.config", restoreClock.Elapsed.TotalMilliseconds); restoreClock.Restart();
            if (!CanRestore(out reason)) throw new InvalidOperationException(reason);
            Validate(snapshot.Dialogue, false);
            if(trail!=null)
            {
                var history=(JArray)snapshot.Dialogue["history"];
                foreach(var point in trail)
                {
                    var prior=(JArray)point.State.Dialogue["history"];
                    if(point.State.Brief.RunId!=snapshot.Brief.RunId || point.State.Brief.SteamId!=snapshot.Brief.SteamId ||
                        prior.Count>history.Count || !prior.SequenceEqual(history.Take(prior.Count),JToken.EqualityComparer))
                        throw new InvalidDataException("回看记录与目标存档的周目或对话分支不一致。");
                }
            }
            Dictionary<string, ISaveLoadValue> incoming = DecodeWorld(snapshot.WorldBytes);
            ValidateMods(incoming, snapshot.Dialogue);
            if (snapshot.Dialogue["continuation"] is JObject continuation)
                DialogueContinuation.ValidateWorld(continuation, incoming.Values);
            foreach (JObject option in (JArray)snapshot.Dialogue["options"])
                if (option["callback"] != null) DialogueContinuation.ValidateCallbackWorld(option["callback"], incoming.Values);
            // Validate the destination before abandoning any current state. Loading does not
            // capture or save the current flow, even when that flow could be serialized.
            cancellationToken.ThrowIfCancellationRequested();
            Trace("restore.preflight", restoreClock.Elapsed.TotalMilliseconds);
            using var resources = new DialogueUiResourceLease();
            var inputGuard = new RestoreInputGuard(UIMgr.GetView<NewTalkView>(false) as NewTalkView);
            restoring = true;
            int token = ++generation;
            try
            {
                await RestoreCore(snapshot.Dialogue, incoming, token);
                if(trail!=null)
                {
                    foreach(var point in trail)
                        if(cachedConfig?.Digest==point.State.Dialogue.Value<string>("configDigest"))point.Config=cachedConfig;
                    RestoreHistoryTrail?.Invoke(trail);
                }
            }
            catch (Exception original)
            {
                ++generation;
                pendingRestore = null;
                pendingRestoreView = null;
                restoredView?.TrySetCanceled();
                if (disposed) throw new OperationCanceledException("插件已关闭，恢复事务取消", original);
                // The user chose to discard the previous flow. Never run it again or require
                // a current-state checkpoint. Only a failed committed restore needs cleanup.
                try { QuietToTitle(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("对话恢复失败，界面清理未完成；磁盘存档未修改", original, cleanupError);
                }
                throw new InvalidOperationException("对话恢复失败，已停止此次恢复并返回标题界面；磁盘存档未修改", original);
            }
            finally
            {
                pendingRestore = null;
                pendingRestoreView = null;
                restoredView = null;
                restoring = false;
                inputGuard.Release(!disposed);
            }
        }

        private async Task RestoreCore(JObject data, Dictionary<string, ISaveLoadValue> world, int token)
        {
            AssertThread();
            if (disposed || token != generation) throw new OperationCanceledException("恢复事务已失效");
            var stageClock = System.Diagnostics.Stopwatch.StartNew();
            BeforePresentationRebuild?.Invoke();
            RetireCurrent();
            AudioMgrEx.StopAllMusic();
            UIMgr.CloseAllView(); // Destroy/OnClose, deliberately not NewTalkView.CloseView (which finishes the story).
            Singleton<ToastCtrl>.Ins.Clear();
            LoadWorld(world);
            Trace("restore.world", stageClock.Elapsed.TotalMilliseconds); stageClock.Restart();
            Singleton<FuncMgr>.Ins.GetGuideData().addGuides = data["pendingGuides"].Type == JTokenType.Null
                ? null : data["pendingGuides"].ToObject<List<int>>(DataJson);
            Singleton<FuncMgr>.Ins.GetGuideData().showingGuide = 0;
            Set(Singleton<RoundMgr>.Ins, "<RoundState>k__BackingField", (RoundState)(data.Value<int?>("roundState") ?? (int)RoundState.FinishPrepare));
            EndQueue().Clear();
            if (data["roundEndQueue"] is JArray endQueue) foreach (int id in endQueue.ToObject<int[]>()) EndQueue().Enqueue(id);
            var game = Get<object>(null, "ins", typeof(global::Game));
            Set(game, "<state>k__BackingField", GameState.Running);
            L2DCtrl.Clear();
            RedpointMgr.Clear();
            // Native running-game controls belong to TopView. HotkeyView is the title-screen
            // bar opened by Game.BackToMain; opening both would duplicate the dialogue controls.
            UIMgr.OpenView<TopView>();
            if (data["continuation"] is JObject continuation)
                await DialogueContinuation.RestoreBaseViews(continuation, nextFrame, () => !disposed && token == generation);
            else UIMgr.OpenView<MainView>(); // Earlier snapshots used the explicitly tracked home-round source.
            if (data["mapRecord"] is JObject mapRecord)
            {
                object map = Singleton<FuncMgr>.Ins.GetMapData();
                foreach (string name in new[] { "recordMapId", "recordBgId", "recordNpcId" }) Set(map, name, mapRecord.Value<int>(name));
            }
            await WaitUntil(() => (data["continuation"] is JObject roots ? DialogueContinuation.BaseViewsReady(roots) : UIMgr.IsViewOpened<MainView>()) && UIMgr.IsViewOpened<TopView>() &&
                UIMgr.GetView<TopView>(false).isViewReady, token);
            Trace("restore.baseViews", stageClock.Elapsed.TotalMilliseconds); stageClock.Restart();
            rootEvent = data.Value<int>("rootEvent");
            sessionId = data.Value<string>("session");
            visit = data.Value<long>("visit");
            pendingRestore = data;
            pendingRestoreGeneration = token;
            pendingRestoreView = UIMgr.GetView<NewTalkView>() as NewTalkView;
            restoredView = new TaskCompletionSource<NewTalkView>();
            UIMgr.OpenView<NewTalkView>(UILayerType.None, null, new object[] { false, data.Value<int>("talkId") });
            await WaitUntil(() => restoredView.Task.IsCompleted, token);
            NewTalkView view = await restoredView.Task;
            await WaitUntil(() => !Get<bool>(view, "isDelaying") && view.isViewReady, token);
            Trace("restore.dialogueView", stageClock.Elapsed.TotalMilliseconds); stageClock.Restart();
            ApplyRoleVisuals(view, (JArray)data["roles"]);
            await DialogueAudioAdapter.RestoreAsync((JObject)data["audio"], nextFrame, () => !disposed && generation == token);
            Trace("restore.audio", stageClock.Elapsed.TotalMilliseconds);
            AssertThread();
            UnityEngine.Random.state = RestoreRandomState((JObject)data["randomState"]);
            Singleton<ToastCtrl>.Ins.Start();
            EventMgr.Send(10003);
            JObject playback = data["playback"] as JObject;
            QueuePlaybackResume(view, playback?.Value<bool>("auto") ?? false, playback?.Value<float>("timeScale") ?? 1f,
                playback?.Value<float?>("autoDelayRemaining") ?? -1f);
        }

        private async Task WaitUntil(Func<bool> ready, int token)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (true)
            {
                if (disposed || token != generation) throw new OperationCanceledException("恢复事务已失效");
                if (ready()) return;
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("等待游戏资源加载超时");
                await nextFrame();
                AssertThread();
            }
        }

        private JObject CaptureDialogue(NewTalkView view, bool detachConfig = false, bool history = false)
        {
            TalkCfg cfg = Get<TalkCfg>(view, "cfg");
            var roles = Get<Dictionary<int, NewTalkRoleData>>(view, "roles");
            var options = ReadOptions(view);
            return new JObject
            {
                ["format"] = 1,
                ["adapter"] = Continuation,
                ["gameModule"] = typeof(global::Game).Module.ModuleVersionId.ToString("D"),
                ["rootEvent"] = rootEvent,
                ["presentationEvent"] = DialoguePresentationPolicy.EventId(view),
                ["continuation"] = DialogueContinuation.Capture(view),
                ["roundState"] = (int)Singleton<RoundMgr>.Ins.RoundState,
                ["roundEndQueue"] = new JArray(EndQueue()),
                ["mapRecord"] = new JObject {
                    ["recordMapId"] = Get<int>(Singleton<FuncMgr>.Ins.GetMapData(), "recordMapId"),
                    ["recordBgId"] = Get<int>(Singleton<FuncMgr>.Ins.GetMapData(), "recordBgId"),
                    ["recordNpcId"] = Get<int>(Singleton<FuncMgr>.Ins.GetMapData(), "recordNpcId") },
                ["session"] = sessionId,
                ["visit"] = visit,
                ["talkId"] = cfg.id,
                ["talk"] = view.talk,
                ["segments"] = new JArray(view.tmpTalks),
                ["segmentIndex"] = view.tmpTalkIdx,
                ["phase"] = view.talkState.ToString(),
                ["visibleText"] = ((TMPro.TextMeshProUGUI)Call(view, "GetTalkTxt")).text,
                ["lastEffectCfgId"] = Get<int>(view, "lastEffectCfgId"),
                ["background"] = Get<int>(view, "curBgId"),
                ["backgroundEffect"] = CaptureBackgroundEffect(view),
                ["countdown"] = Get<float>(view, "countdown"),
                ["defaultOption"] = Get<CommonEvtOptionData>(view, "defaultOption")?.id ?? 0,
                ["playback"] = new JObject {
                    ["auto"] = playbackResumeView == view ? playbackResumeAuto : Get<bool>(view, "enableAutoTalk"),
                    ["timeScale"] = playbackResumeView == view ? playbackResumeScale : Time.timeScale,
                    ["autoDelayRemaining"] = PlaybackDelay(view) },
                ["endBgmChange"] = view.endBgmChange,
                ["talkingPos"] = (int)Get<TalkAxis>(view, "talkingPos"),
                ["roles"] = new JArray(roles.Values.Select(CaptureRole)),
                ["roleCloths"] = JObject.FromObject(Get<Dictionary<int, int>>(view, "roleCloths"), DataJson),
                ["posRoles"] = JObject.FromObject(Get<Dictionary<TalkAxis, List<int>>>(view, "posRoles"), DataJson),
                ["history"] = JArray.FromObject(Get<List<TalkData>>(view, "historys"), DataJson),
                ["options"] = new JArray(options.Select(x => new JObject { ["id"] = x.id, ["rate"] = x.rate,
                    ["callback"] = DialogueContinuation.CaptureCallback(x.callback) })),
                ["configDigest"] = "", // Legacy optional diagnostic; archive integrity is checked separately.
                ["plugins"] = PluginMetadata(),
                ["activeMods"] = new JArray(Singleton<ModCtrl>.Ins.activeMods ?? new List<ulong>()),
                ["audio"] = DialogueAudioAdapter.Capture(),
                ["pendingGuides"] = Singleton<FuncMgr>.Ins.GetGuideData().addGuides == null ? JValue.CreateNull()
                    : (JToken)new JArray(Singleton<FuncMgr>.Ins.GetGuideData().addGuides),
                ["randomState"] = CaptureRandomState(UnityEngine.Random.state)
            };
        }

        private void RestorePresentation(NewTalkView view, JObject data)
        {
            trackedView = view;
            DialoguePresentationPolicy.Bind(view, data.Value<int?>("presentationEvent") ?? data.Value<int>("rootEvent"));
            pendingTalk = 0;
            TalkCfg cfg = Cfg.TalkCfgMap[data.Value<int>("talkId")];
            view.IniSetting();
            Set(view, "talkType", NewTalkType.Talk);
            Set(view, "cfg", cfg);
            Set(view, "talkId", cfg.id);
            Set(view, "evtId", rootEvent);
            Set(view, "lastEffectCfgId", data.Value<int>("lastEffectCfgId"));
            Set(view, "enableAutoTalk", false);
            Set(view, "enableSceneSound", false);
            Set(view, "playEvtGroupBgm", false);
            view.callback = null;
            if (data["continuation"] is JObject continuation) DialogueContinuation.Bind(view, continuation);
            view.endBgmChange = data.Value<bool>("endBgmChange");
            view.talk = data.Value<string>("talk");
            view.tmpTalks = data["segments"].ToObject<List<string>>(DataJson);
            view.tmpTalkIdx = data.Value<int>("segmentIndex");
            view.talkState = (TalkState)Enum.Parse(typeof(TalkState), data.Value<string>("phase"));
            Set(view, "historys", data["history"].ToObject<List<TalkData>>(DataJson));
            Set(view, "roleCloths", data["roleCloths"].ToObject<Dictionary<int, int>>(DataJson));
            Set(view, "posRoles", data["posRoles"].ToObject<Dictionary<TalkAxis, List<int>>>(DataJson));
            Set(view, "talkingPos", (TalkAxis)data.Value<int>("talkingPos"));
            // The original OnOpen is bypassed during restore. Presentation must be
            // attached before native ForceMeshUpdate can trigger a canvas render.
            PreparingPresentation?.Invoke(view);
            Call(view, "RefreshBg", data.Value<int>("background"), 0f);
            ApplyBackgroundEffect(view, data["backgroundEffect"] as JObject);
            Set(view, "enableSceneSound", true);
            view.group_foreground.gameObject.SetActive(false);
            view.group_evt.gameObject.SetActive(false);
            view.group_item.gameObject.SetActive(false);
            view.group_msg.gameObject.SetActive(false);
            view.group_role.gameObject.SetActive(true);
            view.group_talk.gameObject.SetActive(true);
            view.img_talk.gameObject.SetActive(true);
            view.group_option.gameObject.SetActive(view.talkState == TalkState.Option);
            view.txtex_content.text = view.talkState == TalkState.Anim ? data.Value<string>("visibleText") : view.tmpTalks[view.tmpTalkIdx];
            view.btn_click.interactable = true;
            var roles = Get<Dictionary<int, NewTalkRoleData>>(view, "roles");
            foreach (JObject item in (JArray)data["roles"])
            {
                NewTalkRoleData role = RestoreRole(item);
                roles.Add(role.roleId, role);
                Call(view, "BindRoleDataWithCell", role);
                role.cell.transform.anchoredPosition3D = role.targetAnchoredPos;
                role.cell.transform.localScale = Vector(item["scale"]);
                role.hasSetInitPos = true;
            }
            Set(view, "talkingRoles", cfg.roleIds.Where(roles.ContainsKey).Select(id => roles[id]).ToList());
            AccessTools.Method(typeof(NewTalkView), "RefreshTalkingRole", new[] { typeof(List<int>), typeof(string) })
                .Invoke(view, new object[] { cfg.roleIds, cfg.roleName });
            Call(view, "PlayRoleEffect", roles.Keys.Select(id => new List<float> { id }).ToList());
            if (view.talkState == TalkState.Option)
            {
                var restoredOptions = new List<CommonEvtOptionData>();
                foreach (JObject option in (JArray)data["options"])
                {
                    // Passing a singleton option list avoids GetOptions' random subset selection entirely.
                    var item = (List<CommonEvtOptionData>)Call(Singleton<CommonEvtMgr>.Ins, "GetOptions", cfg.id,
                        new List<int> { option.Value<int>("id") }, false, null, option.Value<float>("rate"), null);
                    foreach (var restored in item)
                        if (option["callback"] != null) restored.callback = DialogueContinuation.RestoreCallback(option["callback"]);
                    restoredOptions.AddRange(item);
                }
                view.itemgroup_options.SetDatas(restoredOptions);
                if (data.Value<float?>("countdown") is float remaining && remaining > -1f)
                {
                    int defaultId = data.Value<int>("defaultOption");
                    CommonEvtOptionData fallback = restoredOptions.Single(x => x.id == defaultId);
                    Call(view, "ShowCountDown", true, cfg.time, fallback);
                    Call(view, "RefreshCountDown", remaining);
                }
            }
            view.root_next.gameObject.SetActive(view.talkState == TalkState.AnimEnd || view.talkState == TalkState.Countdown);
            if (view.talkState == TalkState.AnimEnd || view.talkState == TalkState.Countdown)
            {
                view.txtex_content.ForceMeshUpdate();
                Vector2 pos = view.txtex_content.GetLastCharacterBottomRightPos();
                pos.x += 10f;
                view.root_next.anchoredPosition = pos;
            }
            pendingRestore = null;
            restoredView.TrySetResult(view);
        }

        private static readonly string[] BackgroundEffectProperties =
            { "effectFactor", "colorFactor", "blurFactor", "effectMode", "colorMode", "blurMode", "enabled" };

        private static Coffee.UIEffects.UIEffect BackgroundEffect(NewTalkView view)
        {
            UISprite[] backgrounds = Get<UISprite[]>(view, "bgs");
            return backgrounds[Get<int>(view, "curBgIdx")].gameObject.GetComponent<Coffee.UIEffects.UIEffect>();
        }

        private static JObject CaptureBackgroundEffect(NewTalkView view)
        {
            var effect = BackgroundEffect(view);
            var result = new JObject();
            if (effect == null) return result;
            foreach (string name in BackgroundEffectProperties)
            {
                PropertyInfo property = AccessTools.Property(effect.GetType(), name);
                if (property == null || !property.CanRead || !property.CanWrite) throw new MissingMemberException(effect.GetType().Name, name);
                result[name] = JToken.FromObject(property.GetValue(effect, null), DataJson);
            }
            return result;
        }

        private static void ApplyBackgroundEffect(NewTalkView view, JObject state)
        {
            if (state == null || state.Count == 0) return; // Earlier format-1 snapshots had no effect state.
            var effect = BackgroundEffect(view) ?? throw new InvalidOperationException("背景效果组件缺失");
            foreach (string name in BackgroundEffectProperties)
            {
                PropertyInfo property = AccessTools.Property(effect.GetType(), name);
                JToken value = state[name] ?? throw new InvalidDataException("背景效果参数缺失");
                property.SetValue(effect, value.ToObject(property.PropertyType, DataJson), null);
            }
        }

        private static JObject CaptureRole(NewTalkRoleData role)
        {
            var fields = new JObject();
            foreach (FieldInfo field in typeof(NewTalkRoleData).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == "cell") continue;
                object value = field.GetValue(role);
                fields[field.Name] = field.FieldType == typeof(Vector3) ? VectorToken((Vector3)value) : JToken.FromObject(value);
            }
            fields["position"] = VectorToken(role.cell.transform.anchoredPosition3D);
            fields["scale"] = VectorToken(role.cell.transform.localScale);
            fields["sibling"] = role.cell.transform.GetSiblingIndex();
            Color color = role.isIcon ? role.cell.icon_role.image.color : role.cell.l2d_role.color;
            fields["color"] = new JArray(color.r, color.g, color.b, color.a);
            fields["alpha"] = role.isIcon ? role.cell.canvasgroup_role.alpha : role.cell.l2d_role.GetAlpha();
            return fields;
        }

        private static NewTalkRoleData RestoreRole(JObject fields)
        {
            var role = new NewTalkRoleData();
            foreach (FieldInfo field in typeof(NewTalkRoleData).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == "cell") continue;
                JToken value = fields[field.Name] ?? throw new InvalidDataException("立绘数据不完整");
                object parsed = field.FieldType == typeof(Vector3) ? (object)Vector(value) : value.ToObject(field.FieldType, DataJson);
                field.SetValue(role, parsed);
            }
            role.targetAnchoredPos = Vector(fields["position"]);
            role.targetFlip = false;
            role.targetJumpCnt = 0;
            role.targetShake = false;
            role.shakeTime = 0;
            role.delay = 0;
            role.moveTime = 0;
            return role;
        }

        private static void ApplyRoleVisuals(NewTalkView view, JArray data)
        {
            var roles = Get<Dictionary<int, NewTalkRoleData>>(view, "roles");
            foreach (JObject fields in data)
            {
                NewTalkRoleData role = roles[fields.Value<int>("roleId")];
                role.cell.transform.anchoredPosition3D = Vector(fields["position"]);
                role.cell.transform.localScale = Vector(fields["scale"]);
                role.cell.transform.SetSiblingIndex(fields.Value<int>("sibling"));
                JArray c = (JArray)fields["color"];
                Color color = new Color((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
                if (role.isIcon)
                {
                    role.cell.icon_role.image.color = color;
                    role.cell.canvasgroup_role.alpha = fields.Value<float>("alpha");
                }
                else
                {
                    role.cell.l2d_role.SetColor(color);
                    role.cell.l2d_role.SetAlpha(fields.Value<float>("alpha"));
                }
            }
        }

        private static List<CommonEvtOptionData> ReadOptions(NewTalkView view)
        {
            return view.talkState == TalkState.Option
                ? view.itemgroup_options.GetCells().OrderBy(c => c.cellIdx).Select(c => c.data as CommonEvtOptionData).Where(c => c != null).ToList()
                : new List<CommonEvtOptionData>();
        }

        private CheckpointBrief BuildBrief(NewTalkView view)
        {
            var role = Singleton<RoleMgr>.Ins.GetRole();
            int round = Singleton<RoundMgr>.Ins.GetRound();
            RoundCfg roundCfg = Cfg.RoundCfgMap[round];
            int id = Get<TalkCfg>(view, "cfg").id;
            string segment = view.tmpTalks != null && view.tmpTalkIdx >= 0 && view.tmpTalkIdx < view.tmpTalks.Count
                ? view.tmpTalks[view.tmpTalkIdx] : view.talk;
            string summary = Regex.Replace(segment ?? "", "<[^>]*>", "").Replace('\n', ' ');
            return new CheckpointBrief
            {
                SteamId = new DirectoryInfo(PathDefine.SAVE_PATH).Name,
                RunId = role.guid.ToString(),
                Speaker = view.txt_name.text ?? "",
                Summary = summary.Length > 120 ? summary.Substring(0, 120) + "…" : summary,
                GameVersion = Application.version,
                StableNodeKey = sessionId + ":" + visit + ":" + id + ":" + view.tmpTalkIdx + ":" + view.talkState,
                RoleName = role.Name,
                Round = round,
                YearLabel = roundCfg.year + "年",
                SeasonLabel = Cfg.SeasonCfgMap[roundCfg.season].name,
                SeasonId = roundCfg.season,
                Location = Cfg.MapCfgMap.TryGetValue(Singleton<FuncMgr>.Ins.GetMapData().recordMapId, out MapCfg map) ? map.name : "",
                Gender = (int)role.Sex,
                GradeState = role.GradeState
            };
        }

        private byte[] CaptureWorld()
        {
            var mgr = NativeSaveMgr();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            byte[] result = null;
            try
            {
                Measure("world.models", () =>
                {
                    foreach (ISaveLoad obj in Get<List<ISaveLoad>>(mgr, "saveObjList"))
                        Measure("world.model." + obj.GetType().Name, () => { obj.Save(); return true; });
                    return true;
                });
                result = Measure("world.serialize", () => MessagePackSerializer.Serialize(
                    Get<Dictionary<string, ISaveLoadValue>>(mgr, "saveDict"), SaveOptions));
                return result;
            }
            finally { Trace("world.total", elapsed.Elapsed.TotalMilliseconds, result?.Length ?? -1); }
        }

        private T Measure<T>(string stage, Func<T> action)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try { return action(); }
            finally { Trace(stage, elapsed.Elapsed.TotalMilliseconds); }
        }

        private void Trace(string stage, double milliseconds, int bytes = -1)
        {
            if (diagnostics == null || capturingHistory) return;
            try { diagnostics("DialogueSave PERF " + stage + "=" + milliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                "ms" + (bytes < 0 ? "" : " bytes=" + bytes)); }
            catch { /* Diagnostics cannot turn a valid capture into a failed transaction. */ }
        }

        private static Dictionary<string, ISaveLoadValue> DecodeWorld(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > 128 * 1024 * 1024) throw new InvalidDataException("世界快照大小无效");
            var result = MessagePackSerializer.Deserialize<Dictionary<string, ISaveLoadValue>>(bytes, SaveOptions);
            if (result == null || result.Count < 9 || result.Count > 64 || result.Any(p => p.Value == null))
                throw new InvalidDataException("世界快照内容不完整");
            var required = new[] { "RoleModel", "CommonEvtModel", "RoundModel", "BagModel", "ProfileModel", "ShopModel", "TipsModel", "RecordModel", "FuncModel" };
            if (required.Any(type => result.Values.Count(value => value.GetType().FullName == type) != 1))
                throw new InvalidDataException("原版世界模型类型不完整或重复");
            return result;
        }

        private static void LoadWorld(Dictionary<string, ISaveLoadValue> world)
        {
            var mgr = NativeSaveMgr();
            var objs = Get<List<ISaveLoad>>(mgr, "saveObjList");
            Set(mgr, "saveDict", world);
            foreach (ISaveLoad obj in objs) obj.Load();
            foreach (ISaveLoad obj in objs) obj.LoadEnd();
        }

        private void Validate(JObject data, bool checkConfig = true)
        {
            if (data.Value<int>("format") != 1 || data.Value<string>("adapter") != Continuation)
                throw new InvalidDataException("不支持此对话恢复格式");
            if (data.Value<string>("gameModule") != typeof(global::Game).Module.ModuleVersionId.ToString("D"))
                diagnostics?.Invoke("存档来自另一游戏构建，按实际恢复结构检查兼容性。");
            int id = data.Value<int>("talkId");
            if (!Cfg.TalkCfgMap.TryGetValue(id, out TalkCfg cfg) ||
                (data.Value<int>("rootEvent") > 0 && !Cfg.EvtCfgMap.ContainsKey(data.Value<int>("rootEvent"))))
                throw new InvalidDataException("对话或事件配置已缺失");
            if (data["continuation"] is JObject continuation) DialogueContinuation.Validate(continuation);
            else if (data.Value<int>("rootEvent") <= 0) throw new InvalidDataException("缺少剧情返回流程");
            if (data["roundState"] != null && !Enum.IsDefined(typeof(RoundState), data.Value<int>("roundState")))
                throw new InvalidDataException("回合阶段无效");
            if (data["roundEndQueue"] is JArray queue && (queue.Count > 10000 ||
                queue.Any(t => t.Type != JTokenType.Integer || !Cfg.EvtCfgMap.ContainsKey((int)t))))
                throw new InvalidDataException("回合结束事件队列无效");
            if (!(data["segments"] is JArray segments) || segments.Count == 0 || segments.Count > 2048 ||
                data.Value<int>("segmentIndex") < 0 || data.Value<int>("segmentIndex") >= segments.Count ||
                segments.Any(t => t.Type != JTokenType.String)) throw new InvalidDataException("对白段落数据无效");
            string phase = data.Value<string>("phase");
            if (phase != TalkState.Anim.ToString() && phase != TalkState.AnimEnd.ToString() && phase != TalkState.Option.ToString() && phase != TalkState.Countdown.ToString()) throw new InvalidDataException("不支持此对话阶段");
            if (phase == TalkState.Anim.ToString() && (data["visibleText"]?.Type != JTokenType.String || data.Value<string>("visibleText").Length > 1024 * 1024))
                throw new InvalidDataException("逐字播放进度缺失或无效");
            if (!(data["options"] is JArray options) || options.Count > 128 ||
                options.Select(t => t.Value<int>("id")).Distinct().Count() != options.Count ||
                options.Any(t => !Cfg.OptionCfgMap.ContainsKey(t.Value<int>("id")))) throw new InvalidDataException("选项数据无效");
            foreach (JObject option in options)
                if (option["callback"] != null) DialogueContinuation.ValidateCallback(option["callback"]);
            float countdown = data.Value<float?>("countdown") ?? -1f;
            if (float.IsNaN(countdown) || float.IsInfinity(countdown) || countdown < -1f || countdown > cfg.time ||
                (countdown > -1f && (phase != TalkState.Option.ToString() || cfg.time <= 0 ||
                    !options.Any(t => t.Value<int>("id") == data.Value<int>("defaultOption")))))
                throw new InvalidDataException("限时选项状态无效");
            if (data["playback"] is JObject playback)
            {
                float scale = playback.Value<float>("timeScale");
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0 || scale > 100)
                    throw new InvalidDataException("对话播放速度无效");
                float delay = playback.Value<float?>("autoDelayRemaining") ?? -1f;
                if (float.IsNaN(delay) || float.IsInfinity(delay) || delay < -1f || delay > 3600f)
                    throw new InvalidDataException("自动播放等待时间无效");
            }
            // Global configuration digests are retained for diagnostics only.
            if (!(data["roles"] is JArray roles) || roles.Count > 32 ||
                roles.Select(t => t.Value<int>("roleId")).Distinct().Count() != roles.Count ||
                roles.Any(t => !Cfg.PersonCfgMap.ContainsKey(t.Value<int>("roleId")))) throw new InvalidDataException("立绘数据无效");
            foreach (JObject item in roles) RestoreRole(item);
            if (!Cfg.BgCfgMap.ContainsKey(data.Value<int>("background")) || string.IsNullOrEmpty(data.Value<string>("session")))
                throw new InvalidDataException("对话背景或会话标识缺失");
            if (!(data["history"] is JArray history) || history.Count > 10000) throw new InvalidDataException("对话历史无效");
            data["history"].ToObject<List<TalkData>>(DataJson);
            data["roleCloths"].ToObject<Dictionary<int, int>>(DataJson);
            data["posRoles"].ToObject<Dictionary<TalkAxis, List<int>>>(DataJson);
            RestoreRandomState((JObject)data["randomState"]);
            DialogueAudioAdapter.Validate((JObject)data["audio"]);
            if (data["pendingGuides"] == null || (data["pendingGuides"].Type != JTokenType.Null &&
                (!(data["pendingGuides"] is JArray guides) || guides.Count > 10000 || guides.Any(t => t.Type != JTokenType.Integer || (int)t <= 0))))
                throw new InvalidDataException("未提交引导状态无效");
            // Never open plugin DLLs on a restore. Missing/unreadable binaries and
            // diagnostic metadata must not turn into an indirect version gate.
            var savedPlugins=data["plugins"] as JArray;
            var currentPlugins=PluginMetadata();
            if(savedPlugins==null || !savedPlugins.Select(p=>(string)p["guid"]+":"+(string)p["version"])
                .SequenceEqual(currentPlugins.Select(p=>(string)p["guid"]+":"+(string)p["version"])))
                diagnostics?.Invoke("存档的插件版本与当前不同；继续按实际数据恢复。");
        }

        private void ValidateMods(Dictionary<string, ISaveLoadValue> world, JObject data)
        {
            var profile = world.Values.OfType<ProfileModel>().Single();
            ulong[] active = (Singleton<ModCtrl>.Ins.activeMods ?? new List<ulong>()).Distinct().OrderBy(x => x).ToArray();
            ulong[] saved = (profile.modList ?? new List<ulong>()).Distinct().OrderBy(x => x).ToArray();
            if (!(data["activeMods"] is JArray mods) || !Enumerable.SequenceEqual(active, saved) ||
                !Enumerable.SequenceEqual(active, mods.ToObject<ulong[]>(DataJson).Distinct().OrderBy(x => x)))
                diagnostics?.Invoke("启用的 Mod 列表与存档不同；关键节点已单独检查，继续恢复。");
        }

        // Diagnostic comparison only. No plugin version or binary identity is a load gate.
        // Archive structure and actual required game nodes are validated separately.
        private static bool PluginFingerprintsMatch(JArray saved, JArray current)
        {
            JArray Normalize(JArray entries)
            {
                if (entries == null || entries.Any(e => !(e is JObject) || e["guid"]?.Type != JTokenType.String || e["version"]?.Type != JTokenType.String)) return null;
                if (entries.Select(e => e.Value<string>("guid")).Distinct(StringComparer.Ordinal).Count() != entries.Count ||
                    entries.Count(e => e.Value<string>("guid") == DialogueSavePlugin.Id) != 1) return null;
                var copy = (JArray)entries.DeepClone();
                var own = (JObject)copy.Single(e => e.Value<string>("guid") == DialogueSavePlugin.Id);
                own.Remove("module"); own.Remove("sha256");
                return copy;
            }
            var left = Normalize(saved); var right = Normalize(current);
            return left != null && right != null && JToken.DeepEquals(left, right);
        }

        private static JArray PluginMetadata() => new JArray(Chainloader.PluginInfos.Values
            .OrderBy(p=>p.Metadata.GUID,StringComparer.Ordinal).Select(info=>new JObject {
                ["guid"]=info.Metadata.GUID,["version"]=info.Metadata.Version.ToString() }));

        private static JArray PluginFingerprint()
        {
            var result = new JArray();
            foreach (var info in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.GUID, StringComparer.Ordinal))
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                string hash;
                using (var file = File.OpenRead(assembly.Location))
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                result.Add(new JObject { ["guid"] = info.Metadata.GUID, ["version"] = info.Metadata.Version.ToString(),
                    ["module"] = assembly.ManifestModule.ModuleVersionId.ToString("D"), ["sha256"] = hash });
            }
            return result;
        }

        private static JObject CaptureRandomState(UnityEngine.Random.State state)
        {
            var result = new JObject();
            foreach (FieldInfo field in typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(int)) throw new InvalidDataException("此 Unity 随机状态版本尚不支持");
                result[field.Name] = (int)field.GetValue(state);
            }
            if (result.Count != 4) throw new InvalidDataException("Unity 随机状态布局已变化");
            return result;
        }

        private static UnityEngine.Random.State RestoreRandomState(JObject data)
        {
            if (data == null || data.Count != 4) throw new InvalidDataException("随机状态缺失");
            object state = default(UnityEngine.Random.State);
            foreach (FieldInfo field in typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(int) || data[field.Name]?.Type != JTokenType.Integer) throw new InvalidDataException("随机状态布局不匹配");
                field.SetValue(state, data.Value<int>(field.Name));
            }
            return (UnityEngine.Random.State)state;
        }

        // QA only: no double hashing on normal saves. Run against the loaded effective config
        // to prove byte-for-byte legacy digest compatibility before accepting the optimization.
        public bool VerifyDetachedConfigFingerprint()
        {
            AssertThread();
            string legacy = Measure("config.verify.legacy", () => LegacyConfigDigest(null, null));
            ConfigFingerprintSnapshot snapshot = Measure("config.verify.detach", () => ConfigFingerprintSnapshot.Capture(DataJson, (stage, ms) => Trace(stage, ms)));
            string detached = Measure("config.verify.hash", snapshot.ComputeDigest);
            return string.Equals(legacy, detached, StringComparison.Ordinal);
        }

        private Task<string> ConfigHashTask(ConfigFingerprintSnapshot snapshot)
        {
            if (snapshot.Digest != null) return Task.FromResult(snapshot.Digest);
            if (ReferenceEquals(snapshot, idleConfig) && idleConfigHash != null) return idleConfigHash;
            return configHashJobs.GetValue(snapshot, value => {
                var job=Task.Run(value.ComputeDigest);
                job.ContinueWith(failed=>{var observed=failed.Exception;},CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                return job;
            });
        }

        private ConfigFingerprintSnapshot CurrentConfigSnapshot()
        {
            if (cachedConfig != null && Measure("config.compare", cachedConfig.MatchesCurrent)) return cachedConfig;
            if (idleConfig != null && idleConfigHash != null)
            {
                if (idleConfigHash.IsFaulted || idleConfigHash.IsCanceled)
                {
                    var observed = idleConfigHash.Exception;
                    idleConfig = null; idleConfigHash = null; idleConfigTime = null;
                }
                else if (Measure("config.compare.idle", idleConfig.MatchesCurrent)) return idleConfig;
            }
            return Measure("config.detach", () => ConfigFingerprintSnapshot.Capture(DataJson, (stage, ms) => Trace(stage, ms)));
        }

        private string ConfigDigest(TalkCfg cfg, IEnumerable<int> optionIds)
        {
            ConfigFingerprintSnapshot snapshot = CurrentConfigSnapshot();
            string digest = snapshot.ComputeDigest();
            cachedConfig = snapshot; // Entire synchronous path stays on the same main-thread instant.
            return digest;
        }

        private static string LegacyConfigDigest(TalkCfg cfg, IEnumerable<int> optionIds)
        {
            // Recompute from effective, loaded dictionaries at each capture/validation. No cache can hide
            // a hot-reloaded Mod or an edited later branch. Native order is normalized by integer key.
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var text = new StreamWriter(crypto, new UTF8Encoding(false), 8192, true))
            using (var writer = new JsonTextWriter(text) { CloseOutput = false })
            {
                writer.WriteStartObject();
                foreach (PropertyInfo property in typeof(Cfg).GetProperties(BindingFlags.Public | BindingFlags.Static).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!property.Name.EndsWith("CfgMap", StringComparison.Ordinal)) continue;
                    writer.WritePropertyName(property.Name);
                    if (!(property.GetValue(null, null) is IDictionary map)) { writer.WriteNull(); continue; }
                    writer.WriteStartArray();
                    foreach (object key in map.Keys.Cast<object>().OrderBy(Convert.ToString, StringComparer.Ordinal))
                    {
                        writer.WriteStartArray();
                        DataJson.Serialize(writer, key);
                        WriteConfig(writer, map[key]);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject(); writer.Flush(); text.Flush(); crypto.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }

        private static void WriteConfig(JsonTextWriter writer, object value)
        {
            if (!(value is TalkCfg talk) || talk.roles == null || talk.roles.Count == 0)
            {
                DataJson.Serialize(writer, value);
                return;
            }
            // ShowCurTxt sorts the live action list in place. Apply its same ordering to a
            // detached list so a first display and a cold restart yield the same fingerprint.
            var actions = new List<List<float>>(talk.roles);
            actions.Sort((a, b) =>
            {
                if ((int)a[0] != (int)b[0]) return 0;
                if (Cfg.TalkAnimeCfgMap[(int)a[1]].type == 1) return -1;
                return Cfg.TalkAnimeCfgMap[(int)b[1]].type == 1 ? 1 : 0;
            });
            JObject token = JObject.FromObject(talk, DataJson);
            token["roles"] = JArray.FromObject(actions, DataJson);
            token.WriteTo(writer);
        }

        private bool OnlyExpectedViews()
        {
            object mgr = Get<object>(null, "ins", typeof(UIMgr));
            var views = Get<Dictionary<string, BaseView>>(mgr, "viewDict");
            foreach (BaseView view in views.Values)
            {
                if (view.viewState != ViewState.Opened || view.gameObject == null || !view.gameObject.activeInHierarchy) continue;
                string name = view.GetType().Name;
                if (exitCapture && (view is EntryView || name == "CommonComfirmView")) continue;
                if (view is NewTalkView || DialogueContinuation.IsBaseView(view.GetType()) || view is TopView || view is SaveView || name == "HotkeyView" || name == "DescriptionView" || name == "ToastView") continue;
                return false;
            }
            return true;
        }

        private void RetireCurrent()
        {
            playbackResumeView = null;
            playbackResumeAuto = false;
            playbackResumeScale = 1f;
            playbackResumeDelay = -1f;
            captureView = null;
            captureRequested = false;
            object ui = Get<object>(null, "ins", typeof(UIMgr));
            foreach (BaseView view in Get<Dictionary<string, BaseView>>(ui, "viewDict").Values)
                if (view != null) retired.GetValue(view, _ => new object());
            if (UIMgr.GetView<NewTalkView>(false) is NewTalkView old)
            {
                old.callback = null;
                Set(old, "enableAutoTalk", false);
                Singleton<TimerMgr>.Ins.Remove(old.OnClickNext);
                foreach (string name in new[] { "topSeq", "topSeq2", "topWaitSeq" }) Get<Sequence>(old, name)?.Kill(false);
                old.txtex_content?.DOKill(false);
            }
            trackedView = null;
        }

        private void QuietToTitle()
        {
            RetireCurrent();
            UIMgr.CloseAllView();
            global::Game.BackToMain();
        }

        private bool BlockTransition(NewTalkView view)
        {
            return retired.TryGetValue(view, out _) || restoring || capturing ||
                (captureRequested && view == captureView) || playbackResumeView == view || (pauseCount > 0 && view == trackedView);
        }

        internal bool CanAdvancePresentation(NewTalkView view) => !BlockTransition(view);

        private float PlaybackDelay(NewTalkView view)
        {
            if (playbackResumeView == view) return playbackResumeDelay;
            object timer = Singleton<TimerMgr>.Ins;
            Action callback = view.OnClickNext;
            // Delay() stages callbacks in a list before the next timer update. The newest
            // staged callback supersedes an existing dictionary entry for the same action.
            object match = null;
            foreach (object entry in Get<IList>(timer, "needAddList"))
                if (Get<Action>(entry, "callback") == callback) match = entry;
            if (match == null)
            {
                IDictionary pending = Get<IDictionary>(timer, "dataDict");
                if (pending.Contains(callback)) match = pending[callback];
            }
            return match == null ? -1f : Math.Max(0f, Get<float>(match, "delayTime") - Get<float>(match, "passTime"));
        }

        private void QueuePlaybackResume(NewTalkView view, bool automatic, float scale, float delay)
        {
            playbackResumeView = view;
            playbackResumeAuto = automatic;
            playbackResumeScale = scale;
            playbackResumeDelay = delay;
            Set(view, "enableAutoTalk", false);
            Singleton<TimerMgr>.Ins.Remove(view.OnClickNext);
            global::Game.TimeChange(1f);
        }

        private static bool DialogueUpdatePrefix(NewTalkView __instance)
        {
            DialogueCheckpointAdapter adapter = current;
            if (adapter == null) return true;
            if (adapter.retired.TryGetValue(__instance, out _) || adapter.restoring || adapter.capturing ||
                (adapter.captureRequested && adapter.captureView == __instance) ||
                (adapter.pauseCount > 0 && adapter.trackedView == __instance)) return false;
            if (adapter.playbackResumeView == __instance)
            {
                bool automatic = adapter.playbackResumeAuto;
                float scale = adapter.playbackResumeScale;
                float delay = adapter.playbackResumeDelay;
                adapter.playbackResumeView = null;
                Set(__instance, "enableAutoTalk", automatic);
                global::Game.TimeChange(scale);
                __instance.btn_click.interactable = !automatic && scale <= 1f;
                if (__instance.talkState == TalkState.Anim)
                {
                    var text = (TMPro.TextMeshProUGUI)Call(__instance, "GetTalkTxt");
                    string full = __instance.tmpTalks[__instance.tmpTalkIdx];
                    float speed = Get<float>(__instance, "txtSpeed");
                    if(adapter.PresentationTextSpeed!=null)speed=adapter.PresentationTextSpeed(speed);
                    if (speed <= 0) speed = Get<float[]>(__instance, "txtSpeeds")[2];
                    int total = Regex.Replace(full, "<[^>]*>", "").Length;
                    int shown = Math.Min(total, Regex.Replace(text.text ?? "", "<[^>]*>", "").Length);
                    text.DOKill(false);
                    if (shown >= total) { text.text = full; __instance.DoTextEnd(); }
                    else
                    {
                        // DOTween's string plugin otherwise spends time replacing the already
                        // visible prefix. Recreate the native empty-to-full tween and seek it.
                        text.text = "";
                        text.DOText(full, total / speed).OnComplete(__instance.DoTextEnd).SetEase(Ease.Linear)
                            .Goto(shown / speed, true);
                    }
                }
                else if ((automatic || scale > 1f) && !Get<bool>(__instance, "isDelaying") &&
                    (__instance.talkState == TalkState.AnimEnd || __instance.talkState == TalkState.Countdown))
                {
                    if (scale > 1f) __instance.OnClickNext();
                    else Singleton<TimerMgr>.Ins.Delay(__instance.OnClickNext,
                        delay >= 0 ? delay : Get<float>(__instance, "autoTalkTimeSpan"));
                }
            }
            return true;
        }

        private static bool SourcePrefix(out bool __state)
        {
            __state = false;
            if (current == null) return true;
            if (current.restoring || current.capturing || current.pauseCount > 0) return false;
            current.sourceDepth++;
            __state = true;
            return true;
        }
        private static Exception SourceFinalizer(Exception __exception, bool __state) { if (current != null && __state) current.sourceDepth--; return __exception; }

        private static bool EventPrefix(int _id, Action _callback, out bool __state)
        {
            __state = false;
            if (current == null) return true;
            if (current.restoring || current.capturing || current.pauseCount > 0) return false;
            if (current.sourceDepth > 0 && _callback == null)
            {
                current.rootEvent = _id;
                current.sessionId = Guid.NewGuid().ToString("N");
                current.visit = 0;
                current.eventDepth++;
                __state = true;
            }
            else if (current.optionDepth == 0) current.InvalidateOrigin();
            return true;
        }
        private static Exception EventFinalizer(Exception __exception, bool __state) { if (current != null && __state) current.eventDepth--; return __exception; }

        private static bool TalkEntryPrefix(int _id, Action _callback, bool _isNewEvt)
        {
            if (current == null) return true;
            if (current.restoring || current.capturing || current.pauseCount > 0) return false;
            bool supported = current.rootEvent > 0 && _callback == null &&
                (current.eventDepth > 0 || current.sourceDepth > 0 || (current.optionDepth > 0 && !_isNewEvt));
            if (supported) current.pendingTalk = _id;
            else current.InvalidateOrigin();
            return true;
        }

        private static bool OpenPrefix(NewTalkView __instance)
        {
            if (current == null) return true;
            if (current.retired.TryGetValue(__instance, out _)) return false;
            if (current.restoring)
            {
                if (current.pendingRestore != null && ReferenceEquals(__instance, current.pendingRestoreView) &&
                    current.pendingRestoreGeneration == current.generation)
                {
                    try { current.RestorePresentation(__instance, current.pendingRestore); }
                    catch (Exception ex) { current.restoredView.TrySetException(ex); }
                }
                else current.restoredView?.TrySetException(new InvalidOperationException("恢复期间出现了未预期的新对白窗口"));
                return false;
            }
            if (current.pendingTalk > 0 && __instance.parms != null && !(bool)__instance.parms[0] && (int)__instance.parms[1] == current.pendingTalk)
                current.trackedView = __instance;
            else current.EnsureTracked(__instance);
            return true;
        }

        private static bool LoadedPrefix(BaseView __instance, UnityEngine.Object o)
        {
            if (current == null || !current.retired.TryGetValue(__instance, out _)) return true;
            // Resource callbacks from an abandoned load must not resurrect a previous generation's UI.
            if (o != null) UnityEngine.Object.Destroy(o);
            return false;
        }

        private static bool RefreshPrefix(NewTalkView __instance)
        {
            if (current == null) return true;
            if (current.BlockTransition(__instance)) return false;
            current.EnsureTracked(__instance);
            if (__instance == current.trackedView) current.visit++;
            return true;
        }
        private static bool TransitionPrefix(NewTalkView __instance, MethodBase __originalMethod)
        {
            if (current == null) return true;
            if (current.captureRequested && __instance == current.captureView && !current.restoring &&
                !current.capturing && !current.retired.TryGetValue(__instance, out _) &&
                (__originalMethod.Name == "DoTextEnd" || __originalMethod.Name == "ShowOption")) return true;
            return !current.BlockTransition(__instance);
        }
        private static bool AutoPrefix(NewTalkView __instance, bool _true) => current == null || !_true || !current.BlockTransition(__instance);
        private static bool SpeedPrefix(NewTalkView __instance, bool _true) => current == null || !_true || !current.BlockTransition(__instance);
        private static bool OptionPrefix(CommonEvtOptionData _option, out bool __state)
        {
            __state = false;
            if (current == null) return true;
            if (current.restoring || current.capturing || current.captureRequested || current.pauseCount > 0) return false;
            if (current.trackedView != null && _option.isTalkOption && current.IsDialogueContext)
            {
                current.optionDepth++;
                __state = true;
            }
            else current.InvalidateOrigin();
            return true;
        }
        private static Exception OptionFinalizer(Exception __exception, bool __state) { if (current != null && __state) current.optionDepth--; return __exception; }
        private static bool QuietPrefix() => current == null || !current.restoring;
        private static bool GlobalGuidePrefix() => current == null || (!current.capturing && !current.restoring);
        private static bool GuidePrefix(ref bool __result)
        {
            if (current == null || !current.restoring) return true;
            __result = false;
            return false;
        }

        private void InvalidateOrigin() { rootEvent = 0; pendingTalk = 0; trackedView = null; sessionId = null; }
        private void EnsureTracked(NewTalkView view)
        {
            if (trackedView == view && !string.IsNullOrEmpty(sessionId)) return;
            trackedView = view;
            sessionId = Guid.NewGuid().ToString("N");
            visit = 0;
            rootEvent = Get<int>(view, "evtId");
        }
        private static SaveMgr NativeSaveMgr() => Get<SaveMgr>(null, "gameSaveMgr", typeof(SaveMgrEx));
        private static bool IsUiReady()
        {
            object ui = Get<object>(null, "ins", typeof(UIMgr));
            return ui != null && Get<Dictionary<string, BaseView>>(ui, "viewDict") != null;
        }
        private static Queue<int> EndQueue() => Get<Queue<int>>(Singleton<CommonEvtMgr>.Ins, "roundEndEventQueue");
        private static bool Refuse(out string reason, string message) { reason = message; return false; }
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
        private static T Get<T>(object obj, string name, Type type = null) => (T)Field(type ?? obj.GetType(), name).GetValue(obj);
        private static void Set(object obj, string name, object value) => Field(obj.GetType(), name).SetValue(obj, value);
        private static object Call(object obj, string name, params object[] args) => (AccessTools.Method(obj.GetType(), name) ?? throw new MissingMethodException(obj.GetType().FullName, name)).Invoke(obj, args);
        private static JArray VectorToken(Vector3 value) => new JArray(value.x, value.y, value.z);
        private static Vector3 Vector(JToken token) => new Vector3((float)token[0], (float)token[1], (float)token[2]);
        private void AssertThread() { if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("必须在 Unity 主线程操作对话快照"); }

        private void Patch(Type type, string method, string prefix, string postfix, string finalizer, Type[] args = null)
        {
            MethodInfo original = AccessTools.Method(type, method, args) ?? throw new MissingMethodException(type.FullName, method);
            Func<string, HarmonyMethod> hm = name => name == null ? null : new HarmonyMethod(typeof(DialogueCheckpointAdapter), name);
            harmony.Patch(original, hm(prefix), hm(postfix), null, hm(finalizer), null);
            patches.Add(original);
        }

        public void Dispose()
        {
            AssertThread();
            if (disposed) return;
            // Mark terminal before removing patches or waking any asynchronous continuation.
            // The host cancels its NextFrame token only after this method has run.
            disposed = true;
            generation++;
            var errors = new List<Exception>();
            try
            {
                foreach (MethodBase method in patches)
                {
                    try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
                    catch (Exception error) { errors.Add(error); }
                }
            }
            finally
            {
                patches.Clear();
                if (current == this) current = null;
            }
            if (errors.Count > 0) throw new AggregateException("对话适配器补丁清理未全部完成", errors);
        }

        private sealed class PauseLease : IDisposable
        {
            private DialogueCheckpointAdapter owner;
            public PauseLease(DialogueCheckpointAdapter owner) { this.owner = owner; }
            public void Dispose()
            {
                if (owner == null) return;
                owner.AssertThread();
                owner.pauseCount = Math.Max(0, owner.pauseCount - 1);
                owner = null;
            }
        }
    }
}
