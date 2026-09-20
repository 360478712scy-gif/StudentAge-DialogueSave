using System;
using System.IO;
using System.Linq;
using System.Collections;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Evt;
using View.Main;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.Storage;

public static class RuntimeRecoveryQA
{
    public static IEnumerator Run(DialogueCheckpointAdapter adapter, DialogueUiController ui, IDialogueUiService service,
        Action<bool,string> check, Func<Func<bool>,float,string,IEnumerator> until, string root)
    {
        yield return new WaitForSecondsRealtime(1);
        var notice=UIMgr.GetTopView(ViewType.Guide,ViewType.Side);
        if(notice!=null && notice.GetType().Name=="CommonComfirmView") UIMgr.CloseView(notice);
        yield return until(()=>adapter.CanCapture(out _),25,"recovery source ready");
        var repo=new Repository(PathDefine.SAVE_PATH,Path.Combine(root,"data/recovery-stage"),Path.Combine(root,"data/recovery-backup"));
        var old=repo.Scan().Where(r=>r.Status==SaveStatus.Ready).OrderByDescending(r=>r.Header.CreatedUtc,StringComparer.Ordinal)
            .Select(r=>new {Record=r,Save=repo.Load(r.Header.RevisionId)})
            .First(x=>x.Save.Dialogue.Value<int>("talkId")==1900000001 &&
                x.Save.Dialogue.Value<string>("configDigest")=="712AC7722CA6A34190D398BEA9DD531E403E897B1C95C49E9033FECA84617920" &&
                x.Save.Dialogue["activeMods"] is JArray mods && mods.Count==0);
        string hash=Hash(old.Record.FilePath), latest=SaveMgr.GetPref("LatestSaveKey","");
        var timing=new JObject{["oldRevision"]=old.Record.Header.RevisionId,["oldHashBefore"]=hash,["oldPlugins"]=old.Save.Dialogue["plugins"]};
        var reads=new JArray();timing["reads"]=reads;
        File.WriteAllText(Path.Combine(root,"results/recovery-timing.json"),timing.ToString());
        for(int i=0;i<2;i++)
        {
            UiResult result=null;
            bool inject=i==0;
            var watch=System.Diagnostics.Stopwatch.StartNew();
            service.Load(old.Record.Header.RevisionId,r=>{
                if(!r.IsPending){result=r;watch.Stop();}
                if(inject)throw new InvalidOperationException("Intentional QA pending/completion callback failure");
            });
            yield return until(()=>result!=null,60,"old archive actual load completes "+i);
            check(result.Success,"old archive actual load succeeds "+i+": "+result.Message);
            var talk=(NewTalkView)UIMgr.GetView<NewTalkView>();
            check(talk.tmpTalkIdx==old.Save.Dialogue.Value<int>("segmentIndex") &&
                ((Config.TalkCfg)AccessTools.Field(typeof(NewTalkView),"cfg").GetValue(talk)).id==1900000001,
                "old archive restores its exact node and segment");
            check(adapter.CurrentCheckpointBrief.RunId==old.Save.Header.RunId,"old archive restores its own world");
            check(Hash(old.Record.FilePath)==hash,"old archive bytes untouched");
            check(SaveMgr.GetPref("LatestSaveKey","")==latest,"ordinary save selection unchanged");
            reads.Add(new JObject{["attempt"]=i+1,["milliseconds"]=watch.ElapsedMilliseconds});
        }
        check(!adapter.IsPausedForMenu,"callback exceptions do not leak pause lease");
        // A real quick-save goes through the service's capture and atomic publication.
        var before=repo.Scan().Select(r=>r.Header?.RevisionId).ToArray();
        var saveClock=System.Diagnostics.Stopwatch.StartNew();
        service.QuickSave();
        yield return until(()=>service.List(DialogueUiCategory.Quick).Any(r=>!before.Contains(r.RevisionId)),40,"warm quick save commits");
        saveClock.Stop();
        var created=service.List(DialogueUiCategory.Quick).First(r=>!before.Contains(r.RevisionId));
        var saved=repo.Load(created.RevisionId);
        check(saved.World.Length>0 && saved.Dialogue.Value<int>("talkId")==1900000001,"warm save contains full verified world and dialogue");
        timing["warmQuickSaveMs"]=saveClock.ElapsedMilliseconds;
        // Loading while a new capture is pending must abandon it rather than reject the read.
        service.QuickSave();
        UiResult concurrent=null;
        service.Load(old.Record.Header.RevisionId,r=>{if(!r.IsPending)concurrent=r;});
        yield return until(()=>concurrent!=null,60,"load during save request completes");
        check(concurrent.Success,"load during save request succeeds");
        check(Hash(old.Record.FilePath)==hash,"concurrent operation preserves old archive");
        check(SaveMgr.GetPref("LatestSaveKey","")==latest,"all dialogue operations preserve ordinary save selection");
        timing["oldHashAfter"]=Hash(old.Record.FilePath);
        timing["callbackFailuresRecovered"]=true;
        timing["loadDuringSaveSucceeded"]=true;
        File.WriteAllText(Path.Combine(root,"results/recovery-timing.json"),timing.ToString());
        yield return new WaitForSecondsRealtime(1);
        RuntimeFontQA.Capture(Path.Combine(root,"results"));
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(root,"results/recovery-final.png"));
    }
    static string Hash(string path)
    {
        using(var sha=System.Security.Cryptography.SHA256.Create())
        using(var f=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","");
    }
}
