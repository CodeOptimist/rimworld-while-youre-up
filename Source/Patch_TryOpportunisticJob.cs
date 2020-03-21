using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_TryOpportunisticJob
        {
            static readonly HashSet<Thing> cachedReserved = new HashSet<Thing>();
            static readonly Dictionary<Thing, IntVec3> cachedStoreCell = new Dictionary<Thing, IntVec3>();

            static Job TryOpportunisticJob(Pawn_JobTracker __instance, Job job) {
                Debug.WriteLine($"Opportunity checking {job}");
                var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                var jobCell = job.targetA.Cell;
                return TryOpportunisticHaul(pawn, jobCell); // ?? TryOpportunisticClean(pawn, jobCell);
            }

            static Job TryOpportunisticHaul(Pawn pawn, IntVec3 jobCell) {
                cachedReserved.Clear();
                cachedStoreCell.Clear();
                switch (haulProximities.Value) {
                    case HaulProximities.PreferWithin:
                        return _TryOpportunisticHaul(pawn, jobCell, true) ?? _TryOpportunisticHaul(pawn, jobCell, false);
                    case HaulProximities.RequireWithin:
                        return _TryOpportunisticHaul(pawn, jobCell, true);
                    case HaulProximities.Ignore:
                        return _TryOpportunisticHaul(pawn, jobCell, false);
                    default:
                        return null;
                }
            }

            static Job _TryOpportunisticHaul(Pawn pawn, IntVec3 jobCell, bool requireWithinLegRanges) {
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

                    return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }

            [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryOpportunisticJob))]
            static class Pawn_JobTracker_TryOpportunisticJob_Patch
            {
                static List<CodeInstruction> codes, newCodes;
                static int i;

                static void InsertCode(int offset, Func<bool> when, Func<List<CodeInstruction>> what, bool bringLabels = false) {
                    JobsOfOpportunity.InsertCode(ref i, ref codes, ref newCodes, offset, when, what, bringLabels);
                }

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> OpportunisticJobs(IEnumerable<CodeInstruction> instructions) {
                    codes = instructions.ToList();
                    newCodes = new List<CodeInstruction>();
                    i = 0;

                    InsertCode(
                        -3,
                        () => codes[i].LoadsField(AccessTools.Field(typeof(Map), nameof(Map.listerHaulables))),
                        () => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Ldarg_0),
                            new CodeInstruction(OpCodes.Ldarg_1),
                            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_TryOpportunisticJob), nameof(TryOpportunisticJob))),
                            new CodeInstruction(OpCodes.Ret),
                        }, true);

                    for (; i < codes.Count; i++)
                        newCodes.Add(codes[i]);
                    return newCodes.AsEnumerable();
                }
            }
        }
    }
}
