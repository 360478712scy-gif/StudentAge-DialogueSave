using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace StudentAgeDialogueSave.UI
{
    // The settings page edits a draft of the exact entries used by every prompt.
    internal static class ConfirmationOptions
    {
        internal sealed class Option
        {
            internal readonly string Key,Caption,Section,ConfigKey;
            internal Option(string key,string caption,string section="Confirmations",string configKey=null){Key=key;Caption=caption;Section=section;ConfigKey=configKey??key;}
        }
        internal static readonly Option[] Items={
            new Option("SaveEmpty","保存 执行确认"),new Option("Overwrite","保存覆盖 执行确认"),new Option("QuickSave","快速保存 执行确认"),
            new Option("Load","读取 执行确认"),new Option("QuickLoad","快速读取 执行确认"),new Option("NativeLoad","原版读取 执行确认"),
            new Option("ReturnTitle","返回标题 执行确认"),new Option("Jump","对话回溯 执行确认","Interface","ConfirmHistoryJump"),
            new Option("Skip","跳过剧情 执行确认","Interface","ConfirmStorySkip"),new Option("恢复默认","恢复默认 执行确认"),
            new Option("CopySave","复制存档 执行确认"),new Option("SwapSave","交换存档 执行确认"),new Option("Delete","删除存档 执行确认"),
            new Option("NativeOther","其他游戏操作 执行确认")
        };
        internal static ConfigEntry<bool> Entry(ConfigFile config,string key)
        {
            var item=Items.FirstOrDefault(x=>x.Key==key);
            var entry=config.Bind(item?.Section??"Confirmations",item?.ConfigKey??key,true,"ON显示执行确认；OFF直接执行。与下次不再提示共用。");
            if(key.StartsWith("Native_",StringComparison.Ordinal))
            {
                int epoch=config.Bind("Confirmations","NativeResetGeneration",0,"其他执行确认批量重置代次。").Value;
                var seen=config.Bind("ConfirmationGenerations",key,0,"该确认采用的批量设置代次。");
                if(seen.Value!=epoch){entry.Value=Entry(config,"NativeOther").Value;seen.Value=epoch;}
            }
            return entry;
        }
        internal static bool Enabled(ConfigFile config,string key)=>!key.StartsWith("Native_",StringComparison.Ordinal)?Entry(config,key).Value:Entry(config,"NativeOther").Value && Entry(config,key).Value;
        internal static Dictionary<string,bool> Snapshot(ConfigFile config)=>Items.ToDictionary(x=>x.Key,x=>Entry(config,x.Key).Value);
        internal static void Apply(ConfigFile config,Dictionary<string,bool> values,bool resetNative)
        {
            bool autosave=config.SaveOnConfigSet;config.SaveOnConfigSet=false;
            try
            {
                foreach(var item in Items)if(values.TryGetValue(item.Key,out bool value))Entry(config,item.Key).Value=value;
                if(resetNative){var epoch=config.Bind("Confirmations","NativeResetGeneration",0,"其他执行确认批量重置代次。");epoch.Value=checked(epoch.Value+1);}
            }
            finally{config.SaveOnConfigSet=autosave;}
            config.Save();
        }
    }
}
