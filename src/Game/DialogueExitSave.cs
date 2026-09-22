using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Both native menu actions and window-close requests share one bounded save.
    // Failure and timeout always resume the requested exit, without a dialog.
    internal sealed class DialogueExitSave : IDisposable
    {
        static DialogueExitSave active;
        readonly DialogueSaveService service;
        readonly Func<Task> nextFrame;
        readonly Action<string> log;
        bool pending, bypass, quitRequested, disposed;
        bool resetMusic;
        internal bool IsPending => pending;
        internal DialogueExitSave(DialogueSaveService service, Func<Task> nextFrame, Action<string> log, Harmony harmony)
        {
            this.service=service;this.nextFrame=nextFrame;this.log=log; active=this;
            harmony.Patch(AccessTools.Method(typeof(global::Game), "BackToMain"), prefix:new HarmonyMethod(typeof(DialogueExitSave), nameof(BackPrefix)));
            harmony.Patch(AccessTools.Method(typeof(global::Game), "ExitGame"), prefix:new HarmonyMethod(typeof(DialogueExitSave), nameof(ExitPrefix)));
            Application.wantsToQuit += WantsQuit;
        }
        static bool BackPrefix(bool _resetBgm) => active==null || active.Request(false, _resetBgm);
        static bool ExitPrefix() => active==null || active.Request(true, true);
        bool WantsQuit() => Request(true, true);
        internal bool Request(bool quit, bool resetBgm)
        {
            if(disposed || bypass) return true;
            if(pending) { quitRequested |= quit; return false; }
            if(!service.IsDialogueContext) return true;
            pending=true; quitRequested=quit; resetMusic=resetBgm;
            Complete();return false;
        }
        async void Complete()
        {
            using(var cancellation=new CancellationTokenSource())
            {
                try
                {
                    var deadline=System.Diagnostics.Stopwatch.StartNew();
                    Task save=service.SaveBeforeExitAsync(cancellation.Token);
                    while(!save.IsCompleted && deadline.Elapsed.TotalSeconds<5)
                        await nextFrame();
                    if(save.IsCompleted)await save;
                    else
                    {
                        cancellation.Cancel();
                        log("退出存档等待已达上限，继续退出；已提交的旧存档不变。");
                        _=save.ContinueWith(t=>{var observed=t.Exception;}, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                catch(Exception error){log("退出自动存档未完成，继续退出："+error.Message);}
                finally
                {
                    pending=false; bypass=true;
                    try { if(quitRequested){Application.wantsToQuit-=WantsQuit;Application.Quit();}else if(!disposed)global::Game.BackToMain(resetMusic); }
                    finally { if(!quitRequested)bypass=false; }
                }
            }
        }
        public void Dispose(){disposed=true;Application.wantsToQuit-=WantsQuit;if(active==this)active=null;}
    }
}
