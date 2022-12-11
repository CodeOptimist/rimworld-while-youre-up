﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        public static readonly Dictionary<Pawn, SpecialHaul> specialHauls = new Dictionary<Pawn, SpecialHaul>();

        static Job PuahJob(PuahWithBetterUnloading puah, Pawn pawn, Thing thing, IntVec3 storeCell) {
            if (!settings.Enabled || !havePuah || !settings.UsePickUpAndHaulPlus) return null;
            specialHauls.SetOrAdd(pawn, puah);
            puah.TrackThing(thing, storeCell);
            var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker; // dictionary lookup
            return (Job)PuahMethod_WorkGiver_HaulToInventory_JobOnThing.Invoke(puahWorkGiver, new object[] { pawn, thing, false });
        }

        public static bool AlreadyHauling(Pawn pawn) {
            if (specialHauls.ContainsKey(pawn)) return true;

            // because we may load a game with an incomplete haul
            if (havePuah) {
                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var takenToInventory      = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value; // traverse is cached
                if (takenToInventory != null && takenToInventory.Any(t => t != null))
                    return true;
            }

            return false;
        }

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

            // todo do we really care if this is a class method???
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

                // todo
                if (callerName != "TrackThingIfOpportune")
                    Debug.WriteLine($"{RealTime.frameCount} {this} {callerName}: {thing} -> {storeCell}");
            }
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

        [HarmonyPatch]
        static class Puah_JobDriver__GetReport_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

            [HarmonyPostfix]
            static void SpecialHaulGetReport(JobDriver __instance, ref string __result) {
                if (!settings.Enabled || !settings.UsePickUpAndHaulPlus) return;
                if (PuahType_JobDriver_HaulToInventory.IsInstanceOfType(__instance)) {
                    if (specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading puah)
                        __result = puah.GetLoadReport(__result.TrimEnd('.'));
                }

                if (PuahType_JobDriver_UnloadYourHauledInventory.IsInstanceOfType(__instance)) {
                    if (specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading puah)
                        __result = puah.GetUnloadReport(__result.TrimEnd('.'));
                }
            }
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
        static class JobDriver_HaulToCell__MakeNewToils_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) =>
                __instance.AddFinishAction(
                    () => {
                        if (specialHauls.TryGetValue(__instance.pawn, out var specialHaul) && !(specialHaul is PuahWithBetterUnloading))
                            specialHauls.Remove(__instance.pawn);
                    });
        }

        [HarmonyPatch]
        static class Puah_JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils;

            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
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

        [HarmonyPostfix]
        static void TrackInitialHaul(WorkGiver_Scanner __instance, Job __result, Pawn pawn, Thing thing) {
            if (__result == null || !settings.Enabled || !settings.UsePickUpAndHaulPlus) return;

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
}
