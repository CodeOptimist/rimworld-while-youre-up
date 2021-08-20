﻿using System.Collections.Generic;
using System.Linq;
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
        static class Opportunity
        {
            static readonly        Dictionary<Thing, ProximityStage> thingProximityStage        = new Dictionary<Thing, ProximityStage>();
            public static readonly Dictionary<Thing, IntVec3>        cachedOpportunityStoreCell = new Dictionary<Thing, IntVec3>();

            enum ProximityCheck { Both, Either, Ignored }

            // ReSharper disable once UnusedMember.Local
            enum ProximityStage { Initial, PawnToThing, StoreToJob, PawnToThingRegion, Fail, Success }

            public static Job TryHaul(Pawn pawn, IntVec3 jobCell) {
                Job _TryHaul() {
                    switch (settings.HaulProximities) {
                        case Settings.HaulProximitiesEnum.Both:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both);
                        case Settings.HaulProximitiesEnum.Either:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either);
                        case Settings.HaulProximitiesEnum.Ignored:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both)
                                   ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Ignored);
                        default:
                            return null;
                    }
                }

                var result = _TryHaul();
                thingProximityStage.Clear();
                cachedOpportunityStoreCell.Clear();
                return result;
            }

            static ProximityStage CanHaul(ProximityStage proximityStage, Pawn pawn, Thing thing, IntVec3 jobCell, ProximityCheck proximityCheck, out IntVec3 storeCell) {
                storeCell = IntVec3.Invalid;
                var pawnToJob = pawn.Position.DistanceTo(jobCell);

                var pawnToThing = pawn.Position.DistanceTo(thing.Position);
                if (proximityStage < ProximityStage.StoreToJob) {
                    var atMax = settings.MaxStartToThing > 0 && pawnToThing > settings.MaxStartToThing;
                    var atMaxPct = settings.MaxStartToThingPctOrigTrip > 0 && pawnToThing > pawnToJob * settings.MaxStartToThingPctOrigTrip;
                    var pawnToThingFail = atMax || atMaxPct;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when pawnToThingFail: return ProximityStage.PawnToThing;
                    }

                    var thingToJob = thing.Position.DistanceTo(jobCell);
                    // if this one exceeds the maximum the next maxTotalTripPctOrigTrip check certainly will
                    if (settings.MaxTotalTripPctOrigTrip > 0 && pawnToThing + thingToJob > pawnToJob * settings.MaxTotalTripPctOrigTrip) return ProximityStage.Fail;
                    if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) return ProximityStage.Fail;
                    if (thing.IsForbidden(pawn)) return ProximityStage.Fail;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) return ProximityStage.Fail;
                }

                var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(thing, IntVec3.Invalid, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell, true))
                    return ProximityStage.Fail;

                // we need storeCell everywhere, so cache it
                cachedOpportunityStoreCell.SetOrAdd(thing, storeCell);

                var storeToJob = storeCell.DistanceTo(jobCell);
                if (proximityStage < ProximityStage.PawnToThingRegion) {
                    var atMax2 = settings.MaxStoreToJob > 0 && storeToJob > settings.MaxStoreToJob;
                    var atMaxPct2 = settings.MaxStoreToJobPctOrigTrip > 0 && storeToJob > pawnToJob * settings.MaxStoreToJobPctOrigTrip;
                    var storeToJobFail = atMax2 || atMaxPct2;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when storeToJobFail:                                                   return ProximityStage.StoreToJob;
                        case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThing && storeToJobFail: return ProximityStage.StoreToJob;
                    }

                    var thingToStore = thing.Position.DistanceTo(storeCell);
                    if (settings.MaxTotalTripPctOrigTrip > 0 && pawnToThing + thingToStore + storeToJob > pawnToJob * settings.MaxTotalTripPctOrigTrip) return ProximityStage.Fail;
                    if (settings.MaxNewLegsPctOrigTrip > 0 && pawnToThing + storeToJob > pawnToJob * settings.MaxNewLegsPctOrigTrip) return ProximityStage.Fail;
                }

                bool PawnToThingRegionFail() {
                    return settings.MaxStartToThingRegionLookCount > 0 && !pawn.Position.WithinRegions(
                        thing.Position, pawn.Map, settings.MaxStartToThingRegionLookCount, TraverseParms.For(pawn));
                }

                bool StoreToJobRegionFail(IntVec3 _storeCell) {
                    return settings.MaxStoreToJobRegionLookCount > 0 && !_storeCell.WithinRegions(
                        jobCell, pawn.Map, settings.MaxStoreToJobRegionLookCount, TraverseParms.For(pawn));
                }

                switch (proximityCheck) {
                    case ProximityCheck.Both when PawnToThingRegionFail():                                                                 return ProximityStage.PawnToThingRegion;
                    case ProximityCheck.Both when StoreToJobRegionFail(storeCell):                                                         return ProximityStage.Fail;
                    case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThingRegion && StoreToJobRegionFail(storeCell): return ProximityStage.Fail;
                }

                return ProximityStage.Success;
            }

            static Job TryHaulStage(Pawn pawn, IntVec3 jobCell, ProximityCheck proximityCheck) {
                foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()) {
                    if (thingProximityStage.TryGetValue(thing, out var proximityStage) && proximityStage == ProximityStage.Fail)
                        continue;

                    var newProximityStage = CanHaul(proximityStage, pawn, thing, jobCell, proximityCheck, out var storeCell);
                    // Debug.WriteLine($"{pawn} for {thing} proximity stage: {proximityStage} -> {newProximityStage}");
                    thingProximityStage.SetOrAdd(thing, newProximityStage);
                    if (newProximityStage != ProximityStage.Success) continue;

                    if (DebugViewSettings.drawOpportunisticJobs) {
                        pawn.Map.debugDrawer.FlashLine(pawn.Position,  jobCell,        600, SimpleColor.Red);
                        pawn.Map.debugDrawer.FlashLine(pawn.Position,  thing.Position, 600, SimpleColor.Green);
                        pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,      600, SimpleColor.Green);
                        pawn.Map.debugDrawer.FlashLine(storeCell,      jobCell,        600, SimpleColor.Green);
                    }

                    var puahJob = PuahJob(new PuahOpportunity(pawn, jobCell), pawn, thing, storeCell);
                    if (puahJob != null) return puahJob;

                    specialHauls.SetOrAdd(pawn, new SpecialHaul("Opportunistically "));
                    return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }

            public static bool TrackPuahThingIfOpportune(PuahOpportunity opportunity, Thing thing, Pawn pawn, ref IntVec3 foundCell, [CallerMemberName] string callerName = "") {
                var hauls = opportunity.hauls;
                var defHauls = opportunity.defHauls;

                Debug.WriteLine(
                    $"{RealTime.frameCount} {opportunity} {callerName}() {thing} -> {foundCell} "
                    + (hauls.LastOrDefault().thing != thing ? "Added to tracker." : "UPDATED on tracker."));

                // already here because a thing merged into it (or presumably from HasJobOnThing()?)
                // we want to recalculate with the newer store cell since some time has passed
                if (hauls.LastOrDefault().thing == thing)
                    hauls.Pop();

                var prevDefHaul = defHauls.GetValueSafe(thing.def);
                opportunity.TrackThing(thing, foundCell);

                var startToLastThing = 0f;
                var curPos = opportunity.startCell;
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

                var firstStoreToLastStore = 0f;
                curPos = hauls.Last().storeCell;
                foreach (var (_, storeCell) in haulsByUnloadOrder) {
                    firstStoreToLastStore += curPos.DistanceTo(storeCell);
                    curPos = storeCell;
                }

                var lastThingToFirstStore = hauls.Last().thing.Position.DistanceTo(hauls.Last().storeCell);
                var lastStoreToJob = haulsByUnloadOrder.Last().storeCell.DistanceTo(opportunity.jobCell);
                var origTrip = opportunity.startCell.DistanceTo(opportunity.jobCell);
                var totalTrip = startToLastThing + lastThingToFirstStore + firstStoreToLastStore + lastStoreToJob;
                var maxTotalTrip = origTrip * settings.MaxTotalTripPctOrigTrip;
                var newLegs = startToLastThing + firstStoreToLastStore + lastStoreToJob;
                var maxNewLegs = origTrip * settings.MaxNewLegsPctOrigTrip;
                var exceedsMaxTrip = settings.MaxTotalTripPctOrigTrip > 0 && totalTrip > maxTotalTrip;
                var exceedsMaxNewLegs = settings.MaxNewLegsPctOrigTrip > 0 && newLegs > maxNewLegs;
                var isRejected = exceedsMaxTrip || exceedsMaxNewLegs;

                if (isRejected) {
                    foundCell = IntVec3.Invalid;
                    var (rejectedThing, _) = hauls.Pop();
                    Debug.Assert(rejectedThing == thing);
                    if (prevDefHaul == default)
                        defHauls.Remove(rejectedThing.def);
                    else
                        defHauls[rejectedThing.def] = prevDefHaul;
                    return false;
                }

#if DEBUG
                Debug.WriteLine($"APPROVED {hauls.Last()} for {pawn}");
                Debug.WriteLine(
                    $"\tstartToLastThing: {pawn}{opportunity.startCell}"
                    + $" -> {string.Join(" -> ", hauls.Select(x => $"{x.thing}{x.thing.Position}"))} = {startToLastThing}");
                Debug.WriteLine($"\tlastThingToStore: {hauls.Last().thing}{hauls.Last().thing.Position} -> {hauls.Last()} = {lastThingToFirstStore}");
                Debug.WriteLine($"\tstoreToLastStore: {string.Join(" -> ", haulsByUnloadOrder)} = {firstStoreToLastStore}");
                Debug.WriteLine($"\tlastStoreToJob: {haulsByUnloadOrder.Last()} -> {opportunity.jobCell} = {lastStoreToJob}");
                Debug.WriteLine($"\torigTrip: {pawn}{opportunity.startCell} -> {opportunity.jobCell} = {origTrip}");
                Debug.WriteLine($"\ttotalTrip: {startToLastThing} + {lastThingToFirstStore} + {firstStoreToLastStore} + {lastStoreToJob}  = {totalTrip}");
                Debug.WriteLine($"\tmaxTotalTrip: {origTrip} * {settings.MaxTotalTripPctOrigTrip} = {maxTotalTrip}");
                Debug.WriteLine($"\tnewLegs: {startToLastThing} + {firstStoreToLastStore} + {lastStoreToJob} = {newLegs}");
                Debug.WriteLine($"\tmaxNewLegs: {origTrip} * {settings.MaxNewLegsPctOrigTrip} = {maxNewLegs}");
                Debug.WriteLine("");

                Debug.WriteLine("Hauls:");
                foreach (var haul in hauls)
                    Debug.WriteLine($"{haul}");

                Debug.WriteLine("Unloads:");
                // we just order by store with no secondary ordering of thing, so just print store
                foreach (var haul in haulsByUnloadOrder)
                    Debug.WriteLine($"{haul.storeCell.GetSlotGroup(pawn.Map)}"); // thing may not have Map

                Debug.WriteLine("");
#endif

                return true;
            }
        }
    }
}