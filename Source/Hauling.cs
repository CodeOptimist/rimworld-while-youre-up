using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Hauling
        {
            public enum HaulProximities { RequireWithin, PreferWithin, Ignore }

            static readonly HashSet<Thing> cachedReserved = new HashSet<Thing>();
            static readonly Dictionary<Thing, IntVec3> cachedStoreCell = new Dictionary<Thing, IntVec3>();

            public static Job TryHaul(Pawn pawn, IntVec3 jobCell) {
                cachedReserved.Clear();
                cachedStoreCell.Clear();
                switch (haulProximities.Value) {
                    case HaulProximities.PreferWithin:
                        return _TryHaul(pawn, jobCell, true) ?? _TryHaul(pawn, jobCell, false);
                    case HaulProximities.RequireWithin:
                        return _TryHaul(pawn, jobCell, true);
                    case HaulProximities.Ignore:
                        return _TryHaul(pawn, jobCell, false);
                    default:
                        return null;
                }
            }

            static Job _TryHaul(Pawn pawn, IntVec3 jobCell, bool requireWithinLegRanges) {
                var pawnToJob = pawn.Position.DistanceTo(jobCell);
                foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()) {
                    var pawnToThing = pawn.Position.DistanceTo(thing.Position);
                    if (requireWithinLegRanges && maxStartToThing.Value > 0 && pawnToThing > maxStartToThing.Value) continue;
                    if (requireWithinLegRanges && maxStartToThingPctOrigTrip.Value > 0 && pawnToThing > pawnToJob * maxStartToThingPctOrigTrip.Value) continue;
                    var thingToJob = thing.Position.DistanceTo(jobCell);
                    if (maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToJob > pawnToJob * maxTotalTripPctOrigTrip.Value) continue;

                    if (cachedReserved.Contains(thing) || pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) {
                        cachedReserved.Add(thing);
                        continue;
                    }

                    if (thing.IsForbidden(pawn)) continue;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) continue;
                    var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                    if (!cachedStoreCell.TryGetValue(thing, out var storeCell)) {
                        if (!StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell))
                            continue;
                    }

                    cachedStoreCell.SetOrAdd(thing, storeCell);

                    var storeToJob = storeCell.DistanceTo(jobCell);
                    if (requireWithinLegRanges && maxStoreToJob.Value > 0 && storeToJob > maxStoreToJob.Value) continue;
                    if (requireWithinLegRanges && maxStoreToJobPctOrigTrip.Value > 0 && storeToJob > pawnToJob * maxStoreToJobPctOrigTrip.Value) continue;
                    var thingToStore = thing.Position.DistanceTo(storeCell);
                    if (maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToStore + storeToJob > pawnToJob * maxTotalTripPctOrigTrip.Value) continue;

                    if (maxNewLegsPctOrigTrip.Value > 0 && pawnToThing + storeToJob > pawnToJob * maxNewLegsPctOrigTrip.Value) continue;
                    if (!pawn.Position.WithinRegions(thing.Position, pawn.Map, 25, TraverseParms.For(pawn))) continue;
                    if (!storeCell.WithinRegions(jobCell, pawn.Map, 25, TraverseParms.For(pawn))) continue;

                    if (DebugViewSettings.drawOpportunisticJobs) {
                        Log.Message("Opportunistic job spawned");
                        pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, 600, SimpleColor.Red);
                        pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell, 600, SimpleColor.Green);
                        pawn.Map.debugDrawer.FlashLine(storeCell, jobCell, 600, SimpleColor.Blue);
                    }

                    Job puahJob = null;
                    if (haulToInventory.Value && puahWorkGiver != null) {
                        if (AccessTools.Method(PuahWorkGiver_HaulToInventory_Type, "JobOnThing") is MethodInfo method)
                            puahJob = (Job) method.Invoke(puahWorkGiver, new object[] {pawn, thing, false});
                    }

                    return puahJob ?? HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }
        }
    }
}
