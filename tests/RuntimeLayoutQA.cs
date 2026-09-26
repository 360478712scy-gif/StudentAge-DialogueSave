using System;
using System.IO;
using System.Linq;
using System.Collections;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.EventSystems;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

// Non-16:9 window: every mod page must sit centered like the native letterbox, unscaled.
internal static class RuntimeLayoutQA
{
    public static IEnumerator Run(Action<bool,string> check, Func<Func<bool>,float,string,IEnumerator> until, string root)
    {
        var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk!=null && talk.talkState==TalkState.AnimEnd,20,"layout dialogue ready");
        yield return new WaitForSecondsRealtime(1);
        string log="screen="+Screen.width+"x"+Screen.height+"\n";
        var frame=Resources.FindObjectsOfTypeAll<RectTransform>().First(r=>r.name=="Frame" && r.parent!=null && r.parent.name=="DialogueSave.ADV");
        log+=Gap("dialogue",frame,check);
        yield return Shot(root,"layout-dialogue");
        UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
        yield return until(()=>AdvSettingsTransition.Active!=null,15,"layout settings transition starts");
        yield return new WaitForSecondsRealtime(.08f);
        yield return Shot(root,"layout-settings-transition");
        yield return Shot(root,"layout-settings-transition-2");
        yield return until(()=>AdvSettings.Active!=null && AdvSettingsTransition.Active==null,15,"layout settings open");
        yield return new WaitForSecondsRealtime(.5f);
        var settings=(RectTransform)AdvSettings.Active.transform;
        log+=Gap("settings",settings.Cast<Transform>().Select(t=>(RectTransform)t).First(r=>r.name=="Redrawn campus backdrop"),check);
        yield return Shot(root,"layout-settings");
        // Hover one choice: only its own pill may change, no neighbouring atlas lines.
        var choice=AdvSettings.Active.GetComponentsInChildren<UnityEngine.UI.Button>(true).First(b=>b.name.StartsWith("Settings.Choice."));
        ExecuteEvents.Execute(choice.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.pointerEnterHandler);
        yield return new WaitForSecondsRealtime(.3f);
        yield return Shot(root,"layout-choice-hover");
        ExecuteEvents.Execute(choice.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.pointerExitHandler);
        log+="startMode="+Screen.fullScreenMode+" size="+Screen.width+"x"+Screen.height+"\n";
        var view=(SettingView)AccessTools.Field(typeof(AdvSettings),"view").GetValue(AdvSettings.Active);
        var modes=(System.Collections.Generic.List<UnityEngine.UI.Dropdown.OptionData>)AccessTools.Field(typeof(SettingView),"fullscreenOptions").GetValue(view);
        int windowed=modes.FindIndex(o=>(int)AccessTools.Field(o.GetType(),"id").GetValue(o)==(int)FullScreenMode.Windowed);
        if(Screen.fullScreenMode==FullScreenMode.FullScreenWindow)
        {
            view.dropdown_fullscreen.value=windowed; // Same path as the mod's choice button.
            yield return until(()=>Screen.fullScreenMode==FullScreenMode.Windowed,10,"windowed mode actually applied");
            yield return new WaitForSecondsRealtime(1.5f);
            log+="afterWindowed="+Screen.fullScreenMode+" size="+Screen.width+"x"+Screen.height+"\n";
            log+=Gap("settings-windowed",settings.Cast<Transform>().Select(t=>(RectTransform)t).First(r=>r.name=="Redrawn campus backdrop"),check);
            yield return Shot(root,"layout-settings-windowed");
        }
        AdvSettings.Active.Close();
        yield return until(()=>AdvSettings.Active==null && AdvSettingsTransition.Active==null,15,"layout settings closed");
        yield return new WaitForSecondsRealtime(1);
        UIMgr.OpenView<SaveView>(UILayerType.None,null,new object[]{false});
        yield return until(()=>AdvSavePage.Active!=null && AdvSettingsTransition.Active==null,20,"layout archive open");
        yield return new WaitForSecondsRealtime(1);
        log+=Gap("archive",AdvSavePage.Active.transform.Cast<Transform>().Select(t=>(RectTransform)t).First(r=>r.name=="Campus archive background"),check);
        yield return Shot(root,"layout-archive");
        File.WriteAllText(Path.Combine(root,"results/layout.txt"),log);
    }
    static string Gap(string name,RectTransform r,Action<bool,string> check)
    {
        var c=new Vector3[4];r.GetWorldCorners(c);
        float bottom=c[0].y,top=Screen.height-c[1].y,left=c[0].x,right=Screen.width-c[2].x;
        check(Mathf.Abs(bottom-top)<2 && Mathf.Abs(left-right)<2,name+" design area centered (top "+top+" bottom "+bottom+" left "+left+" right "+right+")");
        check(Mathf.Abs((c[2].x-c[0].x)/(c[1].y-c[0].y)-16f/9f)<.01f,name+" keeps 16:9 without stretching");
        return name+": top="+top+" bottom="+bottom+" left="+left+" right="+right+"\n";
    }
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results",name+".png"));yield return new WaitForSecondsRealtime(name.Contains("transition")?.12f:.5f);}
}
