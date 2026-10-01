using System;
using System.Collections.Generic;
using HarmonyLib;
using Sdk;
using UnityEngine;

namespace StudentAgeDialogueSave.GameIntegration
{
    // CloseAllView unloads zero-reference addressable bundles, even when a custom
    // log still displays their fonts. Hold native resource references until the new
    // views own them. Acquire only already-loaded resources; never synchronously load.
    internal sealed class DialogueUiResourceLease : IDisposable
    {
        readonly List<string> paths=new List<string>();
        static int rebuilding;
        static bool unloadPending;
        bool disposed;
        internal static void Install(Harmony harmony)=>harmony.Patch(AccessTools.Method(typeof(ResMgr),"UnloadUnusedAssets"),prefix:new HarmonyMethod(typeof(DialogueUiResourceLease),nameof(BeforeUnload)));
        static bool BeforeUnload(){if(rebuilding==0)return true;unloadPending=true;return false;}
        static readonly System.Reflection.MethodInfo release=AccessTools.Method(typeof(ResMgr),"RecycleResInfo");
        internal DialogueUiResourceLease()
        {
            if(release==null)throw new MissingMethodException("ResMgr.RecycleResInfo");
            var ui=AccessTools.Field(typeof(UIMgr),"ins").GetValue(null);
            var views=(Dictionary<string,BaseView>)AccessTools.Field(typeof(UIMgr),"viewDict").GetValue(ui);
            var wanted=new HashSet<string>(StringComparer.Ordinal);
            foreach(var view in views.Values)
            {
                if(view?.gameObject==null)continue;
                if(!string.IsNullOrEmpty(view.prefabPath))wanted.Add(view.prefabPath);
                if(view.preloadUrls!=null)foreach(string path in view.preloadUrls)if(!string.IsNullOrEmpty(path))wanted.Add(path);
            }
            try
            {
                rebuilding++;
                foreach(string path in wanted)if(ResMgr.HasResLoaded(path))
                {
                    ResMgr.Load<UnityEngine.Object>(path);paths.Add(path);
                }
            }
            catch{Dispose();throw;}
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            foreach(string path in paths)release.Invoke(null,new object[]{path});
            paths.Clear();
            if(--rebuilding==0 && unloadPending){unloadPending=false;ReleaseUnreferenced();}
        }
        // The deferred native unload ends with Resources.UnloadUnusedAssets + GC.Collect, a full
        // scan that stalls 1-2 s with many Mods loaded. Release unreferenced resources now (the
        // native first half); the next native view change still runs the full sweep.
        static readonly System.Reflection.FieldInfo instance=AccessTools.Field(typeof(ResMgr),"ins"),table=AccessTools.Field(typeof(ResMgr),"resDict");
        static void ReleaseUnreferenced()
        {
            var resources=instance==null || table==null ? null : table.GetValue(instance.GetValue(null)) as System.Collections.IDictionary;
            if(resources==null){ResMgr.UnloadUnusedAssets();return;}
            var unused=new List<object>();
            foreach(System.Collections.DictionaryEntry entry in resources)
            {
                var info=entry.Value;if(info==null)continue;
                var count=AccessTools.Field(info.GetType(),"refCount");var unload=AccessTools.Method(info.GetType(),"UnLoad");
                if(count==null || unload==null){ResMgr.UnloadUnusedAssets();return;}
                if((int)count.GetValue(info)<=0){unload.Invoke(info,null);unused.Add(entry.Key);}
            }
            foreach(var key in unused)resources.Remove(key);
        }
    }
}
