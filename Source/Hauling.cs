using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Hauling
        {
            public enum HaulProximities { Both, Either, Ignored }

            public static readonly Dictionary<Pawn, ForPuah> pawnPuah = new Dictionary<Pawn, ForPuah>();
            public static readonly Dictionary<Pawn, bool> pawnHaulToCell = new Dictionary<Pawn, bool>();

            static readonly Dictionary<Thing, ProximityStage> thingProximityStage = new Dictionary<Thing, ProximityStage>();
            public static readonly Dictionary<Thing, IntVec3> cachedStoreCell = new Dictionary<Thing, IntVec3>();

            public static Job TryHaul(Pawn pawn, IntVec3 jobCell) {
                Job _TryHaul() {
                    switch (haulProximities.Value) {
                        case HaulProximities.Both:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both);
                        case HaulProximities.Either:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either);
                        case HaulProximities.Ignored:
                            return TryHaulStage(pawn, jobCell, ProximityCheck.Both) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Ignored);
                        default:
                            return null;
                    }
                }

                var result = _TryHaul();
                thingProximityStage.Clear();
                cachedStoreCell.Clear();
                return result;
            }

            static ProximityStage CanHaul(ProximityStage proximityStage, Pawn pawn, Thing thing, IntVec3 jobCell, ProximityCheck proximityCheck, out IntVec3 storeCell) {
                storeCell = IntVec3.Invalid;
                var pawnToJob = pawn.Position.DistanceTo(jobCell);

                var pawnToThing = pawn.Position.DistanceTo(thing.Position);
                if (proximityStage < ProximityStage.StoreToJob) {
                    var atMax = maxStartToThing.Value > 0 && pawnToThing > maxStartToThing.Value;
                    var atMaxPct = maxStartToThingPctOrigTrip.Value > 0 && pawnToThing > pawnToJob * maxStartToThingPctOrigTrip.Value;
                    var pawnToThingFail = atMax || atMaxPct;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when pawnToThingFail: return ProximityStage.PawnToThing;
                    }

                    var thingToJob = thing.Position.DistanceTo(jobCell);
                    // if this one exceeds the maximum the next maxTotalTripPctOrigTrip check certainly will
                    if (maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToJob > pawnToJob * maxTotalTripPctOrigTrip.Value) return ProximityStage.Fail;
                    if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) return ProximityStage.Fail;
                    if (thing.IsForbidden(pawn)) return ProximityStage.Fail;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) return ProximityStage.Fail;
                }

                // we need storeCell everywhere, so cache it
                if (!cachedStoreCell.TryGetValue(thing, out storeCell)) {
                    var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell)) return ProximityStage.Fail;
                }

                cachedStoreCell.SetOrAdd(thing, storeCell);

                var storeToJob = storeCell.DistanceTo(jobCell);
                if (proximityStage < ProximityStage.PawnToThingRegion) {
                    var atMax2 = maxStoreToJob.Value > 0 && storeToJob > maxStoreToJob.Value;
                    var atMaxPct2 = maxStoreToJobPctOrigTrip.Value > 0 && storeToJob > pawnToJob * maxStoreToJobPctOrigTrip.Value;
                    var storeToJobFail = atMax2 || atMaxPct2;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when storeToJobFail: return ProximityStage.StoreToJob;
                        case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThing && storeToJobFail: return ProximityStage.StoreToJob;
                    }

                    var thingToStore = thing.Position.DistanceTo(storeCell);
                    if (maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToStore + storeToJob > pawnToJob * maxTotalTripPctOrigTrip.Value) return ProximityStage.Fail;
                    if (maxNewLegsPctOrigTrip.Value > 0 && pawnToThing + storeToJob > pawnToJob * maxNewLegsPctOrigTrip.Value) return ProximityStage.Fail;
                }

                bool PawnToThingRegionFail() {
                    return maxStartToThingRegionLookCount.Value > 0 && !pawn.Position.WithinRegions(thing.Position, pawn.Map, maxStartToThingRegionLookCount.Value, TraverseParms.For(pawn));
                }

                bool StoreToJobRegionFail(IntVec3 _storeCell) {
                    return maxStoreToJobRegionLookCount.Value > 0 && !_storeCell.WithinRegions(jobCell, pawn.Map, maxStoreToJobRegionLookCount.Value, TraverseParms.For(pawn));
                }

                switch (proximityCheck) {
                    case ProximityCheck.Both when PawnToThingRegionFail(): return ProximityStage.PawnToThingRegion;
                    case ProximityCheck.Both when StoreToJobRegionFail(storeCell): return ProximityStage.Fail;
                    case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThingRegion && StoreToJobRegionFail(storeCell): return ProximityStage.Fail;
                }

                return ProximityStage.Success;
            }

            static Job TryHaulStage(Pawn pawn, IntVec3 jobCell, ProximityCheck proximityCheck) {
                foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()) {
                    if (thingProximityStage.TryGetValue(thing, out var proximityStage) && proximityStage == ProximityStage.Fail)
                        continue;

                    var newProximityStage = CanHaul(proximityStage, pawn, thing, jobCell, proximityCheck, out var storeCell);
                    Debug.WriteLine($"{pawn} for {thing} proximity stage: {proximityStage} -> {newProximityStage}");
                    thingProximityStage.SetOrAdd(thing, newProximityStage);
                    if (newProximityStage != ProximityStage.Success) continue;

                    if (DebugViewSettings.drawOpportunisticJobs) {
                        Log.Message("Opportunistic job spawned");
                        pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, 600, SimpleColor.Red);
                        pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell, 600, SimpleColor.Green);
                        pawn.Map.debugDrawer.FlashLine(storeCell, jobCell, 600, SimpleColor.Blue);
                    }

                    pawnHaulToCell.SetOrAdd(pawn, true);
                    return PuahJob(pawn, jobCell, thing, storeCell) ?? HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }

            public static Job HaulBeforeCarry(Pawn pawn, IntVec3 dest, Thing th) {
                if (th.ParentHolder is Pawn_InventoryTracker) return null;
                if (th.IsInValidStorage()) return null;
                if (!StoreUtility.TryFindBestBetterStoreCellFor(th, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out var storeCell, false)) return null;

                var supplyFromHereDist = th.Position.DistanceTo(dest);
                var supplyFromStoreDist = storeCell.DistanceTo(dest);
                Debug.WriteLine($"Carry from here: {supplyFromHereDist}; carry from store: {supplyFromStoreDist}");

                // [KV] Infinite Storage https://steamcommunity.com/sharedfiles/filedetails/?id=1233893175
                // infinite storage has an interaction spot 1 tile away from itself
                if (supplyFromStoreDist + 1 < supplyFromHereDist) {
                    Debug.WriteLine($"'{pawn}' prefixed job with storage haul for '{th.Label}' because '{storeCell.GetSlotGroup(pawn.Map)}' is closer to original destination '{dest}'.");
                    return PuahJob(pawn, dest, th, storeCell) ?? HaulAIUtility.HaulToCellStorageJob(pawn, th, storeCell, false);
                }

                return null;
            }

            public static Job PuahJob(Pawn pawn, IntVec3 jobCell, Thing thing, IntVec3 storeCell) {
                if (!haulToInventory.Value || PuahJobOnThing == null) return null;
                Debug.WriteLine("Activating Pick Up And Haul.");
                pawnPuah.SetOrAdd(pawn, new ForPuah {hauls = new List<(Thing thing, IntVec3 store)> {(thing, storeCell)}, startCell = pawn.Position, jobCell = jobCell});
                return (Job) PuahJobOnThing.Invoke(puahWorkGiver, new object[] {pawn, thing, false});
            }

            public class ForPuah
            {
                public List<(Thing thing, IntVec3 store)> hauls;
                public IntVec3 jobCell;
                public IntVec3 startCell;
            }

            enum ProximityCheck { Both, Either, Ignored }

            enum ProximityStage { Initial, PawnToThing, StoreToJob, PawnToThingRegion, Fail, Success }
        }
    }
}
