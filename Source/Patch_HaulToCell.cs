using HarmonyLib;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_HaulToCell
        {
            [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
            static class JobDriver_HaulToCell_MakeNewToils_Patch
            {
                [HarmonyPostfix]
                static void ClearHaulTypeAtFinish(JobDriver __instance) {
                    __instance.AddFinishAction(
                        () => {
                            if (!haulTrackers.TryGetValue(__instance.pawn, out var haulTracker)) return;
                            haulTracker.haulType = SpecialHaulType.None;
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
}
