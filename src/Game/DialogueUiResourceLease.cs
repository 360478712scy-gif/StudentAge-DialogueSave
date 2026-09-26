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
            if(--rebuilding==0 && unloadPending){unloadPending=false;ResMgr.UnloadUnusedAssets();}
        }
    }
}
