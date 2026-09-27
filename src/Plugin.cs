using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

[assembly: AssemblyVersion("0.1.1.0")]
[assembly: AssemblyFileVersion("0.1.1.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DialogueRuntimeQA")]

namespace StudentAgeDialogueSave
{
    [BepInPlugin(Id, "学生时代 · 对话存档", Version)]
    public sealed class DialogueSavePlugin : BaseUnityPlugin
    {
        public const string Id = "local.studentage.dialoguesave";
        public const string Version = "0.2.8";
        internal static DialogueRuntimeHost Host;
        void Awake()
        {
            if (!Config.Bind("General", "Enabled", true, "启用对话存档；修改后重启游戏。关闭不会删除存档。").Value) return;
            if (Host) return;
            try
            {
                bool auto = Config.Bind("Saving", "AutoSave", true, "仅在受支持的稳定对话节点保存自动对话档。").Value;
                int interval = Math.Max(10, Config.Bind("Saving", "AutoSaveIntervalSeconds", 30, "自动对话存档最小间隔，至少10秒。").Value);
                var go = new GameObject("StudentAgeDialogueSave_RuntimeHost");
                go.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(go);
                Host = go.AddComponent<DialogueRuntimeHost>();
                var uiMode = Config.Bind("Interface", "DialogueStyle", "Ask", "对话界面：Ask 首次选择，Original 原版，ADV 渐隐阅读界面；可在游戏设置中切换。");
                Host.Initialize(Logger, auto, interval, uiMode);
            }
            catch (Exception ex)
            {
                Logger.LogError("对话存档未能初始化，保留原版功能：" + ex);
                if (Host) Destroy(Host.gameObject);
                Host = null;
            }
        }
        void OnDestroy()
        {
            // The game destroys BepInEx_Manager during startup. It does not own our runtime.
            if (Host) Logger.LogInfo("引导对象已清理；对话存档由独立运行对象继续提供。");
        }
    }

    public sealed class DialogueRuntimeHost : MonoBehaviour
    {
        readonly Queue<Action> dispatch = new Queue<Action>();
        readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        ManualLogSource log;
        Harmony harmony;
        DialogueCheckpointAdapter adapter;
        DialogueSaveService service;
        DialogueUiController ui;
        AdvDialogueController adv;
        DialogueExitSave exitSave;
        int ownerThread;
        bool disposed;
        float nextAutoCheck;
        string actionState;

