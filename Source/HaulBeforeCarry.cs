﻿using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
        [SuppressMessage("ReSharper", "UnusedType.Local")]
        [SuppressMessage("ReSharper", "UnusedMember.Local")]
        static class WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch
        {
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
                        // job = HaulBeforeSupply(pawn, (Thing) c, foundRes);
                        new CodeInstruction(OpCodes.Ldarg_1),                  // Pawn pawn
                        new CodeInstruction(OpCodes.Ldarg_2),                  // IConstructible c
                        new CodeInstruction(OpCodes.Castclass, typeof(Thing)), // (Thing) c
                        codes[i + 1].Clone(),                                  // Thing foundRes
                        codes[i + 2].Clone(),                                  // Thing foundRes
                        new CodeInstruction(
                            OpCodes.Call, AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch), nameof(HaulBeforeSupply))),
                        new CodeInstruction(OpCodes.Stloc_S, jobVar),

                        // if (job != null) return job;
                        new CodeInstruction(OpCodes.Ldloc_S,   jobVar),
                        new CodeInstruction(OpCodes.Brfalse_S, foundRes),
                        new CodeInstruction(OpCodes.Ldloc_S,   jobVar),
                        new CodeInstruction(OpCodes.Ret),
                    });

                return t.GetFinalCodes();
            }

            static Job HaulBeforeSupply(Pawn pawn, Thing constructible, Thing th) {
                if (!settings.HaulBeforeSupply || !settings.Enabled || AlreadyHauling(pawn)) return null;
                return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(pawn, HaulBeforeCarry(pawn, constructible.Position, th));
            }
        }

        public static Job HaulBeforeCarry(Pawn pawn, IntVec3 destCell, Thing thing) {
            // "Haul before supply" enters here, "Haul before bill" enters from TryOpportunisticJob

            if (thing.ParentHolder is Pawn_InventoryTracker) return null;
            // try to avoid haul-before-carry when there are no extras to grab
            // proper way is to re-check after grabbing everything, but here's a quick hack to at least avoid it with stone chunks
            if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 2)) return null; // already going for 1, so 2 to check for another
            if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                thing, destCell, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var storeCell, true)) return null;

            var fromHereDist = thing.Position.DistanceTo(destCell);
            var fromStoreDist = storeCell.DistanceTo(destCell);
            Debug.WriteLine($"Carry from here: {fromHereDist}; carry from store: {fromStoreDist}");

            if (fromStoreDist < fromHereDist) {
                Debug.WriteLine(
                    $"'{pawn}' prefixed job with haul for '{thing.Label}'"
                    + $" because '{storeCell.GetSlotGroup(pawn.Map)}' is closer to original destination '{destCell}'.");

                if (DebugViewSettings.drawOpportunisticJobs) {
                    // ReSharper disable once RedundantArgumentDefaultValue
                    pawn.Map.debugDrawer.FlashLine(pawn.Position,  thing.Position, 600, SimpleColor.White);
                    pawn.Map.debugDrawer.FlashLine(thing.Position, destCell,       600, SimpleColor.Magenta);
                    pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,      600, SimpleColor.Cyan);
                    pawn.Map.debugDrawer.FlashLine(storeCell,      destCell,       600, SimpleColor.Cyan);
                }

                var puahJob = PuahJob(new PuahBeforeCarry(destCell), pawn, thing, storeCell);
                if (puahJob != null) return puahJob;

                specialHauls.SetOrAdd(pawn, new SpecialHaul("Optimally "));
                return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
            }

            return null;
        }
    }
}
