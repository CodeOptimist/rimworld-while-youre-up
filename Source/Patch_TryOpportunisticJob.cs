using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_TryOpportunisticJob
        {
            static Job TryOpportunisticJob(Pawn_JobTracker jobTracker, Job job) {
//                Debug.WriteLine($"Opportunity checking {job}");
                var pawn = Traverse.Create(jobTracker).Field("pawn").GetValue<Pawn>();
                if (JooStoreUtility.PuahHasThingsHauled(pawn)) {
                    Debug.WriteLine($"{RealTime.frameCount} {pawn} Aborting {MethodBase.GetCurrentMethod().Name}() during {job}; already holding items.");
                    return null;
                }

                var jobCell = job.targetA.Cell;

                if (job.def == JobDefOf.DoBill && settings.HaulBeforeBill && settings.Enabled) {
//                    Debug.WriteLine($"Bill: '{job.bill}' label: '{job.bill.Label}'");
//                    Debug.WriteLine($"Recipe: '{job.bill.recipe}' workerClass: '{job.bill.recipe.workerClass}'");
                    foreach (var localTargetInfo in job.targetQueueB) {
                        if (localTargetInfo.Thing == null) continue;

                        if (HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, localTargetInfo.Thing, false)) {
                            // permitted when bleeding because facilitates whatever bill is important enough to do while bleeding
                            //  may save precious time going back for ingredients... unless we want only 1 medicine ASAP; it's a trade-off
                            var storeJob = Hauling.HaulBeforeCarry(pawn, jobCell, localTargetInfo.Thing);
                            if (storeJob != null) return Helper.CatchStanding(pawn, storeJob);
                        }
                    }
                }

                if (settings.SkipIfBleeding && pawn.health.hediffSet.BleedRateTotal > 0.001f) return null;
//                return Helper.CatchStanding(pawn, Hauling.TryHaul(pawn, jobCell) ?? Cleaning.TryClean(pawn, jobCell));
                return Helper.CatchStanding(pawn, Hauling.TryHaul(pawn, jobCell));
            }

            [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryOpportunisticJob))]
            static class Pawn_JobTracker_TryOpportunisticJob_Patch
            {
                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> OpportunisticJobs(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
                    var t = new Transpiler(_codes, __originalMethod);
                    var listerHaulablesIdx = t.TryFindCodeIndex(code => code.LoadsField(AccessTools.DeclaredField(typeof(Map), nameof(Map.listerHaulables))));
                    var skipMod = generator.DefineLabel();

                    t.TryInsertCodes(
                        -3,
                        (i, codes) => i == listerHaulablesIdx,
                        (i, codes) => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Call,      AccessTools.DeclaredMethod(typeof(Pawn_JobTracker_TryOpportunisticJob_Patch), nameof(IsEnabled))),
                            new CodeInstruction(OpCodes.Brfalse_S, skipMod),

                            new CodeInstruction(OpCodes.Ldarg_0),
                            new CodeInstruction(OpCodes.Ldarg_1),
                            new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Patch_TryOpportunisticJob), nameof(TryOpportunisticJob))),
                            new CodeInstruction(OpCodes.Ret),
                        }, true);

                    t.codes[t.MatchIdx - 3].labels.Add(skipMod);
                    return t.GetFinalCodes();
                }

                static bool IsEnabled() {
                    return settings.Enabled;
                }
            }
        }
    }
}
