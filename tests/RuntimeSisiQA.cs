using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

// Opt-in installed-author-story presentation test. No authored config is replaced.
public static class RuntimeSisiQA
{
    static string authorWorkshop;
    static bool WorkshopRoot(ref string __result){__result=authorWorkshop;return false;}
    public static void Prepare(string root)
    {
        authorWorkshop=Path.Combine(root,"author-workshop");
        var type=System.Reflection.Assembly.LoadFrom(Path.Combine(root,"BepInEx/plugins/EC2BUnofficialPatch.dll")).GetType("EC2BUnofficialPatch.Workshop.ContentRootCatalog");
        if(type==null)throw new Exception("UP resource resolver missing");
        new Harmony("dialogue.qa.sisi-resource-root").Patch(AccessTools.Method(type,"FindWorkshopDirectory"),prefix:new HarmonyMethod(typeof(RuntimeSisiQA),nameof(WorkshopRoot)));
    }
    static TalkCfg Current(NewTalkView t)=>t==null?null:(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(t);
    public static void Validate(string root,Action<bool,string> check)
    {
        var infos=Chainloader.PluginInfos.Values.ToArray();
        File.WriteAllLines(Path.Combine(root,"results/plugins.txt"),infos.Select(p=>p.Metadata.GUID+" "+p.Metadata.Version+" "+p.Location));
        check(infos.Length==3 && infos.Any(p=>p.Metadata.GUID=="sa.EC2B.UnofficialPatch"),"UP really loaded alongside DialogueSave and isolated QA");
        check(infos.All(p=>Path.GetFullPath(p.Location).StartsWith(Path.Combine(root,"BepInEx","plugins"),StringComparison.OrdinalIgnoreCase)),"all three plugin binaries are isolated copies");
        check(File.ReadAllText(Path.Combine(root,"isolation-verified.txt")).StartsWith("All managed assemblies rescanned;"),"UP included in managed write isolation scan");
        check(Singleton<ModCtrl>.Ins.activeMods.Contains(3664928586UL)&&Singleton<ModCtrl>.Ins.activeMods.Contains(3673850497UL),"author story and required compatibility mod enabled by native loader");
        check(Cfg.TalkCfgMap.ContainsKey(1277069001)&&Cfg.CGCfgMap.ContainsKey(1277013),"native loader merged author dialogue and CG configs");
    }
    static IEnumerator AcknowledgePopups(string root,Action<bool,string> check)
    {
        for(int i=0;i<6;i++)
        {
            var top=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
            if(top==null || top is NewTalkView)yield break;
            var buttons=top.gameObject.GetComponentsInChildren<Button>(false).Where(b=>b.IsInteractable() &&
                (b.GetComponentsInChildren<TextMeshProUGUI>(false).Any(t=>t.text.Trim()=="确定") || b.GetComponentsInChildren<Text>(false).Any(t=>t.text.Trim()=="确定"))).ToArray();
            if(buttons.Length==0)yield break;
            check(buttons.Length==1,"one authored reward confirmation: "+top.GetType().Name);
            yield return new WaitForSecondsRealtime(.4f);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/sisi-reward-"+top.GetType().Name+".png"));
            string name=buttons[0].name;buttons[0].name="QA.Sisi.AuthorReward";
            RuntimeUiQA.Click("QA.Sisi.AuthorReward",check);
            if(buttons[0]!=null)buttons[0].name=name;
            yield return new WaitForSecondsRealtime(.5f);
        }
    }
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        AdvDialogueController.Active.SelectMode("ADV");
        // Enter the author's event through native ShowEvent; fixture bypasses calendar/friendship prerequisites only.
        var targets=new HashSet<int>{1277067008,1277067063,1277069009,1277069031,1277069074,1277069096,1277069110};
        var visited=new HashSet<int>();var trace=new List<string>();
        foreach(int eventId in (File.Exists(Path.Combine(root,"sisi-after-funeral.txt"))?new[]{1277069}:new[]{1277067,1277069}))
        {
            var evt=Singleton<CommonEvtMgr>.Ins;
            evt.ShowEvent(eventId);
            yield return until(()=>UIMgr.IsViewOpened<NewTalkView>(),30,"author event opened "+eventId);
            yield return new WaitForSecondsRealtime(3);
            int previous=0;float lastChange=Time.realtimeSinceStartup;bool ended=false;
            for(int step=0;step<1600;step++)
            {
                yield return AcknowledgePopups(root,check);
                var talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                if(talk==null||!talk.gameObject.activeInHierarchy){ended=true;break;}
                var cfg=Current(talk);if(cfg==null){yield return null;continue;}
                if(cfg.id!=previous)
                {
                    previous=cfg.id;lastChange=Time.realtimeSinceStartup;visited.Add(cfg.id);
                    trace.Add(cfg.id+" | "+cfg.content+" | effects="+string.Join(",",cfg.screenEffect??new List<float>()));
                    File.WriteAllLines(Path.Combine(root,"results/sisi-trace.txt"),trace);
                    // Allow native role/background animations, then complete only the native typewriter.
                    yield return new WaitForSecondsRealtime(cfg.bg!=0||cfg.screenEffect?.Count>0?1.1f:.12f);
                }
                if(Time.realtimeSinceStartup-lastChange>=22) check(false,"author line stalled "+cfg.id+" state="+talk.talkState);
                if(talk.talkState==TalkState.AnimEnd && targets.Remove(cfg.id))
                {
                    yield return new WaitForSecondsRealtime(.7f);
                    yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/sisi-"+cfg.id+".png"));
                    var texts=talk.gameObject.GetComponentsInChildren<TextMeshProUGUI>(false).Where(t=>!string.IsNullOrEmpty(t.text)).Select(t=>t.name+" "+t.text);
                    File.WriteAllLines(Path.Combine(root,"results/sisi-"+cfg.id+".txt"),texts);
                    yield return null;
                }
                if(talk.talkState==TalkState.Option)
                {
                    check(cfg.option!=null && cfg.option.Count==1 && Cfg.OptionCfgMap[cfg.option[0]].content=="确定","author ending is a single confirmation");
                    yield return null;yield return null;
                    RuntimeUiQA.Click("ADV.Choice.0",check);
                }
                else talk.OnClickNext();
                yield return new WaitForSecondsRealtime(.065f);
            }
            yield return AcknowledgePopups(root,check);
            check(ended,"author event plays to natural end "+eventId);
            yield return new WaitForSecondsRealtime(.4f);
        }
        check((visited.Contains(1277067063)||File.Exists(Path.Combine(root,"sisi-after-funeral.txt")))&&visited.Contains(1277069074)&&visited.Contains(1277069096),"funeral crying and post-funeral CG reached through authored line chain");
        File.WriteAllText(Path.Combine(root,"results/sisi-complete.txt"),"AUTHOR_EVENTS_WITH_UP_COMPLETED; lines="+visited.Count);
    }
}
