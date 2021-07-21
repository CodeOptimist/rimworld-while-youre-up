using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Hauling
        {
            public enum HaulProximities { Both, Either, Ignored }

            static readonly        Dictionary<Thing, ProximityStage> thingProximityStage        = new Dictionary<Thing, ProximityStage>();
            public static readonly Dictionary<Thing, IntVec3>        cachedOpportunityStoreCell = new Dictionary<Thing, IntVec3>();

            public static Job TryHaul(Pawn pawn, IntVec3 jobCell) {
                Job _TryHaul() {
                    switch (settings.HaulProximities) {
                        case HaulProximities.Both:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both);
                        case HaulProximities.Either:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either);
                        case HaulProximities.Ignored:
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
                if (!JooStoreUtility.TryFindBestBetterStoreCellFor_ClosestToDestCell(thing, IntVec3.Invalid, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell, true))
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

                    var haulTracker = HaulTracker.CreateAndAdd(SpecialHaulType.Opportunity, pawn, jobCell);
                    return PuahJob(haulTracker, pawn, thing, storeCell) ?? HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }

            // "Optimize hauling"
            public static Job HaulBeforeCarry(Pawn pawn, IntVec3 destCell, Thing thing) {
                if (thing.ParentHolder is Pawn_InventoryTracker) return null;
                if (!JooStoreUtility.TryFindBestBetterStoreCellFor_ClosestToDestCell(
                    thing, destCell, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var storeCell, true)) return null;

                var supplyFromHereDist = thing.Position.DistanceTo(destCell);
                var supplyFromStoreDist = storeCell.DistanceTo(destCell);
                // Debug.WriteLine($"Carry from here: {supplyFromHereDist}; carry from store: {supplyFromStoreDist}");

                // [KV] Infinite Storage https://steamcommunity.com/sharedfiles/filedetails/?id=1233893175
                // infinite storage has an interaction spot 1 tile away from itself
                if (supplyFromStoreDist + 1 < supplyFromHereDist) {
                    //                    Debug.WriteLine(
                    //                        $"'{pawn}' prefixed job with haul for '{thing.Label}' because '{storeCell.GetSlotGroup(pawn.Map)}' is closer to original destination '{destCell}'.");

                    if (DebugViewSettings.drawOpportunisticJobs) {
                        // ReSharper disable once RedundantArgumentDefaultValue
                        pawn.Map.debugDrawer.FlashLine(pawn.Position,  thing.Position, 600, SimpleColor.White);
                        pawn.Map.debugDrawer.FlashLine(thing.Position, destCell,       600, SimpleColor.Magenta);
                        pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,      600, SimpleColor.Cyan);
                        pawn.Map.debugDrawer.FlashLine(storeCell,      destCell,       600, SimpleColor.Cyan);
                    }

                    var haulTracker = HaulTracker.CreateAndAdd(SpecialHaulType.HaulBeforeCarry, pawn, destCell);
                    return PuahJob(haulTracker, pawn, thing, storeCell) ?? HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }

            static Job PuahJob(HaulTracker haulTracker, Pawn pawn, Thing thing, IntVec3 storeCell) {
                if (!havePuah || !settings.HaulToInventory || !settings.Enabled) return null;
                haulTracker.Add(thing, storeCell);
                var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker;
                return (Job) PuahJobOnThing.Invoke(puahWorkGiver, new object[] {pawn, thing, false});
            }

            enum ProximityCheck { Both, Either, Ignored }

            // ReSharper disable once UnusedMember.Local
            enum ProximityStage { Initial, PawnToThing, StoreToJob, PawnToThingRegion, Fail, Success }
        }
    }
}
