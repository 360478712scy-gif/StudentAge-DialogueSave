using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

// Metadata-only checks against the installed game. Never executes a game method or initializes a save.
internal static class Program
{
    static int checks;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Main(string[] args)
    {
        string managed = args[0];
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
        if(args.Contains("--ui-interaction"))
        {
            const BindingFlags uiFlags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
            var confirm=game.GetType("View.Hint.CommonComfirmView",true);
            Require(confirm.GetMethod("OnOpen",uiFlags)!=null,"native confirmation open hook");
            Require(confirm.GetField("ok",uiFlags)?.FieldType==typeof(Action),"native confirmation callback type");
            var ask=game.GetType("HintHelper",true).GetMethod("ShowConfirm",uiFlags);
            Require(ask.GetParameters().Select(p=>p.Name).SequenceEqual(new[]{"_desc","_ok","_close","_showCloseBtn","_title","_okTxt","_closeTxt","_tipsTxt","_enableCloseByRightClick"}),"native prompt Harmony argument contract");
            var settings=game.GetType("View.Main.SettingView",true);
            Require(settings.GetField("dropdown_lanuage",uiFlags)!=null,"native language selector field");
            var ui=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed,"UnityEngine.UI.dll"));
            Require(ui.GetType("UnityEngine.UI.Selectable",true).GetMethod("DoStateTransition",uiFlags)?.IsVirtual==true,"pressed state transition override");
            foreach(string name in new[]{"OnPointerDown","OnPointerUp","OnPointerEnter","OnPointerExit"})
                Require(ui.GetType("UnityEngine.UI.Button",true).GetMethod(name,uiFlags)?.IsVirtual==true,"pointer override "+name);
            Console.WriteLine("UI_INTERACTION_CONTRACT_PASS "+checks+" (metadata only; no game execution)");return;
        }
        var talk = game.GetType("View.Evt.NewTalkView", true);
        var fields = new[] { "cfg", "talkType", "evtId", "talkId", "roles", "posRoles", "roleCloths", "historys", "curBgId", "lastEffectCfgId", "waitFrame", "enableAutoTalk", "isDelaying", "isPhoneing", "isShowingCG", "isShowingComic", "countdown", "talkingPos", "talkingRoles", "playEvtGroupBgm", "enableSceneSound", "topSeq", "topSeq2", "topWaitSeq", "curVFX" };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string field in fields) Require(talk.GetField(field, flags) != null, "Missing NewTalkView." + field);
        foreach (string field in new[] { "bgs", "curBgIdx", "defaultOption" })
            Require(talk.GetField(field, flags) != null, "Missing timed-option/background field " + field);
        var effectAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "UIEffect.dll"));
        var effect = effectAssembly.GetType("Coffee.UIEffects.UIEffect", true);
        foreach (string name in new[] { "effectFactor", "colorFactor", "blurFactor", "effectMode", "colorMode", "blurMode", "enabled" })
        {
            var property = effect.GetProperty(name, flags);
            Require(property != null && property.CanRead && property.CanWrite, "Missing background property " + name);
        }
        foreach (string name in new[] { "OnOpen", "RefreshTalk", "OnClickNext", "NextTalk", "OnClickSkip", "CloseView", "DoTextEnd", "ShowOption", "RefreshEvt", "AutoTalk", "SpeedUp", "RefreshBg", "PlayRoleEffect", "BindRoleDataWithCell" })
            Require(talk.GetMethods(flags).Any(m => m.Name == name), "Missing NewTalkView." + name);
        foreach (var item in new[] { ("SaveMgrEx", "gameSaveMgr"), ("Sdk.SaveMgr", "saveDict"), ("Sdk.SaveMgr", "saveObjList"), ("CommonEvtMgr", "roundEndEventQueue"), ("RoundMgr", "<RoundState>k__BackingField"), ("Game", "<state>k__BackingField"), ("Game", "ins"), ("Sdk.UIMgr", "ins"), ("Sdk.UIMgr", "viewDict") })
            Require(game.GetType(item.Item1, true).GetField(item.Item2, flags) != null, "Missing " + item);
        var evt = game.GetType("CommonEvtMgr", true);
        foreach (string name in new[] { "ShowNewRoundEvent", "ShowEvent", "ShowTalk", "SelectOption", "GetOptions" })
            Require(evt.GetMethods(flags).Any(m => m.Name == name), "Missing event method " + name);
        var show = evt.GetMethods(flags).Single(m => m.Name == "ShowTalk" && m.GetParameters()[0].ParameterType == typeof(int));
        Require(show.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "_id", "_callback", "_bgId", "_changeBgm", "_isNewEvt", "_url" }), "ShowTalk patch argument mismatch");
        Require(game.GetType("View.Main.MainView", true).GetMethod("CheckGuide", flags).ReturnType == typeof(bool), "CheckGuide result mismatch");
        foreach (string type in new[] { "View.Main.MapSceneView", "View.Main.MapRoleView" })
            Require(game.GetType(type, true).GetMethod("CheckGuide", flags).ReturnType == typeof(bool), type + " guide result mismatch");
        foreach (var item in new[] { ("View.Main.MapRoleView", "CloseView2"), ("PhoneData", "AddPhoto"), ("AudioMgrEx", "PlayNpcSoundOneShot"), ("Sdk.TimerMgr", "Remove") })
            Require(game.GetType(item.Item1, true).GetMethods(flags).Any(m => m.Name == item.Item2), "Missing restore/auto guard " + item);
        foreach (string field in new[] { "recordMapId", "recordBgId", "recordNpcId" })
            Require(game.GetType("MapData", true).GetField(field, flags) != null, "Missing map record " + field);
        foreach (string field in new[] { "dataDict", "needAddList" })
            Require(game.GetType("Sdk.TimerMgr", true).GetField(field, flags) != null, "Missing automatic playback timer field " + field);
        foreach (string field in new[] { "callback", "delayTime", "passTime" })
            Require(game.GetType("Sdk.TimerMgr+TimerData", true).GetField(field, flags) != null, "Missing automatic playback timer data " + field);
        Require(talk.GetField("txtSpeeds", flags).FieldType == typeof(float[]), "Typing speed table layout changed");
        Require(game.GetType("GuideData", true).GetMethod("SaveGlobalGuide", flags) != null, "Guide global save hook missing");
        var role = game.GetType("View.Evt.NewTalkRoleData", true);
        foreach (FieldInfo field in role.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Require(field.Name == "cell" || field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType.FullName == "UnityEngine.Vector3", "Unexpected role field type: " + field);
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "UnityEngine.CoreModule.dll"));
        var random = core.GetType("UnityEngine.Random+State", true).GetFields(flags);
        Require(random.Length == 4 && random.All(f => f.FieldType == typeof(int)), "Random state layout changed");
        var union = game.GetType("Sdk.ISaveLoadValue", true).GetCustomAttributesData();
        Require(union.Count(a => a.AttributeType.Name == "UnionAttribute") == 11, "Native model union whitelist changed");
        foreach (var rule in new[] {
            ("StudyData", "ShowExamResultComp"),
            ("ActionData+<>c__DisplayClass22_0", "<HelpAction>b__0"),
            ("MiniGameData+<>c__DisplayClass4_0", "<SocialGame>b__0"),
            ("MiniGameData+<>c__DisplayClass5_0", "<EndGame>b__0"),
            ("NegotiationData+<>c__DisplayClass16_0", "<Negotiate>b__0"),
            ("EGameData+<>c", "<RequestEGameStuck>b__17_0"),
            ("IntentData+<>c", "<GetIntentFail>b__17_0"),
            ("View.Main.MainView+<>c", "<OnClickMap>b__26_0"),
            ("Sdk.BaseView", "CloseView") })
        {
            var type = game.GetType(rule.Item1, true);
            var method = type.GetMethod(rule.Item2, flags, null, Type.EmptyTypes, null);
            Require(method != null && method.ReturnType == typeof(void), "Missing continuation " + rule);
            Console.WriteLine("CONTINUATION " + rule + " TOKEN=" + method.MetadataToken + " MVID=" + method.Module.ModuleVersionId);
            if (rule.Item1.Contains("+"))
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    Console.WriteLine("  FIELD " + field.Name + " " + field.FieldType.FullName);
        }
        Console.WriteLine("ADAPTER_CONTRACT_PASS " + checks + " (metadata only; no gameplay or UI validation)");
    }
}
