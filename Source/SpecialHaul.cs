using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        public static readonly Dictionary<Pawn, SpecialHaul> specialHauls = new Dictionary<Pawn, SpecialHaul>();

        public class SpecialHaul
        {
            public string reportPrefix;

            public SpecialHaul(string reportPrefix) {
                this.reportPrefix = reportPrefix;
            }
        }

        public class PuahWithBetterUnloading : SpecialHaul
        {
            public Dictionary<ThingDef, IntVec3> defHauls = new Dictionary<ThingDef, IntVec3>();

            public PuahWithBetterUnloading(string reportPrefix = "") : base(reportPrefix) {
            }

            public void TrackThing(Thing thing, IntVec3 storeCell, bool isInitial = false, [CallerMemberName] string callerName = "") {
#if DEBUG
                // make deterministic, but merges and initial hauls will still fluctuate
                storeCell = storeCell.GetSlotGroup(thing.Map).CellsList[0];
#endif

                defHauls.SetOrAdd(thing.def, storeCell);

                if (this is PuahOpportunity opportunity) {
                    if (isInitial && opportunity.hauls.FirstOrDefault().thing == thing)
                        opportunity.hauls.RemoveAt(0);

                    opportunity.hauls.Insert(isInitial ? 0 : opportunity.hauls.Count, (thing, storeCell));
                }

                if (callerName != "TrackPuahThingIfOpportune")
                    Debug.WriteLine($"{RealTime.frameCount} {this} {callerName}() {thing} -> {storeCell}");
            }
        }

        public class PuahOpportunity : PuahWithBetterUnloading
        {
            public IntVec3 jobCell;
            public IntVec3 startCell;

            // reminder that storeCell is just *some* cell in our stockpile, actual unload cell is determined at unload
            public List<(Thing thing, IntVec3 storeCell)> hauls = new List<(Thing thing, IntVec3 storeCell)>();

            public PuahOpportunity(Pawn pawn, IntVec3 jobCell) : base("Opportunistically ") {
                startCell = pawn.Position;
                this.jobCell = jobCell;
            }
        }

        public class PuahBeforeCarry : PuahWithBetterUnloading
        {
            public IntVec3 destCell;

            public PuahBeforeCarry(IntVec3 destCell) : base("Optimally ") {
                this.destCell = destCell;
            }
        } // ReSharper disable UnusedType.Local
        // ReSharper disable UnusedMember.Local
        // ReSharper disable UnusedParameter.Local

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ClearQueuedJobs))]
        static class Pawn_JobTracker__ClearQueuedJobs_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaul(Pawn ___pawn) => specialHauls.Remove(___pawn);
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
        static class JobDriver_HaulToCell__MakeNewToils_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) =>
                __instance.AddFinishAction(
                    () => {
                        // puah special will be removed after unloading
                        if (specialHauls.TryGetValue(__instance.pawn, out var specialHaul) && !(specialHaul is PuahWithBetterUnloading))
                            specialHauls.Remove(__instance.pawn);
                    });
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
        static class JobDriver_HaulToCell__GetReport_Patch
        {
            [HarmonyPostfix]
            static void SpecialHaulJobReport(JobDriver_HaulToCell __instance, ref string __result) {
                if (!specialHauls.TryGetValue(__instance.pawn, out var specialHaul)) return;
                __result = specialHaul.reportPrefix + __result;
            }
        }

        static partial class Patch_PUAH
        {
            [HarmonyPatch]
            static partial class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                [HarmonyPostfix]
                static void TrackInitialHaul(WorkGiver_Scanner __instance, Job __result, Pawn pawn, Thing thing) {
                    if (__result == null || !settings.HaulToInventory || !settings.Enabled) return;

                    if (!(specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)) {
                        puah = new PuahWithBetterUnloading();
                        specialHauls.SetOrAdd(pawn, puah);
                    }
                    // thing from parameter because targetA is null because things are in queues instead
                    //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                    // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if it's an Opportunity or HaulBeforeCarry
                    puah.TrackThing(thing, __result.targetB.Cell, isInitial: true);
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "MakeNewToils");

                [HarmonyPostfix]
                static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
            }

            [HarmonyPatch]
            static class JobDriver__GetReport_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

                [HarmonyPostfix]
                static void SpecialHaulJobReport(JobDriver __instance, ref string __result) {
                    if (!settings.HaulToInventory || !settings.Enabled) return;

                    if (PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance)) {
                        if (!specialHauls.TryGetValue(__instance.pawn, out var specialHaul)) return;
                        __result = specialHaul.reportPrefix + __result;
                    }

                    if (PuahJobDriver_UnloadYourHauledInventoryType.IsInstanceOfType(__instance))
                        __result = "Efficiently " + __result;
                }
            }
        }
    }
}
