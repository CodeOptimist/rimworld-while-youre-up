using HarmonyLib;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

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
                static void AddJooFinish(JobDriver __instance) {
                    __instance.AddFinishAction(() => Hauling.pawnHaulToCell.Remove(__instance.pawn));
                }
            }

            [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
            static class JobDriver_HaulToCell_GetReport_Patch
            {
                [HarmonyPostfix]
                static void GetJooReportString(JobDriver_HaulToCell __instance, ref string __result) {
                    if (!Hauling.pawnHaulToCell.ContainsKey(__instance.pawn)) return;
                    __result = $"Opportunistically {__result}";
                }
            }
        }
    }
}
