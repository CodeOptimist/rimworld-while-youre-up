using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class JooStoreUtility
        {
            static readonly FieldInfo SkipCellsField = AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells");

            static bool RejectTooFar(Hauling.ForPuah forPuah, Thing thing, Pawn carrier, ref IntVec3 foundCell) {
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
                List<(Thing thing, IntVec3 store)> haulsByUnloadOrder;

                ushort? PuahFirstUnloadableThing((Thing thing, IntVec3 store) haul) {
                    return haul.thing.def.FirstThingCategory?.index;
                }

                if (carrier.carryTracker?.CarriedThing == latestHaul.thing) {
                    haulsByUnloadOrder = hauls.GetRange(0, hauls.Count - 1).OrderBy(PuahFirstUnloadableThing).ToList();
                    haulsByUnloadOrder.Insert(0, latestHaul);
                } else {
                    haulsByUnloadOrder = hauls.OrderBy(PuahFirstUnloadableThing).ToList();
                    var mandatoryFirstStoreIsFirstUnload = hauls.First().store.GetSlotGroup(carrier.Map) == haulsByUnloadOrder.First().store.GetSlotGroup(carrier.Map);
                    if (!mandatoryFirstStoreIsFirstUnload)
                        haulsByUnloadOrder.Insert(0, (thing: null, hauls.First().store));
                }

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

                if (isRejected) return true;
                forPuah.hauls = hauls;
                return false;
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
                if (Hauling.cachedStoreCell.TryGetValue(thing, out foundCell)) {
                    if (Hauling.pawnPuah.TryGetValue(pawn, out var forPuah))
                        return !RejectTooFar(forPuah, thing, pawn, ref foundCell);
                }

                return StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, map, currentPriority, faction, out foundCell, needAccurateResult);
            }

            public static bool PuahAllocateThingAtCell_GetStore(Thing thing, Pawn pawn, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell) {
                var skipCells = (HashSet<IntVec3>) SkipCellsField.GetValue(null);

                // take advantage of our cache because we can
                if (Hauling.cachedStoreCell.TryGetValue(thing, out var cachedCell)) {
                    if (!skipCells.Contains(cachedCell)) {
                        foundCell = cachedCell;
                        skipCells.Add(cachedCell);
                        return true;
                    }
                }

                var groupsList = map.haulDestinationManager.AllGroupsListInPriorityOrder;
                foreach (var slotGroup in groupsList.Where(s => s.Settings.Priority > currentPriority && s.parent.Accepts(thing))) {
                    if (slotGroup.CellsList.Except(skipCells).FirstOrDefault(c => StoreUtility.IsGoodStoreCell(c, map, thing, pawn, faction)) is IntVec3 cell && cell != default) {
                        foundCell = cell;
                        skipCells.Add(cell);

                        if (Hauling.pawnPuah.TryGetValue(pawn, out var forPuah)) {
                            if (RejectTooFar(forPuah, thing, pawn, ref foundCell))
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
