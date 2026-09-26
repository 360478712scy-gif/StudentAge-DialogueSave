using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;
public static class RuntimePerfSweepQA
{
    static AdvBacklog Reader()=>Resources.FindObjectsOfTypeAll<AdvBacklog>().Single(x=>x.gameObject.activeInHierarchy);
    static int created;
    static JObject stages=new JObject();
    static void Begin(ref long __state){__state=System.Diagnostics.Stopwatch.GetTimestamp();}
    static void End(System.Reflection.MethodBase __originalMethod,long __state)
    {
        string key=__originalMethod.DeclaringType.Name+"."+__originalMethod.Name;
        if(stages[key]==null)stages[key]=new JArray();
        ((JArray)stages[key]).Add((System.Diagnostics.Stopwatch.GetTimestamp()-__state)*1000.0/System.Diagnostics.Stopwatch.Frequency);
    }
    static void RowCreated(){created++;}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
        yield return until(()=>talk.talkState==TalkState.AnimEnd&&adv.History.Entries.Count>0,30,"perf sweep stable full history checkpoint");
        var result=new JObject();var native=(List<TalkData>)AccessTools.Field(typeof(NewTalkView),"historys").GetValue(talk);int original=native.Count;
        var probe=new Harmony("dialogue.qa.perf-sweep");probe.Patch(AccessTools.Method(typeof(AdvBacklog),"CreateRow"),prefix:new HarmonyMethod(typeof(RuntimePerfSweepQA),nameof(RowCreated)));
        foreach(var typeAndMethod in new[]{Tuple.Create(typeof(AdvBacklog),"Build"),Tuple.Create(typeof(AdvBacklog),"CreateRow"),Tuple.Create(typeof(AdvBacklogSkin),"Prepare"),Tuple.Create(typeof(AdvBacklogSkin),"Footer")})
            probe.Patch(AccessTools.Method(typeAndMethod.Item1,typeAndMethod.Item2),prefix:new HarmonyMethod(typeof(RuntimePerfSweepQA),nameof(Begin)),postfix:new HarmonyMethod(typeof(RuntimePerfSweepQA),nameof(End)));
        AdvBacklog firstReader=null;
        using(adapter.PauseForMenu())
        try
        {
            var checkpoint=adapter.CaptureHistory();result["fullCheckpointMs"]=checkpoint.CaptureMilliseconds;result["worldBytes"]=checkpoint.State.WorldBytes.Length;
            var person=Cfg.PersonCfgMap.Values.First(p=>p.id>0&&p.gender==2);
            for(int i=0;i<2000;i++)native.Add(new TalkData{roleName=i%3==0?person.name:"",talkRoleIds=i%3==0?new List<int>{person.id}:null,
                content="长日志性能验证 "+i+"：放学后的阳光落在课桌上。\n完整保留对白，不删减较早记录。"});
            var opens=new JArray();result["changedHistoryOpenMs"]=opens;
            for(int round=0;round<4;round++)
            {
                native.Add(new TalkData{content="新增对白 "+round});
                var clock=System.Diagnostics.Stopwatch.StartNew();adv.OpenHistory();opens.Add(clock.Elapsed.TotalMilliseconds);
                yield return until(()=>!Reader().Opening && AdvSettingsTransition.Active==null,8,"perf history opening complete");
                if(firstReader==null)firstReader=Reader();else check(ReferenceEquals(firstReader,Reader()),"changed history reuses modal shell");
                if(round==3)
                {
                    var reader=Reader();var slider=reader.GetComponentInChildren<Slider>();var times=new JArray();result["scrollPageMs"]=times;
                    int before=created;
                    for(int i=0;i<16;i++)
                    {
                        clock.Restart();slider.value=i%2==0?1:0;times.Add(clock.Elapsed.TotalMilliseconds);yield return null;yield return null;
                    }
                    result["scrollCreatedRows"]=created-before;
                    result["retainedRenderRows"]=reader.GetComponentsInChildren<RectTransform>(true).Count(t=>t.name.StartsWith("History row "));
                    check((int)result["scrollCreatedRows"]<18,"back-and-forth navigation reuses row renderers");
                    // Sweep uncached pages, then check the cache is bounded and revisiting still works.
                    for(int i=1;i<=32;i++){slider.value=i/33f;yield return null;}
                    yield return null;
                    check(reader.GetComponentsInChildren<RectTransform>(true).Count(t=>t.name.StartsWith("History row "))<=38,"offscreen renderer cache remains bounded");
                    slider.value=0;yield return null;yield return null;
                    check(reader.GetComponentsInChildren<RectTransform>().Any(t=>t.name=="History row "+(native.Count-1)),"long history latest row reachable");
                    slider.value=1;yield return null;yield return null;
                    check(reader.GetComponentsInChildren<RectTransform>().Any(t=>t.name=="History row 0"),"long history earliest row reachable");
                    yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/perf-long-history.png"));
                }
                adv.CloseModal();yield return until(()=>!adv.ModalOpen,5,"perf history close");
            }
        }
        finally {probe.UnpatchSelf();adv.CloseModal();if(native.Count>original)native.RemoveRange(original,native.Count-original);}
        // Restore ordinary data through the same public reopen path before return testing.
        adv.OpenHistory();yield return until(()=>!Reader().Opening&&AdvSettingsTransition.Active==null,5,"perf normal history after long log");
        check(Reader().GetComponentsInChildren<RectTransform>().Count(t=>t.name.StartsWith("History row "))<8,"obsolete synthetic history removed on reload");
        adv.CloseModal();yield return until(()=>!adv.ModalOpen,5,"perf return to reading");
        result["stagesMs"]=stages;File.WriteAllText(Path.Combine(root,"results/perf-sweep.json"),result.ToString());
        var captions=(Dictionary<string,Sprite>)AccessTools.Field(typeof(AdvSettingsSkin),"captions").GetValue(null);
        yield return until(()=>captions.ContainsKey("返回对话")&&captions.ContainsKey("恢复默认"),15,"ticket lettering warm-up complete");
        foreach(string caption in new[]{"返回对话","恢复默认"})CheckWarmCaption(caption,check,root);
        yield return RuntimeBacklogQA.Run(adapter,check,until,root);
        File.WriteAllText(Path.Combine(root,"results/perf-sweep-success.txt"),"PERF_LONG_HISTORY_BOUNDED_RENDER_REUSE_PIXEL_EQUIVALENT_WARM_CAPTIONS_NATIVE_ROLLBACK_OK");
    }
    static void CheckWarmCaption(string caption,Action<bool,string> check,string root)
    {
        var captions=(Dictionary<string,Sprite>)AccessTools.Field(typeof(AdvSettingsSkin),"captions").GetValue(null);
        var imported=(Dictionary<string,Texture2D>)AccessTools.Field(typeof(AdvSettingsSkin),"imported").GetValue(null);
        string file=caption=="返回对话"?"lettering-return-dialogue.png":"lettering-restore-defaults.png";
        var warm=captions[caption];var oldTexture=imported[file];
        captions.Remove(caption);imported.Remove(file);
        try
        {
            var legacy=(Sprite)AccessTools.Method(typeof(AdvSettingsSkin),"ExtraCaption").Invoke(null,new object[]{caption});
            var read=AccessTools.Method(typeof(RuntimeCgQA),"ReadPixels");
            var a=(Color32[])read.Invoke(null,new object[]{warm.texture});var b=(Color32[])read.Invoke(null,new object[]{legacy.texture});
            int differences=Enumerable.Range(0,a.Length).Count(i=>!a[i].Equals(b[i]));
            File.AppendAllText(Path.Combine(root,"results/lettering-equivalence.txt"),caption+" warm="+warm.rect+" legacy="+legacy.rect+" pixelDifferences="+differences+"\n");
            check(warm.rect==legacy.rect&&differences==0,"time-sliced lettering equals original import pixel-for-pixel: "+caption);
        }
        finally{captions[caption]=warm;imported[file]=oldTexture;}
    }
}
