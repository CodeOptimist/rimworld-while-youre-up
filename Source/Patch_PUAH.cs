using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_PUAH
        {
            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor_Patch
            {
                static bool Prepare() {
                    return PuahWorkGiver_HaulToInventory_Type != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahWorkGiver_HaulToInventory_Type, "TryFindBestBetterStoreCellFor");
                }

                // take advantage of our cache simply because we can
                [HarmonyPrefix]
                static bool UseJooCachedStoreCell(ref bool __result, HashSet<IntVec3> ___skipCells, Thing thing, ref IntVec3 foundCell) {
                    if (Hauling.cachedStoreCell.TryGetValue(thing, out var cachedCell)) {
                        if (___skipCells.Contains(cachedCell)) return true;
                        foundCell = cachedCell;
                        ___skipCells.Add(cachedCell);
                        __result = true;
                        return false;
                    }

                    return true;
                }

                [HarmonyPostfix]
                internal static void RejectTooFar(ref bool __result, Thing thing, Pawn carrier, ref IntVec3 foundCell) {
                    if (foundCell == IntVec3.Invalid) return;
                    if (!Hauling.pawnPuah.TryGetValue(carrier, out var forPuah)) return;

                    var hauls = new List<(Thing thing, IntVec3 store)>(forPuah.hauls);
                    var latestHaul = (thing, store: foundCell);
                    if (hauls.Last().thing != latestHaul.thing)
                        hauls.Add(latestHaul);

                    var startToLatestThing = 0f;
                    var curPos = forPuah.startCell;
                    foreach (var (thing_, _) in hauls) {
                        startToLatestThing += curPos.DistanceTo(thing_.Position);
                        curPos = thing_.Position;
                    }

                    // actual unloading cells are determined on-the-fly, but these will represent the parent stockpiles with equal correctness
                    // may also be extras if don't all fit in one cell, etc.
                    var haulsByUnloadOrder = hauls.GetRange(0, hauls.Count - 1).OrderBy(haul => haul.thing.def.FirstThingCategory?.index).ToList();
                    haulsByUnloadOrder.Insert(0, latestHaul); // already holding this one

                    var storeToLastStore = 0f;
                    var curPos_ = latestHaul.store;
                    foreach (var (_, store) in haulsByUnloadOrder) {
                        storeToLastStore += curPos_.DistanceTo(store);
                        curPos_ = store;
                    }

                    var latestThingToStore = latestHaul.thing.Position.DistanceTo(latestHaul.store);
                    var lastStoreToJob = haulsByUnloadOrder.Last().store.DistanceTo(forPuah.jobCell);
                    var origTrip = forPuah.startCell.DistanceTo(forPuah.jobCell);
                    var totalTrip = startToLatestThing + latestThingToStore + storeToLastStore + lastStoreToJob;
                    var maxTotalTrip = origTrip * maxTotalTripPctOrigTrip.Value;
                    var newLegs = startToLatestThing + storeToLastStore + lastStoreToJob;
                    var maxNewLegs = origTrip * maxNewLegsPctOrigTrip.Value;
                    var exceedsMaxTrip = maxTotalTripPctOrigTrip.Value > 0 && totalTrip > maxTotalTrip;
                    var exceedsMaxNewLegs = maxNewLegsPctOrigTrip.Value > 0 && newLegs > maxNewLegs;
                    var isRejected = exceedsMaxTrip || exceedsMaxNewLegs;

                    Debug.WriteLine($"{(isRejected ? "REJECTED" : "APPROVED")} {latestHaul} for {carrier}");
                    Debug.WriteLine($"\tstartToLatestThing: {carrier}{forPuah.startCell} -> {string.Join(" -> ", hauls.Select(x => $"{x.thing}{x.thing.Position}"))} = {startToLatestThing}");
                    Debug.WriteLine($"\tlatestThingToStore: {latestHaul.thing}{latestHaul.thing.Position} -> {latestHaul} = {latestThingToStore}");
                    Debug.WriteLine($"\tstoreToLastStore: {string.Join(" -> ", haulsByUnloadOrder)} = {storeToLastStore}");
                    Debug.WriteLine($"\tlastStoreToJob: {haulsByUnloadOrder.Last()} -> {forPuah.jobCell} = {lastStoreToJob}");
                    Debug.WriteLine($"\torigTrip: {carrier}{forPuah.startCell} -> {forPuah.jobCell} = {origTrip}");
                    Debug.WriteLine($"\ttotalTrip: {startToLatestThing} + {latestThingToStore} + {storeToLastStore} + {lastStoreToJob}  = {totalTrip}");
                    Debug.WriteLine($"\tmaxTotalTrip: {origTrip} * {maxTotalTripPctOrigTrip.Value} = {maxTotalTrip}");
                    Debug.WriteLine($"\tnewLegs: {startToLatestThing} + {storeToLastStore} + {lastStoreToJob} = {newLegs}");
                    Debug.WriteLine($"\tmaxNewLegs: {origTrip} * {maxNewLegsPctOrigTrip.Value} = {maxNewLegs}");
                    Debug.WriteLine("");

                    if (isRejected) {
                        foundCell = IntVec3.Invalid;
                        __result = false;
                        return;
                    }

                    forPuah.hauls = hauls;
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_HasJobOnThing_Patch
            {
                static bool Prepare() {
                    return PuahWorkGiver_HaulToInventory_Type != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahWorkGiver_HaulToInventory_Type, "HasJobOnThing");
                }

                [HarmonyPrefix]
                static void ClearJooDataForNewHaul(Pawn pawn) {
                    // keep for the haulMoreWork toil that extends our path, otherwise clear it for fresh distance calculations
                    if (pawn.CurJobDef?.defName != "HaulToInventory") Hauling.pawnPuah.Remove(pawn);
                }

                // we need to patch PUAH's use of vanilla TryFindBestBetterStoreCellFor within HasJobOnThing for the haulMoreWork toil
                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> _RejectTooFar(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.Method(typeof(WorkGiver_HaulToInventory_HasJobOnThing_Patch), nameof(RejectTooFar)));
                }

                static bool RejectTooFar(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult = true) {
                    // again, why not use our cache
                    var isFound = Hauling.cachedStoreCell.TryGetValue(t, out foundCell);
                    if (!isFound)
                        isFound = StoreUtility.TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out foundCell, needAccurateResult);

                    WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor_Patch.RejectTooFar(ref isFound, t, carrier, ref foundCell);
                    return isFound;
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_JobOnThing_Patch
            {
                static bool Prepare() {
                    return PuahWorkGiver_HaulToInventory_Type != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahWorkGiver_HaulToInventory_Type, "JobOnThing");
                }

                // why not take advantage of our cache here as well
                static bool UseJooCachedStoreCell(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult = true) {
                    return Hauling.cachedStoreCell.TryGetValue(t, out foundCell)
                           || StoreUtility.TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out foundCell, needAccurateResult);
                }

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> _UseJooCachedStoreCell(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.Method(typeof(WorkGiver_HaulToInventory_JobOnThing_Patch), nameof(UseJooCachedStoreCell)));
                }
            }
        }
    }
}
