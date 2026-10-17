using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Config;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using View.Evt;

// Exercise native skipping through long/empty chains, unread boundaries,
// repeated input, menu pauses and final continuations in isolated game data.
internal static class RuntimeSceneSkipQA
{
    const int First=1902001001, Last=1902001512, Probe=16000002;
    static NewTalkView Talk=>UIMgr.GetView<NewTalkView>(false) as NewTalkView;
    static TalkCfg Current=>Talk==null?null:AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(Talk) as TalkCfg;
    static bool Pending=>(bool)AccessTools.Field(typeof(AdvDialogueController),"sceneSkipPending").GetValue(AdvDialogueController.Active);
    static float Value=>Singleton<CommonEvtMgr>.Ins.GetEvtSaveData(1,Probe);
    static void Request()=>AccessTools.Method(typeof(AdvDialogueController),"RequestSceneSkip").Invoke(AdvDialogueController.Active,null);
    static List<int> Seen=>(List<int>)AccessTools.Field(typeof(GlobalModel),"historyTalks").GetValue(AccessTools.Field(typeof(GlobalMgr),"model").GetValue(Singleton<GlobalMgr>.Ins));
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        yield return until(()=>adapter.CanCapture(out _) && Talk.talkState==TalkState.AnimEnd,25,"scene skip fixture stable");
        var snapshot=adapter.Capture();var source=Current;var adv=AdvDialogueController.Active;
        bool unread=adv.Preferences.SkipUnread.Value;
        var confirm=ConfirmationOptions.Entry(adv.Configuration,"Skip");bool confirmed=confirm.Value;
        float baseline=Value;int callbacks=0;var times=new List<string>();
        check(!Enumerable.Range(First,Last-First+1).Any(Cfg.TalkCfgMap.ContainsKey),"skip fixture nodes do not replace native data");
        for(int id=First;id<=Last;id++)Cfg.TalkCfgMap[id]=new TalkCfg{
            id=id,bg=source.bg,roleIds=source.roleIds,roleName=source.roleName,
            content=id!=First && id!=Last && id%13==0?"":"跳过检查 "+id,
            effect=id==Last?null:new List<List<float>>{new List<float>{50,2,1,Probe,1}},
            nextTalk=id==Last?null:new List<int>{id+1},
            option=id==Last?new List<int>{1900000001,1900000002}:null};
        try
        {
            confirm.Value=false;adv.Preferences.SkipUnread.Value=true;
            Seen.RemoveAll(id=>id>=First && id<=Last);
            Talk.RefreshTalk(First);
            yield return until(()=>Current.id==First && Talk.talkState==TalkState.Anim,20,"long chain starts typing");
            var clock=System.Diagnostics.Stopwatch.StartNew();
            Request();Request();Request();
            yield return until(()=>Current.id==Last && Talk.talkState==TalkState.Option,20,"long scene skip reaches the choice");
            clock.Stop();times.Add("512-node typing skip ms="+clock.Elapsed.TotalMilliseconds);
            check(!Pending,"choice clears pending skip");
            check(Enumerable.Range(First,Last-First).All(Singleton<GlobalMgr>.Ins.HasSeenTalk),"every skipped node including empty dialogue is marked read");
            check(Value==baseline+(Last-First),"all skipped effects execute exactly once despite repeated requests: actual="+Value+" baseline="+baseline+" expected="+(baseline+(Last-First)));
            var options=Talk.itemgroup_options.GetCells().Select(c=>((CommonEvtOptionData)c.data).id).ToArray();
            Request();yield return new WaitForSecondsRealtime(.3f);
            check(Current.id==Last && Talk.talkState==TalkState.Option && options.SequenceEqual(Talk.itemgroup_options.GetCells().Select(c=>((CommonEvtOptionData)c.data).id)),"skip at choice does not select or advance");

            // Read first nodes only. The first unseen node must be displayed once;
            // completing it on this visit must not allow a second skip to move on.
            const int unseen=First+7;
            Seen.RemoveAll(id=>id>=unseen && id<=Last);
            adv.Preferences.SkipUnread.Value=false;
            yield return until(()=>adapter.CanAdvancePresentation(Talk),10,"read boundary fixture accepts refresh");
            Talk.RefreshTalk(First);
            yield return until(()=>Current.id==First && Talk.talkState==TalkState.Anim,20,"read revisit starts");
            Request();
            yield return until(()=>Current.id==unseen && Talk.talkState==TalkState.AnimEnd,20,"skip stops at first unread node");
            check(!Pending && !Singleton<GlobalMgr>.Ins.HasSeenTalk(unseen+1),"unread boundary leaves no pending skip or next-node read mark");
            Request();Request();yield return new WaitForSecondsRealtime(.4f);
            check(Current.id==unseen && !Pending && !adv.ModalOpen,"repeated unread skip stays on current line without a modal");

            // A pending skip may not run while a menu owns the dialogue pause.
            adv.Preferences.SkipUnread.Value=true;
            yield return until(()=>adapter.CanAdvancePresentation(Talk),10,"pause fixture accepts refresh");
            Talk.RefreshTalk(First);
            yield return until(()=>Current.id==First && Talk.talkState==TalkState.Anim,20,"pause fixture starts typing");
            Request();
            using(adapter.PauseForMenu())
            {
                yield return new WaitForSecondsRealtime(.5f);
                check(Current.id==First && Pending,"menu pause holds queued skip without advancing");
            }
            yield return until(()=>Current.id==Last && Talk.talkState==TalkState.Option && !Pending,20,"closing menu resumes pending skip to choices");

            // Leave through the actual native final-line callback, never an
            // artificial completion. A multiline last node must still complete.
            Cfg.TalkCfgMap[Last].option=null;
            Cfg.TalkCfgMap[Last].content="最后一段第一行。\n最后一段第二行。\n最后一段第三行。";
            yield return until(()=>adapter.CanAdvancePresentation(Talk),10,"terminal fixture accepts refresh");
            Talk.RefreshTalk(Last);
            yield return until(()=>Current.id==Last && Talk.talkState==TalkState.Anim,20,"terminal multiline begins typing");
            var view=Talk;var nativeCallback=view.callback;
            view.callback=()=>{callbacks++;nativeCallback?.Invoke();};
            Request();Request();
            yield return until(()=>view.viewState!=ViewState.Opened,20,"skip completes the terminal multiline scene");
            yield return new WaitForSecondsRealtime(.5f);
            check(callbacks==1 && !Pending,"terminal callback runs exactly once and clears pending skip");
            File.WriteAllLines(Path.Combine(root,"results/scene-skip.txt"),times);
        }
        finally{adv.Preferences.SkipUnread.Value=unread;confirm.Value=confirmed;}
        var restore=adapter.RestoreAsync(snapshot);
        yield return until(()=>restore.IsCompleted,35,"restore original fixture after skip matrix");restore.GetAwaiter().GetResult();
        check(Value==baseline,"skip fixture effects removed by complete world restore");
        foreach(int id in Enumerable.Range(First,Last-First+1))Cfg.TalkCfgMap.Remove(id);
        yield return RuntimePhoneQA.Run(adapter,check,until,root);
    }
}
