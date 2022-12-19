using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        public static readonly Dictionary<Pawn, BaseDetour> detours = new();

        public enum DetourType { Inactive, Opportunity, BeforeCarry, ExistingElsePuah, Puah, PuahOpportunity, PuahBeforeCarry }

        // 'record' for a pretty `Debug.WriteLine(detour);`
        public partial record BaseDetour
        {
            public DetourType type;

            public Dictionary<ThingDef, IntVec3> puah_defHauls = new();

            public IntVec3         opportunity_puah_startCell;
            public LocalTargetInfo opportunity_jobTarget; // vanilla & PUAH
            public int             opportunity_puah_unloadedTick;
            // reminder that storeCell is just *some* cell in our stockpile, actual unload cell is determined at unload
            public List<(Thing thing, IntVec3 storeCell)> opportunity_hauls = new();

            public IntVec3         beforeCarry_puah_storeCell;
            public LocalTargetInfo beforeCarry_carryTarget; // vanilla & PUAH

            public void Deactivate() {
                type = DetourType.Inactive;
                puah_defHauls.Clear();
                opportunity_hauls.Clear();
            }

            public void TrackPuahThing(Thing thing, IntVec3 storeCell, bool prepend = false, bool trackDef = true) {
                if (trackDef)
                    puah_defHauls.SetOrAdd(thing.def, storeCell);
                if (type == DetourType.PuahOpportunity) {
                    // already here because a thing merged into it, or duplicate from HasJobOnThing()
                    // we want to recalculate with the newer store cell since some time has passed
                    if (opportunity_hauls.LastOrDefault().thing == thing)
                        opportunity_hauls.Pop();

                    if (prepend) {
                        if (opportunity_hauls.FirstOrDefault().thing == thing)
                            opportunity_hauls.RemoveAt(0);
                        opportunity_hauls.Insert(0, (thing, storeCell));
                    } else
                        opportunity_hauls.Add((thing, storeCell));
                }
            }

            public void GetJobReport(ref string text, bool isLoad) {
                if (type == DetourType.Inactive) return;
                text = text.TrimEnd('.');
                var suffix = isLoad ? "_LoadReport" : "_UnloadReport";
                text = type switch {
                    DetourType.Puah => ("PickUpAndHaulPlus" + suffix).ModTranslate(text.Named("ORIGINAL")),
                    DetourType.Opportunity or DetourType.PuahOpportunity
                        => ("Opportunity" + suffix).ModTranslate(text.Named("ORIGINAL"), opportunity_jobTarget.Label.Named("DESTINATION")),
                    DetourType.BeforeCarry or DetourType.PuahBeforeCarry
                        => ("HaulBeforeCarry" + suffix).ModTranslate(text.Named("ORIGINAL"), beforeCarry_carryTarget.Label.Named("DESTINATION")),
                    _ => text,
                };
            }
        }

        public static BaseDetour SetOrAddDetour(Pawn pawn, DetourType type,
            IntVec3? startCell = null, LocalTargetInfo? jobTarget = null,
            IntVec3? storeCell = null, LocalTargetInfo? carryTarget = null) {
            if (!detours.TryGetValue(pawn, out var detour)) {
                detour        = new BaseDetour();
                detours[pawn] = detour;
            }

            if (type == DetourType.ExistingElsePuah) {
                if (detour.type is DetourType.PuahOpportunity or DetourType.PuahBeforeCarry)
                    return detour;
                type = DetourType.Puah;
            }

            detour.opportunity_puah_startCell = startCell ?? IntVec3.Invalid;
            detour.opportunity_jobTarget      = jobTarget ?? LocalTargetInfo.Invalid;
            detour.beforeCarry_puah_storeCell = storeCell ?? IntVec3.Invalid;
            detour.beforeCarry_carryTarget    = carryTarget ?? LocalTargetInfo.Invalid;

            detour.Deactivate(); // wipe lists
            detour.type = type;  // reactivate
            return detour;
        }

        static Job PuahJob(Pawn pawn, Thing thing) {
            var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker; // dictionary lookup
            return (Job)PuahMethod_WorkGiver_HaulToInventory_JobOnThing.Invoke(puahWorkGiver, new object[] { pawn, thing, false });
        }

        partial class Puah_WorkGiver_HaulToInventory__JobOnThing_Patch
        {
            [HarmonyPostfix]
            static void TrackInitialHaul(Job __result, Pawn pawn, Thing thing) {
                if (__result is null || !settings.Enabled || !settings.UsePickUpAndHaulPlus) return;
                // thing from parameter because targetA is null because things are in queues instead
                //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                // PUAH has a `haulMoreWork` toil that can re-trigger `JobOnThing()` for every type of detour
                var detour = SetOrAddDetour(pawn, DetourType.ExistingElsePuah);
                detour.TrackPuahThing(thing, __result.targetB.Cell, prepend: true);
            }
        }

    #region reports
        [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
        static class JobDriver_HaulToCell__GetReport_Patch
        {
            [HarmonyPostfix]
            static void GetDetourReport(JobDriver_HaulToCell __instance, ref string __result) => detours.GetValueSafe(__instance.pawn)?.GetJobReport(ref __result, isLoad: true);
        }

        [HarmonyPatch]
        static class Puah_JobDriver__GetReport_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

            [HarmonyPostfix]
            static void GetDetourReport(JobDriver __instance, ref string __result) {
                if (!settings.Enabled || !settings.UsePickUpAndHaulPlus) return;

                var isLoad   = PuahType_JobDriver_HaulToInventory.IsInstanceOfType(__instance);
                var isUnload = PuahType_JobDriver_UnloadYourHauledInventory.IsInstanceOfType(__instance);
                if (isLoad || isUnload)
                    detours.GetValueSafe(__instance.pawn)?.GetJobReport(ref __result, isLoad);
            }
        }
    #endregion

    #region cleanup
        [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
        static class JobDriver_HaulToCell__MakeNewToils_Patch
        {
            [HarmonyPostfix]
            static void ClearDetourOnFinish(JobDriver __instance) =>
                __instance.AddFinishAction(
                    () => {
                        var detour = detours.GetValueSafe(__instance.pawn);
                        if (detour?.type is DetourType.Opportunity or DetourType.BeforeCarry)
                            detour.Deactivate();
                    });
        }

        [HarmonyPatch]
        static class Puah_JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils;

            [HarmonyPostfix]
            static void ClearDetourOnFinish(JobDriver __instance) =>
                __instance.AddFinishAction(
                    () => {
                        var detour = detours.GetValueSafe(__instance.pawn);
                        if (detour is not null) {
                            // #AvoidConsecutiveOpportunities 
                            if (detour.type == DetourType.PuahOpportunity)
                                detour.opportunity_puah_unloadedTick = RealTime.frameCount;
                            detour.Deactivate();
                        }
                    });
        }

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ClearQueuedJobs))]
        static class Pawn_JobTracker__ClearQueuedJobs_Patch
        {
            [HarmonyPostfix]
            static void ClearDetour(Pawn ___pawn) {
                if (___pawn is not null)
                    detours.GetValueSafe(___pawn)?.Deactivate();
            }
        }

        [HarmonyPatch(typeof(Pawn), nameof(Pawn.Destroy))]
        static class Pawn__Destroy_Patch
        {
            [HarmonyPostfix]
            static void ClearDetour(Pawn __instance) => detours.Remove(__instance);
        }
    #endregion
    }
}
