using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

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

                if (haulBeforeBill.Value && haveCommonSense && (bool) CsHaulingOverBillsSetting.GetValue(csSettings)) {
                    haulBeforeBill.Value = false;
                    haulBeforeBill.ForceSaveChanges();
                }

                if (job.def == JobDefOf.DoBill && haulBeforeBill.Value && enabled.Value) {
                    foreach (var localTargetInfo in job.targetQueueB) {
                        if (localTargetInfo.Thing == null) continue;

                        // permitted when bleeding because facilitates whatever bill is important enough to do while bleeding
                        //  may save precious time going back for ingredients... unless we only want 1 medicine ASAP; it's a trade-off
                        var storeJob = Hauling.HaulBeforeCarry(pawn, jobCell, localTargetInfo.Thing);
                        if (storeJob != null) return storeJob;
                    }
                }

                if (skipIfBleeding.Value && pawn.health.hediffSet.BleedRateTotal > 0.001f) return null;
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
                static IEnumerable<CodeInstruction> OpportunisticJobs(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
                    codes = instructions.ToList();
                    newCodes = new List<CodeInstruction>();
                    i = 0;

                    var listerHaulablesIdx = codes.FindIndex(code => code.LoadsField(AccessTools.Field(typeof(Map), nameof(Map.listerHaulables))));
                    var skipModLabel = generator.DefineLabel();

                    InsertCode(
                        -3,
                        () => i == listerHaulablesIdx,
                        () => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Pawn_JobTracker_TryOpportunisticJob_Patch), nameof(IsEnabled))),
                            new CodeInstruction(OpCodes.Brfalse_S, skipModLabel),

                            new CodeInstruction(OpCodes.Ldarg_0),
                            new CodeInstruction(OpCodes.Ldarg_1),
                            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_TryOpportunisticJob), nameof(TryOpportunisticJob))),
                            new CodeInstruction(OpCodes.Ret),
                        }, true);

                    if (listerHaulablesIdx != -1)
                        codes[i - 3].labels.Add(skipModLabel);

                    for (; i < codes.Count; i++)
                        newCodes.Add(codes[i]);
                    return newCodes.AsEnumerable();
                }

                static bool IsEnabled() {
                    return enabled.Value;
                }
            }
        }
    }
}
