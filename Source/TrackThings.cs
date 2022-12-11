using System.Collections.Generic;
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
        public static readonly Dictionary<Pawn, HaulDetour> haulDetours = new Dictionary<Pawn, HaulDetour>();

        static Job PuahJob(PuahDetour puahDetour, Pawn pawn, Thing thing, IntVec3 storeCell) {
            if (!settings.Enabled || !havePuah || !settings.UsePickUpAndHaulPlus) return null;
            haulDetours.SetOrAdd(pawn, puahDetour);
            puahDetour.TrackThing(thing, storeCell);
            var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker; // dictionary lookup
            return (Job)PuahMethod_WorkGiver_HaulToInventory_JobOnThing.Invoke(puahWorkGiver, new object[] { pawn, thing, false });
        }

        public static bool AlreadyHauling(Pawn pawn) {
            if (haulDetours.ContainsKey(pawn)) return true;

            // because we may load a game with an incomplete haul
            if (havePuah) {
                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var takenToInventory      = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value; // traverse is cached
                if (takenToInventory != null && takenToInventory.Any(t => t != null))
                    return true;
            }

            return false;
        }

        public abstract class HaulDetour
        {
            public          LocalTargetInfo destTarget;
            public abstract string          GetLoadReport(string text);
        }

        // technically normal PUAH hauls aren't a detour, unless you count the unloading improvements...
        public class PuahDetour : HaulDetour
        {
            public Dictionary<ThingDef, IntVec3> defHauls = new Dictionary<ThingDef, IntVec3>();

            public override string GetLoadReport(string text)   => "PickUpAndHaulPlus_LoadReport".ModTranslate(text.Named("ORIGINAL"));
            public virtual  string GetUnloadReport(string text) => "PickUpAndHaulPlus_UnloadReport".ModTranslate(text.Named("ORIGINAL"));

            // todo do we really care if this is a class method???
            public void TrackThing(Thing thing, IntVec3 storeCell, bool prepend = false, bool trackDef = true, [CallerMemberName] string callerName = "") {
#if DEBUG
                // make deterministic, but merges and initial hauls will still fluctuate
                storeCell = storeCell.GetSlotGroup(thing.Map).CellsList[0];
#endif
                if (trackDef)
                    defHauls.SetOrAdd(thing.def, storeCell);

                if (this is PuahOpportunityDetour opportunity) {
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
                if (!haulDetours.TryGetValue(__instance.pawn, out var specialHaul)) return;
                __result = specialHaul.GetLoadReport(__result.TrimEnd('.'));
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
                // this nesting order so we don't lookup outside of our own mod code
                if (PuahType_JobDriver_HaulToInventory.IsInstanceOfType(__instance)) {
                    if (haulDetours.GetValueSafe(__instance.pawn) is PuahDetour puahDetour)
                        __result = puahDetour.GetLoadReport(__result.TrimEnd('.'));
                } else if (PuahType_JobDriver_UnloadYourHauledInventory.IsInstanceOfType(__instance)) {
                    if (haulDetours.GetValueSafe(__instance.pawn) is PuahDetour puahDetour)
                        __result = puahDetour.GetUnloadReport(__result.TrimEnd('.'));
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
                        if (haulDetours.TryGetValue(__instance.pawn, out var specialHaul) && !(specialHaul is PuahDetour))
                            haulDetours.Remove(__instance.pawn);
                    });
        }

        [HarmonyPatch]
        static class Puah_JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils;

            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => haulDetours.Remove(__instance.pawn));
        }

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ClearQueuedJobs))]
        static class Pawn_JobTracker__ClearQueuedJobs_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaul(Pawn ___pawn) {
                if (___pawn != null)
                    haulDetours.Remove(___pawn);
            }
        }

        [HarmonyPostfix]
        static void TrackInitialHaul(WorkGiver_Scanner __instance, Job __result, Pawn pawn, Thing thing) {
            if (__result == null || !settings.Enabled || !settings.UsePickUpAndHaulPlus) return;

            if (!(haulDetours.GetValueSafe(pawn) is PuahDetour puahDetour)) {
                puahDetour = new PuahDetour();
                haulDetours.SetOrAdd(pawn, puahDetour);
            }

            // thing from parameter because targetA is null because things are in queues instead
            //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
            // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if it's an Opportunity or HaulBeforeCarry
            puahDetour.TrackThing(thing, __result.targetB.Cell, prepend: true);
        }
    }
}
