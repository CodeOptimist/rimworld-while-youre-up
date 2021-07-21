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
        static class Patch_ResourceDeliverJobFor
        {
            [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
            static class WorkGiver_ConstructDeliverResources_ResourceDeliverJobFor_Patch
            {
                static Job HaulBeforeSupply(Pawn pawn, Thing constructible, Thing th) {
                    if (!settings.HaulBeforeSupply || !settings.Enabled) return null;
                    if (JooStoreUtility.PuahHasThingsHauled(pawn)) {
                        Debug.WriteLine($"{RealTime.frameCount} {pawn} Aborting {MethodBase.GetCurrentMethod().Name}() already holding items.");
                        return null;
                    }

                    return Helper.CatchStanding(pawn, Hauling.HaulBeforeCarry(pawn, constructible.Position, th));
                }

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> _HaulBeforeSupply(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
                    var t = new Transpiler(_codes, __originalMethod);

                    var nearbyResourcesIdx =
                        t.TryFindCodeIndex(code => code.Calls(AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources), "FindAvailableNearbyResources")));
                    var foundResIdx = t.TryFindCodeLastIndex(nearbyResourcesIdx, code => code.opcode == OpCodes.Brfalse) + 1;
                    var foundRes = generator.DefineLabel();
                    t.codes[foundResIdx].labels.Add(foundRes);
                    var returnJobIdx = t.TryFindCodeIndex(foundResIdx, code => code.opcode == OpCodes.Ret);
                    var jobVar = t.codes[returnJobIdx - 1].operand;

                    t.TryInsertCodes(
                        0,
                        (i, codes) => i == foundResIdx,
                        (i, codes) => new List<CodeInstruction> {
                            // job = _HaulBeforeSupply(pawn, (Thing) c, foundRes);
                            new CodeInstruction(OpCodes.Ldarg_1),                  // Pawn pawn
                            new CodeInstruction(OpCodes.Ldarg_2),                  // IConstructible c
                            new CodeInstruction(OpCodes.Castclass, typeof(Thing)), // (Thing) c
                            codes[i + 1].Clone(),                                  // Thing foundRes
                            codes[i + 2].Clone(),                                  // Thing foundRes
                            new CodeInstruction(
                                OpCodes.Call, AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources_ResourceDeliverJobFor_Patch), nameof(HaulBeforeSupply))),
                            new CodeInstruction(OpCodes.Stloc_S, jobVar),

                            // if (job != null) return job;
                            new CodeInstruction(OpCodes.Ldloc_S,   jobVar),
                            new CodeInstruction(OpCodes.Brfalse_S, foundRes),
                            new CodeInstruction(OpCodes.Ldloc_S,   jobVar),
                            new CodeInstruction(OpCodes.Ret),
                        });

                    return t.GetFinalCodes();
                }
            }
        }
    }
}
