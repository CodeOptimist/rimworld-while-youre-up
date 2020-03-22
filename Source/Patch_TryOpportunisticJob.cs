using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_TryOpportunisticJob
        {
            static Job TryOpportunisticJob(Pawn_JobTracker __instance, Job job) {
                Debug.WriteLine($"Opportunity checking {job}");
                var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                var jobCell = job.targetA.Cell;
                return Hauling.TryHaul(pawn, jobCell); // ?? Cleaning.TryClean(pawn, jobCell);
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
