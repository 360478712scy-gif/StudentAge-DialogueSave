using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Main;
using StudentAgeDialogueSave.UI;

// Native saves keep their card number: new autosaves and repeated overwrites never move them.
internal static class RuntimeNativeSlotsQA
{
    public static IEnumerator Run(Action<bool,string> check, Func<Func<bool>,float,string,IEnumerator> until, string root)
    {
        string dir=Path.GetFullPath(PathDefine.SAVE_PATH);
        UIMgr.OpenView<SaveView>(UILayerType.None,null,new object[]{true});
        yield return until(()=>AdvSavePage.Active!=null && AdvSettingsTransition.Active==null,20,"native slots archive open");
        var page=AdvSavePage.Active;var t=typeof(AdvSavePage);
        AccessTools.Field(t,"mode").SetValue(page,3);AccessTools.Field(t,"nativeAutomatic").SetValue(page,false);
        var refresh=AccessTools.Method(t,"RefreshRecords");refresh.Invoke(page,null);
        var slots=(Dictionary<int,DialogueUiRecord>)AccessTools.Field(t,"slots").GetValue(page);
        Func<Dictionary<int,string>> snapshot=()=>slots.ToDictionary(p=>p.Key,p=>p.Value.RevisionId);
        var before=snapshot();check(before.Count>0,"isolated fixture has manual native saves ("+before.Count+")");
        string log="before: "+string.Join(", ",before.Select(p=>p.Key+"="+p.Value))+"\n";
        // A new autosave (a later round) must not renumber manual saves.
        var auto=Directory.GetFiles(dir,"*.autosave").OrderBy(f=>f).First();string extra=Path.Combine(dir,Path.GetFileName(auto).Replace(".autosave",".7.autosave"));
        var name=Path.GetFileName(auto);int dot=name.LastIndexOf('.',name.Length-10);string fake=Path.Combine(dir,name.Substring(0,dot+1)+"99.autosave");
        File.Copy(auto,fake,true);
        refresh.Invoke(page,null);var afterAuto=snapshot();
        check(before.All(p=>afterAuto.TryGetValue(p.Key,out var f) && f==p.Value) && afterAuto.Count==before.Count,"new autosave leaves every manual card in place");
        File.Delete(fake);refresh.Invoke(page,null);
        // Overwrite the same card three times.
        int card=before.Keys.First();var save=AccessTools.Method(t,"Save");
        for(int i=0;i<3;i++)
        {
            string old=slots[card].RevisionId;
            save.Invoke(page,new object[]{card,slots[card]});
            yield return until(()=>!(bool)AccessTools.Field(t,"busy").GetValue(page),30,"overwrite "+i+" finished");
            refresh.Invoke(page,null);var now=snapshot();
            log+="overwrite "+i+": "+string.Join(", ",now.Select(p=>p.Key+"="+p.Value))+"\n";
            check(now.TryGetValue(card,out var file) && file!=old,"overwrite "+i+" stays on card "+card);
            check(now.Count==before.Count && before.Keys.Where(k=>k!=card).All(k=>now.TryGetValue(k,out var f) && f==before[k]),"overwrite "+i+" moves no other card");
            yield return new WaitForSecondsRealtime(1.1f);
        }
        File.WriteAllText(Path.Combine(root,"results/native-slots.txt"),log);
    }
}
