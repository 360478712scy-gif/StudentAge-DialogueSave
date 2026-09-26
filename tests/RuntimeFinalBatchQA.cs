using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.GameIntegration;

public static class RuntimeFinalBatchQA
{
    static object Field(object o,string n)=>AccessTools.Field(o.GetType(),n).GetValue(o);
    static bool Idle()=>AdvSettingsTransition.Active==null && (AdvSettings.Active==null || !(bool)Field(AdvSettings.Active,"transitionBusy")) && (AdvSavePage.Active==null || AdvSavePage.Active.GetComponent<CanvasGroup>().alpha==1);
    static void Click(string n,Action<bool,string> c)=>RuntimeUiQA.Click(n,c);
    static IEnumerator Shot(string root,string name){yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/final-"+name+".png"));yield return null;}
    static Slider Slider(string n)=>AdvSettings.Active.GetComponentsInChildren<Slider>().Single(s=>s.name=="Settings.Slider."+n);
    internal static IEnumerator Run(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;adv.SelectMode("ADV");
        yield return until(()=>adapter.CanCapture(out _),25,"final batch dialogue ready");
        RuntimeAdvQA.IsolateInput(true);
        var baseline=adapter.Capture();
        if(File.Exists(Path.Combine(root,"final-unload.txt"))){yield return RollbackUnload(adapter,ui,check,until,root);yield break;}
        if(File.Exists(Path.Combine(root,"final-extra.txt"))){yield return Supplement(adapter,ui,service,check,until,root);yield break;}
        if(!File.Exists(Path.Combine(root,"final-resume.txt")))
        {
            UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});
            yield return until(()=>AdvSettings.Active!=null&&Idle(),15,"five page settings opens");
            var settings=AdvSettings.Active;
            var tabs=settings.GetComponentsInChildren<Button>().Where(b=>b.name.StartsWith("Settings.Tab")).Select(b=>b.GetComponent<RectTransform>()).OrderBy(r=>r.anchoredPosition.x).ToArray();
            check(tabs.Length==5&&tabs.All(r=>Mathf.Abs(r.sizeDelta.x-209.6f)<.1f&&r.sizeDelta.y==64),"five equal 209.6 by 64 tabs");
            check(Mathf.Abs(tabs[4].anchoredPosition.x+tabs[4].sizeDelta.x-tabs[0].anchoredPosition.x-1176)<.1f&&Enumerable.Range(1,4).All(i=>Mathf.Abs(tabs[i].anchoredPosition.x-tabs[i-1].anchoredPosition.x-tabs[i-1].sizeDelta.x-32)<.1f),"five tabs retain total span and equal gaps");
            foreach(int id in Enumerable.Range(0,5)){Click("Settings.Tab"+id,check);yield return until(Idle,8,"settings tab transition "+id);check(settings.Tab==id,"settings tab selects "+id);yield return Shot(root,"settings-"+id);}
            check(settings.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Settings.Confirm.")&&!b.name.Contains("All"))==ConfirmationOptions.Items.Length*2,"confirmation page exposes all ON OFF pairs");
            settings.ShowTab(3);float previous=adv.Preferences.TextSpeed.Value;
            Slider("TextSpeed").value=43;Slider("AutoSpeed").value=77;
            var watch=System.Diagnostics.Stopwatch.StartNew();for(int i=0;i<25;i++)settings.ShowTab(i%5);watch.Stop();settings.ShowTab(3);
            check(settings.BuiltPages==5&&Slider("TextSpeed").value==43,"cached five pages preserve mod draft");
            File.WriteAllText(Path.Combine(root,"results/final-settings-timing.txt"),"cached_25_tabs_ms="+watch.Elapsed.TotalMilliseconds);
            Click("Settings.Cancel",check);yield return until(()=>AdvSettings.Active==null&&Idle(),12,"settings cancel closes");check(adv.Preferences.TextSpeed.Value==previous,"cancel leaves mod preference unchanged");
            UIMgr.OpenView<SettingView>(UILayerType.Tips,null,new object[]{true});yield return until(()=>AdvSettings.Active!=null&&Idle(),15,"settings cached reopen");
            check(AdvSettings.Active==settings,"settings reuses cached hierarchy");settings.ShowTab(3);Slider("TextSpeed").value=43;
            Click("Settings.Apply",check);yield return until(()=>AdvSettings.Active==null&&Idle(),12,"settings apply closes");check(adv.Preferences.TextSpeed.Value==43&&!adapter.IsPausedForMenu,"mod apply persists and releases pause");adv.Preferences.TextSpeed.Value=previous;adv.ApplyPreferences();
            yield return NativeAndVisual(ui,service,check,until,root);
            yield return RuntimeModalEightQA.Run(adapter,ui,service,check,until,root);
        }
        yield return CgVariants(adapter,check,until,root);
        var restore=adapter.RestoreAsync(baseline);yield return until(()=>restore.IsCompleted,30,"final restore normal dialogue");if(restore.IsFaulted)throw restore.Exception;
        // A real click schedules confirmation work at the end of its exit animation.
        // Unloading during that interval must revoke it, including queued dialogs.
        ConfirmationOptions.Entry(adv.Configuration,"Action").Value=true;
        int yes=0,no=0;AdvConfirmation.Show(TMP_Settings.defaultFontAsset,"Action","检查连续确认窗口不会穿透。",()=>yes++,()=>no++);
        yield return until(Idle,10,"first queued prompt opens");
        AdvConfirmation.Show(TMP_Settings.defaultFontAsset,"Action","第二个确认窗口",()=>yes++,()=>no++);
        Click("ADV.ActionConfirm",check);check(yes==0,"confirmation action waits for closing animation");
        yield return until(()=>Idle()&&AdvConfirmation.IsOpen&&yes==1,12,"queued prompt opens after first finishes");
        AdvConfirmation.Cancel();yield return until(()=>Idle()&&!AdvConfirmation.IsOpen,10,"queued prompt cancels");check(yes==1&&no==1,"queued callbacks execute exactly once");
        yield return RuntimeLifecycleQA.Run(adapter,ui,check,until,root);
        File.WriteAllText(Path.Combine(root,"results/final-batch-success.txt"),"FINAL_BATCH_SETTINGS_NATIVE_ARCHIVE_CG_MODAL_LIFECYCLE_OK");
    }
    static IEnumerator NativeAndVisual(DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        bool done=false,ok=false;Game.ManualSaveGame(990,"QA native default",null,v=>{done=true;ok=v;});yield return until(()=>done,30,"actual native manual fixture");check(ok,"native manual fixture committed");
        string manual=SaveMgr.GetPref("LatestSaveKey","");done=false;ok=false;Game.AutoSaveGame(v=>{done=true;ok=v;});yield return until(()=>done,30,"actual native automatic fixture");check(ok,"native automatic fixture committed");
        ui.Open(false);yield return until(()=>AdvSavePage.Active!=null&&Idle()&&!service.IsListing,20,"final archive opens");var page=AdvSavePage.Active;
        check(page.transform.Find("Archive paper soft shadow").GetComponent<Graphic>().raycastTarget==false,"soft outer shadow does not intercept controls");
        var ticket=page.transform.Find("Archive.Return").GetComponent<Image>();check(ticket.type==Image.Type.Sliced,"return ticket preserves plane end cap proportions");
        var art=ticket.transform.Find("Ticket lettering area").GetChild(0).GetComponent<RectTransform>();check(Mathf.Abs(art.anchoredPosition.x-((RectTransform)art.parent).rect.width/2+130-8)<.2f,"return lettering retains eight unit optical adjustment");
        foreach(int mode in new[]{1,0,2,3})
        {
            Click("Archive.Tab"+mode,check);yield return until(Idle,10,"archive theme "+mode);yield return new WaitForSecondsRealtime(.5f);
            check(page.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Archive.Slot"))==12,"archive always displays twelve whole slots "+mode);
            yield return Shot(root,"archive-theme-"+mode);
        }
        var native=(NativeArchiveBrowser)Field(page,"native");
        yield return until(()=>native.Slots.Values.Any(d=>d.fileName==manual&&d.previewState==2),20,"native manual header ready");
        var record=native.Record(native.Slots.Values.Single(d=>d.fileName==manual));
        string caption=(string)AccessTools.Method(typeof(AdvSavePage),"Caption").Invoke(page,new object[]{record});
        check(!string.IsNullOrEmpty(record.RoleName)&&!string.IsNullOrEmpty(record.Location)&&caption.Contains(record.RoleName)&&caption.Contains(record.Location)&&caption.Contains("<color=#"),"native default shows named protagonist location and seasonal color");
        foreach(int season in new[]{1,2,3,4}){record.SeasonId=season;record.SeasonLabel=new[]{"春","夏","秋","冬"}[season-1];check(((string)AccessTools.Method(typeof(AdvSavePage),"Caption").Invoke(page,new object[]{record})).Contains(record.SeasonLabel),"native default season label "+season);}
        record.Comment="手写备注 <不解析>";check((string)AccessTools.Method(typeof(AdvSavePage),"Caption").Invoke(page,new object[]{record})==record.Comment,"handwritten native note takes precedence");
        Click("Archive.Tool3",check);Click("Archive.Tool0",check);yield return null;
        var positions=(Dictionary<int,int>)Field(page,"nativePositions");check(positions.Count>0&&positions.Values.All(i=>native.Slots[i].isAuto)&&(int)Field(page,"operation")==3,"nonempty automatic list coexists with delete mode");
        Click("Archive.Tool0",check);yield return null;positions=(Dictionary<int,int>)Field(page,"nativePositions");check(positions.Count>0&&positions.Values.All(i=>native.Slots[i].isManual),"nonempty manual list excludes automatic and quick saves");
        Click("Archive.Tab1",check);yield return until(Idle,10,"load theme restored");
        var e=new PointerEventData(EventSystem.current){scrollDelta=new Vector2(0,-24)};int before=(int)Field(page,"page");page.OnScroll(e);yield return null;
        check((int)Field(page,"page")==before+1&&page.GetComponentsInChildren<Button>().Count(b=>b.name.StartsWith("Archive.Slot"))==12,"large wheel delta changes exactly one complete page");
        int h=AdvSkin.PlayedHovers,c=AdvSkin.PlayedClicks;var detail=page.transform.Find("Archive.Detail");
        if(detail!=null){ExecuteEvents.Execute(detail.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.pointerEnterHandler);Click(detail.name,check);check(AdvSkin.PlayedHovers>h&&AdvSkin.PlayedClicks>c,"archive hover and click both dispatch sounds");}
        Screen.SetResolution(1280,720,false);yield return new WaitForSecondsRealtime(.5f);yield return Shot(root,"archive-1280");
        Screen.SetResolution(1920,1080,false);yield return new WaitForSecondsRealtime(.5f);
        Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&Idle(),10,"final archive closes");
    }
    static IEnumerator RollbackUnload(DialogueCheckpointAdapter adapter,DialogueUiController ui,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var adv=AdvDialogueController.Active;yield return new WaitForSecondsRealtime(.3f);adv.OpenHistory();yield return until(()=>Idle()&&adv.ModalOpen,10,"rollback unload backlog opens");
        ConfirmationOptions.Entry(adv.Configuration,"Jump").Value=true;
        AccessTools.Method(typeof(AdvDialogueController),"RequestRollback").Invoke(adv,new object[]{0});yield return until(Idle,10,"rollback unload prompt opens");
        check(Field(adv,"rollbackPrompt")!=null,"real rollback confirmation exists");int called=0;
        AccessTools.Method(typeof(AdvDialogueController),"CloseRollbackPromptThen").Invoke(adv,new object[]{(Action)(()=>called++)});
        check(AdvSettingsTransition.Active!=null&&called==0,"rollback callback is deferred during exit");
        UnityEngine.Object.Destroy(Resources.FindObjectsOfTypeAll<DialogueRuntimeHost>().Single().gameObject);yield return null;yield return null;yield return new WaitForSecondsRealtime(.5f);
        check(called==0,"unloading cancels pending rollback completion");check(AdvDialogueController.Active==null&&AdvSettingsTransition.Active==null,"rollback unload releases owner and animation");
        check(!Resources.FindObjectsOfTypeAll<Canvas>().Any(c=>c.gameObject.activeInHierarchy&&c.name.StartsWith("DialogueSave.")),"rollback unload leaves no modal or canvas");
        File.WriteAllText(Path.Combine(root,"results/final-unload-success.txt"),"ROLLBACK_EXIT_CALLBACK_REVOKED_ON_UNLOAD_OK");
    }
    static IEnumerator Supplement(DialogueCheckpointAdapter adapter,DialogueUiController ui,DialogueSaveService service,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var map=Singleton<FuncMgr>.Ins.GetMapData();int before=map.recordMapId;
        int target=Cfg.MapCfgMap.Values.First(m=>m.id!=1&&m.bg>0&&Cfg.BgCfgMap.ContainsKey(m.bg)).id;
        map.recordMapId=target;bool done=false,ok=false;
        Game.ManualSaveGame(991,"QA location",null,v=>{done=true;ok=v;});yield return until(()=>done,30,"nonhome native save created");map.recordMapId=before;check(ok,"nonhome native save commits");string file=SaveMgr.GetPref("LatestSaveKey","");
        ui.Open(false);yield return until(()=>AdvSavePage.Active!=null&&Idle()&&!service.IsListing,20,"supplement archive opens");Click("Archive.Tab3",check);yield return until(Idle,10,"native supplemental list opens");
        var page=AdvSavePage.Active;var native=(NativeArchiveBrowser)Field(page,"native");
        yield return until(()=>native.Slots.Values.Any(d=>d.fileName==file&&d.previewState==2),20,"nonhome header loaded");
        var positions=(Dictionary<int,int>)Field(page,"nativePositions");int display=positions.Single(p=>native.Slots[p.Value].fileName==file).Key;
        AccessTools.Method(typeof(AdvSavePage),"SetPage").Invoke(page,new object[]{(display-1)/12+1});yield return null;
        var record=native.Record(native.Slots.Values.Single(d=>d.fileName==file));check(record.Location==Cfg.MapCfgMap[target].name&&record.BackgroundId==Cfg.MapCfgMap[target].bg,"native archive uses saved nonhome location and background");
        var photo=page.transform.Find("Archive cards/Archive.Slot"+display+"/Saved background").GetComponent<Image>();yield return until(()=>photo.sprite!=null,15,"nonhome thumbnail rendered");check((string)Field(photo.GetComponent<ArchivePreview>(),"current")==Cfg.BgCfgMap[record.BackgroundId].GetBgUrl(record.GradeState),"nonhome card loads its corresponding resource");
        AccessTools.Method(typeof(AdvSavePage),"ShowDetail").Invoke(page,new object[]{display,record});yield return Shot(root,"native-location");
        Click("Archive.Tool2",check);yield return null;Click("Archive.Slot"+display,check);yield return until(Idle,10,"supplement note editor opens");
        var input=Resources.FindObjectsOfTypeAll<TMP_InputField>().Single(f=>f.gameObject.activeInHierarchy);input.text="中文备注：放学后的约定。\n第二行保留内容。";input.caretPosition=input.text.Length;yield return null;
        input.selectionStringAnchorPosition=3;input.selectionStringFocusPosition=7;input.ForceLabelUpdate();yield return Shot(root,"note-selection");check(input.selectionStringAnchorPosition!=input.selectionStringFocusPosition,"note supports selecting Chinese text across caret positions");
        var text=input.text;Click("Note.Apply",check);yield return until(()=>!page.EditorOpen&&Idle()&&!(bool)Field(page,"busy"),20,"Chinese multiline note commits");
        check(native.Note(native.Slots.Values.Single(d=>d.fileName==file))==text,"native handwritten multiline note roundtrips");
        Click("Archive.Return",check);yield return until(()=>AdvSavePage.Active==null&&Idle(),10,"supplement archive closes");
        var adv=AdvDialogueController.Active;ConfirmationOptions.Entry(adv.Configuration,"Action").Value=true;
        AdvConfirmation.Show(TMP_Settings.defaultFontAsset,"Action","读取这个存档会离开当前进度，是否继续？",()=>{});
        yield return new WaitForSecondsRealtime(.11f);yield return Shot(root,"confirmation-opening-midpoint");yield return until(Idle,10,"confirmation animation finishes");
        AdvConfirmation.Cancel();yield return new WaitForSecondsRealtime(.11f);yield return Shot(root,"confirmation-closing-midpoint");yield return until(()=>Idle()&&!AdvConfirmation.IsOpen,10,"confirmation close completes");
        yield return CgVariants(adapter,check,until,root);
        File.WriteAllText(Path.Combine(root,"results/final-extra-success.txt"),"NONHOME_NATIVE_NOTE_ANIMATION_ORIGINAL_CG_GEOMETRY_OK");
    }
    static IEnumerator CgVariants(DialogueCheckpointAdapter adapter,Action<bool,string> check,Func<Func<bool>,float,string,IEnumerator> until,string root)
    {
        var baseline=adapter.Capture();var role=Singleton<RoleMgr>.Ins.GetRole();var sex=AccessTools.Property(role.GetType(),"Sex");var original=role.Sex;
        var originalCfg=(TalkCfg)Field(UIMgr.GetView<NewTalkView>(),"cfg");var oldEffect=originalCfg.screenEffect;
        var cgIds=Cfg.CGCfgMap.Keys.Where(id=>id>0&&!(Cfg.CGCfgMap[id].comic?.Count>0)).OrderByDescending(id=>Cfg.CGCfgMap[id].urls.Count).Take(2).ToArray();
        try
        {
            for(int i=0;i<2;i++)
            {
                sex.SetValue(Singleton<RoleMgr>.Ins.GetRole(),i==0?GenderDefine.Female:GenderDefine.Male,null);
                var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();var cfg=(TalkCfg)Field(talk,"cfg");cfg.screenEffect=new List<float>{i==0?4019:4015,cgIds[i%cgIds.Length]};bool opened=false;
                string line="这是逐字显示中保存的 CG 内容，读取后继续当前句子，不跳到下一句。";
                AccessTools.Method(typeof(NewTalkView),i==0?"ShowMiniCG":"ShowCG").Invoke(talk,new object[]{cgIds[i%cgIds.Length],(Action)(()=>{opened=true;talk.talk=line;talk.tmpTalks=new List<string>{line};talk.tmpTalkIdx=0;talk.DoText(line);})});
                yield return until(()=>opened&&talk.talkState==TalkState.Anim&&(int)Field(talk,"waitFrame")==0,25,"CG variant typing "+i);
                var lease=adapter.PauseForMenu();
                yield return until(()=>adapter.CanCapture(out _),10,"paused CG variant capturable "+i);
                var snapshot=adapter.Capture();lease.Dispose();check((bool)snapshot.Dialogue["cg"]["mini"]==(i==0),"CG full or mini identity captured "+i);
                string url=(string)snapshot.Dialogue["cg"]["url"];var restore=adapter.RestoreAsync(snapshot);yield return until(()=>restore.IsCompleted,30,"CG variant restore "+i);if(restore.IsFaulted)throw restore.Exception;
                talk=(NewTalkView)UIMgr.GetView<NewTalkView>();yield return until(()=>talk.talkState==TalkState.AnimEnd&&adapter.CanCapture(out _),20,"restored CG finishes same sentence "+i);
                check((string)adapter.Capture().Dialogue["cg"]["url"]==url&&talk.tmpTalkIdx==0,"CG variant resource and current line preserved "+i);
                var name=(TextMeshProUGUI)Field(AdvDialogueController.Active,"nameLabel");check((name.fontStyle&FontStyles.Bold)==0,"CG name and brackets use normal weight");
                yield return Shot(root,"cg-variant-"+i);
                var panel=(CGView)Field(talk,"cgPanel");var picture=i==0?panel.icon_cg_mini:panel.icon_cg;
                var rect=picture.transform as RectTransform;var before=new Vector3[4];var after=new Vector3[4];rect.GetWorldCorners(before);
                var adv=AdvDialogueController.Active;adv.SelectMode("Original");yield return null;yield return null;
                rect.GetWorldCorners(after);check(Enumerable.Range(0,4).All(n=>Vector3.Distance(before[n],after[n])<2),"CG image geometry matches original mode "+i);
                if(i==0)yield return Shot(root,"mini-original-comparison");adv.SelectMode("ADV");yield return null;
            }
        }
        finally{sex.SetValue(Singleton<RoleMgr>.Ins.GetRole(),original,null);originalCfg.screenEffect=oldEffect;}
        var normal=adapter.RestoreAsync(baseline);yield return until(()=>normal.IsCompleted,30,"CG variants restore original state");if(normal.IsFaulted)throw normal.Exception;
    }
}
