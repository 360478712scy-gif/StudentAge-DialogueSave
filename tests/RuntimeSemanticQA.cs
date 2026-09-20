using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Config;
using HarmonyLib;
using Sdk;
using StudentAgeDialogueSave.GameIntegration;
using UnityEngine;
using View.Evt;

// Invoked by RuntimeQA only after its native-world/path isolation checks have passed.
// These are real TalkCfg / OptionCfg effects, not replacements for the effect runner.
public static class RuntimeSemanticQA
{
    private const int First = 1901000101, Choice = 1901000102, Final = 1901000103;
    private const int OptionA = 1901000111, OptionB = 1901000112;
    // Both integers are exactly representable by the game's float effect parameters.
    private const int ProbeEvent = 1, ProbePosition = 16000000;
    private static NewTalkView Talk { get { return UIMgr.GetView<NewTalkView>(false) as NewTalkView; } }
    private static float Value { get { return Singleton<CommonEvtMgr>.Ins.GetEvtSaveData(ProbeEvent, ProbePosition); } }
    private static List<float> Effect(float delta) { return new List<float> { 50, 2, ProbeEvent, ProbePosition, delta }; }
    private static List<List<float>> Effects(float delta) { return new List<List<float>> { Effect(delta) }; }
    private static bool Stable(DialogueCheckpointAdapter adapter, string text)
    {
        string reason;
        return adapter.CanCapture(out reason) && Talk != null && Talk.txtex_content.text == text;
    }

