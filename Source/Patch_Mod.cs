using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// for Harmony patches
// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [HarmonyPatch(typeof(JobUtility), nameof(JobUtility.TryStartErrorRecoverJob))]
        static class JobUtility__TryStartErrorRecoverJob_Patch
        {
            static int    lastFrameCount;
            static Pawn   lastPawn;
            static string lastCallerName;

            [HarmonyPrefix]
            static void OfferSupport(Pawn pawn) {
                if (RealTime.frameCount == lastFrameCount && pawn == lastPawn) {
                    Log.Warning(
                        $"[{mod.Content.Name}] You're welcome to 'Share logs' to my Discord: https://discord.gg/pnZGQAN \n" +
                        $"[{mod.Content.Name}] Below \"10 jobs in one tick\" error occurred during {lastCallerName}, but could be from several mods.");
                }
            }

            public static Job CatchStanding(Pawn pawn, Job job, [CallerMemberName] string callerName = "") {
                lastPawn       = pawn;
                lastFrameCount = RealTime.frameCount;
                lastCallerName = callerName;
                return job;
            }
        }

        [HarmonyPatch]
        static class Dialog_ModSettings__Dialog_ModSettings_Patch
        {
            static MethodBase TargetMethod() {
                if (haveHugs)
                    return AccessTools.DeclaredConstructor(HugsType_Dialog_VanillaModSettings, new[] { typeof(Verse.Mod) });
                return AccessTools.DeclaredConstructor(typeof(Dialog_ModSettings), new[] { typeof(Verse.Mod) });
            }

            [HarmonyPostfix]
            static void SyncDrawSettingToVanilla() => settings.DrawSpecialHauls = DebugViewSettings.drawOpportunisticJobs;
        }

        [HarmonyPatch]
        static class Dialog_ModSettings__DoWindowContents_Patch
        {
            static MethodBase TargetMethod() {
                if (haveHugs)
                    return AccessTools.DeclaredMethod(HugsType_Dialog_VanillaModSettings, "DoWindowContents");
                return AccessTools.DeclaredMethod(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents));
            }

            [HarmonyPostfix]
            static void CheckCommonSenseSetting(object __instance) {
                var curMod = SettingsCurModField.GetValue(__instance);

                if (settings.HaulBeforeCarry_Bills && haveCommonSense && (bool)CsField_Settings_HaulingOverBills.GetValue(null)) {
                    var csMod = LoadedModManager.GetMod(CsType_CommonSense);
                    if (curMod == mod) {
                        CsField_Settings_HaulingOverBills.SetValue(null, false);
                        csMod.WriteSettings();
                        Messages.Message(
                            $"[{mod.Content.Name}] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                    } else if (curMod == csMod) {
                        settings.HaulBeforeCarry_Bills = false;
                        //mod.WriteSettings(); // no save because we handle it best on loading
                        Messages.Message(
                            $"[{mod.Content.Name}] Unticked setting in While You're Up: \"Haul extra bill ingredients closer\". (Can't use both.)",
                            MessageTypeDefOf.SilentInput,
                            false);
                    }
                }
            }
        }
    }
}
