using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

                var afterNearbyIdx =
                    t.TryFindCodeIndex(code => code.Calls(AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources), "FindAvailableNearbyResources")));
                afterNearbyIdx += 1;
                var foundResIdx = t.TryFindCodeLastIndex(afterNearbyIdx, code => code.opcode == OpCodes.Brfalse) + 1;
                var afterNearby = generator.DefineLabel();
                t.codes[afterNearbyIdx].labels.Add(afterNearby);

                var needField = AccessTools.FindIncludingInnerTypes(typeof(WorkGiver_ConstructDeliverResources), ty => AccessTools.DeclaredField(ty, "need"));
                var needObjIdx = t.TryFindCodeIndex(code => code.Is(OpCodes.Newobj, AccessTools.DeclaredConstructor(needField.DeclaringType)));

                var returnJobIdx = t.TryFindCodeIndex(afterNearbyIdx, code => code.opcode == OpCodes.Ret);
                var jobVar = t.codes[returnJobIdx - 1].operand;

                t.TryInsertCodes(
                    0,
                    (i, codes) => i == afterNearbyIdx,
                    (i, codes) => new List<CodeInstruction> {
                        // job = HaulBeforeSupply(pawn, (Thing) c, foundRes);
                        new CodeInstruction(OpCodes.Ldarg_1),                                // Pawn pawn
                        new CodeInstruction(OpCodes.Ldloc_S, codes[needObjIdx + 1].operand), // ThingDefCountClass <>c__DisplayClass9_1
                        new CodeInstruction(OpCodes.Ldfld,   needField),                     //                                        .need
                        new CodeInstruction(OpCodes.Ldarg_2),                                // IConstructible c
                        new CodeInstruction(OpCodes.Castclass, typeof(Thing)),               // (Thing) c
                        codes[foundResIdx + 1].Clone(),                                      // Thing foundRes
                        codes[foundResIdx + 2].Clone(),                                      // Thing foundRes
                        new CodeInstruction(
                            OpCodes.Call, AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch), nameof(HaulBeforeSupply))),
                        new CodeInstruction(OpCodes.Stloc_S, jobVar),

                        // if (job != null) return job;
                        new CodeInstruction(OpCodes.Ldloc_S,   jobVar),
                        new CodeInstruction(OpCodes.Brfalse_S, afterNearby),
                        new CodeInstruction(OpCodes.Ldloc_S,   jobVar),
                        new CodeInstruction(OpCodes.Ret),
                    });

                return t.GetFinalCodes();
            }

            static Job HaulBeforeSupply(Pawn pawn, ThingDefCountClass need, Thing constructible, Thing th) {
                if (!settings.HaulBeforeCarry_Supplies || !settings.Enabled || AlreadyHauling(pawn)) return null;
                if (pawn.WorkTagIsDisabled(WorkTags.ManualDumb | WorkTags.Hauling | WorkTags.AllWork)) return null; // like TryOpportunisticJob()

                var mostThing = WorkGiver_ConstructDeliverResources.resourcesAvailable.DefaultIfEmpty().MaxBy(x => x.stackCount);
                // too difficult to know in advance if there are no extras for PUAH
                if (!havePuah || !settings.UsePickUpAndHaulPlus)
                    if (mostThing.stackCount <= need.count)
                        return null; // there are no extras
                return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(pawn, HaulBeforeCarry(pawn, constructible.Position, mostThing ?? th));
            }
        }

        public class PuahBeforeCarry : PuahWithBetterUnloading
        {
            public LocalTargetInfo carryTarget;
            public IntVec3         storeCell;

            public PuahBeforeCarry(LocalTargetInfo carryTarget, IntVec3 storeCell) {
                this.carryTarget = carryTarget;
                this.storeCell = storeCell;
            }

            public override string GetLoadReport(string text)   => "HaulBeforeCarry_LoadReport".ModTranslate(text.Named("ORIGINAL"), carryTarget.Label.Named("DESTINATION"));
            public override string GetUnloadReport(string text) => "HaulBeforeCarry_UnloadReport".ModTranslate(text.Named("ORIGINAL"), carryTarget.Label.Named("DESTINATION"));
        }

        public static Job HaulBeforeCarry(Pawn pawn, LocalTargetInfo carryTarget, Thing thing) {
            // "Haul before supply" enters here, "Haul before bill" enters from TryOpportunisticJob

            if (thing.ParentHolder is Pawn_InventoryTracker) return null;
            // try to avoid haul-before-carry when there are no extras to grab
            // proper way is to re-check after grabbing everything, but here's a quick hack to at least avoid it with stone chunks
            if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 2)) return null; // already going for 1, so 2 to check for another

            if (!TryFindBestBetterStoreCellFor_ClosestToTarget(
                thing, IntVec3.Invalid, carryTarget, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var storeCell, true)) return null;

            var fromHereDist = thing.Position.DistanceTo(carryTarget.Cell);
            var fromStoreDist = storeCell.DistanceTo(carryTarget.Cell);
            Debug.WriteLine($"Carry from here: {fromHereDist}; carry from store: {fromStoreDist}");

            if (fromStoreDist < fromHereDist) {
                Debug.WriteLine(
                    $"'{pawn}' prefixed job with haul for '{thing.Label}'"
                    + $" because '{storeCell.GetSlotGroup(pawn.Map)}' is closer to original destination '{carryTarget} {carryTarget.Cell}'.");

                if (DebugViewSettings.drawOpportunisticJobs) {
                    // ReSharper disable once RedundantArgumentDefaultValue
                    pawn.Map.debugDrawer.FlashLine(pawn.Position,  thing.Position,   600, SimpleColor.White);
                    pawn.Map.debugDrawer.FlashLine(thing.Position, carryTarget.Cell, 600, SimpleColor.Magenta);
                    pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,        600, SimpleColor.Cyan);
                    pawn.Map.debugDrawer.FlashLine(storeCell,      carryTarget.Cell, 600, SimpleColor.Cyan);
#if DEBUG
                    // if (!Find.Selector.SelectedPawns.Any())
                    //     CameraJumper.TrySelect(pawn);
#endif
                }

                var puahJob = PuahJob(new PuahBeforeCarry(carryTarget, storeCell), pawn, thing, storeCell);
                if (puahJob != null) return puahJob;

                specialHauls.SetOrAdd(pawn, new SpecialHaul("HaulBeforeCarry_LoadReport", carryTarget));
                return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
            }

            return null;
        }
    }
}
