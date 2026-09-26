using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace StudentAgeDialogueSave.UI
{
    // The reference translation uses installed Microsoft YaHei. No proprietary
    // font bytes are redistributed. CrossOver's dynamic Font handle omits font
    // data, so route only our private handle to the installed face in TextCore.
    internal static class AdvChineseFont
    {
        const string PatchId="local.studentage.dialoguesave.chinese-font";
        static TMP_FontAsset asset;
        static Font handle;
        static byte[] bytes;
        static Harmony harmony;
        static bool attempted;
        internal static TMP_FontAsset Get(TMP_FontAsset fallback)
        {
            if(attempted)return asset;
            attempted=true;
            try
            {
                string path=Font.GetPathsToOSFonts().FirstOrDefault(p=>string.Equals(Path.GetFileName(p),"msyh.ttc",StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(p),"msyh.ttf",StringComparison.OrdinalIgnoreCase));
                if(path==null){var candidate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"msyh.ttc");if(File.Exists(candidate))path=candidate;}
                if(path==null)return null;
                bytes=File.ReadAllBytes(path);
                handle=Font.CreateDynamicFontFromOSFont("Microsoft YaHei",64);
                harmony=new Harmony(PatchId);
                harmony.Patch(AccessTools.Method(typeof(FontEngine),"LoadFontFace",new[]{typeof(Font),typeof(int)}),prefix:new HarmonyMethod(typeof(AdvChineseFont),nameof(LoadFace)));
                asset=TMP_FontAsset.CreateFontAsset(handle,64,9,GlyphRenderMode.SDFAA,2048,2048);
                if(asset==null || !(asset.faceInfo.familyName.IndexOf("YaHei",StringComparison.OrdinalIgnoreCase)>=0 || asset.faceInfo.familyName.Contains("微软雅黑")))
                {ReleaseResources();return null;}
                asset.name="ADV.MicrosoftYaHei.Chinese";
                asset.fallbackFontAssetTable=new System.Collections.Generic.List<TMP_FontAsset>();
                if(fallback!=null && fallback!=asset)asset.fallbackFontAssetTable.Add(fallback);
                return asset;
            }
            catch(Exception ex){Debug.LogWarning("DialogueSave Chinese font fallback: "+ex.Message);ReleaseResources();return null;}
        }
        static bool LoadFace(Font font,int pointSize,ref FontEngineError __result)
        {
            if(font!=handle || bytes==null)return true;
            __result=FontEngine.LoadFontFace(bytes,pointSize);return false;
        }
        static void ReleaseResources()
        {
            harmony?.UnpatchSelf();harmony=null;
            if(asset!=null){foreach(var atlas in asset.atlasTextures)if(atlas!=null)UnityEngine.Object.Destroy(atlas);UnityEngine.Object.Destroy(asset.material);UnityEngine.Object.Destroy(asset);}
            if(handle!=null)UnityEngine.Object.Destroy(handle);
            asset=null;handle=null;bytes=null;
        }
        internal static void Release(){ReleaseResources();attempted=false;}
    }
}
