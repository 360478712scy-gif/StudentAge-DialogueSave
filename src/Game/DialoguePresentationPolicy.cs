using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sdk;
using View.Evt;

namespace StudentAgeDialogueSave.GameIntegration
{
    // UI provenance is independent of the save adapter's resumable continuation.
    // NewTalkView is pooled and its native evtId is not cleared by ShowTalk.
    internal static class DialoguePresentationPolicy
    {
        sealed class Origin { internal int Event; }
        static readonly ConditionalWeakTable<NewTalkView,Origin> origins=new ConditionalWeakTable<NewTalkView,Origin>();
        static int eventScope,pendingTalk,pendingEvent;
        internal static int EventId(NewTalkView view)=>view!=null && origins.TryGetValue(view,out var origin)?origin.Event:0;
        internal static bool IsEvent(NewTalkView view)=>EventId(view)>0;
        internal static bool IsTransition(NewTalkView view)=>view?.group_foreground!=null && view.group_foreground.gameObject.activeSelf;
        internal static void Bind(NewTalkView view,int eventId){if(view!=null)origins.GetOrCreateValue(view).Event=Math.Max(0,eventId);}
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CommonEvtMgr),"ShowEvent"),
                prefix:new HarmonyMethod(typeof(DialoguePresentationPolicy),nameof(EventEntering)){priority=Priority.First},
                finalizer:new HarmonyMethod(typeof(DialoguePresentationPolicy),nameof(EventLeaving)));
            harmony.Patch(AccessTools.Method(typeof(CommonEvtMgr),"ShowTalk",new[]{typeof(int),typeof(Action),typeof(int),typeof(bool),typeof(bool),typeof(string)}),
                prefix:new HarmonyMethod(typeof(DialoguePresentationPolicy),nameof(TalkEntering)){priority=Priority.First});
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnOpen"),
                prefix:new HarmonyMethod(typeof(DialoguePresentationPolicy),nameof(Opening)){priority=Priority.First});
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"RefreshEvt"),
                prefix:new HarmonyMethod(typeof(DialoguePresentationPolicy),nameof(EventRefreshing)){priority=Priority.First});
        }
        static void EventEntering(int _id,out int __state){__state=eventScope;eventScope=Config.Cfg.EvtCfgMap.ContainsKey(_id)?_id:0;}
        static Exception EventLeaving(int __state,Exception __exception){eventScope=__state;return __exception;}
        static void TalkEntering(int _id,bool _isNewEvt)
        {
            if(!Config.Cfg.TalkCfgMap.ContainsKey(_id))return;
            var view=UIMgr.GetView<NewTalkView>(false) as NewTalkView;
            bool opened=view!=null && view.viewState==ViewState.Opened;
            int origin=eventScope>0?eventScope:(!_isNewEvt && opened?EventId(view):0);
            if(opened)Bind(view,origin);
            else {pendingTalk=_id;pendingEvent=origin;}
        }
        static void Opening(NewTalkView __instance)
        {
            var args=__instance.parms;
            int id=args!=null && args.Length>1?(int)args[1]:0;
            bool evt=args!=null && args.Length>0 && args[0] is bool flag && flag;
            Bind(__instance,evt?id:(id==pendingTalk?pendingEvent:0));
            pendingTalk=pendingEvent=0;
        }
        static void EventRefreshing(NewTalkView __instance,int __0){Bind(__instance,__0);}
    }
}