    public static IEnumerator Run(DialogueCheckpointAdapter adapter, Action<bool, string> check,
        Func<Func<bool>, float, string, IEnumerator> until)
    {
        check(Paths.GameRootPath.Replace('\\', '/').EndsWith("/student-age-dialogue-save/qa/runtime") &&
            PathDefine.SAVE_PATH.Replace('\\', '/').Contains("/student-age-dialogue-save/qa/runtime/data/") &&
            Application.companyName == "DlgSaveQA" && Application.productName == "DialogSave",
            "semantic QA refuses any non-isolated runtime");
        string reason;
        check(adapter.CanCapture(out reason), "semantic QA begins at a native stable tracked dialogue");
        check(CommonEvtMgr.GenEffector(Effect(1)).GetType().FullName == "Effect.EffectorSetEventValue",
            "native effect 50 subtype 2 is the actual additive event-world effector");
        int[] talkIds = { First, Choice, Final }, optionIds = { OptionA, OptionB };
        check(talkIds.All(id => !Cfg.TalkCfgMap.ContainsKey(id)) && optionIds.All(id => !Cfg.OptionCfgMap.ContainsKey(id)),
            "semantic fixture IDs do not replace existing dialogue or options");

        var original = adapter.Capture();
        string latest = SaveMgr.GetPref("LatestSaveKey", "");
        var source = (TalkCfg)AccessTools.Field(typeof(NewTalkView), "cfg").GetValue(Talk);
        EvtCfg priorEvent;
        bool hadEvent = Cfg.EvtCfgMap.TryGetValue(1, out priorEvent);
        float baseline = Value;
        var guideBefore = Singleton<FuncMgr>.Ins.GetGuideData().addGuides;
        var expectedGuides = guideBefore == null ? new List<int>() : new List<int>(guideBefore);
        // MainView.CheckGuide is suppressed by the isolated driver, not by production code.
        int guideId = Cfg.GuideCfgMap.Keys.First(id => id > 0);
        if (!expectedGuides.Contains(guideId)) expectedGuides.Add(guideId);
        const string firstText = "语义验证甲：本句效果已经结算。";
        const string choiceText = "语义验证乙：后续效果已结算，请选择。";
        const string finalText = "语义验证丙：选项已经继续。";
        try
        {
            Cfg.TalkCfgMap[First] = new TalkCfg { id = First, bg = source.bg, roleIds = source.roleIds,
                roleName = source.roleName, content = firstText, effect = Effects(3), nextTalk = new List<int> { Choice } };
            Cfg.TalkCfgMap[Choice] = new TalkCfg { id = Choice, bg = source.bg, roleIds = source.roleIds,
                roleName = source.roleName, content = choiceText, effect = Effects(5), option = new List<int> { OptionA, OptionB } };
            Cfg.TalkCfgMap[Final] = new TalkCfg { id = Final, bg = source.bg, roleIds = source.roleIds,
                roleName = source.roleName, content = finalText };
            Cfg.OptionCfgMap[OptionA] = new OptionCfg { id = OptionA, content = "增加七", effect = Effects(7), talkId = new List<int> { Final } };
            Cfg.OptionCfgMap[OptionB] = new OptionCfg { id = OptionB, content = "增加十一", effect = Effects(11), talkId = new List<int> { Final } };
            // Event 1 has an explicit native unconditional queue path. Its original config is restored below.
            Cfg.EvtCfgMap[1] = new EvtCfg { id = 1, type = 1, title = "原生效果语义隔离验证", talkId = new List<int> { First } };
            UIMgr.CloseView<NewTalkView>();
            Singleton<CommonEvtMgr>.Ins.EnqueueEvt(0, 1);
            Singleton<CommonEvtMgr>.Ins.ShowNewRoundEvent();
            yield return until(() => Stable(adapter, firstText), 25, "semantic first line native effect complete");
            check(Value == baseline + 3, "current-line native effect executed once before capture");
            Singleton<FuncMgr>.Ins.GetGuideData().addGuides = new List<int>(expectedGuides);
            var atFirst = adapter.Capture();

            Talk.NextTalk();
            yield return until(() => Stable(adapter, choiceText) && Talk.talkState == TalkState.Option, 25, "semantic next-line choices stable");
            check(Value == baseline + 8, "previously pending next-line effect executes once on original timeline");
            CommonEvtMgr.GenEffector(Effect(100)).Run();
            check(Value == baseline + 108, "native world mutation probe is additive");
            Singleton<FuncMgr>.Ins.GetGuideData().addGuides = new List<int>();
            yield return Restore(adapter, atFirst, until, "semantic restore already-applied first line");
            check(Value == baseline + 3, "restore rewinds world without replaying already-applied effect");
            check(Singleton<FuncMgr>.Ins.GetGuideData().addGuides != null &&
                Singleton<FuncMgr>.Ins.GetGuideData().addGuides.SequenceEqual(expectedGuides),
                "GuideData pending addGuides survives native world restore");
            for (int frame = 0; frame < 3; frame++) yield return null;
            check(Value == baseline + 3, "late callbacks do not replay restored current-line effect");
            Talk.NextTalk();
            yield return until(() => Stable(adapter, choiceText) && Talk.talkState == TalkState.Option, 25, "semantic restored next-line choices stable");
            check(Value == baseline + 8, "pending next-line native effect continues exactly once after restore");
            var atOptions = adapter.Capture();
            var choices = Talk.itemgroup_options.GetCells().Select(c => (CommonEvtOptionData)c.data).ToArray();
            check(choices.Select(c => c.id).OrderBy(i => i).SequenceEqual(new[] { OptionA, OptionB }),
                "captured options are the actual native option cells");
            Singleton<CommonEvtMgr>.Ins.SelectOption(choices.Single(c => c.id == OptionA));
            yield return until(() => Stable(adapter, finalText), 25, "semantic original option continuation stable");
            check(Value == baseline + 15, "selected native option effect runs exactly once");
            yield return Restore(adapter, atOptions, until, "semantic restore pending options");
            check(Value == baseline + 8 && Talk.talkState == TalkState.Option,
                "restoring options neither replays line effect nor pre-executes option effect");
            Singleton<CommonEvtMgr>.Ins.SelectOption(Talk.itemgroup_options.GetCells()
                .Select(c => (CommonEvtOptionData)c.data).Single(c => c.id == OptionA));
            yield return until(() => Stable(adapter, finalText), 25, "semantic restored option continuation stable");
            check(Value == baseline + 15, "pending native option effect continues exactly once after restore");
            for (int frame = 0; frame < 3; frame++) yield return null;
            check(Value == baseline + 15, "option continuation has no delayed duplicate effect");

            string contentBefore = Cfg.TalkCfgMap[Final].content;
            try
            {
                Cfg.TalkCfgMap[Final].content += "配置已改变";
                yield return RejectUnchanged(adapter, atOptions, check, until, "配置", "changed configuration");
            }
            finally { Cfg.TalkCfgMap[Final].content = contentBefore; }

            // A self-contained synthetic Mod dependency exercises the real preflight validator.
            // No Mod package is loaded, downloaded, or altered.
            var modCtrl = Singleton<ModCtrl>.Ins;
            var modsBefore = modCtrl.activeMods;
            var profile = (ProfileModel)AccessTools.Field(typeof(ProfileMgr), "model").GetValue(Singleton<ProfileMgr>.Ins);
            var profileModsBefore = profile.modList;
            GameCheckpoint requiringMissingMod;
            try
            {
                var mods = modsBefore == null ? new List<ulong>() : new List<ulong>(modsBefore);
                ulong syntheticMod = 18446744073709500000UL;
                while (mods.Contains(syntheticMod)) syntheticMod--;
                mods.Add(syntheticMod);
                modCtrl.activeMods = mods;
                // Native ProfileMgr.Save copies activeMods into ProfileModel during CaptureWorld.
                requiringMissingMod = adapter.Capture();
            }
            finally { modCtrl.activeMods = modsBefore; profile.modList = profileModsBefore; }
            yield return RejectUnchanged(adapter, requiringMissingMod, check, until, "Mod", "missing synthetic Mod dependency");
            check(SaveMgr.GetPref("LatestSaveKey", "") == latest, "semantic restore and preflight rejection never modify LatestSaveKey");
        }
        finally
        {
            foreach (int id in talkIds) Cfg.TalkCfgMap.Remove(id);
            foreach (int id in optionIds) Cfg.OptionCfgMap.Remove(id);
            if (hadEvent) Cfg.EvtCfgMap[1] = priorEvent; else Cfg.EvtCfgMap.Remove(1);
        }
        yield return Restore(adapter, original, until, "semantic QA restores original isolated fixture");
        check(Value == baseline && SaveMgr.GetPref("LatestSaveKey", "") == latest,
            "semantic fixture cleans up world probes and preserves ordinary slot preference");
    }

