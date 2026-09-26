using HarmonyLib;
using MiniGame.Fight;

namespace StudentAgeDialogueSave.GameIntegration
{
    internal static class NativeFightInputFix
    {
        internal static void Install(Harmony harmony)=>harmony.Patch(AccessTools.Method(typeof(FightMiniGameView),"InitUI"),postfix:new HarmonyMethod(typeof(NativeFightInputFix),nameof(Initialized)));
        static void Initialized(FightMiniGameView __instance)
        {
            // Native InitUI wires start/guide, but omits skip. Use the native key
            // handler so visibility/eligibility and settlement stay authoritative.
            __instance.btn_skip.AddClick(()=>__instance.OnHotKeyInput(122));
        }
    }
}
