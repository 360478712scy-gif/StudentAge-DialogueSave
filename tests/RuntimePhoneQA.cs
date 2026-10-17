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
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.Storage;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;

// Phone calls: fast-forward passes through them; scene skip from inside a call ends the call first.
internal static class RuntimePhoneQA
{
    static readonly List<int> phoneSounds=new List<int>();
    static void PhoneSound(int _id){if(_id==42035 || _id==42036)phoneSounds.Add(_id);}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var b=Cfg.TalkCfgMap[1900000001];var log=new List<string>();
        TalkCfg Line(int id,string text,List<float> effect,int next,List<int> options=null)=>Cfg.TalkCfgMap[id]=new TalkCfg{id=id,bg=b.bg,roleIds=b.roleIds,roleName=b.roleName,content=text,screenEffect=effect,nextTalk=next==0?new List<int>():new List<int>{next},option=options??new List<int>()};
        Line(1900000301,"电话响了。",null,1900000302);

        Line(1900000303,"电话那头沉默了一会儿。",null,1900000304);
        Line(1900000304,"电话挂断了。",null,1900000305);
        Line(1900000305,"挂了电话，回到教室。",null,1900000306);
        // Copy a real native call-start line (its effect parameters, roles and highlights).
        var native=Cfg.TalkCfgMap.Values.First(t=>t.screenEffect!=null && t.screenEffect.Count>=3 && t.screenEffect[0]==4007f && Cfg.BgCfgMap.ContainsKey((int)t.screenEffect[1]) && Cfg.PersonCfgMap.ContainsKey((int)t.screenEffect[2]) && t.roleIds!=null && t.roleIds.Count>0);
        float callEffect=native.screenEffect[0];
        var start=(TalkCfg)typeof(object).GetMethod("MemberwiseClone",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(native,null);
        start.id=1900000302;start.content="喂？是我。";start.nextTalk=new List<int>{1900000303};start.option=new List<int>();start.miniGame?.Clear();start.effect=new List<List<float>>();Cfg.TalkCfgMap[start.id]=start;
        log.Add("native call line "+native.id+" effect="+string.Join(",",native.screenEffect)+" roles="+string.Join(",",native.roleIds)+" bg="+native.bg);
        Cfg.TalkCfgMap[1900000304].screenEffect=new List<float>{4008f};
        foreach(int id in new[]{1900000303,1900000304}){Cfg.TalkCfgMap[id].bg=start.bg;Cfg.TalkCfgMap[id].roleIds=start.roleIds;Cfg.TalkCfgMap[id].roleName=start.roleName;}
        Line(1900000306,"接下来怎么办？",null,0,new List<int>{1900000001,1900000002});
        b.nextTalk=new List<int>{1900000301};
        var adv=AdvDialogueController.Active;bool unread=adv.Preferences.SkipUnread.Value;adv.Preferences.SkipUnread.Value=true;
        Func<NewTalkView> talk=()=>UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        Func<int> current=()=>talk()==null?0:((TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk())).id;
        Func<bool> phone=()=>(bool)AccessTools.Field(typeof(NewTalkView),"isPhoneing").GetValue(talk());
        Func<int> hiddenRoles=()=>((IDictionary)AccessTools.Field(typeof(NewTalkView),"roles").GetValue(talk())).Values.Cast<object>()
            .Count(r=>{var cell=AccessTools.Field(r.GetType(),"cell").GetValue(r) as Component;return cell!=null && !cell.gameObject.activeSelf;});
        try
        {
            // 1. Step into the call, then scene-skip from inside it.
            Cfg.OptionCfgMap[1900000001].talkId=new List<int>{1900000301};
            foreach(int id in new[]{1900000301,1900000302,1900000303})
            {
                yield return until(()=>talk()!=null && talk().talkState==TalkState.AnimEnd && adapter.CanAdvancePresentation(talk()) && !(bool)AccessTools.Field(typeof(NewTalkView),"isDelaying").GetValue(talk()),20,"stable before "+id);
                talk().OnClickNext();
                yield return until(()=>current()==id && talk().talkState==TalkState.AnimEnd,20,"reached "+id);
            }
            yield return new WaitForSecondsRealtime(1f);
            log.Add("in call at "+current()+": phone="+phone());
            check(phone(),"fixture is inside a phone call");
            if(File.Exists(Path.Combine(root,"phone-baseline.txt")))
            {
                bool allowed=adapter.CanCapture(out string reason);
                check(!allowed && reason.Contains("特殊演出"),"0.2.8 reproduces permanent phone save refusal: "+reason);
                File.WriteAllText(Path.Combine(root,"results/phone-baseline.txt"),reason);
                yield break;
            }
            // The dialogue skin may move text, never phone participant geometry.
            var advGeometry=Geometry(talk());
            adv.SelectMode("Original");yield return new WaitForSecondsRealtime(.6f);
            var nativeGeometry=Geometry(talk());
            check(Newtonsoft.Json.Linq.JToken.DeepEquals(advGeometry,nativeGeometry),"phone role geometry is identical in Original and ADV");
            File.WriteAllText(Path.Combine(root,"results/phone-geometry-parity.json"),new Newtonsoft.Json.Linq.JObject{["adv"]=advGeometry,["original"]=nativeGeometry}.ToString());
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/phone-original.png"));yield return new WaitForSecondsRealtime(.3f);
            adv.SelectMode("ADV");yield return new WaitForSecondsRealtime(.6f);
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/phone-adv.png"));yield return new WaitForSecondsRealtime(.3f);
            yield return Roundtrip(adapter,check,until,root,talk,current,phone,hiddenRoles);
            check(Newtonsoft.Json.Linq.JToken.DeepEquals(advGeometry,Geometry(talk())),"phone roles keep original geometry through save/load");

            AccessTools.Method(typeof(AdvDialogueController),"BeginSceneSkip").Invoke(adv,null);
            for(int wait=0;wait<60 && !(talk()!=null && current()==1900000306 && talk().talkState==TalkState.Option);wait++)
            {
                yield return new WaitForSecondsRealtime(.5f);
                if(wait%4==0)log.Add("t="+wait/2f+"s at "+current()+" state="+talk()?.talkState+" delaying="+AccessTools.Field(typeof(NewTalkView),"isDelaying").GetValue(talk())+" phone="+phone()+" pending="+AccessTools.Field(typeof(AdvDialogueController),"sceneSkipPending").GetValue(adv)+" advance="+adapter.CanAdvancePresentation(talk()));
                File.WriteAllLines(Path.Combine(root,"results/phone.txt"),log);
            }
            yield return new WaitForSecondsRealtime(1f);
            log.Add("scene skip at "+current()+" phone="+phone()+" hiddenRoles="+hiddenRoles());
            check(current()==1900000306 && talk().talkState==TalkState.Option,"phone scene skip reaches choices rather than stalling");
            foreach(int id in new[]{1900000303,1900000304,1900000305})check(Singleton<GlobalMgr>.Ins.HasSeenTalk(id),"skipped phone line is read: "+id);
            check(!phone(),"scene skip from inside a call ends the call");
            check(hiddenRoles()==0,"no character left hidden after skipping a call");
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/phone-after-skip.png"));yield return new WaitForSecondsRealtime(.5f);
            // 2. The first choice replays the call; fast-forward must pass through it.
            RuntimeUiQA.Click("ADV.Choice.0",check);
            yield return until(()=>current()==1900000301,20,"choice replays the call chain");
            AccessTools.Method(typeof(AdvDialogueController),"ToggleSkip").Invoke(adv,null);
            yield return until(()=>current()==1900000306 && talk().talkState==TalkState.Option,30,"fast-forward passes through the phone call to the choice");
            yield return new WaitForSecondsRealtime(1f);
            log.Add("fast-forward reached choice; phone="+phone()+" hiddenRoles="+hiddenRoles());
            check(!phone() && hiddenRoles()==0,"after fast-forward the call has ended and roles are visible");
            // Skip before dialling, during the split-screen animation, and while
            // hanging up. Duplicate requests must not restart either phone effect.
            var confirm=ConfirmationOptions.Entry(adv.Configuration,"Skip");bool priorConfirm=confirm.Value;
            var soundProbe=new Harmony("qa.phone.skip-boundary-sounds");
            soundProbe.Patch(AccessTools.Method(typeof(AudioMgrEx),"PlayUISound",new[]{typeof(int),typeof(float)}),prefix:new HarmonyMethod(typeof(RuntimePhoneQA),nameof(PhoneSound)));
            try
            {
                confirm.Value=false;
                foreach(string phase in new[]{"before-dial","dial-animation","hangup-animation"})
                {
                    phoneSounds.Clear();
                    RuntimeUiQA.Click("ADV.Choice.0",check);
                    yield return until(()=>current()==1900000301 && talk().talkState==TalkState.AnimEnd,20,phase+" begins before call");
                    if(phase!="before-dial")
                    {
                        talk().OnClickNext();
                        yield return until(()=>current()==1900000302 && talk().talkState==TalkState.Anim,20,phase+" reaches dial animation");
                        if(phase=="hangup-animation")
                        {
                            foreach(int id in new[]{1900000303,1900000304})
                            {
                                yield return until(()=>talk().talkState==TalkState.AnimEnd && !(bool)AccessTools.Field(typeof(NewTalkView),"isDelaying").GetValue(talk()),20,"stable before boundary "+id);
                                talk().OnClickNext();
                                yield return until(()=>current()==id && talk().talkState==TalkState.Anim,20,"reached boundary "+id);
                            }
                        }
                    }
                    for(int press=0;press<3;press++)AccessTools.Method(typeof(AdvDialogueController),"RequestSceneSkip").Invoke(adv,null);
                    yield return until(()=>current()==1900000306 && talk().talkState==TalkState.Option,25,phase+" skip reaches choice without stall");
                    yield return new WaitForSecondsRealtime(.4f);
                    check(!phone() && hiddenRoles()==0 && !(bool)AccessTools.Field(typeof(AdvDialogueController),"sceneSkipPending").GetValue(adv),phase+" cleans phone participants and pending state");
                    check(phoneSounds.Count(id=>id==42035)==1 && phoneSounds.Count(id=>id==42036)==1,phase+" plays dial and hangup exactly once despite repeated requests");
                    log.Add(phase+" passed; sounds="+string.Join(",",phoneSounds));
                }
            }
            finally{soundProbe.UnpatchSelf();confirm.Value=priorConfirm;}
        }
        finally{adv.Preferences.SkipUnread.Value=unread;File.WriteAllLines(Path.Combine(root,"results/phone.txt"),log);}
    }
    static Newtonsoft.Json.Linq.JObject Geometry(NewTalkView view)
    {
        var result=new Newtonsoft.Json.Linq.JObject();
        foreach(var pair in (Dictionary<int,NewTalkRoleData>)AccessTools.Field(typeof(NewTalkView),"roles").GetValue(view))
        {
            var r=pair.Value;if(r.cell==null)continue;
            var pos=r.cell.transform.anchoredPosition3D;var scale=r.cell.transform.localScale;
            result[pair.Key.ToString()]=new Newtonsoft.Json.Linq.JObject{["x"]=Math.Round(pos.x,1),["y"]=Math.Round(pos.y,1),["scaleX"]=Math.Round(scale.x,2),["scaleY"]=Math.Round(scale.y,2),["phone"]=(int)r.phoneState};
        }
        return result;
    }
    static IEnumerator Roundtrip(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root,
        Func<NewTalkView> talk,Func<int> current,Func<bool> phone,Func<int> hiddenRoles)
    {
        var host=UnityEngine.Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().First();
        var service=(DialogueSaveService)AccessTools.Field(typeof(DialogueRuntimeHost),"service").GetValue(host);
        var confirm=ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"QuickSave");bool old=confirm.Value;confirm.Value=false;
        var phoneImage=talk().icon_bg_3;string url=phoneImage.url;int origin=phoneImage.image.fillOrigin;
        var temp=((List<int>)AccessTools.Field(typeof(NewTalkView),"tmpRoleIds").GetValue(talk())).ToArray();
        try
        {
            yield return until(()=>adapter.CanCapture(out _),8,"phone capture preflight stable");
            var originalView=talk();var invalid=adapter.Capture();invalid.Dialogue["phone"]["background"]=int.MaxValue;
            var rejected=adapter.RestoreAsync(invalid);yield return until(()=>rejected.IsCompleted,15,"missing phone background preflight completes");
            check(rejected.IsFaulted && talk()==originalView && phone(),"missing phone background rejected before current world is discarded");var observed=rejected.Exception;
            invalid=adapter.Capture();invalid.Dialogue["phone"]["participants"]=new Newtonsoft.Json.Linq.JArray(int.MaxValue);
            rejected=adapter.RestoreAsync(invalid);yield return until(()=>rejected.IsCompleted,15,"missing phone participant preflight completes");
            check(rejected.IsFaulted && talk()==originalView && phone(),"missing phone participant rejected before current world is discarded");observed=rejected.Exception;
            for(int repeat=0;repeat<2;repeat++)
            {
                yield return until(()=>adapter.CanCapture(out _),8,"phone becomes saveable");
                var before=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).ToArray();
                service.QuickSave();service.QuickSave();
                yield return until(()=>service.List(DialogueUiCategory.Quick).Any(r=>!before.Contains(r.RevisionId)),30,"phone quick save commits");
                var added=service.List(DialogueUiCategory.Quick).Where(r=>!before.Contains(r.RevisionId)).ToArray();
                check(added.Length==1,"repeated quick-save request commits exactly once");
                talk().OnClickNext();yield return until(()=>current()==1900000304 && !phone(),20,"call hangs up before load");
                UiResult result=null;service.Load(added[0].RevisionId,r=>{if(!r.IsPending)result=r;});
                yield return until(()=>result!=null,60,"phone save loads");check(result.Success,"phone load succeeds: "+result.Message);
                yield return until(()=>current()==1900000303 && phone() && talk().icon_bg_3.image.sprite!=null,20,"phone restored at saved line");
                check(talk().icon_bg_3.url==url && talk().icon_bg_3.image.fillOrigin==origin && Mathf.Abs(talk().icon_bg_3.image.fillAmount-.5f)<.001f,"phone background and split geometry restored");
                check(temp.SequenceEqual((List<int>)AccessTools.Field(typeof(NewTalkView),"tmpRoleIds").GetValue(talk())),"temporary phone participants restored for native hangup cleanup");
                yield return new WaitForSecondsRealtime(1f);
                var adv=AdvDialogueController.Active;
                var canvas=(GameObject)AccessTools.Field(typeof(AdvDialogueController),"canvas").GetValue(adv);
                var shown=(TMPro.TextMeshProUGUI)AccessTools.Field(typeof(AdvDialogueController),"shownText").GetValue(adv);
                File.AppendAllText(Path.Combine(root,"results/phone-visibility.txt"),"canvas="+canvas?.activeSelf+" top="+UIMgr.GetTopView(ViewType.Guide,ViewType.Side)?.GetType().Name+
                    " transition="+AccessTools.Property(typeof(AdvDialogueController),"Transition").GetValue(adv)+" text="+shown?.gameObject.activeInHierarchy+"\n");
                check(canvas.activeInHierarchy && shown.gameObject.activeInHierarchy,"restored phone dialogue remains visible and operable");
                yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/phone-restored-"+repeat+".png"));yield return new WaitForSecondsRealtime(.5f);
            }
        }
        finally{confirm.Value=old;}
    }

}
