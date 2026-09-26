using System.Collections.Generic;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Main;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Native OnFullScreenChange picks a 16:9 resolution for the new window, and indexes
    // [Count-1] when the display offers none (e.g. 2560x1664 Macs under CrossOver), throwing
    // before the window changes. Only that case is handled here; everything else stays native.
    internal static class NativeWindowModeFix
    {
        internal static void Install(Harmony harmony)=>harmony.Patch(AccessTools.Method(typeof(SettingView),"OnFullScreenChange"),prefix:new HarmonyMethod(typeof(NativeWindowModeFix),nameof(Change)));
        static bool Change(SettingView __instance,int _option)
        {
            var sizes=AccessTools.Field(typeof(SettingView),"resolutionOptions").GetValue(__instance) as List<Dropdown.OptionData>;
            var modes=AccessTools.Field(typeof(SettingView),"fullscreenOptions").GetValue(__instance) as List<Dropdown.OptionData>;
            if(sizes==null || sizes.Count>0 || modes==null || _option<0 || _option>=modes.Count)return true;
            var mode=(FullScreenMode)(int)AccessTools.Field(modes[_option].GetType(),"id").GetValue(modes[_option]);
            if(mode!=FullScreenMode.Windowed || Screen.fullScreenMode!=FullScreenMode.FullScreenWindow)return true;
            var display=Screen.currentResolution;
            int height=Mathf.Min(1080,display.height-100),width=Mathf.RoundToInt(height*16f/9f);
            if(width>display.width-40){width=display.width-40;height=Mathf.RoundToInt(width*9f/16f);}
            UIMgr.SetResolution(width,height,mode);
            return false;
        }
    }
}