    private static IEnumerator Restore(DialogueCheckpointAdapter adapter, GameCheckpoint checkpoint,
        Func<Func<bool>, float, string, IEnumerator> until, string label)
    {
        Task task = adapter.RestoreAsync(checkpoint);
        yield return until(() => task.IsCompleted, 40, label);
        if (task.IsFaulted) throw task.Exception;
        if (task.IsCanceled) throw new Exception(label + " unexpectedly cancelled");
        yield return until(() => { string reason; return adapter.CanCapture(out reason); }, 15, label + " capturable");
    }

    private static IEnumerator RejectUnchanged(DialogueCheckpointAdapter adapter, GameCheckpoint checkpoint,
        Action<bool, string> check, Func<Func<bool>, float, string, IEnumerator> until, string expectedReason, string label)
    {
        var before = Talk;
        var role = Singleton<RoleMgr>.Ins.GetRole();
        var world = AccessTools.Field(typeof(CommonEvtMgr), "model").GetValue(Singleton<CommonEvtMgr>.Ins);
        float beforeValue = Value;
        string beforeText = before.txtex_content.text;
        string beforeLatest = SaveMgr.GetPref("LatestSaveKey", "");
        Task task = adapter.RestoreAsync(checkpoint);
        yield return until(() => task.IsCompleted, 15, label + " preflight finished");
        check(task.IsFaulted && task.Exception.ToString().Contains(expectedReason), label + " rejected with relevant explanation");
        check(ReferenceEquals(before, Talk) && ReferenceEquals(role, Singleton<RoleMgr>.Ins.GetRole()) &&
            ReferenceEquals(world, AccessTools.Field(typeof(CommonEvtMgr), "model").GetValue(Singleton<CommonEvtMgr>.Ins)) &&
            Value == beforeValue && Talk.txtex_content.text == beforeText && SaveMgr.GetPref("LatestSaveKey", "") == beforeLatest,
            label + " rejected before changing world, live UI, or ordinary save preference");
    }
}
