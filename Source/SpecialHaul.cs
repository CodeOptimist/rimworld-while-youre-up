// TryFindBestBetterStorageFor() looks for both slot group and non-slot group (twice the search), returns preference for non-slot group


using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        public static readonly Dictionary<Pawn, SpecialHaul> specialHauls = new Dictionary<Pawn, SpecialHaul>();

        public class SpecialHaul
        {
            readonly string reportKey;
            LocalTargetInfo target;

            protected SpecialHaul() {
            }

            public SpecialHaul(string reportKey, LocalTargetInfo target) {
                this.reportKey = reportKey;
                this.target    = target;
            }

            public string GetReport(string text) {
                if (this is PuahWithBetterUnloading puah)
                    return puah.GetLoadReport(text);
                return reportKey.ModTranslate(text.Named("ORIGINAL"), target.Label.Named("DESTINATION"));
            }
        }

        public class PuahWithBetterUnloading : SpecialHaul
        {
            public Dictionary<ThingDef, IntVec3> defHauls = new Dictionary<ThingDef, IntVec3>();

            public virtual string GetLoadReport(string text)   => "PickUpAndHaulPlus_LoadReport".ModTranslate(text.Named("ORIGINAL"));
            public virtual string GetUnloadReport(string text) => "PickUpAndHaulPlus_UnloadReport".ModTranslate(text.Named("ORIGINAL"));

            public void TrackThing(Thing thing, IntVec3 storeCell, bool prepend = false, bool trackDef = true, [CallerMemberName] string callerName = "") {
#if DEBUG
                // make deterministic, but merges and initial hauls will still fluctuate
                storeCell = storeCell.GetSlotGroup(thing.Map).CellsList[0];
#endif
                if (trackDef)
                    defHauls.SetOrAdd(thing.def, storeCell);

                if (this is PuahOpportunity opportunity) {
                    // already here because a thing merged into it, or duplicate from HasJobOnThing()
                    // we want to recalculate with the newer store cell since some time has passed
                    if (opportunity.hauls.LastOrDefault().thing == thing)
                        opportunity.hauls.Pop();

                    // special case
                    if (prepend) {
                        if (opportunity.hauls.FirstOrDefault().thing == thing)
                            opportunity.hauls.RemoveAt(0);
                        opportunity.hauls.Insert(0, (thing, storeCell));
                    } else
                        opportunity.hauls.Add((thing, storeCell));
                }

                if (callerName != "TrackThingIfOpportune")
                    Debug.WriteLine($"{RealTime.frameCount} {this} {callerName}: {thing} -> {storeCell}");
            }
        } // ReSharper disable UnusedType.Local
        // ReSharper disable UnusedMember.Local
        // ReSharper disable UnusedParameter.Local

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

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ClearQueuedJobs))]
        static class Pawn_JobTracker__ClearQueuedJobs_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaul(Pawn ___pawn) {
                if (___pawn != null)
                    specialHauls.Remove(___pawn);
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
                        if (specialHauls.TryGetValue(__instance.pawn, out var specialHaul) && !(specialHaul is PuahWithBetterUnloading))
                            specialHauls.Remove(__instance.pawn);
                    });
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
        static class JobDriver_HaulToCell__GetReport_Patch
        {
            [HarmonyPostfix]
            static void SpecialHaulGetReport(JobDriver_HaulToCell __instance, ref string __result) {
                if (!specialHauls.TryGetValue(__instance.pawn, out var specialHaul)) return;
                __result = specialHaul.GetReport(__result.TrimEnd('.'));
            }
        }

        static partial class Patch_PUAH
        {
            [HarmonyPatch]
            static partial class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                [HarmonyPostfix]
                static void TrackInitialHaul(WorkGiver_Scanner __instance, Job __result, Pawn pawn, Thing thing) {
                    if (__result == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return;

                    if (!(specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)) {
                        puah = new PuahWithBetterUnloading();
                        specialHauls.SetOrAdd(pawn, puah);
                    }
                    // thing from parameter because targetA is null because things are in queues instead
                    //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                    // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if it's an Opportunity or HaulBeforeCarry
                    puah.TrackThing(thing, __result.targetB.Cell, prepend: true);
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => PuahUyhi_MakeNewToils;

                [HarmonyPostfix]
                static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
            }

            [HarmonyPatch]
            static class JobDriver__GetReport_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

                [HarmonyPostfix]
                static void SpecialHaulGetReport(JobDriver __instance, ref string __result) {
                    if (!settings.UsePickUpAndHaulPlus || !settings.Enabled) return;
                    if (PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance)) {
                        if (specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading puah)
                            __result = puah.GetLoadReport(__result.TrimEnd('.'));
                    }

                    if (PuahJobDriver_UnloadYourHauledInventoryType.IsInstanceOfType(__instance)) {
                        if (specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading puah)
                            __result = puah.GetUnloadReport(__result.TrimEnd('.'));
                    }
                }
            }
        }
    }
}
