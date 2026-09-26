using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Config;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Main;
using View.Evt;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.Storage;

[BepInPlugin("local.studentage.dialoguesave.qa","Dialogue Save Isolated QA","0.1.0")]
[BepInDependency(DialogueSavePlugin.Id)]
public sealed class DialogueQaPlugin:BaseUnityPlugin
{
    void Awake()
    {
        string root=Paths.GameRootPath;
        if(!root.Replace('\\','/').EndsWith("/student-age-dialogue-save/qa/runtime"))throw new Exception("QA only");
        if(File.Exists(Path.Combine(root,"sisi-up-mode.txt")))RuntimeSisiQA.Prepare(root);
        var obj=new GameObject("DialogueQA.PersistentHost");obj.hideFlags=HideFlags.HideAndDontSave;DontDestroyOnLoad(obj);
        obj.AddComponent<DialogueQaDriver>().Initialize(root,Logger);
    }
}
public sealed class DialogueQaDriver:MonoBehaviour
{
    string root,output;ManualLogSource log;readonly List<string> checks=new List<string>();
    DialogueCheckpointAdapter adapter;DialogueUiController ui;IDialogueUiService service;
    public void Initialize(string path,ManualLogSource logger){root=path;log=logger;output=Path.Combine(root,"results");Directory.CreateDirectory(output);Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;}
    void Check(bool pass,string message){if(!pass)throw new Exception(message);checks.Add(message);log.LogInfo("QA_PASS "+message);File.WriteAllLines(Path.Combine(output,"checks.txt"),checks);}
    IEnumerator Start()
    {
        log.LogInfo("QA_DRIVER_START");
        if(File.Exists(Path.Combine(root,"adv-mode.txt")))RuntimeAdvQA.IsolateInput(true);
        var stack=new Stack<IEnumerator>();stack.Push(Run());bool failed=false;
        while(stack.Count>0){bool more=false;object next=null;try{more=stack.Peek().MoveNext();if(more)next=stack.Peek().Current;}catch(Exception ex){failed=true;File.WriteAllText(Path.Combine(output,"failed.txt"),ex.ToString());log.LogError(ex);}if(failed)break;if(!more){stack.Pop();continue;}var nested=next as IEnumerator;if(nested!=null){stack.Push(nested);continue;}yield return next;}
        if(failed){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(output,"failed-screen.png"));yield return null;}
        RuntimeAdvQA.IsolateInput(false);
        if(!failed)File.WriteAllText(Path.Combine(output,"success.txt"),File.Exists(Path.Combine(root,"sisi-up-mode.txt")) ? "SISI_AUTHORED_PRESENTATION_WITH_UP_OK" : File.Exists(Path.Combine(root,"archive-mode.txt")) ? "DIALOGUE_ARCHIVE_AND_INTERACTION_OK" : File.Exists(Path.Combine(root,"feedback-mode.txt")) ? "DIALOGUE_FEEDBACK_FIXES_OK" : File.Exists(Path.Combine(root,"hotfix-mode.txt")) ? "DIALOGUE_HOTFIX_RUNTIME_OK" : File.Exists(Path.Combine(root,"adv-only.txt")) ? "DIALOGUE_ADV_FIRST_FRAME_AND_LOG_PREVIEW_OK" : File.Exists(Path.Combine(root,"adv-mode.txt")) ? "DIALOGUE_ADV_FUNCTIONAL_OK_PERFORMANCE_SEPARATE" : File.Exists(Path.Combine(root,"recovery-mode.txt")) ? "DIALOGUE_RECOVERY_RUNTIME_OK" : File.Exists(Path.Combine(root,"font-mode.txt")) ? "DIALOGUE_FONT_RUNTIME_OK" : File.Exists(Path.Combine(root,"controls-mode.txt")) ? "DIALOGUE_CONTROLS_RUNTIME_OK" : File.Exists(Path.Combine(root,"preview-mode.txt")) ? "DIALOGUE_LOAD_UI_PREVIEW_OK" : "DIALOGUE_ISOLATED_RUNTIME_OK");
        if(!failed && File.Exists(Path.Combine(root,"preview-mode.txt"))) yield break;
        yield return new WaitForSecondsRealtime(2);Application.Quit();
    }
    static bool SkipFixtureWarning()=>false;
    void OnDestroy(){log?.LogInfo("QA_DRIVER_DESTROYED");}
    void Update(){RuntimeControlsQA.ObserveFrame(Time.unscaledDeltaTime);}
    IEnumerator Run()
    {
        yield return Until(()=>UIMgr.IsViewOpened<EntryView>(),90,"entry loaded");
        Check(PathDefine.SAVE_PATH.Replace('\\','/').Contains("/student-age-dialogue-save/qa/runtime/data/"),"managed save path isolated");
        Check(Application.companyName=="DlgSaveQA" && Application.productName=="DialogSave","native player identity isolated");
        if(File.Exists(Path.Combine(root,"sisi-up-mode.txt"))) RuntimeSisiQA.Validate(root,Check);
        else { RuntimeCompatibilityQA.Install(root, Check);
        File.WriteAllText(Path.Combine(output,"compatibility-checks.txt"), RuntimeCompatibilityQA.RunPureSelfChecks()); }
        var host=Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().FirstOrDefault();Check(host!=null,"persistent runtime host remains after native bootstrap");
        adapter=(DialogueCheckpointAdapter)AccessTools.Field(typeof(DialogueRuntimeHost),"adapter").GetValue(host);
        ui=(DialogueUiController)AccessTools.Field(typeof(DialogueRuntimeHost),"ui").GetValue(host);
        service=(IDialogueUiService)AccessTools.Field(typeof(DialogueRuntimeHost),"service").GetValue(host);
        Check(adapter!=null&&ui!=null&&service!=null,"plugin initialized all components");
        if(File.Exists(Path.Combine(root,"preview-mode.txt")) && !File.Exists(Path.Combine(root,"controls-mode.txt")) && !File.Exists(Path.Combine(root,"font-mode.txt")) && !File.Exists(Path.Combine(root,"recovery-mode.txt")) && !File.Exists(Path.Combine(root,"adv-mode.txt")))
        {
            UIMgr.OpenView<SaveView>(UILayerType.None,null,new object[]{false});
            yield return Until(()=>UIMgr.IsViewOpened<SaveView>(),20,"preview load window");
            yield return new WaitForSecondsRealtime(3);
            Click("DialogueSave.Entry");
            yield return new WaitForSecondsRealtime(1);
            yield return Shot("preview-dialogue-load");
            File.WriteAllText(Path.Combine(output,"preview-ready.txt"),"PREVIEW_READY");
            yield break;
        }
        if(File.Exists(Path.Combine(root,"adv-mode.txt")) && !File.Exists(Path.Combine(root,"adv-only.txt")))
            yield return RuntimeAdvQA.FirstRun(Check,Until,root);
        var startupStyle=AdvDialogueController.Active.Mode;
        AdvDialogueController.Active.SelectMode("Original"); // This bootstrap validates the native nine-slot page.
        SaveMgr.SetPref("LatestSaveKey","QA_SENTINEL");
        UIMgr.OpenView<SaveView>(UILayerType.None,null,new object[]{false});
        yield return Until(()=>UIMgr.IsViewOpened<SaveView>(),20,"native load window");yield return new WaitForSecondsRealtime(3);
        yield return Shot("01-native-load-entry");
        Click("DialogueSave.Entry");yield return new WaitForSecondsRealtime(.5f);yield return Shot("02-dialogue-load-categories");
        ClickNativeManual();yield return new WaitForSecondsRealtime(.3f);
        Check(((SaveView)UIMgr.GetView<SaveView>()).tabgroup_top.GetCells().Count==3,"ordinary three load tabs preserved");
        UIMgr.CloseView<SaveView>();
        AdvDialogueController.Active.SelectMode(startupStyle);
        // Fixture dependencies are historical test data. Stop its asynchronous Steam
        // title lookup before loading, so a late warning cannot cover QA screenshots.
        new Harmony("dialogue.qa.fixture-mods").Patch(AccessTools.Method(typeof(ProfileMgr),"ValidateModList"),prefix:new HarmonyMethod(typeof(DialogueQaDriver),nameof(SkipFixtureWarning)));
        bool done=false,loaded=false;SaveMgrEx.LoadAynsc(root,"fixture.save",16,ok=>{done=true;loaded=ok;});
        yield return Until(()=>done,30,"isolated fixture loaded");Check(loaded,"fixture decoding succeeded");
        // This fixture is synthetic QA data from an earlier test project, never a live player save.
        // Its old Mod IDs must not impersonate dependencies of this isolated current runtime.
        var fixtureProfile=(ProfileModel)AccessTools.Field(typeof(ProfileMgr),"model").GetValue(Singleton<ProfileMgr>.Ins);
        fixtureProfile.modList=new List<ulong>(Singleton<ModCtrl>.Ins.activeMods ?? new List<ulong>());
        UIMgr.CloseAllView();
        var game=AccessTools.Field(typeof(global::Game),"ins").GetValue(null);AccessTools.Field(typeof(global::Game),"<state>k__BackingField").SetValue(game,GameState.Running);
        AccessTools.Field(typeof(RoundMgr),"<RoundState>k__BackingField").SetValue(Singleton<RoundMgr>.Ins,RoundState.FinishPrepare);
        // Suppress only native tutorial popups for this deterministic fixture, not save/restore hooks.
        new Harmony("dialogue.qa.guides").Patch(AccessTools.Method(typeof(MainView),"CheckGuide"),prefix:new HarmonyMethod(typeof(DialogueQaDriver),nameof(NoGuide)));
        UIMgr.OpenView<MainView>();UIMgr.OpenView<TopView>();
        yield return Until(()=>UIMgr.IsViewOpened<MainView>()&&UIMgr.IsViewOpened<TopView>(),25,"main fixture views");
        if(File.Exists(Path.Combine(root,"controls-mode.txt")))
        {
            yield return new WaitForSecondsRealtime(1);
            var notice=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
            if(notice!=null && notice.GetType().Name=="CommonComfirmView")
            {
                File.WriteAllLines(Path.Combine(output,"isolated-fixture-notice.txt"),
                    notice.gameObject.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Select(t=>t.text));
                UIMgr.CloseView(notice);
                yield return new WaitForSecondsRealtime(.5f);
            }
            // Native five-entry pause menu before any dialogue decoration, for visual comparison.
            UIMgr.OpenView<EntryView>(UILayerType.None,null,new object[]{true,true});
            yield return Until(()=>global::Game.IsMenuOpened(),15,"native pause menu reference");
            yield return new WaitForSecondsRealtime(1);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(output,"controls-00-native-escape-reference.png"));
            var entry=(EntryView)UIMgr.GetView<EntryView>();
            File.WriteAllText(Path.Combine(output,"controls-00-native-escape-reference.txt"),
                "anchorMin="+entry.group_btn.anchorMin+" anchorMax="+entry.group_btn.anchorMax+
                " pivot="+entry.group_btn.pivot+" position="+entry.group_btn.anchoredPosition+
                " size="+entry.group_btn.sizeDelta+" scale="+entry.group_btn.localScale);
            UIMgr.CloseView<EntryView>();
            yield return new WaitForSecondsRealtime(.5f);
        }
        Singleton<FuncMgr>.Ins.GetMapData().recordMapId=1;
        var evt=Singleton<CommonEvtMgr>.Ins;
        var model=(CommonEvtModel)AccessTools.Field(typeof(CommonEvtMgr),"model").GetValue(evt);model.evtQueues.Clear();
        ((Queue<int>)AccessTools.Field(typeof(CommonEvtMgr),"roundEndEventQueue").GetValue(evt)).Clear();
        if(File.Exists(Path.Combine(root,"sisi-up-mode.txt"))) {yield return RuntimeSisiQA.Run(adapter,Check,Until,root);yield break;}
        int bg=Cfg.BgCfgMap.Keys.First(id=>id>0);
        var originalTalk=Cfg.TalkCfgMap.Values.First(t=>t.bg>0 && t.roleIds!=null && t.roleIds.Count>0 && !string.IsNullOrEmpty(t.content));
        bg=originalTalk.bg;
        Cfg.TalkCfgMap[1900000001]=new TalkCfg{id=1900000001,bg=bg,roleIds=originalTalk.roleIds,roleName=originalTalk.roleName,content="这是一段隔离测试对白。现在保存，读取后应仍停在这句话。",nextTalk=new List<int>{1900000002}};
        Cfg.TalkCfgMap[1900000002]=new TalkCfg{id=1900000002,bg=bg,roleIds=originalTalk.roleIds,roleName=originalTalk.roleName,content="这是保存之后的下一句。",option=new List<int>{1900000001,1900000002}};
        Cfg.OptionCfgMap[1900000001]=new OptionCfg{id=1900000001,content="继续第一条分支",talkId=new List<int>{1900000003}};
        Cfg.OptionCfgMap[1900000002]=new OptionCfg{id=1900000002,content="继续第二条分支",talkId=new List<int>{1900000003}};
        Cfg.TalkCfgMap[1900000003]=new TalkCfg{id=1900000003,bg=bg,roleIds=originalTalk.roleIds,roleName=originalTalk.roleName,content="选项后的对白正确继续。"};
        Cfg.EvtCfgMap[1]=new EvtCfg{id=1,type=1,title="隔离恢复验证",talkId=new List<int>{1900000001}};
        if(File.Exists(Path.Combine(root,"controls-mode.txt")))
        {
            bool nativeSaved=false,nativeSuccess=false;
            global::Game.ManualSaveGame(1,"隔离普通读档验证",null,ok=>{nativeSuccess=ok;nativeSaved=true;});
            yield return Until(()=>nativeSaved,30,"create isolated ordinary save fixture");
            Check(nativeSuccess,"ordinary fixture saved by native game");
            yield return null;
            File.WriteAllText(Path.Combine(output,"ordinary-fixture-name.txt"),SaveMgr.GetPref("LatestSaveKey",""));
            SaveMgr.SetPref("LatestSaveKey","QA_SENTINEL");
        }
        if(File.Exists(Path.Combine(root,"adv-only.txt")))gameObject.AddComponent<AdvFirstFrameQA>();
        if(File.Exists(Path.Combine(root,"local-font-preview.txt")))RuntimeSkinQA.LoadLocalFont(root);
        if(File.Exists(Path.Combine(root,"archive-controls.txt")))
        {var person=Cfg.PersonCfgMap.Values.Single(p=>p.name=="肖清雅");foreach(int id in new[]{1900000001,1900000002}){Cfg.TalkCfgMap[id].roleIds=new List<int>{person.id};Cfg.TalkCfgMap[id].roleName=null;}}
        evt.EnqueueEvt(0,1);evt.ShowNewRoundEvent();
        yield return Until(()=>adapter.IsSupportedDialogueContext,20,"native queued dialogue tracked");
        if(File.Exists(Path.Combine(root,"archive-routing.txt"))) {yield return RuntimeArchiveRoutingQA.Run(adapter,ui,(DialogueSaveService)service,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"perf-sweep.txt"))) {yield return RuntimePerfSweepQA.Run(adapter,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"final-batch.txt"))) {yield return RuntimeFinalBatchQA.Run(adapter,ui,(DialogueSaveService)service,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"modal-eight.txt"))) {yield return RuntimeModalEightQA.Run(adapter,ui,(DialogueSaveService)service,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"archive-controls.txt"))) {yield return RuntimeArchiveControlsQA.Run(adapter,ui,(DialogueSaveService)service,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"archive-six.txt"))) {yield return RuntimeArchiveSixQA.Run(adapter,ui,(DialogueSaveService)service,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"archive-refine.txt"))) {yield return RuntimeArchiveRefineQA.Run(adapter,ui,(DialogueSaveService)service,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"presentation-visual.txt")))
        {
            yield return RuntimePresentationQA.Visual(adapter,Check,Until,root);yield break;
        }
        if(File.Exists(Path.Combine(root,"presentation-mode.txt")))
        {
            yield return RuntimePresentationQA.Run(adapter,ui,service,Check,Until,root);
            yield break;
        }
        if(File.Exists(Path.Combine(root,"feedback-mode.txt")))
        {
            yield return RuntimeFeedbackQA.Run(adapter,Check,Until,root);
            yield break;
        }
        if(File.Exists(Path.Combine(root,"lifecycle-mode.txt"))){yield return RuntimeLifecycleQA.Run(adapter,ui,Check,Until,root);yield break;}
        if(File.Exists(Path.Combine(root,"archive-mode.txt")))
        {if(!File.Exists(Path.Combine(root,"interaction-only.txt")))yield return RuntimeArchiveQA.Run(adapter,ui,service,Check,Until,root);yield return RuntimeInteractionQA.Run(adapter,(DialogueSaveService)service,Check,Until,root);if(!File.Exists(Path.Combine(root,"settings-mode.txt")))yield break;}
        if(File.Exists(Path.Combine(root,"hotfix-mode.txt")))
        {
            yield return RuntimeHotfixQA.Run(adapter,ui,service,Check,Until,root);
            yield break;
        }
        if(File.Exists(Path.Combine(root,"adv-mode.txt")))
        {
            if(File.Exists(Path.Combine(root,"cg-visual.txt")))yield return RuntimeCgQA.Run(adapter,Check,Until,root);
            else if(File.Exists(Path.Combine(root,"backlog-visual.txt")))yield return RuntimeBacklogQA.Run(adapter,Check,Until,root);
            else if(File.Exists(Path.Combine(root,"settings-mode.txt")))
            {
                yield return RuntimeSettingsQA.Run(adapter,Check,Until,root);
                if(File.Exists(Path.Combine(root,"batch-visual.txt")))
                {yield return Until(()=>AdvSettingsTransition.Active==null,10,"settings appearance test close completed");yield return RuntimeBacklogQA.Run(adapter,Check,Until,root);yield return RuntimeCgQA.Run(adapter,Check,Until,root);}
            }
            else if(File.Exists(Path.Combine(root,"skin-mode.txt")))yield return RuntimeSkinQA.Run(adapter,Check,Until,root);
            else if(File.Exists(Path.Combine(root,"adv-only.txt")))yield return RuntimeAdvQA.Preview(adapter,service,Check,Until,root);
            else yield return RuntimeAdvQA.Run(adapter,ui,service,Check,Until,root);
            yield break;
        }
        if(File.Exists(Path.Combine(root,"recovery-mode.txt")))
        {
            yield return RuntimeRecoveryQA.Run(adapter,ui,service,Check,Until,root);
            yield break;
        }
        if(File.Exists(Path.Combine(root,"font-mode.txt")))
        {
            yield return Until(()=>RuntimeControlsQA.HasFour(ui) && adapter.CanCapture(out _) && ((TopView)UIMgr.GetView<TopView>()).itemgroup_key.GetCells().Count(c=>c.gameObject.activeInHierarchy)>=3,25,"font preview has complete native dialogue toolbar");
            yield return new WaitForSecondsRealtime(1);
            RuntimeFontQA.Capture(output);
            yield return Shot("font-dialogue-toolbar");
            yield break;
        }
        if(File.Exists(Path.Combine(root,"controls-mode.txt")))
        {
            yield return RuntimeControlsQA.Run(adapter,ui,service,Check,Until,Shot,root);
            File.WriteAllText(Path.Combine(output,"controls-ready.txt"),"CONTROLS_QA_READY");
            yield break;
        }
        yield return Until(()=>adapter.CanCapture(out _),20,"first stable line: "+adapter.Reason);
        yield return new WaitForSecondsRealtime(1);yield return Shot("03-dialogue-toolbar");
        var captureWatch=System.Diagnostics.Stopwatch.StartNew();var checkpoint=adapter.Capture();captureWatch.Stop();File.WriteAllText(Path.Combine(output,"capture-ms.txt"),captureWatch.ElapsedMilliseconds.ToString());Check(checkpoint.WorldBytes.Length>0,"detached world captured");
        var repo=new Repository(PathDefine.SAVE_PATH,Path.Combine(root,"data/test-stage"),Path.Combine(root,"data/test-backup"));
        var b=checkpoint.Brief;
        var record=repo.Publish(new SaveEnvelope{Header=new SaveHeader{SteamId=b.SteamId,RunId=b.RunId,Category="manual",LogicalSlot="1",DeviceId=Guid.NewGuid().ToString("N"),GameVersion=b.GameVersion,PluginVersion=DialogueSavePlugin.Version,AdapterVersion="round-dialogue-v1",Speaker=b.Speaker,Summary=b.Summary,RoleName=b.RoleName,YearLabel=b.YearLabel,SeasonLabel=b.SeasonLabel,SeasonId=b.SeasonId,Location=b.Location,Gender=b.Gender,GradeState=b.GradeState},World=checkpoint.WorldBytes,Dialogue=checkpoint.Dialogue});
        Check(repo.Load(record.Header.RevisionId).World.Length==checkpoint.WorldBytes.Length,"Windows atomic repository publish/load");
        ui.Open(true);yield return Until(()=>UIMgr.IsViewOpened<SaveView>(),20,"dialogue save window");yield return new WaitForSecondsRealtime(1);yield return Shot("04-dialogue-save-grid");
        ClickNativeManual();yield return new WaitForSecondsRealtime(.5f);
        Check(!UIMgr.IsViewOpened<SaveView>(),"toolbar save return closes to dialogue");
        ((NewTalkView)UIMgr.GetView<NewTalkView>()).NextTalk();
        yield return Until(()=>adapter.CanCapture(out _)&&((NewTalkView)UIMgr.GetView<NewTalkView>()).talkState==TalkState.Option,20,"stable choices capturable");
        var optionCheckpoint=adapter.Capture();
        var restore=adapter.RestoreAsync(checkpoint);yield return Until(()=>restore.IsCompleted,35,"restore first line");if(restore.IsFaulted)throw restore.Exception;
        Check(((NewTalkView)UIMgr.GetView<NewTalkView>()).txtex_content.text==(string)checkpoint.Dialogue["segments"][0],"first line text and segment restored");
        Check(UIMgr.IsViewOpened<TopView>(),"native in-game toolbar rebuilt after restore");
        Check(SaveMgr.GetPref("LatestSaveKey","")=="QA_SENTINEL","ordinary LatestSaveKey unchanged");
        yield return new WaitForSecondsRealtime(1);yield return Shot("05-restored-dialogue");
        restore=adapter.RestoreAsync(optionCheckpoint);yield return Until(()=>restore.IsCompleted,35,"restore choices");if(restore.IsFaulted)throw restore.Exception;
        var talk=((NewTalkView)UIMgr.GetView<NewTalkView>());Check(talk.talkState==TalkState.Option,"choice phase restored");
        Check(talk.itemgroup_options.GetCells().Count==2,"actual options restored");
        yield return new WaitForSecondsRealtime(1);yield return Shot("06-restored-choices");
        evt.SelectOption((CommonEvtOptionData)talk.itemgroup_options.GetCells()[0].data);
        yield return Until(()=>adapter.CanCapture(out _)&&((NewTalkView)UIMgr.GetView<NewTalkView>()).txtex_content.text.Contains("正确继续"),20,"restored choice continues");
        yield return RuntimeSemanticQA.Run(adapter,Check,Until);
        ui.Open(false);yield return Until(()=>UIMgr.IsViewOpened<SaveView>(),20,"dialogue load window");yield return new WaitForSecondsRealtime(1);yield return Shot("07-dialogue-load-card");
        Check(SaveMgr.GetPref("LatestSaveKey","")=="QA_SENTINEL","LatestSaveKey remains unchanged after all operations");
    }
    IEnumerator Until(Func<bool> predicate,float seconds,string label){float end=Time.realtimeSinceStartup+seconds;while(Time.realtimeSinceStartup<end){bool ok=false;try{ok=predicate();}catch{}if(ok){Check(true,label);yield break;}yield return null;}throw new Exception("Timed out: "+label+"; adapter="+adapter?.Reason);}
    IEnumerator Shot(string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(output,name+".png"));File.WriteAllText(Path.Combine(output,name+".json"),ui.CaptureLayoutReport());DumpHotkeyHierarchy(name);RuntimeUiQA.CheckLayout(ui,Check);yield return new WaitForSecondsRealtime(.3f);}
    void DumpHotkeyHierarchy(string name)
    {
        var lines=new List<string>();
        foreach(var obj in Resources.FindObjectsOfTypeAll<GameObject>().Where(o=>o.name.StartsWith("HotkeyView@Main") && o.activeInHierarchy))
        {
            lines.Add("ROOT "+obj.GetInstanceID()+" "+obj.name+" "+obj.transform.parent?.name);
            foreach(var text in obj.GetComponentsInChildren<TMPro.TextMeshProUGUI>(false))
            {
                var path=new List<string>();for(Transform t=text.transform;t!=null && t!=obj.transform;t=t.parent)path.Insert(0,t.name);
                lines.Add(string.Join("/",path.ToArray())+" text="+text.text+" world="+text.transform.position+" rect="+text.rectTransform.rect+" scale="+text.transform.lossyScale+" components="+string.Join(",",text.GetComponents<Component>().Select(c=>c.GetType().Name).ToArray()));
            }
        }
        foreach(var text in Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>().Where(t=>t.gameObject.activeInHierarchy && new[]{"日志","自动","菜单"}.Contains(t.text)))
        {
            var path=new List<string>();for(Transform t=text.transform;t!=null;t=t.parent)path.Insert(0,t.name);
            lines.Add("GLOBAL_TMP "+string.Join("/",path.ToArray())+" text="+text.text+" world="+text.transform.position);
        }
        foreach(var text in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>().Where(t=>t.gameObject.activeInHierarchy && new[]{"日志","自动","菜单"}.Contains(t.text)))
        {
            var path=new List<string>();for(Transform t=text.transform;t!=null;t=t.parent)path.Insert(0,t.name);
            lines.Add("GLOBAL_TEXT "+string.Join("/",path.ToArray())+" text="+text.text+" world="+text.transform.position);
        }
        foreach(var raw in Resources.FindObjectsOfTypeAll<UnityEngine.UI.RawImage>().Where(t=>t.gameObject.activeInHierarchy))
        {
            var path=new List<string>();for(Transform t=raw.transform;t!=null;t=t.parent)path.Insert(0,t.name);
            lines.Add("GLOBAL_TEXTURE "+string.Join("/",path.ToArray())+" texture="+(raw.texture==null?"null":raw.texture.name)+" type="+raw.texture?.GetType().FullName);
        }
        File.WriteAllLines(Path.Combine(output,name+"-hierarchy.txt"),lines);
    }
    static bool NoGuide(ref bool __result){__result=false;return false;}
    void ClickNativeManual(){var cell=((SaveView)UIMgr.GetView<SaveView>()).tabgroup_top.GetCells()[0];var old=cell.gameObject.name;cell.gameObject.name="QA.NativeManual";try{Click("QA.NativeManual");}finally{if(cell.gameObject!=null)cell.gameObject.name=old;}}
    void Click(string name){RuntimeUiQA.Click(name,Check);}
}
