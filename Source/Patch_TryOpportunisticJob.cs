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
                var cell = job.targetA.Cell;
                var num = pawn.Position.DistanceTo(cell);

                var list = pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling();
                for (var i = 0; i < list.Count; i++) {
                    var thing = list[i];
                    var num2 = pawn.Position.DistanceTo(thing.Position);
                    if (num2 <= 30f && num2 <= num * 0.5f && num2 + thing.Position.DistanceTo(cell) <= num * 1.7f
                        && pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) == null && !thing.IsForbidden(pawn)
                        && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) {
                        var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                        var invalid = IntVec3.Invalid;
                        if (StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out invalid)) {
                            var num3 = invalid.DistanceTo(cell);
                            if (num3 <= 50f && num3 <= num * 0.6f && num2 + thing.Position.DistanceTo(invalid) + num3 <= num * 1.7f && num2 + num3 <= num
                                && pawn.Position.WithinRegions(thing.Position, pawn.Map, 25, TraverseParms.For(pawn)) && invalid.WithinRegions(cell, pawn.Map, 25, TraverseParms.For(pawn))) {
                                if (DebugViewSettings.drawOpportunisticJobs) {
                                    Log.Message("Opportunistic job spawned");
                                    pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, 600, SimpleColor.Red);
                                    pawn.Map.debugDrawer.FlashLine(thing.Position, invalid, 600, SimpleColor.Green);
                                    pawn.Map.debugDrawer.FlashLine(invalid, cell, 600, SimpleColor.Blue);
                                }

                                return HaulAIUtility.HaulToCellStorageJob(pawn, thing, invalid, false);
                            }
                        }
                    }
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
