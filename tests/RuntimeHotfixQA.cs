using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Components;
using Config;
using GenUI.Talk;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.Storage;
using StudentAgeDialogueSave.UI;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Main;

public static class RuntimeHotfixQA
{
    static NewTalkView Talk => (NewTalkView)UIMgr.GetView<NewTalkView>(false);
    static bool failPublish;
    static bool PublishFailure(){if(failPublish)throw new IOException("QA injected disk failure");return true;}
    public static IEnumerator Run(DialogueCheckpointAdapter adapter, DialogueUiController ui, IDialogueUiService api,
        Action<bool,string> check, Func<Func<bool>,float,string,IEnumerator> until, string root)
    {
        var service=(DialogueSaveService)api;
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        if(notice!=null && notice.GetType().Name=="CommonComfirmView")UIMgr.CloseView(notice);
        yield return until(()=>adapter.CanCapture(out _),25,"hotfix start at stable dialogue");
        var take=adapter.CaptureAsync();yield return until(()=>take.IsCompleted,40,"hotfix capture");
        var snapshot=take.GetAwaiter().GetResult();
        string savedLine=snapshot.Dialogue.Value<string>("talk");
        var cfg=Cfg.TalkCfgMap[snapshot.Dialogue.Value<int>("talkId")];
        string before=cfg.content;cfg.content+=" updated mod text";
        snapshot.Dialogue["configDigest"]=new string('A',64);
        snapshot.Dialogue["gameModule"]="old-build";
        snapshot.Dialogue["activeMods"]=new JArray(999UL);
        foreach(JObject plugin in snapshot.Dialogue["plugins"]){plugin["version"]="0.0.1";plugin["sha256"]="old-build";}
        var restore=adapter.RestoreAsync(snapshot);
        yield return until(()=>restore.IsCompleted,40,"restore after configuration and plugin update");
        restore.GetAwaiter().GetResult();
        check(Talk.talk==savedLine,"updated mod restores saved text without version/hash gate");cfg.content=before;
        var missing=new GameCheckpoint{WorldBytes=snapshot.WorldBytes,Brief=snapshot.Brief,Dialogue=(JObject)snapshot.Dialogue.DeepClone()};
        missing.Dialogue["talkId"]=int.MaxValue;
        var existing=Talk;var invalid=adapter.RestoreAsync(missing);
        yield return until(()=>invalid.IsCompleted,10,"missing-node preflight");
        check(invalid.IsFaulted && Talk==existing,"genuinely missing node still fails before discarding current world");
        var observed=invalid.Exception;

        var repo=new Repository(PathDefine.SAVE_PATH,Path.Combine(root,"data/hotfix-stage"),Path.Combine(root,"data/hotfix-backup"));
        var old=repo.Scan().Where(r=>r.Status==SaveStatus.Ready).OrderByDescending(r=>r.Header.CreatedUtc)
            .FirstOrDefault(r=>repo.Load(r.Header.RevisionId).Dialogue.Value<int>("talkId")>=310107000 && repo.Load(r.Header.RevisionId).Dialogue.Value<int>("talkId")<310108000);
        if(old!=null)
        {
            byte[] oldBytes=File.ReadAllBytes(old.FilePath);
            UiResult loaded=null;api.Load(old.Header.RevisionId,r=>{if(!r.IsPending)loaded=r;});
            yield return until(()=>loaded!=null,45,"load preexisting screenshot-scene archive");
            check(loaded.Success,"preexisting archive loads after mod compatibility fix: "+loaded.Message);
            check(Enumerable.SequenceEqual(oldBytes,File.ReadAllBytes(old.FilePath)),"old archive remains byte-for-byte unchanged");
        }
        // Exercise actual save cards and the native paged description callback.
        ui.Open(true);yield return until(()=>UIMgr.IsViewOpened<SaveView>() && !api.IsListing && !api.IsPreparingSave,40,"save card tooltip page ready");
        var descriptions=UIMgr.GetView<SaveView>().gameObject.GetComponentsInChildren<Description>(true)
            .Where(d=>d.getDescription!=null && d.getDescription(0).HasValue).ToArray();
        check(descriptions.Length>0,"populated save card has a readable tooltip");
        foreach(var description in descriptions)check(!description.getDescription(1).HasValue,"tooltip stops after the first page");
        UIMgr.CloseView<SaveView>();yield return null;

        // Actual screenshot story contains comic presentation commands.
        var comicTalk=Cfg.TalkCfgMap.Values.First(c=>c.id>=310107000 && c.id<310108000 && c.screenEffect?.Count>=3 && c.screenEffect[0]==4016);
        int comic=(int)comicTalk.screenEffect[1];
        int pages=Cfg.CGCfgMap[comic].comic.Count;
        File.WriteAllText(Path.Combine(root,"results/comic-target.json"),JObject.FromObject(comicTalk).ToString());
        for(int n=0;n<4;n++)
        {
            int page=n==3?1:Math.Min(n+1,pages);
            var view=Talk;
            var pageTalk=Cfg.TalkCfgMap.Values.First(c=>c.id>=310107000 && c.id<310108000 && c.screenEffect?.Count>=3 && c.screenEffect[0]==4016 && (int)c.screenEffect[1]==comic && (int)c.screenEffect[2]==page);
            yield return until(()=>adapter.CanAdvancePresentation(view),15,"native comic transition ready");
            view.RefreshTalk(pageTalk.id);
            yield return until(()=>AccessTools.Field(typeof(NewTalkView),"comicPanel").GetValue(view) is ComicView panelReady && panelReady.isViewReady && (int)AccessTools.Field(typeof(ComicView),"page").GetValue(panelReady)==page,20,"native story comic opens/reopens "+n);
            yield return new WaitForSecondsRealtime(2);
            var panel=(ComicView)AccessTools.Field(typeof(NewTalkView),"comicPanel").GetValue(view);
            yield return until(()=>panel.gameObject.GetComponentsInChildren<Image>(true).Count(i=>i.name=="icon_item" && i.gameObject.activeInHierarchy && i.enabled && i.sprite!=null && i.color.a>=.99f)>=Cfg.CGCfgMap[comic].comic.Take(page).Sum(),20,"all comic panels finish loading and fading "+n);
            check((int)panel.parms[0]==comic && (int)panel.parms[1]==page,"reused comic retains current page parameters");
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/hotfix-comic-"+n+".png"));
            File.WriteAllText(Path.Combine(root,"results/comic-images-"+n+".json"),JArray.FromObject(panel.gameObject.GetComponentsInChildren<Image>(true).Select(i=>new {name=i.name,active=i.gameObject.activeInHierarchy,enabled=i.enabled,sprite=i.sprite==null?null:i.sprite.name,texture=i.sprite==null?null:i.sprite.texture.name,sibling=i.transform.parent.GetSiblingIndex(),position=i.rectTransform.anchoredPosition.ToString(),size=i.rectTransform.rect.size.ToString(),alpha=i.color.a,depth=i.depth,cull=i.canvasRenderer.cull,rendererAlpha=i.canvasRenderer.GetAlpha(),material=i.materialForRendering.name,overrideTexture=i.overrideSprite==null?null:i.overrideSprite.texture.name,ancestors=string.Join(" > ",i.GetComponentsInParent<Transform>(true).Select(t=>t.name+"["+t.GetSiblingIndex()+"]")),groups=string.Join(",",i.GetComponentsInParent<CanvasGroup>(true).Select(g=>g.name+":"+g.alpha))})).ToString());
            var pictures=panel.gameObject.GetComponentsInChildren<Image>(true).Where(i=>i.name=="icon_item" && i.gameObject.activeInHierarchy && i.enabled && i.sprite!=null).ToArray();
            check(pictures.Length>0,"comic illustrations visibly loaded on pass "+n);
            check(panel.txtex_talk.GetComponent<ContentSizeFitter>().enabled && panel.txtex_talk.transform.parent.name!="Reading","comic retains native font container and size fitter");
            check(!Resources.FindObjectsOfTypeAll<Canvas>().Any(c=>c.name=="DialogueSave.ADV" && c.gameObject.activeInHierarchy),"comic has no ADV overlay or controls");
            check(!Resources.FindObjectsOfTypeAll<Button>().Any(b=>b.name.StartsWith("DialogueSave.Hotkey") && b.gameObject.activeInHierarchy),"comic has no added native toolbar controls");
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/hotfix-comic-"+n+".png"));
            yield return null;
            var probe=new GameObject("Comic callback probe",typeof(RectTransform));
            var icon=new GameObject("icon_item",typeof(RectTransform),typeof(Image));icon.transform.SetParent(probe.transform,false);
            var cell=new Cell_ComicItemUI(probe);
            AccessTools.Method(typeof(ComicPresentationAdapter),"Recycle").Invoke(null,new object[]{cell});
            ComicPresentationAdapter.Complete(cell,0,pictures[0].sprite);
            check(cell.icon_item.image.sprite==null,"out-of-order callback after recycle is ignored");
            UnityEngine.Object.Destroy(probe);
            if(n>=2)
            {
                AccessTools.Method(typeof(NewTalkView),"HideCGComic").Invoke(view,null);
                yield return new WaitForSecondsRealtime(.65f);
            }
        }
        restore=adapter.RestoreAsync(snapshot);yield return until(()=>restore.IsCompleted,40,"restore after comic checks");restore.GetAwaiter().GetResult();
        yield return until(()=>adapter.CanCapture(out _),20,"history tests start stable");
        yield return RuntimeHistoryRollbackQA.Run(adapter,check,until,root);
        yield return until(()=>adapter.CanCapture(out _),20,"exit save starts stable");
        // Return-to-title must first publish an independent automatic archive.
        global::Game.BackToMain();
        yield return until(()=>global::Game.GetGameState()==GameState.Start && UIMgr.IsViewOpened<EntryView>(),15,"return to title after exit autosave");
        var exit=Repository.FindHeads(repo.Scan()).Where(r=>r.Header.Category=="auto" && r.Header.LogicalSlot=="2").OrderByDescending(r=>r.Header.CreatedUtc).FirstOrDefault();
        check(exit!=null && repo.Load(exit.Header.RevisionId).Dialogue.Value<int>("talkId")==snapshot.Dialogue.Value<int>("talkId"),"return to title committed the exact current dialogue");
        restore=adapter.RestoreAsync(snapshot);yield return until(()=>restore.IsCompleted,40,"restore exit archive world");restore.GetAwaiter().GetResult();
        yield return until(()=>adapter.CanCapture(out _),20,"failed exit test starts stable");
        var fault=new Harmony("dialoguesave.qa.exit-failure");
        fault.Patch(AccessTools.Method(typeof(Repository),"PublishChecked"),prefix:new HarmonyMethod(typeof(RuntimeHotfixQA),nameof(PublishFailure)));
        failPublish=true;
        try
        {
            global::Game.BackToMain();
            yield return until(()=>global::Game.GetGameState()==GameState.Start && UIMgr.IsViewOpened<EntryView>(),15,"injected disk failure does not block return to title");
            check(File.Exists(exit.FilePath),"failed exit preserves prior committed autosave");
        }
        finally{failPublish=false;fault.UnpatchSelf();}
        restore=adapter.RestoreAsync(snapshot);yield return until(()=>restore.IsCompleted,40,"prepare actual application quit in dialogue");restore.GetAwaiter().GetResult();
        yield return until(()=>adapter.CanCapture(out _),20,"actual quit is in a stable dialogue");
        File.WriteAllText(Path.Combine(root,"results/exit-before-quit.json"),JArray.FromObject(repo.Scan().Where(r=>r.Status==SaveStatus.Ready && r.Header.Category=="auto" && r.Header.LogicalSlot=="2").Select(r=>r.Header.RevisionId)).ToString());
        File.WriteAllText(Path.Combine(root,"results/hotfix-complete.txt"),"COMPATIBILITY_TOOLTIP_COMIC_EXIT_PASS");
    }
}
