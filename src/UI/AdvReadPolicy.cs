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
        static NewTalkView enteredView;
        static int enteredTalk;
        static bool readOnEntry;
        internal static bool CanSkip(NewTalkView view)
        {
            if (!Applies(view)) return true;
            var cfg=CfgField.GetValue(view) as TalkCfg;
            return cfg!=null && (AdvDialogueController.Active.Preferences.SkipUnread.Value ||
                (ReferenceEquals(enteredView,view) && enteredTalk==cfg.id ? readOnEntry : Singleton<GlobalMgr>.Ins.HasSeenTalk(cfg.id)));
        }
        static void Entering(NewTalkView __instance,int _talkId)
        {
            if(!Applies(__instance))return;
            enteredView=__instance;enteredTalk=_talkId;
            readOnEntry=Singleton<GlobalMgr>.Ins.HasSeenTalk(_talkId);
        }
        internal static bool PhoneBoundary(TalkCfg cfg)=>cfg?.screenEffect!=null && cfg.screenEffect.Count>0 &&
            (cfg.screenEffect[0]==4007 || cfg.screenEffect[0]==4008);
        // Clearing all roles in the middle of a call loses its remote participants.
        // Keep them until the native hang-up effect performs its own removal/reset.
        static void ClearForSkip(NewTalkView view)
        {
            if(!Applies(view) || !(bool)AccessTools.Field(typeof(NewTalkView),"isPhoneing").GetValue(view))
                AccessTools.Method(typeof(NewTalkView),"ClearRoles").Invoke(view,null);
        }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"RefreshTalk",new[]{typeof(int),typeof(bool)}),prefix:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Entering)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"OnClickSkip"),prefix:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Begin)),transpiler:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Boundaries)));
            harmony.Patch(AccessTools.Method(typeof(NewTalkView),"DoTextEnd"),prefix:new HarmonyMethod(typeof(AdvReadPolicy),nameof(Completed)));
        }
        static bool Applies(NewTalkView view)=>AdvDialogueController.Active?.UsesAdv(view)==true;
        static bool Begin(NewTalkView __instance)
        {
            if(!Applies(__instance))return true;
            var cfg=CfgField.GetValue(__instance) as TalkCfg;
            if(cfg==null)return true;
            return CanSkip(__instance);
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
            if(PhoneBoundary(cfg)) {view.RefreshTalk(cfg.id,false);return true;}
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
                if((instruction.opcode==OpCodes.Call || instruction.opcode==OpCodes.Callvirt) &&
                    Equals(instruction.operand,AccessTools.Method(typeof(NewTalkView),"ClearRoles")))
                {
                    var replacement=new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(AdvReadPolicy),nameof(ClearForSkip)));
                    replacement.labels.AddRange(instruction.labels);replacement.blocks.AddRange(instruction.blocks);
                    yield return replacement;continue;
                }
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
