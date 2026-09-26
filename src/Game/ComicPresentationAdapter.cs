using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine.UI;
using View.Main;
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
        sealed class Request { internal int Generation; internal ComicView Owner; internal PageGate Lead; }
        sealed class PageGate { internal NewTalkView View; internal int Id,Page; internal string LeadUrl; internal Image LeadImage; internal float CompletedAt; internal Func<bool> LeadValid; internal bool Ready; internal Action Callback; internal readonly Dictionary<GameObject,bool> Hidden=new Dictionary<GameObject,bool>(); }
        sealed class Picture { internal Sprite Sprite; internal bool Done; internal Action<Sprite> Waiting; }
        static PageGate gate;
        static readonly Dictionary<string,Picture> pictures=new Dictionary<string,Picture>();
        static readonly System.Reflection.MethodInfo release=AccessTools.Method(typeof(ResMgr),"RecycleResInfo");
        static readonly System.Reflection.FieldInfo comicPanel=AccessTools.Field(typeof(NewTalkView),"comicPanel");
        static readonly Func<string,string> ComicUrl=(Func<string,string>)Delegate.CreateDelegate(typeof(Func<string,string>),AccessTools.Method(typeof(global::Game).Assembly.GetType("Sdk.ResPath"),"ToComicUrl"));
        static string Resolve(string url){if(url.StartsWith("Mods",StringComparison.Ordinal))url=Singleton<ModCtrl>.Ins.GetFullUrl(url);return Path.IsPathRooted(url)?url:TextureUrl(LocalizationMgr.GetLocalizeUrl(url));}
        static void Load(string url,Action<Sprite> callback)
        {
            string path=Resolve(url);
            if(pictures.TryGetValue(path,out var existing)){if(existing.Done)callback?.Invoke(existing.Sprite);else existing.Waiting+=callback;return;}
            var picture=new Picture{Waiting=callback};pictures[path]=picture;
            Action<Sprite> complete=sprite=>{picture.Done=true;picture.Sprite=sprite;var waiting=picture.Waiting;picture.Waiting=null;waiting?.Invoke(sprite);};
            if(Path.IsPathRooted(path))ResMgr.LoadExternSpriteAsync(path,complete,false);else ResMgr.LoadSpriteAsync(path,complete);
        }
        internal static void Clear()
        {
            if(gate!=null)foreach(var pair in gate.Hidden)if(pair.Key!=null)pair.Key.SetActive(pair.Value);
            gate=null;
            foreach(string path in pictures.Keys)release.Invoke(null,new object[]{path});pictures.Clear();
        }
        internal static void Tick()
        {
            var current=gate;
            if(current==null || current.Ready || current.LeadImage==null || current.LeadValid?.Invoke()!=true)return;
            var image=current.LeadImage;
            // The persistent host owns this gate: pooled cells are temporarily
            // disabled/reparented and native transitions can cancel tween timers.
            if(!image.gameObject.activeInHierarchy || !image.enabled || image.sprite==null || image.color.a<.99f)return;
            if(Time.realtimeSinceStartup-current.CompletedAt<.5f)return;
            current.Ready=true;ReleaseText(current);
        }
        static void ToolbarRefreshed(TopView __instance)
        {
            if(gate?.View?.gameObject!=null && __instance.itemgroup_key!=null)Hide(gate,__instance.itemgroup_key.gameObject);
        }
        static void Ending(NewTalkView __instance){if(gate?.View==__instance)Clear();}
        static void Hide(PageGate current,GameObject obj){if(obj==null)return;if(!current.Hidden.ContainsKey(obj))current.Hidden[obj]=obj.activeSelf;obj.SetActive(false);}
        static void HideText(PageGate current)
        {
            if(comicPanel.GetValue(current.View) is ComicView panel && panel.gameObject!=null)
            {Hide(current,panel.txtex_talk.gameObject);Hide(current,panel.txt_name.gameObject);Hide(current,panel.txt_name2.gameObject);Hide(current,panel.root_next.gameObject);}
        }
        static void ReleaseText(PageGate current)
        {
            if(gate!=current || !current.Ready || current.Callback==null || current.View.gameObject==null)return;
            var callback=current.Callback;current.Callback=null;
            // Restore only the native caption targets; toolbar remains hidden throughout the comic.
            if(comicPanel.GetValue(current.View) is ComicView panel)
                foreach(var obj in new[]{panel.txtex_talk.gameObject,panel.txt_name.gameObject,panel.txt_name2.gameObject,panel.root_next.gameObject})
                    if(current.Hidden.TryGetValue(obj,out bool active)){obj.SetActive(active);current.Hidden.Remove(obj);}
            if(comicPanel.GetValue(current.View) is ComicView readyPanel)readyPanel.txtex_talk.gameObject.SetActive(true);
            callback();
        }
        static readonly ConditionalWeakTable<UICell, Request> requests = new ConditionalWeakTable<UICell, Request>();
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(TopView),"Refresh"),postfix:new HarmonyMethod(typeof(ComicPresentationAdapter),nameof(ToolbarRefreshed)));
            harmony.Patch(AccessTools.Method(typeof(TopView),"RefreshHotkey"),postfix:new HarmonyMethod(typeof(ComicPresentationAdapter),nameof(ToolbarRefreshed)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView), "HideCGComic"), postfix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(Ending)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView), "HideComic"), postfix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(Ending)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView), "OnClose"), postfix:new HarmonyMethod(typeof(ComicPresentationAdapter), nameof(Ending)));
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
        static void PreparePage(NewTalkView __instance, int _id, int _page, ref Action _callback)
        {
            var panel=comicPanel.GetValue(__instance) as ComicView;
            if(panel!=null)panel.parms=new object[]{_id,_page};
            if(!Cfg.CGCfgMap.TryGetValue(_id,out var cfg) || _page<1 || _page>cfg.comic.Count)return;
            if(gate!=null && (gate.View!=__instance || gate.Id!=_id))Clear();
            var current=new PageGate{View=__instance,Id=_id,Page=_page,LeadUrl=ComicUrl(cfg.GetImgUrl()+"/"+_page+"-1")};
            if(gate!=null)foreach(var pair in gate.Hidden)current.Hidden[pair.Key]=pair.Value;
            gate=current;HideText(current);
            var top=UIMgr.GetView<TopView>(false) as TopView;
            if(top?.itemgroup_key!=null)Hide(current,top.itemgroup_key.gameObject);
            var callback=_callback;
            _callback=()=>{if(gate!=current)return;HideText(current);current.Callback=callback;ReleaseText(current);};
            // Request this page and the following page together, while the native
            // prefab and its staggered cells initialize. Retained refs end with the comic.
            for(int page=_page;page<=Math.Min(_page+1,cfg.comic.Count);page++)
                for(int i=1;i<=cfg.comic[page-1];i++)Load(ComicUrl(cfg.GetImgUrl()+"/"+page+"-"+i),null);
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
            // Pool render callbacks can be deferred until idx has advanced. Match
            // the cell data against the exact URL handed to pool.Pop by native Play.
            request.Lead=gate!=null && string.Equals(cell.data as string,gate.LeadUrl,StringComparison.Ordinal)?gate:null;
            var image=cell.icon_item.image;
            image.DOKill(); image.enabled=false; image.sprite=null;
            cell.icon_item.showWhenComp=true;
            string url=cell.data as string;
            Action<Sprite> complete=sprite=>Complete(cell, token, sprite);
            if(string.IsNullOrEmpty(url)) return false;
            Load(url,complete);
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
            var current=request.Lead;
            if(current!=null && gate==current && comicPanel.GetValue(current.View)==request.Owner)
            {
                current.LeadImage=image;current.CompletedAt=Time.realtimeSinceStartup;
                current.LeadValid=()=>request.Generation==token;

            }
        }
    }

}
