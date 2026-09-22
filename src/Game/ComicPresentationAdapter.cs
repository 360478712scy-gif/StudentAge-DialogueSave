using System;
using System.IO;
using System.Runtime.CompilerServices;
using Config;
using DG.Tweening;
using GenUI.Talk;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Evt;

namespace StudentAgeDialogueSave.GameIntegration
{
    // Only comic cells use this loader. Pooled placeholders and old asynchronous
    // image completions must never overwrite the current panel's illustration.
    internal static class ComicPresentationAdapter
    {
        static readonly Func<string,string> TextureUrl=(Func<string,string>)Delegate.CreateDelegate(typeof(Func<string,string>), AccessTools.Method(typeof(global::Game).Assembly.GetType("Sdk.ResPath"), "ToTextureUrl"));
        sealed class Request { internal int Generation; internal ComicView Owner; }
        static readonly ConditionalWeakTable<UICell, Request> requests = new ConditionalWeakTable<UICell, Request>();
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ComicView), "OnOpen"), prefix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(Opening)));
            harmony.Patch(AccessTools.Method(typeof(ComicView), "OnRender"), prefix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(Render)));
            harmony.Patch(AccessTools.Method(typeof(ComicView), "OnRecycle"), prefix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(Recycle)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView), "ShowComic"), prefix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(PreparePage)));
        }
        static void Opening(ComicView __instance)
        {
            // Keep the underlying scene visible until the first illustration arrives.
            // An opaque empty native panel otherwise looks like a failed white screen.
            var background=__instance.gameObject.GetComponent<UnityEngine.UI.Image>();
            if(background!=null)background.enabled=false;
        }
        static void PreparePage(NewTalkView __instance, int _id, int _page)
        {
            // The native cached-panel branch calls Show with the previous parms.
            var panel = AccessTools.Field(typeof(NewTalkView), "comicPanel").GetValue(__instance) as ComicView;
            if(panel != null) panel.parms = new object[] { _id, _page };
        }
        static bool Recycle(UICell _cell)
        {
            requests.GetValue(_cell, _ => new Request()).Generation++;
            var image=((Cell_ComicItemUI)_cell).icon_item.image;
            image.DOKill(); image.sprite=null; image.enabled=false;
            // Do not issue an asynchronous common6/img_empty request on a reusable cell.
            return false;
        }
        static bool Render(ComicView __instance, UICell _cell)
        {
            var cell=(Cell_ComicItemUI)_cell;
            var request=requests.GetValue(cell, _ => new Request()); int token=++request.Generation; request.Owner=__instance;
            var image=cell.icon_item.image;
            image.DOKill(); image.enabled=false; image.sprite=null;
            cell.icon_item.showWhenComp=true;
            string url=cell.data as string;
            Action<Sprite> complete=sprite=>Complete(cell, token, sprite);
            if(string.IsNullOrEmpty(url)) return false;
            if(url.StartsWith("Mods",StringComparison.Ordinal))url=Singleton<ModCtrl>.Ins.GetFullUrl(url);
            if(Path.IsPathRooted(url)) ResMgr.LoadExternSpriteAsync(url, complete, false);
            else ResMgr.LoadSpriteAsync(TextureUrl(LocalizationMgr.GetLocalizeUrl(url)), complete);
            return false;
        }
        internal static void Complete(Cell_ComicItemUI cell, int token, Sprite sprite)
        {
            if(!requests.TryGetValue(cell,out var request) || request.Generation!=token || cell.gameObject==null || sprite==null)return;
            var image=cell.icon_item.image;
            image.DOKill(); image.enabled=true;
            if(request.Owner?.gameObject!=null)
            {
                var background=request.Owner.gameObject.GetComponent<UnityEngine.UI.Image>();
                if(background!=null)background.enabled=true;
            }
            cell.icon_item.SetSprite(sprite); // Preserve the original entry animation.
        }
    }
}
