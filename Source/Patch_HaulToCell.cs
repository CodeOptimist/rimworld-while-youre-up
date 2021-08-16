using HarmonyLib;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
        static class JobDriver_HaulToCell_MakeNewToils_Patch
        {
            [HarmonyPostfix]
            static void ClearTrackingAfterHaul(JobDriver __instance) {
                __instance.AddFinishAction(
                    () => {
                        haulTrackers.Remove(__instance.pawn);
                        Debug.WriteLine($"{RealTime.frameCount} {__instance.pawn} Finished Haul. Wiped tracking.");
                    });
            }
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
        static class JobDriver_HaulToCell_GetReport_Patch
        {
            [HarmonyPostfix]
            static void CustomJobReport(JobDriver_HaulToCell __instance, ref string __result) {
                if (!haulTrackers.TryGetValue(__instance.pawn, out var haulTracker)) return;
                __result = haulTracker.GetJobReportPrefix() + __result;
            }
        }
    }
}
