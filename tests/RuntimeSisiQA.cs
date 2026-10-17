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
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

// Opt-in installed-author-story presentation test. No authored config is replaced.
public static class RuntimeSisiQA
{
    static string authorWorkshop;
    static int greetingCompletions;
    static void GreetingCompleted()=>greetingCompletions++;
    static bool WorkshopRoot(ref string __result){__result=authorWorkshop;return false;}
    public static void Prepare(string root)
    {
        authorWorkshop=Path.Combine(root,"author-workshop");
        var type=System.Reflection.Assembly.LoadFrom(Path.Combine(root,"BepInEx/plugins/EC2BUnofficialPatch.dll")).GetType("EC2BUnofficialPatch.Workshop.ContentRootCatalog");
        if(type==null)throw new Exception("UP resource resolver missing");
        new Harmony("dialogue.qa.sisi-resource-root").Patch(AccessTools.Method(type,"FindWorkshopDirectory"),prefix:new HarmonyMethod(typeof(RuntimeSisiQA),nameof(WorkshopRoot)));
    }
    static TalkCfg Current(NewTalkView t)=>t==null?null:(TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(t);
    static bool ArchiveIdle()=>AdvSavePage.Active!=null && AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1 &&
        AdvSettingsTransition.Active==null && !AdvConfirmation.IsOpen && !(bool)AccessTools.Field(typeof(AdvSavePage),"busy").GetValue(AdvSavePage.Active);
    static IEnumerator ManualRoundtrip(DialogueCheckpointAdapter adapter,DialogueSaveService service,int talkId,
        Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        foreach(string key in new[]{"SaveEmpty","Load"})ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,key).Value=true;
        check(service.IsDialogueContext,"author Mod recognized as dialogue context "+talkId);
        RuntimeUiQA.Click("ADV.保存",check);
        yield return until(()=>ArchiveIdle()&&!service.IsListing,25,"author SAVE opens manual dialogue archive "+talkId);
        check(!(bool)AccessTools.Property(typeof(AdvSavePage),"NativeSlots").GetValue(AdvSavePage.Active),"Mod SAVE uses dialogue snapshots, not native world-only saves "+talkId);
        RuntimeUiQA.Click("Archive.PageNext",check);yield return null;
        int page=(int)AccessTools.Field(typeof(AdvSavePage),"pageCount").GetValue(AdvSavePage.Active);
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{page});yield return null;
        int slot=(page-1)*12+1;
        RuntimeUiQA.Click("Archive.Slot"+slot,check);
        yield return until(()=>AdvConfirmation.IsOpen&&AdvSettingsTransition.Active==null,10,"author manual save confirmation "+talkId);
        RuntimeUiQA.Click("ADV.SaveEmptyConfirm",check);
        yield return until(()=>ArchiveIdle()&&service.ArchiveList(DialogueUiCategory.Manual).Any(r=>r.Slot==slot&&r.RunId==service.ArchiveRun),40,"author manual slot persisted "+talkId);
        var saved=service.ArchiveList(DialogueUiCategory.Manual).Single(r=>r.Slot==slot&&r.RunId==service.ArchiveRun);
        check(saved.CanLoad,"authored manual save is readable "+talkId+": "+saved.StatusReason);
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/sisi-manual-saved-"+talkId+".png"));yield return null;
        RuntimeUiQA.Click("Archive.Tab1",check);yield return until(ArchiveIdle,10,"author load tab ready");
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(AdvSavePage.Active,new object[]{page});yield return null;
        RuntimeUiQA.Click("Archive.Slot"+slot,check);
        yield return until(()=>AdvConfirmation.IsOpen&&AdvSettingsTransition.Active==null,10,"author manual load confirmation");
        RuntimeUiQA.Click("ADV.LoadConfirm",check);
        yield return until(()=>AdvSavePage.Active==null&&!adapter.IsRestoring&&adapter.CanCapture(out _),60,"author manual slot restored "+talkId);
        check(Current(UIMgr.GetView<NewTalkView>(false) as NewTalkView)?.id==talkId,"manual UI roundtrip preserves authored dialogue node "+talkId);
    }
    static IEnumerator OriginalRoundtrip(DialogueCheckpointAdapter adapter,DialogueSaveService service,int talkId,
        Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until)
    {
        AdvDialogueController.Active.Preferences.ArchiveScope.Value=AdvArchiveScope.Disabled;
        ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"Load").Value=false;
        var host=Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().First();
        var ui=(DialogueUiController)AccessTools.Field(typeof(DialogueRuntimeHost),"ui").GetValue(host);
        Func<DialogueSavePage> page=()=>((Dictionary<View.Main.SaveView,DialogueSavePage>)AccessTools.Field(typeof(DialogueUiController),"pages").GetValue(ui))[UIMgr.GetView<View.Main.SaveView>(false) as View.Main.SaveView];
        Func<bool> ready=()=>UIMgr.IsViewOpened<View.Main.SaveView>() && !service.IsListing && AdvSettingsTransition.Active==null &&
            !(UIMgr.GetView<View.Main.LoadingView>(false)?.gameObject.activeInHierarchy??false) &&
            Resources.FindObjectsOfTypeAll<Transform>().Any(t=>t.name=="DialogueSave.Grid"&&t.gameObject.activeInHierarchy);
        Action<int,string> select=(index,button)=>
        {
            var p=page();AccessTools.Field(typeof(DialogueSavePage),"page").SetValue(p,index/9+1);p.RefreshRecords();Canvas.ForceUpdateCanvases();
            var card=Resources.FindObjectsOfTypeAll<Transform>().Single(t=>t.name=="DialogueSave.Card."+(index%9)&&t.gameObject.activeInHierarchy);
            var b=card.GetComponentsInChildren<Button>(false).Single(x=>x.name==button);string old=b.name;b.name="QA.Sisi.OriginalSlot";
            RuntimeUiQA.Click(b.name,check);if(b!=null)b.name=old;
        };
        Func<int,DialogueUiRecord> record=index=>
        {
            var item=((System.Collections.IList)AccessTools.Field(typeof(DialogueSavePage),"items").GetValue(page()))[index];
            return (DialogueUiRecord)AccessTools.Field(item.GetType(),"Record").GetValue(item);
        };
        yield return until(()=>Resources.FindObjectsOfTypeAll<Button>().Any(b=>b.name=="ADV.保存"&&b.gameObject.activeInHierarchy),10,"ADV toolbar rebuilt after archive scope change");
        // A just-created Canvas has not registered graphic depths until it renders.
        yield return new WaitForEndOfFrame();yield return null;
        yield return until(()=>AdvSettingsTransition.Active==null&&!AdvDialogueController.Active.ModalOpen,10,"previous ADV archive exit transition finished");
        RuntimeUiQA.Click("ADV.保存",check);yield return until(ready,25,"original-style Mod dialogue save page opens");
        check((bool)AccessTools.Field(typeof(DialogueSavePage),"saving").GetValue(page()),"original-style dialogue subpage retains requested save mode");
        var before=service.ArchiveList(DialogueUiCategory.Manual).Select(r=>r.RevisionId).ToArray();
        var items=(System.Collections.IList)AccessTools.Field(typeof(DialogueSavePage),"items").GetValue(page());
        int empty=Enumerable.Range(0,items.Count).First(i=>record(i)==null);select(empty,"btn_add");
        yield return until(()=>service.ArchiveList(DialogueUiCategory.Manual).Any(r=>!before.Contains(r.RevisionId)),40,"original-style new slot writes real Mod snapshot");
        var saved=service.ArchiveList(DialogueUiCategory.Manual).First(r=>!before.Contains(r.RevisionId));
        check(saved.CanLoad,"original-style Mod snapshot validates after writing");
        UIMgr.CloseView<View.Main.SaveView>();yield return until(()=>!UIMgr.IsViewOpened<View.Main.SaveView>()&&adapter.CanCapture(out _),15,"original-style save pause released");
        yield return until(()=>Resources.FindObjectsOfTypeAll<Button>().Any(b=>b.name=="ADV.读取"&&b.gameObject.activeInHierarchy),10,"ADV toolbar visible after original-style archive close");
        yield return new WaitForEndOfFrame();yield return null;
        yield return until(()=>AdvSettingsTransition.Active==null&&!AdvDialogueController.Active.ModalOpen,10,"original-style save exit has no remaining input guard");
        RuntimeUiQA.Click("ADV.读取",check);yield return until(ready,25,"original-style Mod dialogue load page opens");
        items=(System.Collections.IList)AccessTools.Field(typeof(DialogueSavePage),"items").GetValue(page());
        int target=Enumerable.Range(0,items.Count).First(i=>record(i)?.RevisionId==saved.RevisionId);select(target,"btn_click");
        RuntimeUiQA.Click("DialogueSave.Commit",check);
        yield return until(()=>!UIMgr.IsViewOpened<View.Main.SaveView>()&&!adapter.IsRestoring&&adapter.CanCapture(out _),60,"original-style saved Mod dialogue restores through UI");
        check(Current(UIMgr.GetView<NewTalkView>(false) as NewTalkView)?.id==talkId,"original-style UI restores the same authored greeting node");
        AdvDialogueController.Active.Preferences.ArchiveScope.Value=AdvArchiveScope.DialogueOnly;
    }
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
    static IEnumerator SourceEntries(DialogueCheckpointAdapter adapter,DialogueSaveService service,
        Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        const int eventId=1277156;
        int mapId=3,bg=Cfg.MapCfgMap[mapId].bg;
        UIMgr.OpenView<View.Main.MapSceneView>(UILayerType.None,null,new object[]{mapId,bg});
        yield return until(()=>UIMgr.IsViewOpened<View.Main.MapSceneView>(),25,"native scene opened for Sisi greeting");
        yield return new WaitForSecondsRealtime(1);
        var scene=UIMgr.GetView<View.Main.MapSceneView>(false) as View.Main.MapSceneView;
        // Match the native pending-greeting UI state, then enter through its method.
        scene.ShowCloseBtn(false);
        AccessTools.Field(typeof(View.Main.MapSceneView),"enableCloseByRightClick").SetValue(scene,false);
        greetingCompletions=0;
        new Harmony("dialogue.qa.sisi.scene-greeting").Patch(AccessTools.Method(typeof(View.Main.MapSceneView),"<ShowGreetingEvt>b__47_0"),
            prefix:new HarmonyMethod(typeof(RuntimeSisiQA),nameof(GreetingCompleted)));
        AccessTools.Field(typeof(View.Main.MapSceneView),"greetingEvtId").SetValue(scene,eventId);
        scene.ShowGreetingEvt();
        yield return until(()=>UIMgr.IsViewOpened<NewTalkView>(),15,"Sisi greeting entered through native scene method");
        yield return new WaitForSecondsRealtime(2);
        var talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        if(talk.talkState==TalkState.Anim)talk.OnClickNext();
        yield return until(()=>talk.talkState==TalkState.AnimEnd,10,"native greeting text settled");
        yield return new WaitForSecondsRealtime(2);
        bool can=adapter.CanCapture(out string reason);
        string callback=talk.callback?.Method.DeclaringType?.FullName+"."+talk.callback?.Method.Name;
        File.WriteAllText(Path.Combine(root,"results/sisi-greeting-capture.txt"),"can="+can+" reason="+reason+" callback="+callback);
        check(can,"native Sisi scene greeting saves: "+reason+" callback="+callback);
        var continuation=DialogueContinuation.Capture(talk);
        check(continuation["callback"].Value<string>("rule")=="site-greeting","greeting snapshot identifies the audited native continuation");
        var wrongScene=(Newtonsoft.Json.Linq.JObject)continuation.DeepClone();
        wrongScene["callback"]["fields"]["mapId"]=Cfg.MapCfgMap.Keys.First(id=>id!=mapId);
        bool refused=false;try{DialogueContinuation.Validate(wrongScene);}catch(InvalidDataException){refused=true;}
        check(refused,"greeting callback cannot bind to a different saved scene");
        var wrongMethod=(Newtonsoft.Json.Linq.JObject)continuation.DeepClone();wrongMethod["callback"]["token"]=(int)wrongMethod["callback"]["token"]+1;
        refused=false;try{DialogueContinuation.Validate(wrongMethod);}catch(InvalidDataException){refused=true;}
        check(refused,"unknown greeting method token remains rejected");
        refused=false;try{DialogueContinuation.CaptureCallback(GreetingCompleted);}catch(InvalidOperationException){refused=true;}
        check(refused,"arbitrary third-party continuation is still refused");
        var leave=(Newtonsoft.Json.Linq.JObject)continuation.DeepClone();leave["callback"]["rule"]="leave-site";
        leave["callback"]["token"]=AccessTools.Method(typeof(BaseView),"CloseView").MetadataToken;
        DialogueContinuation.Validate(leave);
        check(Newtonsoft.Json.Linq.JToken.DeepEquals(leave["callback"],DialogueContinuation.CaptureCallback(DialogueContinuation.RestoreCallback(leave["callback"]))),"existing leave-site continuation still roundtrips without invoking it");
        yield return ManualRoundtrip(adapter,service,Current(talk).id,check,until,root);
        talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        yield return OriginalRoundtrip(adapter,service,Current(talk).id,check,until);
        talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
        scene=UIMgr.GetView<View.Main.MapSceneView>(false) as View.Main.MapSceneView;
        check(ReferenceEquals(talk.callback.Target,scene),"loaded greeting callback binds to restored scene instance");
        check(greetingCompletions==0,"save and load never replay greeting completion");
        check(!(bool)AccessTools.Field(typeof(View.Main.MapSceneView),"enableCloseByRightClick").GetValue(scene),"restored pending greeting keeps scene close blocked");
        for(int step=0;step<120 && UIMgr.IsViewOpened<NewTalkView>();step++)
        {
            talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            yield return until(()=>adapter.CanAdvancePresentation(talk),10,"restored greeting presentation released");
            if(talk.talkState==TalkState.Option)RuntimeUiQA.Click("ADV.Choice.0",check);
            else talk.OnClickNext();
            yield return new WaitForSecondsRealtime(.15f);
        }
        check(!UIMgr.IsViewOpened<NewTalkView>()&&greetingCompletions==1,"authored Sisi greeting ends and native completion executes exactly once");
        check(UIMgr.IsViewOpened<View.Main.MapSceneView>() && (bool)AccessTools.Field(typeof(View.Main.MapSceneView),"enableCloseByRightClick").GetValue(scene),"greeting returns to usable original scene after loading");
        yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/sisi-greeting-returned-scene.png"));yield return null;
    }
    public static IEnumerator Run(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        AdvDialogueController.Active.SelectMode("ADV");
        // Previous settings QA deliberately persists Disabled; this suite exercises
        // the default archive route and must establish its own isolated preference.
        AdvDialogueController.Active.Preferences.ArchiveScope.Value=AdvArchiveScope.DialogueOnly;
        var host=Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().First();
        var service=(DialogueSaveService)AccessTools.Field(typeof(DialogueRuntimeHost),"service").GetValue(host);
        var confirmation=ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"QuickSave");confirmation.Value=false;
        if(File.Exists(Path.Combine(root,"sisi-source-entries.txt"))){yield return SourceEntries(adapter,service,check,until,root);yield break;}
        var roundtrips=new HashSet<int>{1277069001,1277069074,1277069096};
        // Enter the author's event through native ShowEvent; fixture bypasses calendar/friendship prerequisites only.
        var targets=new HashSet<int>{1277067008,1277067063,1277069009,1277069031,1277069074,1277069096,1277069110};
        var visited=new HashSet<int>();var trace=new List<string>();var saveProbe=new HashSet<int>();var saveLog=new List<string>();
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
                if((talk.talkState==TalkState.AnimEnd || talk.talkState==TalkState.Option) && saveProbe.Add(cfg.id))
                {
                    // Record whether a player could save on this stable authored line, and why not.
                    yield return null;
                    bool can=adapter.CanCapture(out string why);
                    string detail="";
                    if(!can)
                    {
                        detail=" | type="+AccessTools.Field(typeof(NewTalkView),"talkType").GetValue(talk)+" waitFrame="+AccessTools.Field(typeof(NewTalkView),"waitFrame").GetValue(talk)+"/"+Time.frameCount+" delaying="+AccessTools.Field(typeof(NewTalkView),"isDelaying").GetValue(talk)+" optionDepth="+AccessTools.Field(typeof(DialogueCheckpointAdapter),"optionDepth").GetValue(adapter)+" state="+talk.talkState;
                        yield return new WaitForSecondsRealtime(1.5f);
                        bool later=adapter.CanCapture(out string laterWhy);detail+=" | after1.5s="+(later?"OK":laterWhy);
                    }
                    saveLog.Add(cfg.id+" | "+(can?"OK":why)+" | effects="+string.Join(",",cfg.screenEffect??new List<float>())+" bg="+cfg.bg+detail);
                    File.WriteAllLines(Path.Combine(root,"results/sisi-save-probe.txt"),saveLog);
                }
                if(talk.talkState==TalkState.AnimEnd && roundtrips.Remove(cfg.id))
                {
                    yield return until(()=>adapter.CanCapture(out _),10,"authored Mod line saveable "+cfg.id);
                    yield return ManualRoundtrip(adapter,service,cfg.id,check,until,root);
                    talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                    if(cfg.id==1277069001)
                    {
                        var audio=(Newtonsoft.Json.Linq.JObject)adapter.Capture().Dialogue["audio"];
                        check(audio.Value<float>("volume")==20f,"real authored music volume scale 20 captured without normalization");
                        foreach(float bad in new[]{-1f,float.NaN,float.PositiveInfinity})
                        {
                            var invalid=(Newtonsoft.Json.Linq.JObject)audio.DeepClone();invalid["volume"]=bad;
                            bool rejected=false;try{DialogueAudioAdapter.Validate(invalid);}catch(InvalidDataException){rejected=true;}
                            check(rejected,"invalid audio scale still rejected: "+bad);
                        }
                        var oldPath=Path.Combine(root,"audio-volume-legacy.dsav");var oldBytes=File.ReadAllBytes(oldPath);
                        var old=StudentAgeDialogueSave.Storage.SaveCodec.Decode(StudentAgeDialogueSave.Storage.SaveCodec.Read(oldBytes));
                        var loadOld=adapter.RestoreAsync(new GameCheckpoint{WorldBytes=old.World,Dialogue=old.Dialogue,Brief=new CheckpointBrief{SteamId=old.Header.SteamId,RunId=old.Header.RunId,GameVersion=old.Header.GameVersion}});
                        yield return until(()=>loadOld.IsCompleted,60,"previously failed real Mod snapshot loads without editing");loadOld.GetAwaiter().GetResult();
                        check(Enumerable.SequenceEqual(oldBytes,File.ReadAllBytes(oldPath)),"previously failed Mod file remains byte-for-byte unchanged");
                        talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                        yield return until(()=>adapter.CanCapture(out _),15,"old author snapshot stable");
                        check(((Newtonsoft.Json.Linq.JObject)adapter.Capture().Dialogue["audio"]).Value<float>("volume")==20f,"old Mod native music scale preserved on actual restore");
                    }
                    var before=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).ToArray();
                    int effectStage=(int)AccessTools.Field(typeof(NewTalkView),"lastEffectCfgId").GetValue(talk);
                    service.QuickSave();
                    yield return until(()=>service.List(DialogueUiCategory.Quick).Any(r=>!before.Contains(r.RevisionId)),40,"authored Mod save commits "+cfg.id);
                    string revision=service.List(DialogueUiCategory.Quick).First(r=>!before.Contains(r.RevisionId)).RevisionId;
                    UiResult result=null;service.Load(revision,r=>{if(!r.IsPending)result=r;});
                    yield return until(()=>result!=null,60,"authored Mod disk save loads "+cfg.id);
                    check(result.Success,"real author Mod disk roundtrip succeeds "+cfg.id+": "+result.Message);
                    talk=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
                    check(Current(talk).id==cfg.id && (int)AccessTools.Field(typeof(NewTalkView),"lastEffectCfgId").GetValue(talk)==effectStage,"authored line and committed effect stage preserved "+cfg.id);
                    yield return new WaitForSecondsRealtime(.5f);
                    yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/sisi-restored-"+cfg.id+".png"));yield return null;
                }
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
        check(roundtrips.Count==0,"all three author ordinary/CG disk roundtrips exercised");
        check((visited.Contains(1277067063)||File.Exists(Path.Combine(root,"sisi-after-funeral.txt")))&&visited.Contains(1277069074)&&visited.Contains(1277069096),"funeral crying and post-funeral CG reached through authored line chain");
        File.WriteAllText(Path.Combine(root,"results/sisi-complete.txt"),"AUTHOR_EVENTS_WITH_UP_COMPLETED; lines="+visited.Count);
    }
}
