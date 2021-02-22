using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        public static readonly Dictionary<Pawn, PuahHaulTracker> haulTrackers = new Dictionary<Pawn, PuahHaulTracker>();

        public class PuahHaulTracker
        {
            public List<(Thing thing, IntVec3 storeCell)> hauls;
            public IntVec3                                startCell;
            public IntVec3                                jobCell;
        }

        static class JooStoreUtility
        {
            static bool AddOpportuneHaulToTracker(PuahHaulTracker haulTracker, Thing thing, Pawn pawn, ref IntVec3 foundCell) {
                var hauls = new List<(Thing thing, IntVec3 storeCell)>(haulTracker.hauls);
                if (hauls.Last().thing != thing)
                    hauls.Add((thing, storeCell: foundCell));

                var startToLastThing = 0f;
                var curPos = haulTracker.startCell;
                foreach (var (thing_, _) in hauls) {
                    startToLastThing += curPos.DistanceTo(thing_.Position);
                    curPos = thing_.Position;
                }

                // actual unloading cells are determined on-the-fly, but these will represent the parent stockpiles with equal correctness
                // may also be extras if don't all fit in one cell, etc.
                List<(Thing thing, IntVec3 storeCell)> haulsByUnloadOrder;

                ushort? PuahFirstUnloadableThing((Thing thing, IntVec3 storeCell) haul) {
                    return haul.thing.def.FirstThingCategory?.index;
                }

                if (pawn.carryTracker?.CarriedThing == hauls.Last().thing) {
                    haulsByUnloadOrder = hauls.GetRange(0, hauls.Count - 1).OrderBy(PuahFirstUnloadableThing).ToList();
                    haulsByUnloadOrder.Insert(0, hauls.Last());
                } else {
                    haulsByUnloadOrder = hauls.OrderBy(PuahFirstUnloadableThing).ToList();
                    var mandatoryFirstStoreIsFirstUnload = hauls.First().storeCell.GetSlotGroup(pawn.Map) == haulsByUnloadOrder.First().storeCell.GetSlotGroup(pawn.Map);
                    if (!mandatoryFirstStoreIsFirstUnload)
                        haulsByUnloadOrder.Insert(0, (thing: null, hauls.First().storeCell));
                }

                var storeToLastStore = 0f;
                curPos = hauls.Last().storeCell;
                foreach (var (_, storeCell) in haulsByUnloadOrder) {
                    storeToLastStore += curPos.DistanceTo(storeCell);
                    curPos = storeCell;
                }

                var lastThingToStore = hauls.Last().thing.Position.DistanceTo(hauls.Last().storeCell);
                var lastStoreToJob = haulsByUnloadOrder.Last().storeCell.DistanceTo(haulTracker.jobCell);
                var origTrip = haulTracker.startCell.DistanceTo(haulTracker.jobCell);
                var totalTrip = startToLastThing + lastThingToStore + storeToLastStore + lastStoreToJob;
                var maxTotalTrip = origTrip * maxTotalTripPctOrigTrip.Value;
                var newLegs = startToLastThing + storeToLastStore + lastStoreToJob;
                var maxNewLegs = origTrip * maxNewLegsPctOrigTrip.Value;
                var exceedsMaxTrip = maxTotalTripPctOrigTrip.Value > 0 && totalTrip > maxTotalTrip;
                var exceedsMaxNewLegs = maxNewLegsPctOrigTrip.Value > 0 && newLegs > maxNewLegs;
                var isRejected = exceedsMaxTrip || exceedsMaxNewLegs;

                if (!isRejected) {
                    Debug.WriteLine($"{(isRejected ? "REJECTED" : "APPROVED")} {hauls.Last()} for {pawn}");
//                    Debug.WriteLine(
//                        $"\tstartToLastThing: {pawn}{haulTracker.startCell} -> {string.Join(" -> ", hauls.Select(x => $"{x.thing}{x.thing.Position}"))} = {startToLastThing}");
//                    Debug.WriteLine($"\tlastThingToStore: {hauls.Last().thing}{hauls.Last().thing.Position} -> {hauls.Last()} = {lastThingToStore}");
//                    Debug.WriteLine($"\tstoreToLastStore: {string.Join(" -> ", haulsByUnloadOrder)} = {storeToLastStore}");
//                    Debug.WriteLine($"\tlastStoreToJob: {haulsByUnloadOrder.Last()} -> {haulTracker.jobCell} = {lastStoreToJob}");
//                    Debug.WriteLine($"\torigTrip: {pawn}{haulTracker.startCell} -> {haulTracker.jobCell} = {origTrip}");
//                    Debug.WriteLine($"\ttotalTrip: {startToLastThing} + {lastThingToStore} + {storeToLastStore} + {lastStoreToJob}  = {totalTrip}");
//                    Debug.WriteLine($"\tmaxTotalTrip: {origTrip} * {maxTotalTripPctOrigTrip.Value} = {maxTotalTrip}");
//                    Debug.WriteLine($"\tnewLegs: {startToLastThing} + {storeToLastStore} + {lastStoreToJob} = {newLegs}");
//                    Debug.WriteLine($"\tmaxNewLegs: {origTrip} * {maxNewLegsPctOrigTrip.Value} = {maxNewLegs}");
//                    Debug.WriteLine("");
                }

                if (isRejected) return false;
                haulTracker.hauls = hauls;
                return true;
            }

            public static bool TryFindBestBetterStoreCellFor_ClosestToDestCell(Thing thing, IntVec3 destCell, Pawn pawn, Map map, StoragePriority currentPriority,
                Faction faction, out IntVec3 foundCell, bool needAccurateResult) {
                var allowEqualPriority = destCell.IsValid && haulToEqualPriority.Value;
                var closestSlot = IntVec3.Invalid;
                var closestDistSquared = (float) int.MaxValue;
                var foundPriority = currentPriority;

                foreach (var slotGroup in map.haulDestinationManager.AllGroupsListInPriorityOrder) {
                    if (slotGroup.Settings.Priority < foundPriority) break;
                    if (slotGroup.Settings.Priority < currentPriority) break;
                    if (!allowEqualPriority && slotGroup.Settings.Priority == currentPriority) break;

                    if (allowEqualPriority && slotGroup == map.haulDestinationManager.SlotGroupAt(thing.Position)) continue;
                    if (!slotGroup.parent.Accepts(thing)) continue;

                    var position = destCell.IsValid ? destCell : thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : pawn.PositionHeld;
                    var maxCheckedCells = needAccurateResult ? (int) Math.Floor((double) slotGroup.CellsList.Count * Rand.Range(0.005f, 0.018f)) : 0;
                    for (var i = 0; i < slotGroup.CellsList.Count; i++) {
                        var cell = slotGroup.CellsList[i];
                        var distSquared = (float) (position - cell).LengthHorizontalSquared;
                        if (distSquared > closestDistSquared) continue;
                        if (!StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, faction)) continue;

                        closestSlot = cell;
                        closestDistSquared = distSquared;
                        foundPriority = slotGroup.Settings.Priority;

                        if (i >= maxCheckedCells) break;
                    }
                }

                foundCell = closestSlot;
                return foundCell.IsValid;
            }

            public static bool PuahHasJobOnThing_HasStore(Thing thing, Pawn pawn, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell,
                bool needAccurateResult) {
                if (Hauling.cachedOpportunityStoreCell.TryGetValue(thing, out foundCell)) {
                    if (haulTrackers.TryGetValue(pawn, out var haulTracker))
                        return AddOpportuneHaulToTracker(haulTracker, thing, pawn, ref foundCell);
                }

                return StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, map, currentPriority, faction, out foundCell, needAccurateResult);
            }

            public static bool PuahAllocateThingAtCell_GetStore(Thing thing, Pawn pawn, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell) {
                var skipCells = (HashSet<IntVec3>) AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);

                // take advantage of our cache because we can
                if (Hauling.cachedOpportunityStoreCell.TryGetValue(thing, out var cachedCell)) {
                    if (!skipCells.Contains(cachedCell)) {
                        foundCell = cachedCell;
                        skipCells.Add(cachedCell);
                        return true;
                    }
                }

                var groupsList = map.haulDestinationManager.AllGroupsListInPriorityOrder;
                foreach (var slotGroup in groupsList.Where(s => s.Settings.Priority > currentPriority && s.parent.Accepts(thing))) {
                    if (slotGroup.CellsList.Except(skipCells).FirstOrDefault(c => StoreUtility.IsGoodStoreCell(c, map, thing, pawn, faction)) is IntVec3 cell
                        && cell != default) {
                        foundCell = cell;
                        skipCells.Add(cell);

                        if (haulTrackers.TryGetValue(pawn, out var haulTracker)) {
                            if (!AddOpportuneHaulToTracker(haulTracker, thing, pawn, ref foundCell))
                                break;
                        }

                        return true;
                    }
                }

                foundCell = IntVec3.Invalid;
                return false;
            }
        }
    }
}
