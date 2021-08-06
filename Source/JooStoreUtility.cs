using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        public enum SpecialHaulType { None, Opportunity, HaulBeforeCarry }

        public static readonly Dictionary<Pawn, HaulTracker> haulTrackers = new Dictionary<Pawn, HaulTracker>();

        public class HaulTracker
        {
            public SpecialHaulType haulType;

            // reminder that storeCell is just *some* cell in our stockpile, actual unload cell is determined at unload
            public List<(Thing thing, IntVec3 storeCell)> hauls;    // for opportune checking (with jobCell)
            public Dictionary<ThingDef, IntVec3>          defHauls; // for unload ordering

            public IntVec3 startCell;
            public IntVec3 jobCell  = IntVec3.Invalid; // when our haul is an opportunity on the way to a job
            public IntVec3 destCell = IntVec3.Invalid; // when store isn't the destination (e.g. bill ingredients, blueprint supplies)

            HaulTracker() {
            }

            public static HaulTracker CreateAndAdd(SpecialHaulType haulType, Pawn pawn, IntVec3 cell) {
                var haulTracker = new HaulTracker { haulType = haulType };

                if (havePuah) {
                    haulTracker.hauls = new List<(Thing thing, IntVec3 storeCell)>();
                    haulTracker.defHauls = new Dictionary<ThingDef, IntVec3>();
                }

                haulTracker.startCell = pawn.Position;
                switch (haulType) {
                    case SpecialHaulType.Opportunity:
                        haulTracker.jobCell = cell;
                        break;
                    case SpecialHaulType.HaulBeforeCarry:
                        haulTracker.destCell = cell;
                        break;
                    case SpecialHaulType.None: break;
                    default:                   throw new ArgumentOutOfRangeException(nameof(haulType), haulType, null);
                }

                haulTrackers.SetOrAdd(pawn, haulTracker);
                return haulTracker;
            }

            public string GetJobReportPrefix() {
                switch (haulType) {
                    case SpecialHaulType.Opportunity:     return "Opportunistically ";
                    case SpecialHaulType.HaulBeforeCarry: return "Optimally ";
                    default:                              return "";
                }
            }

            public void Add(Thing thing, IntVec3 storeCell, bool isInitial = false, [CallerMemberName] string callerName = "") {
#if DEBUG
                // make deterministic, but merges and initial hauls will still fluctuate
                storeCell = storeCell.GetSlotGroup(thing.Map).CellsList[0];
#endif

                string verb;
                if (isInitial && hauls.FirstOrDefault().thing == thing) {
                    hauls.RemoveAt(0);
                    verb = "UPDATED on tracker.";
                } else
                    verb = isInitial ? "PREPENDED to tracker." : "Added to tracker.";

                hauls.Insert(isInitial ? 0 : hauls.Count, (thing, storeCell));
                defHauls.SetOrAdd(thing.def, storeCell);

                if (callerName != "AddOpportuneHaulToTracker")
                    Debug.WriteLine($"{RealTime.frameCount} {haulType} {callerName}() {thing} -> {storeCell} {verb}");
            }
        }

        static class JooStoreUtility
        {
            static bool AddOpportuneHaulToTracker(HaulTracker haulTracker, Thing thing, Pawn pawn, ref IntVec3 foundCell, [CallerMemberName] string callerName = "") {
                var hauls = haulTracker.hauls;
                var defHauls = haulTracker.defHauls;

                Debug.WriteLine(
                    $"{RealTime.frameCount} {SpecialHaulType.Opportunity} {callerName}() {thing} -> {foundCell} "
                    + (hauls.LastOrDefault().thing != thing ? "Added to tracker." : "UPDATED on tracker."));

                // already here because a thing merged into it (or presumably from HasJobOnThing()?)
                // we want to recalculate with the newer store cell since some time has passed
                if (hauls.LastOrDefault().thing == thing)
                    hauls.Pop();

                var prevDefHaulIfRejected = defHauls.GetValueSafe(thing.def);
                haulTracker.Add(thing, foundCell);

                var startToLastThing = 0f;
                var curPos = haulTracker.startCell;
                foreach (var (thing_, _) in hauls) {
                    startToLastThing += curPos.DistanceTo(thing_.Position);
                    curPos = thing_.Position;
                }

                List<(Thing thing, IntVec3 storeCell)> GetHaulsByUnloadOrder() {
                    List<(Thing thing, IntVec3 storeCell)> haulsUnordered, haulsByUnloadOrder_;
                    if (pawn.carryTracker?.CarriedThing == hauls.Last().thing) {
                        haulsUnordered = new List<(Thing thing, IntVec3 storeCell)>(hauls.GetRange(0, hauls.Count - 1));
                        haulsByUnloadOrder_ = new List<(Thing thing, IntVec3 storeCell)> { hauls.Last() };
                    } else {
                        haulsByUnloadOrder_ = new List<(Thing thing, IntVec3 storeCell)> { hauls.First() };
                        haulsUnordered = new List<(Thing thing, IntVec3 storeCell)>(hauls.GetRange(1, hauls.Count - 1));
                    }

                    var curPos_ = haulsByUnloadOrder_.First().storeCell;
                    while (haulsUnordered.Count > 0) {
                        // actual unloading cells are determined on-the-fly, but these will represent the parent stockpiles with equal correctness
                        //  may also be extras if don't all fit in one cell, etc.
                        var closestUnload = haulsUnordered.OrderBy(h => defHauls[h.thing.def].DistanceTo(curPos_)).First();
                        haulsUnordered.Remove(closestUnload);
                        haulsByUnloadOrder_.Add(closestUnload);
                        curPos_ = closestUnload.storeCell;
                    }

                    return haulsByUnloadOrder_;
                }

                // note our thing navigation is recorded with hauls, and our store navigation is calculated by haulsByUnloadOrder

                var haulsByUnloadOrder = GetHaulsByUnloadOrder();

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
                var maxTotalTrip = origTrip * settings.MaxTotalTripPctOrigTrip;
                var newLegs = startToLastThing + storeToLastStore + lastStoreToJob;
                var maxNewLegs = origTrip * settings.MaxNewLegsPctOrigTrip;
                var exceedsMaxTrip = settings.MaxTotalTripPctOrigTrip > 0 && totalTrip > maxTotalTrip;
                var exceedsMaxNewLegs = settings.MaxNewLegsPctOrigTrip > 0 && newLegs > maxNewLegs;
                var isRejected = exceedsMaxTrip || exceedsMaxNewLegs;

                // Debug.WriteLine($"{(isRejected ? "REJECTED" : "APPROVED")} {hauls.Last()} for {pawn}");
                // if (!isRejected) {
                //     Debug.WriteLine(
                //         $"\tstartToLastThing: {pawn}{haulTracker.startCell} -> {string.Join(" -> ", hauls.Select(x => $"{x.thing}{x.thing.Position}"))} = {startToLastThing}");
                //     Debug.WriteLine($"\tlastThingToStore: {hauls.Last().thing}{hauls.Last().thing.Position} -> {hauls.Last()} = {lastThingToStore}");
                //     Debug.WriteLine($"\tstoreToLastStore: {string.Join(" -> ", haulsByUnloadOrder)} = {storeToLastStore}");
                //     Debug.WriteLine($"\tlastStoreToJob: {haulsByUnloadOrder.Last()} -> {haulTracker.jobCell} = {lastStoreToJob}");
                //     Debug.WriteLine($"\torigTrip: {pawn}{haulTracker.startCell} -> {haulTracker.jobCell} = {origTrip}");
                //     Debug.WriteLine($"\ttotalTrip: {startToLastThing} + {lastThingToStore} + {storeToLastStore} + {lastStoreToJob}  = {totalTrip}");
                //     Debug.WriteLine($"\tmaxTotalTrip: {origTrip} * {settings.MaxTotalTripPctOrigTrip} = {maxTotalTrip}");
                //     Debug.WriteLine($"\tnewLegs: {startToLastThing} + {storeToLastStore} + {lastStoreToJob} = {newLegs}");
                //     Debug.WriteLine($"\tmaxNewLegs: {origTrip} * {settings.MaxNewLegsPctOrigTrip} = {maxNewLegs}");
                //     Debug.WriteLine("");
                // }

                if (isRejected) {
                    foundCell = IntVec3.Invalid;
                    var (rejectedThing, _) = hauls.Pop();
                    Debug.Assert(rejectedThing == thing);
                    if (prevDefHaulIfRejected == default)
                        defHauls.Remove(rejectedThing.def);
                    else
                        defHauls[rejectedThing.def] = prevDefHaulIfRejected;
                    return false;
                }

                Debug.WriteLine("Hauls:");
                foreach (var haul in hauls)
                    Debug.WriteLine($"{haul}");

                Debug.WriteLine("Unloads:");
                // we just order by store with no secondary ordering of thing, so just print store
                foreach (var haul in haulsByUnloadOrder)
                    Debug.WriteLine($"{haul.storeCell.GetSlotGroup(pawn.Map)}"); // thing may not have Map

                Debug.WriteLine("");
                return true;
            }

            public static bool TryFindBestBetterStoreCellFor_ClosestToDestCell(Thing thing, IntVec3 destCell, Pawn pawn, Map map, StoragePriority currentPriority,
                Faction faction, out IntVec3 foundCell, bool needAccurateResult, HashSet<IntVec3> skipCells = null) {
                if (!destCell.IsValid && Hauling.cachedOpportunityStoreCell.TryGetValue(thing, out foundCell))
                    return true;

                var closestSlot = IntVec3.Invalid;
                var closestDistSquared = (float)int.MaxValue;
                var foundPriority = currentPriority;

                foreach (var slotGroup in map.haulDestinationManager.AllGroupsListInPriorityOrder) {
                    if (slotGroup.Settings.Priority < foundPriority) break;
                    if (slotGroup.Settings.Priority < currentPriority) break;
                    if (slotGroup.Settings.Priority == currentPriority && !destCell.IsValid) break;

                    if (destCell.IsValid) {
                        // haulTracker.haulType == SpecialHaulType.HaulBeforeCarry
                        if (!settings.HaulToEqualPriority && slotGroup.Settings.Priority == currentPriority) break;
                        var optimizeHaulFilter = settings.OptimizeHaul_Auto ? settings.OptimizeHaulDefaultFilter : settings.OptimizeHaul_BuildingFilter;
                        if (slotGroup.parent is Building_Storage buildingStorage && !optimizeHaulFilter.Allows(buildingStorage.def)) continue;
                        if (settings.HaulToEqualPriority && slotGroup == map.haulDestinationManager.SlotGroupAt(thing.Position)) continue;
                    }

                    if (!slotGroup.parent.Accepts(thing)) continue;

                    var position = destCell.IsValid ? destCell : thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : pawn.PositionHeld;
                    if (Find.TickManager.CurTimeSpeed > TimeSpeed.Normal)
                        needAccurateResult = false;
                    var maxCheckedCells = needAccurateResult ? (int)Math.Floor((double)slotGroup.CellsList.Count * Rand.Range(0.005f, 0.018f)) : 0;
                    for (var i = 0; i < slotGroup.CellsList.Count; i++) {
                        var cell = slotGroup.CellsList[i];
                        var distSquared = (float)(position - cell).LengthHorizontalSquared;
                        if (distSquared > closestDistSquared) continue;
                        if (skipCells != null && skipCells.Contains(cell)) continue;
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
                if (!settings.HaulToInventory || !settings.Enabled)
                    return StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, map, currentPriority, faction, out foundCell, needAccurateResult);

                var haulTracker = haulTrackers.GetValueSafe(pawn);
                // use our version for the haul to equal priority setting
                if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                    thing, haulTracker?.destCell ?? IntVec3.Invalid, pawn, map, currentPriority, faction, out foundCell, false)) return false;
                return haulTracker == null || haulTracker.haulType != SpecialHaulType.Opportunity || AddOpportuneHaulToTracker(haulTracker, thing, pawn, ref foundCell);
            }

            public static bool PuahAllocateThingAtCell_TryFindBestBetterStoreCellFor(Thing thing, Pawn pawn, Map map, StoragePriority currentPriority, Faction faction,
                out IntVec3 foundCell) {
                var skipCells = (HashSet<IntVec3>)AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);
                var haulTracker = haulTrackers.GetValueSafe(pawn) ?? HaulTracker.CreateAndAdd(SpecialHaulType.None, pawn, IntVec3.Invalid);
                if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                    thing, haulTracker.destCell, pawn, map, currentPriority, faction, out foundCell, haulTracker.destCell.IsValid, skipCells)) return false;

                skipCells.Add(foundCell);
                if (haulTracker.haulType == SpecialHaulType.Opportunity) return AddOpportuneHaulToTracker(haulTracker, thing, pawn, ref foundCell);
                haulTracker.Add(thing, foundCell);
                return true;
            }

            public static ThingCount PuahFirstUnloadableThing(Pawn pawn) {
                var hauledToInventoryComp =
                    (ThingComp)AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahCompHauledToInventoryType).Invoke(pawn, null);
                var thingsHauled = Traverse.Create(hauledToInventoryComp).Method("GetHashSet").GetValue<HashSet<Thing>>();

                // should only be necessary because haulTrackers aren't currently saved in file like CompHauledToInventory
                IntVec3 GetStoreCell(HaulTracker haulTracker_, Thing thing) {
                    if (haulTracker_.defHauls.TryGetValue(thing.def, out var storeCell))
                        return storeCell;
                    if (TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        thing, haulTracker_.destCell, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false))
                        haulTracker_.defHauls.Add(thing.def, storeCell);
                    return storeCell; // IntVec3.Invalid is okay here
                }

                var firstThingToUnload = thingsHauled.FirstOrDefault();
                if (haulTrackers.TryGetValue(pawn, out var haulTracker))
                    firstThingToUnload = thingsHauled.OrderBy(t => GetStoreCell(haulTracker, t).DistanceTo(pawn.Position)).FirstOrDefault();
                if (firstThingToUnload == default) return default;

                var thingsFound = pawn.inventory.innerContainer.Where(t => thingsHauled.Contains(t));
                if (!thingsFound.Contains(firstThingToUnload)) {
                    // can't be removed from dropping / delivering, so remove now
                    thingsHauled.Remove(firstThingToUnload);

                    // because of merges
                    var thingFoundByDef = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == firstThingToUnload.def);
                    if (thingFoundByDef != default)
                        return new ThingCount(thingFoundByDef, thingFoundByDef.stackCount);
                }

                return new ThingCount(firstThingToUnload, firstThingToUnload.stackCount);
            }

            public static bool AlreadyHauling(Pawn pawn) {
                if (haulTrackers.ContainsKey(pawn)) return true;

                // because we may load a game with an incomplete haul
                if (havePuah) {
                    var hauledToInventoryComp =
                        (ThingComp)AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahCompHauledToInventoryType).Invoke(pawn, null);
                    var takenToInventory = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value;
                    if (takenToInventory != null && takenToInventory.Any(t => t != null)) return true;
                }

                return false;
            }
        }
    }
}
