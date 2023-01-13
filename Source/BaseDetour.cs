using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    partial class Mod
    {
        static readonly Dictionary<Pawn, BaseDetour> detours = new();

        public enum DetourType { Inactive, HtcOpportunity, HtcBeforeCarry, ExistingElsePuah, Puah, PuahOpportunity, PuahBeforeCarry }

        static bool AlreadyHauling(Pawn pawn) {
            if (detours.TryGetValue(pawn, out var detour) && detour.type != DetourType.Inactive) return true;

            // because we may load a game with an incomplete haul
            if (havePuah) {
                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var takenToInventory      = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value; // `Traverse` is cached
                if (takenToInventory is not null && takenToInventory.Any(t => t is not null))
                    return true;
            }

            return false;
        }

        // 'record' for a pretty `Debug.WriteLine(detour);`
        // can't use `?` operator with `record struct`, which makes usage with `detours` more annoying
        public partial record BaseDetour
        {
            public DetourType type;

            public record struct Puah(Dictionary<ThingDef, IntVec3> defHauls);
            public Puah puah = new() { defHauls = new Dictionary<ThingDef, IntVec3>(16) };

            // reminder that `storeCell` is just *some* cell in our stockpile, actual unload cell is determined at unload
            public record struct Opportunity(LocalTargetInfo jobTarget, List<(Thing thing, IntVec3 storeCell)> hauls)
            {
                public record struct OpportunityPuah(IntVec3 startCell, int unloadedTick)
                {
                    public static List<(Thing thing, IntVec3 storeCell)> haulsByUnloadDistanceOrdered;
                    public static List<(Thing thing, IntVec3 storeCell)> haulsByUnloadDistancePending;
                }
                public OpportunityPuah puah = new();
            }
            public Opportunity opportunity = new() { hauls = new List<(Thing thing, IntVec3 storeCell)>(16) };

            public record struct BeforeCarry(LocalTargetInfo carryTarget)
            {
                public record struct BeforeCarryPuah(IntVec3 storeCell);
                public BeforeCarryPuah puah = new();
            }
            // ReSharper disable once RedundantDefaultMemberInitializer
            public BeforeCarry beforeCarry = new();

            public void Deactivate() {
                type = DetourType.Inactive;
                puah.defHauls.Clear();
                opportunity.hauls.Clear();
            }

            public void TrackPuahThing(Thing thing, IntVec3 storeCell, bool prepend = false, bool trackDef = true) {
                if (trackDef)
                    puah.defHauls.SetOrAdd(thing.def, storeCell);

                if (type == DetourType.PuahOpportunity) {
                    // already here because a thing merged into it, or duplicate from `HasJobOnThing()`
                    // we want to recalculate with the newer store cell since some time has passed
                    // (this check does not belong in the `else` below)
                    if (opportunity.hauls.LastOrDefault().thing == thing)
                        opportunity.hauls.Pop();

                    if (prepend) {
                        if (opportunity.hauls.FirstOrDefault().thing == thing)
                            opportunity.hauls.RemoveAt(0);
                        opportunity.hauls.Insert(0, (thing, storeCell));
                    } else
                        opportunity.hauls.Add((thing, storeCell));
                }
            }

            public void GetJobReport(ref string text, bool isLoad) {
                if (type == DetourType.Inactive) return;

                text = text.TrimEnd('.');
                var suffix = isLoad ? "_LoadReport" : "_UnloadReport";
                text = type switch {
                    DetourType.Puah => ("PickUpAndHaulPlus" + suffix).ModTranslate(text.Named("ORIGINAL")),
                    DetourType.HtcOpportunity or DetourType.PuahOpportunity
                        => ("Opportunity" + suffix).ModTranslate(text.Named("ORIGINAL"), opportunity.jobTarget.Label.Named("DESTINATION")),
                    DetourType.HtcBeforeCarry or DetourType.PuahBeforeCarry
                        => ("HaulBeforeCarry" + suffix).ModTranslate(text.Named("ORIGINAL"), beforeCarry.carryTarget.Label.Named("DESTINATION")),
                    _ => text,
                };
            }
        }

        static BaseDetour SetOrAddDetour(Pawn pawn, DetourType type,
            IntVec3? startCell = null, LocalTargetInfo? jobTarget = null,
            IntVec3? storeCell = null, LocalTargetInfo? carryTarget = null) {
            if (!detours.TryGetValue(pawn, out var detour)) {
                detour        = new BaseDetour();
                detours[pawn] = detour;
            }

            BaseDetour Result(BaseDetour result) {
                Debug.WriteLine($"{pawn} {type}; {result.type}");
                return result;
            }

            if (type == DetourType.ExistingElsePuah) {
                if (detour.type is DetourType.PuahOpportunity or DetourType.PuahBeforeCarry)
                    return Result(detour);

                type = DetourType.Puah;
            }

            detour.opportunity.puah.startCell = startCell ?? IntVec3.Invalid;
            detour.opportunity.jobTarget      = jobTarget ?? LocalTargetInfo.Invalid;
            detour.beforeCarry.puah.storeCell = storeCell ?? IntVec3.Invalid;
            detour.beforeCarry.carryTarget    = carryTarget ?? LocalTargetInfo.Invalid;

            detour.Deactivate(); // wipe lists
            detour.type = type;  // reactivate
            return Result(detour);
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

                // PUAH has a `haulMoreWork` toil that can re-trigger `JobOnThing()` for every type of detour
                var detour = SetOrAddDetour(pawn, DetourType.ExistingElsePuah);
                // thing from parameter because targetA is null because things are in queues instead
                //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
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
                        if (detour?.type is DetourType.HtcOpportunity or DetourType.HtcBeforeCarry)
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
                            // :RepeatOpportunity
                            if (detour.type == DetourType.PuahOpportunity)
                                detour.opportunity.puah.unloadedTick = RealTime.frameCount;
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
