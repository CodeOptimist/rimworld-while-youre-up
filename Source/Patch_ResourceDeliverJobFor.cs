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
        static class Patch_ResourceDeliverJobFor
        {
            [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
            static class WorkGiver_ConstructDeliverResources_ResourceDeliverJobFor_Patch
            {
                static List<CodeInstruction> codes, newCodes;
                static int i;

                static void InsertCode(int offset, Func<bool> when, Func<List<CodeInstruction>> what, bool bringLabels = false) {
                    JobsOfOpportunity.InsertCode(ref i, ref codes, ref newCodes, offset, when, what, bringLabels);
                }

                static Job _HaulBeforeSupply(Pawn pawn, Thing constructible, Thing th) {
                    if (!haulBeforeSupply.Value) return null;
                    if (th.IsInValidStorage()) return null;
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(th, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out var storeCell, false)) return null;

                    var supplyFromHereDist = th.Position.DistanceTo(constructible.Position);
                    var supplyFromStoreDist = storeCell.DistanceTo(constructible.Position);
                    Debug.WriteLine($"Supply from here: {supplyFromHereDist}; supply from store: {supplyFromStoreDist}");

                    if (supplyFromStoreDist < supplyFromHereDist) {
                        Debug.WriteLine($"'{pawn}' replaced supply job with haul job for '{th.Label}' because '{storeCell.GetSlotGroup(pawn.Map)}' is closer to '{constructible}'.");
                        return Hauling.PuahJob(pawn, constructible.Position, th, storeCell) ?? HaulAIUtility.HaulToCellStorageJob(pawn, th, storeCell, false);
                    }

                    return null;
                }

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> HaulBeforeSupply(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
                    codes = instructions.ToList();
                    newCodes = new List<CodeInstruction>();
                    i = 0;

                    // locate patch
                    var nearbyResourcesIdx = codes.FindIndex(code => code.Calls(AccessTools.Method(typeof(WorkGiver_ConstructDeliverResources), "FindAvailableNearbyResources")));
                    var foundResIdx = nearbyResourcesIdx == -1 ? -1 : codes.FindLastIndex(nearbyResourcesIdx, code => code.opcode == OpCodes.Brfalse);

                    // just to reuse the same local variable IL that's returned here
                    var returnJobIdx = foundResIdx == -1 ? -1 : codes.FindIndex(foundResIdx, code => code.opcode == OpCodes.Ret);

                    var resourceFoundLabel = generator.DefineLabel();
                    InsertCode(
                        1,
                        () => i == foundResIdx,
                        () => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Ldarg_1), // Pawn
                            new CodeInstruction(OpCodes.Ldarg_2), // IConstructible
                            new CodeInstruction(OpCodes.Castclass, typeof(Thing)),

                            // Thing (foundRes)
                            new CodeInstruction(codes[i - 2]),
                            new CodeInstruction(codes[i - 1]),

                            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WorkGiver_ConstructDeliverResources_ResourceDeliverJobFor_Patch), nameof(_HaulBeforeSupply))),
                            // return only if non-null
                            new CodeInstruction(OpCodes.Stloc_S, codes[returnJobIdx - 1].operand),
                            new CodeInstruction(codes[returnJobIdx - 1]),
                            new CodeInstruction(OpCodes.Brfalse_S, resourceFoundLabel),
                            new CodeInstruction(codes[returnJobIdx - 1]),
                            new CodeInstruction(OpCodes.Ret),
                        }, true);

                    // where we jump if our call returned null
                    if (foundResIdx != -1)
                        codes[foundResIdx + 1].labels.Add(resourceFoundLabel);

                    for (; i < codes.Count; i++)
                        newCodes.Add(codes[i]);
                    return newCodes.AsEnumerable();
                }
            }
        }
    }
}
