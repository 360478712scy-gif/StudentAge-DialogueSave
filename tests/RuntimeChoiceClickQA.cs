using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Config;
using Sdk;
using UnityEngine;
using View.Evt;
using StudentAgeDialogueSave.UI;

// The line stays on screen above its choices, and a real pointer click still selects a choice.
internal static class RuntimeChoiceClickQA
{
    public static IEnumerator Run(Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        Func<NewTalkView> talk=()=>UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        Func<int> current=()=>talk()==null?0:((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk())).id;
        var log=new List<string>();
        yield return until(()=>talk()!=null && talk().talkState==TalkState.AnimEnd,20,"first line stable");
        talk().OnClickNext();
        yield return until(()=>current()==1900000002 && talk().talkState==TalkState.Option,20,"choice line shown");
        yield return new WaitForSecondsRealtime(1.5f);
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/choice-click-before.png"));yield return new WaitForSecondsRealtime(.5f);
        RuntimeUiQA.Click("ADV.Choice.0",check);
        float start=Time.realtimeSinceStartup;
        while(Time.realtimeSinceStartup-start<5 && current()==1900000002)yield return null;
        log.Add("after click: talk="+current()+" state="+talk()?.talkState);
        File.WriteAllLines(Path.Combine(root,"results/choice-click.txt"),log);
        check(current()!=1900000002,"clicking a choice advances the story");
    }
}
