using System.Collections.Generic;
using System.Linq;
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
            static readonly        Dictionary<Thing, ProximityStage> thingProximityStage = new Dictionary<Thing, ProximityStage>();
            public static readonly Dictionary<Thing, IntVec3>        cachedStoreCells    = new Dictionary<Thing, IntVec3>();

            enum ProximityCheck { Both, Either, Ignored }

            // ReSharper disable once UnusedMember.Local
            enum ProximityStage { Initial, PawnToThing, StoreToJob, PawnToThingRegion, Fail, Success }

            public static Job TryHaul(Pawn pawn, LocalTargetInfo jobTarget) {
                Job _TryHaul() {
                    switch (settings.Opportunity_HaulProximities) {
                        case Settings.HaulProximitiesEnum.Both:
                            return TryHaulStage(pawn, jobTarget, ProximityCheck.Both);
                        case Settings.HaulProximitiesEnum.Either:
                            return TryHaulStage(pawn, jobTarget, ProximityCheck.Both) ?? TryHaulStage(pawn, jobTarget, ProximityCheck.Either);
                        case Settings.HaulProximitiesEnum.Ignored:
                            return TryHaulStage(pawn, jobTarget, ProximityCheck.Both)
                                   ?? TryHaulStage(pawn, jobTarget, ProximityCheck.Either) ?? TryHaulStage(pawn, jobTarget, ProximityCheck.Ignored);
                        default:
                            return null;
                    }
                }

                var result = _TryHaul();
                thingProximityStage.Clear();
                cachedStoreCells.Clear();
                return result;
            }

            static ProximityStage CanHaul(ProximityStage proximityStage, Pawn pawn, Thing thing, LocalTargetInfo jobTarget, ProximityCheck proximityCheck, out IntVec3 storeCell) {
                storeCell = IntVec3.Invalid;
                var pawnToJob = pawn.Position.DistanceTo(jobTarget.Cell);

                var pawnToThing = pawn.Position.DistanceTo(thing.Position);
                if (proximityStage < ProximityStage.StoreToJob) {
                    var atMax = settings.Opportunity_MaxStartToThing > 0 && pawnToThing > settings.Opportunity_MaxStartToThing;
                    var atMaxPct = settings.Opportunity_MaxStartToThingPctOrigTrip > 0 && pawnToThing > pawnToJob * settings.Opportunity_MaxStartToThingPctOrigTrip;
                    var pawnToThingFail = atMax || atMaxPct;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when pawnToThingFail: return ProximityStage.PawnToThing;
                    }

                    var thingToJob = thing.Position.DistanceTo(jobTarget.Cell);
                    // if this one exceeds the maximum the next maxTotalTripPctOrigTrip check certainly will
                    if (settings.Opportunity_MaxTotalTripPctOrigTrip > 0 && pawnToThing + thingToJob > pawnToJob * settings.Opportunity_MaxTotalTripPctOrigTrip)
                        return ProximityStage.Fail;
                    if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) return ProximityStage.Fail;
                    if (thing.IsForbidden(pawn)) return ProximityStage.Fail;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) return ProximityStage.Fail;
                }

                var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                if (!cachedStoreCells.TryGetValue(thing, out storeCell)
                    && !TryFindBestBetterStoreCellFor_ClosestToTarget(thing, jobTarget, IntVec3.Invalid, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell, true))
                    return ProximityStage.Fail;

                // we need storeCell everywhere, so cache it
                cachedStoreCells.SetOrAdd(thing, storeCell);

                var storeToJob = storeCell.DistanceTo(jobTarget.Cell);
                if (proximityStage < ProximityStage.PawnToThingRegion) {
                    var atMax2 = settings.Opportunity_MaxStoreToJob > 0 && storeToJob > settings.Opportunity_MaxStoreToJob;
                    var atMaxPct2 = settings.Opportunity_MaxStoreToJobPctOrigTrip > 0 && storeToJob > pawnToJob * settings.Opportunity_MaxStoreToJobPctOrigTrip;
                    var storeToJobFail = atMax2 || atMaxPct2;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when storeToJobFail:                                                   return ProximityStage.StoreToJob;
                        case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThing && storeToJobFail: return ProximityStage.StoreToJob;
                    }

                    var thingToStore = thing.Position.DistanceTo(storeCell);
                    if (settings.Opportunity_MaxTotalTripPctOrigTrip > 0 && pawnToThing + thingToStore + storeToJob > pawnToJob * settings.Opportunity_MaxTotalTripPctOrigTrip)
                        return ProximityStage.Fail;
                    if (settings.Opportunity_MaxNewLegsPctOrigTrip > 0 && pawnToThing + storeToJob > pawnToJob * settings.Opportunity_MaxNewLegsPctOrigTrip)
                        return ProximityStage.Fail;
                }

                bool PawnToThingRegionFail() {
                    return settings.Opportunity_MaxStartToThingRegionLookCount > 0 && !pawn.Position.WithinRegions(
                        thing.Position, pawn.Map, settings.Opportunity_MaxStartToThingRegionLookCount, TraverseParms.For(pawn));
                }

                bool StoreToJobRegionFail(IntVec3 _storeCell) {
                    return settings.Opportunity_MaxStoreToJobRegionLookCount > 0 && !_storeCell.WithinRegions(
                        jobTarget.Cell, pawn.Map, settings.Opportunity_MaxStoreToJobRegionLookCount, TraverseParms.For(pawn));
                }

                switch (proximityCheck) {
                    case ProximityCheck.Both when PawnToThingRegionFail():                                                                 return ProximityStage.PawnToThingRegion;
                    case ProximityCheck.Both when StoreToJobRegionFail(storeCell):                                                         return ProximityStage.Fail;
                    case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThingRegion && StoreToJobRegionFail(storeCell): return ProximityStage.Fail;
                }

                return ProximityStage.Success;
            }

            static Job TryHaulStage(Pawn pawn, LocalTargetInfo jobTarget, ProximityCheck proximityCheck) {
                foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()) {
                    if (thingProximityStage.TryGetValue(thing, out var proximityStage) && proximityStage == ProximityStage.Fail)
                        continue;

                    var newProximityStage = CanHaul(proximityStage, pawn, thing, jobTarget, proximityCheck, out var storeCell);
                    // Debug.WriteLine($"{pawn} for {thing} proximity stage: {proximityStage} -> {newProximityStage}");
                    thingProximityStage.SetOrAdd(thing, newProximityStage);
                    if (newProximityStage != ProximityStage.Success) continue;

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

                return null;
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
                var exceedsMaxTrip = settings.Opportunity_MaxTotalTripPctOrigTrip > 0 && totalTrip > maxTotalTrip;
                var exceedsMaxNewLegs = settings.Opportunity_MaxNewLegsPctOrigTrip > 0 && newLegs > maxNewLegs;
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
