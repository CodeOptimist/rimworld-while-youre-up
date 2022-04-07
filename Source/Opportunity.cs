using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        static class Opportunity
        {
            public static readonly Dictionary<Thing, IntVec3> cachedStoreCells = new Dictionary<Thing, IntVec3>();

            enum CanHaulResult { RangeFail, HardFail, Success }

            struct MaxRanges
            {
                public int   expandCount;
                public float startToThing, startToThingPctOrigTrip;
                public float storeToJob,   storeToJobPctOrigTrip;

                public static MaxRanges operator *(MaxRanges maxRanges, int multiplier) {
                    maxRanges.expandCount += 1;
                    maxRanges.startToThing *= multiplier;
                    maxRanges.startToThingPctOrigTrip *= multiplier;
                    maxRanges.storeToJob *= multiplier;
                    maxRanges.storeToJobPctOrigTrip *= multiplier;
                    return maxRanges;
                }
            }

            public static Job TryHaul(Pawn pawn, LocalTargetInfo jobTarget) {
                Job _TryHaul() {
                    var maxRanges = new MaxRanges {
                        startToThing = settings.Opportunity_MaxStartToThing,
                        startToThingPctOrigTrip = settings.Opportunity_MaxStartToThingPctOrigTrip,
                        storeToJob = settings.Opportunity_MaxStoreToJob,
                        storeToJobPctOrigTrip = settings.Opportunity_MaxStoreToJobPctOrigTrip,
                    };

                    var i = 0;
                    var haulables = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                    while (haulables.Count > 0) {
                        if (i == haulables.Count) {
                            maxRanges *= 2;
                            i = 0;
                        }

                        var thing = haulables[i];
                        var canHaul = CanHaul(pawn, thing, jobTarget, maxRanges, out var storeCell);
                        switch (canHaul) {
                            case CanHaulResult.RangeFail:
                                if (settings.Opportunity_PathChecker == Settings.PathCheckerEnum.Vanilla)
                                    goto case CanHaulResult.HardFail;

                                i++;
                                continue;
                            case CanHaulResult.HardFail:
                                haulables.RemoveAt(i);
                                continue;
                            case CanHaulResult.Success:
                                if (DebugViewSettings.drawOpportunisticJobs) {
                                    pawn.Map.debugDrawer.FlashLine(pawn.Position,  jobTarget.Cell, 600, SimpleColor.Red);
                                    pawn.Map.debugDrawer.FlashLine(pawn.Position,  thing.Position, 600, SimpleColor.Green);
                                    pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,      600, SimpleColor.Green);
                                    pawn.Map.debugDrawer.FlashLine(storeCell,      jobTarget.Cell, 600, SimpleColor.Green);
#if DEBUG
                                    // if (!Find.Selector.SelectedPawns.Any())
                                    //     CameraJumper.TrySelect(pawn);
#endif
                                }

                                var puahJob = PuahJob(new PuahOpportunity(pawn, jobTarget), pawn, thing, storeCell);
                                if (puahJob != null) return puahJob;

                                specialHauls.SetOrAdd(pawn, new SpecialHaul("Opportunity_LoadReport", jobTarget));
                                return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                        }
                    }

                    return null;
                }

                var result = _TryHaul();
                cachedStoreCells.Clear();
                return result;
            }

            static CanHaulResult CanHaul(Pawn pawn, Thing thing, LocalTargetInfo jobTarget, MaxRanges maxRanges, out IntVec3 storeCell) {
                storeCell = IntVec3.Invalid;
                var pawnToJob = pawn.Position.DistanceTo(jobTarget.Cell);

                var startToThing = pawn.Position.DistanceTo(thing.Position);
                if (startToThing > maxRanges.startToThing) return CanHaulResult.RangeFail;
                if (startToThing > pawnToJob * maxRanges.startToThingPctOrigTrip) return CanHaulResult.RangeFail;

                var thingToJob = thing.Position.DistanceTo(jobTarget.Cell);
                // if this one exceeds the maximum the next maxTotalTripPctOrigTrip check certainly will
                if (startToThing + thingToJob > pawnToJob * settings.Opportunity_MaxTotalTripPctOrigTrip)
                    return CanHaulResult.HardFail;
                if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) return CanHaulResult.HardFail;
                if (thing.IsForbidden(pawn)) return CanHaulResult.HardFail;
                if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) return CanHaulResult.HardFail;

                var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                if (!cachedStoreCells.TryGetValue(thing, out storeCell) && !TryFindBestBetterStoreCellFor_ClosestToTarget(
                        thing, jobTarget, IntVec3.Invalid, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell, maxRanges.expandCount == 0))
                    return CanHaulResult.HardFail;

                // we need storeCell everywhere, so cache it
                cachedStoreCells.SetOrAdd(thing, storeCell);

                var storeToJob = storeCell.DistanceTo(jobTarget.Cell);
                if (storeToJob > maxRanges.storeToJob) return CanHaulResult.RangeFail;
                if (storeToJob > pawnToJob * maxRanges.storeToJobPctOrigTrip) return CanHaulResult.RangeFail;

                if (startToThing + storeToJob > pawnToJob * settings.Opportunity_MaxNewLegsPctOrigTrip)
                    return CanHaulResult.HardFail;
                var thingToStore = thing.Position.DistanceTo(storeCell);
                if (startToThing + thingToStore + storeToJob > pawnToJob * settings.Opportunity_MaxTotalTripPctOrigTrip)
                    return CanHaulResult.HardFail;

                if (settings.Opportunity_PathChecker == Settings.PathCheckerEnum.Pathfinding) {
                    float GetPathCost(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode) {
                        var pawnPath = pawn.Map.pathFinder.FindPath(start, dest, TraverseParms.For(pawn), peMode);
                        var result = pawnPath.TotalCost;
                        pawnPath.ReleaseToPool();
                        return result;
                    }

                    var pawnToThingPathCost = GetPathCost(pawn.Position, thing, PathEndMode.ClosestTouch);
                    if (pawnToThingPathCost == 0) return CanHaulResult.HardFail;
                    var storeToJobPathCost = GetPathCost(storeCell, jobTarget, PathEndMode.Touch);
                    if (storeToJobPathCost == 0) return CanHaulResult.HardFail;
                    var pawnToJobPathCost = GetPathCost(pawn.Position, jobTarget, PathEndMode.Touch);
                    if (pawnToJobPathCost == 0) return CanHaulResult.HardFail;

                    if (pawnToThingPathCost + storeToJobPathCost > pawnToJobPathCost * settings.Opportunity_MaxNewLegsPctOrigTrip)
                        return CanHaulResult.HardFail;

                    var thingToStorePathCost = GetPathCost(thing.Position, storeCell, PathEndMode.ClosestTouch);
                    if (thingToStorePathCost == 0) return CanHaulResult.HardFail;

                    if (pawnToThingPathCost + thingToStorePathCost + storeToJobPathCost > pawnToJobPathCost * settings.Opportunity_MaxTotalTripPctOrigTrip)
                        return CanHaulResult.HardFail;
                }

                // not worth the performance to continually expand (our expansions are just to facilitate a preference for the best opportunities)
                // but always try for the very best initially, so we're at least as good as vanilla
                else if (maxRanges.expandCount == 0) {
                    if (!pawn.Position.WithinRegions(thing.Position, pawn.Map, settings.Opportunity_MaxStartToThingRegionLookCount, TraverseParms.For(pawn)))
                        return CanHaulResult.RangeFail;

                    if (!storeCell.WithinRegions(jobTarget.Cell, pawn.Map, settings.Opportunity_MaxStoreToJobRegionLookCount, TraverseParms.For(pawn)))
                        return CanHaulResult.RangeFail;
                }

                return CanHaulResult.Success;
            }
        }

        public class PuahOpportunity : PuahWithBetterUnloading
        {
            public LocalTargetInfo jobTarget;
            public IntVec3         startCell;

            // reminder that storeCell is just *some* cell in our stockpile, actual unload cell is determined at unload
            public List<(Thing thing, IntVec3 storeCell)> hauls = new List<(Thing thing, IntVec3 storeCell)>();

            public PuahOpportunity(Pawn pawn, LocalTargetInfo jobTarget) {
                startCell = pawn.Position;
                this.jobTarget = jobTarget;
            }

            public override string GetLoadReport(string text)   => "Opportunity_LoadReport".ModTranslate(text.Named("ORIGINAL"), jobTarget.Label.Named("DESTINATION"));
            public override string GetUnloadReport(string text) => "Opportunity_UnloadReport".ModTranslate(text.Named("ORIGINAL"), jobTarget.Label.Named("DESTINATION"));

            public bool TrackThingIfOpportune(Thing thing, Pawn pawn, ref IntVec3 foundCell) {
                Debug.Assert(thing != null);
                var isPrepend = pawn.carryTracker?.CarriedThing == thing;
                TrackThing(thing, foundCell, isPrepend, trackDef: false);

                var curPos = startCell;
                var startToLastThing = 0f;
                foreach (var (thing_, _) in hauls) {
                    startToLastThing += curPos.DistanceTo(thing_.Position);
                    curPos = thing_.Position;
                }

                // every time, since our foundCell could fit anywhere
                List<(Thing thing, IntVec3 storeCell)> GetHaulsByUnloadDistance() {
                    // PUAH WorkGiver always takes us to the first
                    var ordered = new List<(Thing thing, IntVec3 storeCell)> { hauls.First() };
                    var pending = new List<(Thing thing, IntVec3 storeCell)>(hauls.GetRange(1, hauls.Count - 1));

                    while (pending.Count > 0) {
                        // only used for distance checks, so it's perfectly correct if due to close-together or oddly-shaped stockpiles these cells
                        //  aren't ordered by their parent stockpile
                        // actual unloading cells are determined on-the-fly (even extras if don't fit, etc.) but this represents them with equal correctness
                        var closestHaul = pending.MinBy(x => x.storeCell.DistanceTo(ordered.Last().storeCell));
                        ordered.Add(closestHaul);
                        pending.Remove(closestHaul);
                    }

                    return ordered;
                }

                var haulsByUnloadDistance = GetHaulsByUnloadDistance();
                var lastThingToFirstStore = curPos.DistanceTo(haulsByUnloadDistance.First().storeCell);

                curPos = haulsByUnloadDistance.First().storeCell;
                var firstStoreToLastStore = 0f;
                foreach (var haul in haulsByUnloadDistance) {
                    firstStoreToLastStore += curPos.DistanceTo(haul.storeCell);
                    curPos = haul.storeCell;
                }

                var lastStoreToJob = curPos.DistanceTo(jobTarget.Cell);

                var origTrip = startCell.DistanceTo(jobTarget.Cell);
                var totalTrip = startToLastThing + lastThingToFirstStore + firstStoreToLastStore + lastStoreToJob;
                var maxTotalTrip = origTrip * settings.Opportunity_MaxTotalTripPctOrigTrip;
                var newLegs = startToLastThing + firstStoreToLastStore + lastStoreToJob;
                var maxNewLegs = origTrip * settings.Opportunity_MaxNewLegsPctOrigTrip;
                var exceedsMaxTrip = totalTrip > maxTotalTrip;
                var exceedsMaxNewLegs = newLegs > maxNewLegs;
                var isRejected = exceedsMaxTrip || exceedsMaxNewLegs;

                if (isRejected) {
                    foundCell = IntVec3.Invalid;
                    hauls.RemoveAt(isPrepend ? 0 : hauls.Count - 1);
                    return false;
                }

                defHauls.SetOrAdd(thing.def, foundCell);

#if DEBUG
                var storeCells = haulsByUnloadDistance.Select(x => x.storeCell).ToList();
                Debug.WriteLine($"APPROVED {thing} -> {foundCell} for {pawn}");
                Debug.WriteLine($"\tstartToLastThing: {pawn} {startCell} -> {string.Join(" -> ", hauls.Select(x => $"{x.thing} {x.thing.Position}"))} = {startToLastThing}");
                Debug.WriteLine($"\tlastThingToFirstStore: {thing} {thing.Position} -> {storeCells.First().GetSlotGroup(pawn.Map)} {storeCells.First()} = {lastThingToFirstStore}");
                Debug.WriteLine($"\tfirstStoreToLastStore: {string.Join(" -> ", storeCells.Select(x => $"{x.GetSlotGroup(pawn.Map)} {x}"))} = {firstStoreToLastStore}");
                Debug.WriteLine($"\tlastStoreToJob: {storeCells.Last().GetSlotGroup(pawn.Map)} {storeCells.Last()} -> {jobTarget} {jobTarget.Cell} = {lastStoreToJob}");
                Debug.WriteLine($"\torigTrip: {pawn} {startCell} -> {jobTarget} {jobTarget.Cell} = {origTrip}");
                Debug.WriteLine($"\ttotalTrip: {startToLastThing} + {lastThingToFirstStore} + {firstStoreToLastStore} + {lastStoreToJob}  = {totalTrip}");
                Debug.WriteLine($"\tmaxTotalTrip: {origTrip} * {settings.Opportunity_MaxTotalTripPctOrigTrip} = {maxTotalTrip}");
                Debug.WriteLine($"\tnewLegs: {startToLastThing} + {firstStoreToLastStore} + {lastStoreToJob} = {newLegs}");
                Debug.WriteLine($"\tmaxNewLegs: {origTrip} * {settings.Opportunity_MaxNewLegsPctOrigTrip} = {maxNewLegs}");
                Debug.WriteLine("");

                // true unload order depends on distance to pawn at the time
                var slotGroupUnloadOrder = haulsByUnloadDistance.Select(x => x.storeCell.GetSlotGroup(pawn.Map)).Distinct().ToList();
                var haulsByUnloadOrder = haulsByUnloadDistance.OrderBy(x => slotGroupUnloadOrder.IndexOf(x.storeCell.GetSlotGroup(pawn.Map)))
                    .ThenBy(x => x.thing.def.FirstThingCategory?.index)
                    .ThenBy(x => x.thing.def.defName)
                    .ToList();

                Debug.WriteLine($"{haulsByUnloadOrder.Count} Hauls:");
                foreach (var haul in haulsByUnloadOrder)
                    Debug.WriteLine(haul);

                Debug.WriteLine($"{haulsByUnloadOrder.Count} Unloads:");
                foreach (var haul in haulsByUnloadOrder)
                    Debug.WriteLine($"{haul.storeCell.GetSlotGroup(pawn.Map)}");

                Debug.WriteLine("");
#endif

                return true;
            }
        }
    }
}
