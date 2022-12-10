using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing))]
        static class WorkGiver_Scanner__HasJobOnThing_Patch
        {
            [HarmonyPrefix]
            static void CheckForSpecialHaul(out bool __state, Pawn pawn) {
                __state = specialHauls.ContainsKey(pawn);
            }

            [HarmonyPostfix]
            static void ClearTempSpecialHaul(bool __state, Pawn pawn) {
                if (!__state)
                    specialHauls.Remove(pawn);
            }
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
        static class JobDriver_HaulToCell__MakeNewToils_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) =>
                __instance.AddFinishAction(
                    () => {
                        // puah special will be removed after unloading
                        // todo perf?
                        if (specialHauls.TryGetValue(__instance.pawn, out var specialHaul) && !(specialHaul is PuahWithBetterUnloading))
                            specialHauls.Remove(__instance.pawn);
                    });
        }

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ClearQueuedJobs))]
        static class Pawn_JobTracker__ClearQueuedJobs_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaul(Pawn ___pawn) {
                if (___pawn != null)
                    specialHauls.Remove(___pawn);
            }
        }
    }
}
