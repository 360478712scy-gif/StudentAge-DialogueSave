using System.Collections.Generic;
using System.Reflection.Emit;
using Config;
using HarmonyLib;
using Sdk;
using View.Evt;

namespace StudentAgeDialogueSave.UI
{
    // Keep the native skip loop (including effects and conditional branches).
    // Check the actual reached node after each assignment, never predict a path.
    internal static class AdvReadPolicy
    {
        static readonly System.Reflection.FieldInfo CfgField=AccessTools.Field(typeof(NewTalkView),"cfg");
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnClickSkip"),prefix:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Begin)),transpiler:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Boundaries)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"DoTextEnd"),prefix:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Completed)));
        }
        static bool Applies(NewTalkView view)=>AdvDialogueController.Active?.UsesAdv(view)==true;
        static bool Begin(NewTalkView __instance)
        {
            if(!Applies(__instance))return true;
            var cfg=CfgField.GetValue(__instance) as TalkCfg;
            if(cfg==null)return true;
            return AdvDialogueController.Active.Preferences.SkipUnread.Value || Singleton<GlobalMgr>.Ins.HasSeenTalk(cfg.id);
        }
        static void Completed(NewTalkView __instance)
        {
            if(!Applies(__instance) || __instance.talkState!=TalkState.Anim || __instance.tmpTalkIdx+1<__instance.tmpTalks.Count)return;
            var cfg=CfgField.GetValue(__instance) as TalkCfg;
            if(cfg!=null)Singleton<GlobalMgr>.Ins.AddTalkToHistory(cfg.id);
        }
        static bool Reached(NewTalkView view)
        {
            if(!Applies(view))return false;
            var cfg=(TalkCfg)CfgField.GetValue(view);
            if(!AdvDialogueController.Active.Preferences.SkipUnread.Value && !Singleton<GlobalMgr>.Ins.HasSeenTalk(cfg.id))
            {view.RefreshTalk(cfg.id,false);return true;}
            return false;
        }
        static void RecordSkipped(NewTalkView view)
        {
            if(Applies(view) && CfgField.GetValue(view) is TalkCfg cfg)Singleton<GlobalMgr>.Ins.AddTalkToHistory(cfg.id);
        }
        static IEnumerable<CodeInstruction> Boundaries(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
        {
            foreach(var instruction in instructions)
            {
                // Record the node that was actually left, including an empty line.
                if(instruction.opcode==OpCodes.Stfld && Equals(instruction.operand,CfgField))
                {
                    yield return instruction;
                    var proceed=generator.DefineLabel();
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(AdvReadPolicy),nameof(Reached)));
                    yield return new CodeInstruction(OpCodes.Brfalse,proceed);
                    yield return new CodeInstruction(OpCodes.Ret);
                    var next=new CodeInstruction(OpCodes.Nop);next.labels.Add(proceed);yield return next;
                }
                else
                {
                    // Native branches call GetNextTalk after processing each node.
                    if(instruction.opcode==OpCodes.Call && instruction.operand is System.Reflection.MethodInfo method &&
                        method.Name=="GetNextTalk" && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType==typeof(TalkCfg))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(AdvReadPolicy),nameof(RecordSkipped)));
                    }
                    yield return instruction;
                    if(instruction.opcode==OpCodes.Stfld && Equals(instruction.operand,AccessTools.Field(typeof(NewTalkView),"isSkipped")))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(AdvReadPolicy),nameof(RecordSkipped)));
                    }
                }
            }
        }
    }
}
