using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        public static readonly Dictionary<Thing, IntVec3> opportunityCachedStoreCells = new(); // #CacheTick

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryOpportunisticJob))]
        static class Pawn_JobTracker__TryOpportunisticJob_Patch
        {
            static bool IsEnabled() {
                return settings.Enabled;
            }

            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> _TryOpportunisticJob(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
                var t                  = new Transpiler(_codes, __originalMethod);
                var listerHaulablesIdx = t.TryFindCodeIndex(code => code.LoadsField(AccessTools.DeclaredField(typeof(Map), nameof(Map.listerHaulables))));
                var skipMod            = generator.DefineLabel();

                t.TryInsertCodes(
                    -3,
                    (i, codes) => i == listerHaulablesIdx,
                    (i, codes) => new List<CodeInstruction> {
                        new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Pawn_JobTracker__TryOpportunisticJob_Patch), nameof(IsEnabled))),
                        new(OpCodes.Brfalse_S, skipMod),

                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldarg_2),
                        new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Pawn_JobTracker__TryOpportunisticJob_Patch), nameof(TryOpportunisticJob))),
                        new(OpCodes.Ret),
                    }, true);

                t.codes[t.MatchIdx - 3].labels.Add(skipMod);
                return t.GetFinalCodes();
            }

            // vanilla checks for job.def.allowOpportunisticPrefix and lots of other things before this
            // our settings.Enabled check is done prior to this in the transpiler
            static Job TryOpportunisticJob(Pawn_JobTracker jobTracker, Job job) {
                // Debug.WriteLine($"Opportunity checking {job}");
                var pawn = Traverse.Create(jobTracker).Field("pawn").GetValue<Pawn>();
                if (AlreadyHauling(pawn)) return null;

                if (job.def == JobDefOf.DoBill && settings.HaulBeforeCarry_Bills) {
                    Debug.WriteLine($"Bill: '{job.bill}' label: '{job.bill.Label}'");
                    Debug.WriteLine($"Recipe: '{job.bill.recipe}' workerClass: '{job.bill.recipe.workerClass}'");
                    for (var i = 0; i < job.targetQueueB.Count; i++) {
                        var ingredient = job.targetQueueB[i];
                        if (ingredient.Thing is null) continue;

                        if (!havePuah || !settings.UsePickUpAndHaulPlus) { // too difficult to know in advance if there are no extras for PUAH
                            if (ingredient.Thing.stackCount <= job.countQueue[i])
                                continue; // there are no extras
                        }

                        if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, ingredient.Thing, false)) continue; // fast check

                        // permitted when bleeding because facilitates whatever bill is important enough to do while bleeding
                        //  may save precious time going back for ingredients... unless we want only 1 medicine ASAP; it's a trade-off

                        var storeJob = BeforeCarryDetourJob(pawn, job.targetA, ingredient.Thing); // #BeforeBillDetour
                        if (storeJob is not null) return JobUtility__TryStartErrorRecoverJob_Patch.CatchStandingJob(pawn, storeJob);
                    }
                }

                if (new[] {
                        JobDefOf.PrepareCaravan_CollectAnimals, JobDefOf.PrepareCaravan_GatherAnimals,
                        JobDefOf.PrepareCaravan_GatherDownedPawns, JobDefOf.PrepareCaravan_GatherItems,
                    }.Contains(job.def)) return null;
                if (pawn.health.hediffSet.BleedRateTotal > 0.001f) return null;

                // use first ingredient location if bill
                var jobTarget = job.def == JobDefOf.DoBill ? job.targetQueueB?.FirstOrDefault() ?? job.targetA : job.targetA;
                return JobUtility__TryStartErrorRecoverJob_Patch.CatchStandingJob(pawn, OpportunityJob(pawn, jobTarget));
            }
        }

        enum CanHaulResult { RangeFail, HardFail, Success }

        struct MaxRanges
        {
            public int   expandCount;
            public float startToThing, startToThingPctOrigTrip;
            public float storeToJob,   storeToJobPctOrigTrip;

            [TweakValue("WhileYoureUp", 1.1f, 3f)]
            // ReSharper disable once FieldCanBeMadeReadOnly.Local
            public static float heuristicExpandFactor = 2f;

            public static MaxRanges operator *(MaxRanges maxRanges, float multiplier) {
                maxRanges.expandCount             += 1;
                maxRanges.startToThing            *= multiplier;
                maxRanges.startToThingPctOrigTrip *= multiplier;
                maxRanges.storeToJob              *= multiplier;
                maxRanges.storeToJobPctOrigTrip   *= multiplier;
                return maxRanges;
            }
        }

        public static Job OpportunityJob(Pawn pawn, LocalTargetInfo jobTarget) {
            Job _OpportunityJob() {
                var maxRanges = new MaxRanges {
                    startToThing            = settings.Opportunity_MaxStartToThing,
                    startToThingPctOrigTrip = settings.Opportunity_MaxStartToThingPctOrigTrip,
                    storeToJob              = settings.Opportunity_MaxStoreToJob,
                    storeToJobPctOrigTrip   = settings.Opportunity_MaxStoreToJobPctOrigTrip,
                };

                var i         = 0;
                var haulables = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                while (haulables.Count > 0) {
                    if (i == haulables.Count) {
                        // By expanding gradually, our slow checks will be performed in the most optimistic order that we can check for cheaply
                        //  (i.e. thing already close to pawn, storage close to job). Excellent opportunities may satisfy neither of these,
                        // but it's the best cheap heuristic we have, and better than random.
                        // todo a smaller number, maybe 1.1f? might actually perform much better here? it's a TweakValue now
                        maxRanges *= MaxRanges.heuristicExpandFactor;
                        i         =  0;
                    }

                    var thing   = haulables[i];
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
                            // todo test our heuristic expand factor more thoroughly
                            Debug.WriteLine($"Checked: {1 - (haulables.Count - 1) / (float)pawn.Map.listerHaulables.haulables.Count:P}. Expansions: {maxRanges.expandCount}");

                            if (DebugViewSettings.drawOpportunisticJobs) {
                                for (var _ = 0; _ < 3; _++) {
                                    var duration = 600;
                                    pawn.Map.debugDrawer.FlashCell(pawn.Position,  0.50f, pawn.Name.ToStringShort, duration);
                                    pawn.Map.debugDrawer.FlashCell(thing.Position, 0.62f, pawn.Name.ToStringShort, duration);
                                    pawn.Map.debugDrawer.FlashCell(storeCell,      0.22f, pawn.Name.ToStringShort, duration);
                                    pawn.Map.debugDrawer.FlashCell(jobTarget.Cell, 0.0f,  pawn.Name.ToStringShort, duration);

                                    pawn.Map.debugDrawer.FlashLine(pawn.Position,  jobTarget.Cell, duration, SimpleColor.Red);
                                    pawn.Map.debugDrawer.FlashLine(pawn.Position,  thing.Position, duration, SimpleColor.Green);
                                    pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,      duration, SimpleColor.Green);
                                    // first ingredient if a bill
                                    pawn.Map.debugDrawer.FlashLine(storeCell, jobTarget.Cell, duration, SimpleColor.Green);
                                }
                            }

                            if (havePuah && settings.UsePickUpAndHaulPlus) {
                                var detour = SetOrAddDetour(pawn, DetourType.PuahOpportunity, startCell: pawn.Position, jobTarget: jobTarget);
                                detour.TrackPuahThing(thing, storeCell);
                                var puahJob = PuahJob(pawn, thing);
                                if (puahJob is not null) return puahJob;
                            }

                            SetOrAddDetour(pawn, DetourType.Opportunity, jobTarget: jobTarget);
                            return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                    }
                }

                return null;
            }

            var result = _OpportunityJob();
            opportunityCachedStoreCells.Clear(); // #CacheTick
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
            if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) is not null) return CanHaulResult.HardFail;
            if (thing.IsForbidden(pawn)) return CanHaulResult.HardFail;
            if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) return CanHaulResult.HardFail;

            var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
            if (!opportunityCachedStoreCells.TryGetValue(thing, out storeCell)) {
                if (!TryFindBestBetterStoreCellFor_ClosestToTarget(
                        thing, jobTarget, IntVec3.Invalid, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell, maxRanges.expandCount == 0))
                    return CanHaulResult.HardFail;
            }
            // else
            //     Debug.WriteLine($"{RealTime.frameCount} Cache hit! (Size: {opportunityCachedStoreCells.Count}) CanHaulResult");

            // we need storeCell everywhere, so cache it
            opportunityCachedStoreCells.SetOrAdd(thing, storeCell);

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
                    var result   = pawnPath.TotalCost;
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

            // try for the very best initially, so we're at least as good as vanilla
            else if (maxRanges.expandCount == 0) {
                if (!pawn.Position.WithinRegions(thing.Position, pawn.Map, settings.Opportunity_MaxStartToThingRegionLookCount, TraverseParms.For(pawn)))
                    return CanHaulResult.RangeFail;

                if (!storeCell.WithinRegions(jobTarget.Cell, pawn.Map, settings.Opportunity_MaxStoreToJobRegionLookCount, TraverseParms.For(pawn)))
                    return CanHaulResult.RangeFail;
            }

            return CanHaulResult.Success;
        }

        partial record BaseDetour
        {
            public bool TrackPuahThingIfOpportune(Thing thing, Pawn pawn, ref IntVec3 foundCell) {
                var isPrepend = pawn.carryTracker?.CarriedThing == thing;
                TrackPuahThing(thing, foundCell, isPrepend, trackDef: false);

                var curPos           = opportunity_startCell;
                var startToLastThing = 0f;
                foreach (var (thing_, _) in opportunity_hauls) {
                    startToLastThing += curPos.DistanceTo(thing_.Position);
                    curPos           =  thing_.Position;
                }

                // every time, since our foundCell could fit anywhere
                List<(Thing thing, IntVec3 storeCell)> GetHaulsByUnloadDistance() {
                    // PUAH WorkGiver always takes us to the first
                    var ordered = new List<(Thing thing, IntVec3 storeCell)> { opportunity_hauls.First() };
                    var pending = new List<(Thing thing, IntVec3 storeCell)>(opportunity_hauls.GetRange(1, opportunity_hauls.Count - 1));

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
                    curPos                =  haul.storeCell;
                }

                var lastStoreToJob = curPos.DistanceTo(opportunity_jobTarget.Cell);

                var origTrip          = opportunity_startCell.DistanceTo(opportunity_jobTarget.Cell);
                var totalTrip         = startToLastThing + lastThingToFirstStore + firstStoreToLastStore + lastStoreToJob;
                var maxTotalTrip      = origTrip * settings.Opportunity_MaxTotalTripPctOrigTrip;
                var newLegs           = startToLastThing + firstStoreToLastStore + lastStoreToJob;
                var maxNewLegs        = origTrip * settings.Opportunity_MaxNewLegsPctOrigTrip;
                var exceedsMaxTrip    = totalTrip > maxTotalTrip;
                var exceedsMaxNewLegs = newLegs > maxNewLegs;
                var isRejected        = exceedsMaxTrip || exceedsMaxNewLegs;

                if (isRejected) {
                    foundCell = IntVec3.Invalid;
                    opportunity_hauls.RemoveAt(isPrepend ? 0 : opportunity_hauls.Count - 1);
                    return false;
                }

                Debug.Assert(thing is not null, nameof(thing) + " is not null");
                puah_defHauls.SetOrAdd(thing.def, foundCell);

#if DEBUG
                var storeCells = haulsByUnloadDistance.Select(x => x.storeCell).ToList();
                Debug.WriteLine($"APPROVED {thing} -> {foundCell} for {pawn}");
                Debug.WriteLine(
                    $"\tstartToLastThing: {pawn} {opportunity_startCell} -> {string.Join(" -> ", opportunity_hauls.Select(x => $"{x.thing} {x.thing.Position}"))} = {startToLastThing}");
                Debug.WriteLine($"\tlastThingToFirstStore: {thing} {thing.Position} -> {storeCells.First().GetSlotGroup(pawn.Map)} {storeCells.First()} = {lastThingToFirstStore}");
                Debug.WriteLine($"\tfirstStoreToLastStore: {string.Join(" -> ", storeCells.Select(x => $"{x.GetSlotGroup(pawn.Map)} {x}"))} = {firstStoreToLastStore}");
                Debug.WriteLine(
                    $"\tlastStoreToJob: {storeCells.Last().GetSlotGroup(pawn.Map)} {storeCells.Last()} -> {opportunity_jobTarget} {opportunity_jobTarget.Cell} = {lastStoreToJob}");
                Debug.WriteLine($"\torigTrip: {pawn} {opportunity_startCell} -> {opportunity_jobTarget} {opportunity_jobTarget.Cell} = {origTrip}");
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
