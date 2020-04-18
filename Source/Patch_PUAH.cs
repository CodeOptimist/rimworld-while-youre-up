using System.Collections.Generic;
using System.Diagnostics;
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

                    Debug.WriteLine($"Evaluating if {thing} is an opportunistic haul for {carrier}.");
                    var pawnToJob = carrier.Position.DistanceTo(forPuah.jobCell);
                    var pawnToThing = carrier.Position.DistanceTo(thing.Position);
                    var thingToFirstStore = thing.Position.DistanceTo(forPuah.firstStore);
                    var curCumStoreDistance = forPuah.curCumStoreDistance + forPuah.prevStore.DistanceTo(foundCell);
                    var storeToJob = foundCell.DistanceTo(forPuah.jobCell);

                    var exceedsMaxTrip = maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToFirstStore + curCumStoreDistance + storeToJob > pawnToJob * maxTotalTripPctOrigTrip.Value;
                    var exceedsMaxNewLegs = maxNewLegsPctOrigTrip.Value > 0 && pawnToThing + curCumStoreDistance + storeToJob > pawnToJob * maxNewLegsPctOrigTrip.Value;
                    if (exceedsMaxTrip || exceedsMaxNewLegs) {
                        foundCell = IntVec3.Invalid;
                        __result = false;
                        Debug.WriteLine($"{carrier} denied hauling {thing} to inventory because it isn't opportunistic.");
                        return;
                    }

                    forPuah.prevStore = foundCell;
                    forPuah.curCumStoreDistance = curCumStoreDistance;
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
                    if (pawn.CurJobDef?.defName != "HaulToInventory")
                        Hauling.pawnPuah.Remove(pawn);
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