        // Routine traces (timings, toolbar state, per-line history waits, each save's commit)
        // go to Debug, which BepInEx does not write by default. Problems stay visible.
        static readonly string[] RoutinePrefixes = { "DialogueSave PERF ", "对话操作栏状态：", "回看等待记录 ", "对话存档本地提交完成：", "退出对话自动存档已完成", "存档的插件版本与当前不同" };
        void Diagnose(string message)
        {
            if (message == null) return;
            bool routine = RoutinePrefixes.Any(message.StartsWith) ||
                System.Text.RegularExpressions.Regex.IsMatch(message, "^已回收被覆盖的旧对话存档 [1-9] 个");
            if (routine) log.LogDebug(message); else log.LogInfo(message);
        }
        internal void Initialize(ManualLogSource logger, bool auto, int interval, BepInEx.Configuration.ConfigEntry<string> uiMode)
        {
            log = logger; ownerThread = Thread.CurrentThread.ManagedThreadId;
            harmony = new Harmony(DialogueSavePlugin.Id);
            adapter = new DialogueCheckpointAdapter(NextFrame, Diagnose);
            DialoguePresentationPolicy.Install(harmony);
            adapter.Install(harmony);
            service = new DialogueSaveService(adapter, Post, Diagnose, NextFrame, shutdown.Token, auto, interval);
            ComicPresentationAdapter.Install(harmony);
            NativeFightInputFix.Install(harmony);
            NativeWindowModeFix.Install(harmony);
            DialogueUiResourceLease.Install(harmony);
            exitSave = new DialogueExitSave(service, NextFrame, Diagnose, harmony);
            ui = new DialogueUiController(service, Diagnose);
            ui.Install(harmony);
            service.RecordsChanged += ui.RefreshRecords;
            adv = new AdvDialogueController(adapter, ui, service, uiMode, shutdown.Token, Diagnose, harmony);
            StartCoroutine(WarmSettings());
            log.LogInfo("对话存档已初始化；实际可用范围由对话来源与稳定状态检查决定。");
        }
        System.Collections.IEnumerator WarmSettings()
        {
            while(!disposed && (adv?.IsAdv!=true || !DialogueUiController.IsUiReady()))yield return null;
            if(!disposed)yield return AdvSettingsSkin.Warm();
            if(!disposed)yield return AdvBacklogSkin.Warm();
            if(!disposed)yield return AdvArchiveSkin.Warm();
        }
        internal void Post(Action action)
        {
            if (disposed || action == null) return;
            if (Thread.CurrentThread.ManagedThreadId == ownerThread) { action(); return; }
            lock (dispatch) { if (!disposed) dispatch.Enqueue(action); }
        }
        internal Task NextFrame()
        {
            if (disposed) return Task.FromCanceled(shutdown.Token);
            var completion = new TaskCompletionSource<bool>();
            var registration = shutdown.Token.Register(() => completion.TrySetCanceled());
            Post(() => StartCoroutine(CompleteNextFrame(completion, registration)));
            return completion.Task;
        }
        IEnumerator CompleteNextFrame(TaskCompletionSource<bool> completion, CancellationTokenRegistration registration)
        {
            yield return null;
            registration.Dispose();
            if (disposed) completion.TrySetCanceled(); else completion.TrySetResult(true);
        }
        void LateUpdate()
        {
            if(disposed)return;
            try{adv?.LatePresentation();}catch(Exception ex){log?.LogWarning("ADV显示同步失败："+ex.Message);}
        }
        void Update()
        {
            if (disposed) return;
            try { ui?.Tick(); } catch (Exception ex) { log?.LogWarning("对话窗口状态更新失败：" + ex.Message); }
            for (int i = 0; i < 32; i++)
            {
                Action action;
                lock (dispatch) { if (dispatch.Count == 0) break; action = dispatch.Dequeue(); }
                try { action(); } catch (Exception ex) { log?.LogError("对话存档异步回调失败：" + ex); }
            }
            try { ComicPresentationAdapter.Tick(); adv?.Tick(); } catch (Exception ex) { log?.LogWarning("ADV界面更新失败：" + ex.Message); }
            if (Time.realtimeSinceStartup < nextAutoCheck) return;
            nextAutoCheck = Time.realtimeSinceStartup + 1f;
            try { service?.WarmListing(); } catch(Exception ex){log?.LogWarning("存档目录预读失败："+ex.Message);}
            try
            {
                // These actions remain available throughout dialogue playback; transient
                // capture readiness must not destroy and recreate the native toolbar.
                string state = service.IsDialogueContext.ToString();
                if (state != actionState) { actionState = state; ui.RefreshActions(); }
            }
            catch (Exception ex) { log?.LogWarning("对话操作栏状态更新失败：" + ex.Message); }
            try { service?.TickAutoSave(); } catch (Exception ex) { log?.LogWarning("本次自动对话存档已跳过：" + ex.Message); }
        }
        void OnApplicationQuit() { if(exitSave?.IsPending!=true)Shutdown(); }
        void OnDestroy() { Shutdown(); }
        void Shutdown()
        {
            if (disposed) return; disposed = true;
            StopAllCoroutines();
            try
            {
                Cleanup(() => exitSave?.Dispose());
                Cleanup(() => ComicPresentationAdapter.Clear());
                Cleanup(() => adv?.Dispose());
                Cleanup(StudentAgeDialogueSave.UI.Letterbox.Release);
                Cleanup(() => service?.Dispose());
                Cleanup(() => ui?.Dispose());
                Cleanup(() => adapter?.Dispose());
                Cleanup(() => shutdown.Cancel());
                Cleanup(() => harmony?.UnpatchSelf());
            }
            finally
            {
                lock (dispatch) dispatch.Clear();
                if (DialogueSavePlugin.Host == this) DialogueSavePlugin.Host = null;
            }
        }
        void Cleanup(Action action) { try { action(); } catch (Exception ex) { log?.LogError("对话存档清理异常：" + ex); } }
    }
}
