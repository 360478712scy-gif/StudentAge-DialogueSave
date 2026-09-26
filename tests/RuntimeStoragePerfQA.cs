using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using UnityEngine;
using StudentAgeDialogueSave;
using StudentAgeDialogueSave.GameIntegration;
using StudentAgeDialogueSave.UI;
using StudentAgeDialogueSave.Storage;

// Real-size storage timing: the isolated save folder is seeded (by tools/storage_perf_seed.py)
// with copies of a long-played history. Only the isolated QA folders are read or written.
internal static class RuntimeStoragePerfQA
{
    public static IEnumerator Run(DialogueCheckpointAdapter adapter, DialogueSaveService service,
        Action<bool,string> check, Func<Func<bool>,float,string,IEnumerator> until, string root)
    {
        string output=Path.Combine(root,"results/storage-perf.json");
        var timing=new JObject{["plugin"]=DialogueSavePlugin.Version};
        void Flush()=>File.WriteAllText(output,timing.ToString());
        string saves=Path.GetFullPath(PathDefine.SAVE_PATH);
        timing["filesWhenQaStarts"]=Directory.GetFiles(saves,"dialogue_*.dsav").Length;
        timing["bytesWhenQaStarts"]=Directory.GetFiles(saves,"dialogue_*.dsav").Sum(p=>new FileInfo(p).Length);
        var listing=Stopwatch.StartNew();
        yield return until(()=>!service.IsListing && (bool)AccessTools.Field(typeof(DialogueSaveService),"initialized").GetValue(service),180,"service initial listing");
        timing["waitForServiceListingMs"]=listing.ElapsedMilliseconds;
        Flush();

        // A separate repository over the same folders measures pure listing cost in this runtime.
        var repo=new Repository(saves,Path.Combine(root,"data/perf-stage"),Path.Combine(root,"data/perf-backup"));
        var clock=Stopwatch.StartNew();var rows=repo.Scan();timing["freshScanMs"]=clock.ElapsedMilliseconds;timing["freshScanFiles"]=rows.Count;
        clock.Restart();repo.Scan();timing["repeatScanMs"]=clock.ElapsedMilliseconds;
        Flush();

        var quickConfirm=ConfirmationOptions.Entry(AdvDialogueController.Active.Configuration,"QuickSave");bool previous=quickConfirm.Value;quickConfirm.Value=false;
        var saves3=new JArray();timing["quickSaveMs"]=saves3;string latest=null;
        try
        {
            for(int i=0;i<3;i++)
            {
                yield return until(()=>adapter.CanCapture(out _),25,"capture ready "+i);
                var before=service.List(DialogueUiCategory.Quick).Select(r=>r.RevisionId).ToArray();
                clock.Restart();
                service.QuickSave();
                yield return until(()=>service.List(DialogueUiCategory.Quick).Any(r=>!before.Contains(r.RevisionId)),120,"quick save commits "+i);
                saves3.Add(clock.ElapsedMilliseconds);
                latest=service.List(DialogueUiCategory.Quick).First(r=>!before.Contains(r.RevisionId)).RevisionId;
                Flush();
                yield return new WaitForSecondsRealtime(.5f);
            }
        }
        finally{quickConfirm.Value=previous;}

        var loads=new JArray();timing["loadMs"]=loads;
        for(int i=0;i<2;i++)
        {
            UiResult result=null;clock.Restart();
            service.Load(latest,r=>{if(!r.IsPending)result=r;});
            yield return until(()=>result!=null,120,"load completes "+i);
            loads.Add(clock.ElapsedMilliseconds);
            check(result.Success,"quick save loads "+i+": "+result.Message);
            Flush();
            yield return new WaitForSecondsRealtime(1);
        }
        timing["filesAtEnd"]=Directory.GetFiles(saves,"dialogue_*.dsav").Length;
        timing["bytesAtEnd"]=Directory.GetFiles(saves,"dialogue_*.dsav").Sum(p=>new FileInfo(p).Length);
        Flush();
        check(true,"storage timing recorded");
    }
}
