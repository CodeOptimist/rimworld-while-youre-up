using System;
using System.Collections.Generic;
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
            static Job TryOpportunisticJob(Pawn_JobTracker __instance, Job job) {
                var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                var jobCell = job.targetA.Cell;
                var pawnToJob = pawn.Position.DistanceTo(jobCell);

                foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()) {
                    var pawnToThing = pawn.Position.DistanceTo(thing.Position);
                    if (pawnToThing > 30f) continue;
                    if (pawnToThing > pawnToJob * 0.5f) continue;
                    var thingToJob = thing.Position.DistanceTo(jobCell);
                    if (pawnToThing + thingToJob > pawnToJob * 1.7f) continue;
                    if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) continue;
                    if (thing.IsForbidden(pawn)) continue;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) continue;

                    var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out var storeCell)) continue;

                    var storeToJob = storeCell.DistanceTo(jobCell);
                    if (storeToJob > 50f) continue;
                    if (storeToJob > pawnToJob * 0.6f) continue;
                    var thingToStore = thing.Position.DistanceTo(storeCell);
                    if (pawnToThing + thingToStore + storeToJob > pawnToJob * 1.7f) continue;
                    if (pawnToThing + storeToJob > pawnToJob) continue;
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
