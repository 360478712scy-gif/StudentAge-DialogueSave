using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Configuration;
using StudentAgeDialogueSave.UI;
static class Program
{
    static int count;
    static void Check(bool value,string label){if(!value)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
    static void Main()
    {
        string file=Path.Combine(AppContext.BaseDirectory,"fixture-"+Guid.NewGuid().ToString("N")+".cfg");
        var config=new ConfigFile(file,false);
        var snapshot=ConfirmationOptions.Snapshot(config);
        Check(snapshot.Count==14 && snapshot.Values.All(v=>v),"all real operation groups default ON");
        snapshot["Load"]=false;Check(ConfirmationOptions.Enabled(config,"Load"),"editing or cancelling a draft cannot suppress a live confirmation");
        ConfirmationOptions.Apply(config,new Dictionary<string,bool>{{"Load",false}},false);
        Check(!ConfirmationOptions.Enabled(config,"Load") && ConfirmationOptions.Enabled(config,"Delete"),"only selected operation is disabled");
        ConfirmationOptions.Entry(config,"恢复默认").Value=false;
        ConfirmationOptions.Apply(config,new Dictionary<string,bool>{{"Load",true}},false);
        Check(!ConfirmationOptions.Enabled(config,"恢复默认"),"applying unrelated settings preserves popup remember choice");
        ConfirmationOptions.Entry(config,"Native_previously_seen").Value=false;
        config.Bind("Confirmations","Native_unseen_after_restart",false);config.Save();
        ConfirmationOptions.Apply(config,ConfirmationOptions.Items.ToDictionary(x=>x.Key,x=>true),true);
        var reopened=new ConfigFile(file,false);
        Check(ConfirmationOptions.Enabled(reopened,"Native_previously_seen") && ConfirmationOptions.Enabled(reopened,"Native_unseen_after_restart"),"all ON restores stored native opt-outs including unbound entries after restart");
        ConfirmationOptions.Apply(reopened,ConfirmationOptions.Items.ToDictionary(x=>x.Key,x=>false),true);
        var off=new ConfigFile(file,false);
        Check(ConfirmationOptions.Items.All(i=>!ConfirmationOptions.Enabled(off,i.Key)) && !ConfirmationOptions.Enabled(off,"Native_new_future_prompt"),"all OFF persists for all groups and future native prompts");
        ConfirmationOptions.Apply(off,new Dictionary<string,bool>{{"Jump",true},{"Skip",true}},false);
        Check(off.Bind("Interface","ConfirmHistoryJump",false).Value && off.Bind("Interface","ConfirmStorySkip",false).Value,"rollback and skip reuse original entries");
        Func<string,float> measure=t=>new System.Globalization.StringInfo(t).LengthInTextElements;
        string shortText="保存当前进度吗？";Check(ConfirmationText.Wrap(shortText,18,measure)==shortText,"short message stays on one line");
        string source="读取这个对话存档将会离开当前进度，是否继续？";
        string wrapped=ConfirmationText.Wrap(source,18,measure);var lines=wrapped.Split('\n');
        Check(wrapped.Replace("\n","")==source && lines.All(l=>measure(l)<=18) && lines.Min(l=>measure(l))>3,"long message wraps without lost text or orphan final character");
        string emojis=string.Concat(Enumerable.Repeat("春🌸夏🍉秋🍂冬❄️",5));var result=ConfirmationText.Wrap(emojis,18,measure);
        Check(result.Replace("\n","")==emojis && result.Split('\n').All(l=>!char.IsLowSurrogate(l[0]) && !char.IsHighSurrogate(l[l.Length-1])),"emoji and combined characters remain intact");
        Check(ConfirmationText.Wrap("执行确认\n保存吗？",18,measure)=="执行确认\n保存吗？","explicit paragraph boundaries survive");
        File.Delete(file);Console.WriteLine("CONFIRMATION_CHECK_OK "+count);
    }
}
