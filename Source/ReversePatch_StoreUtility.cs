using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        [HarmonyPatch]
        public static class ReversePatch
        {
            [HarmonyReversePatch] // TODO breaks with Snapshot; need newer Harmony?
            [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
            public static bool ClosestStoreCellToDest(Thing th, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult, IntVec3 dest,
                bool allowEqualPriority) {
                IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
                    var t = new Transpiler(_codes, __originalMethod);

                    var getPriority_Idx = t.TryFindCodeIndex(code => code.Calls(AccessTools.PropertyGetter(typeof(StorageSettings), nameof(StorageSettings.Priority))));
                    var branchLessEqual_Idx = t.TryFindCodeIndex(getPriority_Idx, code => code.opcode == OpCodes.Ble_S);
                    t.codes[branchLessEqual_Idx] = t.codes[branchLessEqual_Idx].Clone(OpCodes.Blt_S);
                    // if (priority < storagePriority || priority   currentPriority)
                    //                                            <
                    //     break;

                    var noBreak_Label = generator.DefineLabel();
                    t.TryInsertCodes(
                        1,
                        (i, codes) => i == branchLessEqual_Idx,
                        (i, codes) => new List<CodeInstruction> {
                            codes[i - 2].Clone(),                                   // if (priority
                            codes[i - 1].Clone(),                                   //                 currentPriority)
                            new CodeInstruction(OpCodes.Bne_Un_S, noBreak_Label),   //              !=
                                                                                    //     goto NoBreak;
                            new CodeInstruction(OpCodes.Ldarg_S, 8),                // if (allowEqualPriority)
                            new CodeInstruction(OpCodes.Brtrue_S, noBreak_Label),   //     goto NoBreak;
                            codes[i].Clone(OpCodes.Br_S),                           // break;
                        });                                                         //
                    t.codes[t.InsertIdx].labels.Add(noBreak_Label);                 // NoBreak:

                    t.TryInsertCodes(
                        0,
                        (i, codes) => codes[i].Calls(AccessTools.Method(typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker")),
                        (i, codes) => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Ldarg_S, 7),    // pass dest to Worker
                        });
                    t.codes[t.MatchIdx] = new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ReversePatch), nameof(ClosestStoreCellToDestWorker)));

                    return t.GetFinalCodes();
                }

                // make compiler happy
                var _ = Transpiler(default, default, default);
                foundCell = default;
                return default;
            }

            [HarmonyReversePatch] // TODO breaks with Snapshot; need newer Harmony?
            [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker")]
            public static void ClosestStoreCellToDestWorker(Thing th, Pawn carrier, Map map, Faction faction, SlotGroup slotGroup, bool needAccurateResult, ref IntVec3 closestSlot,
                ref float closestDistSquared, ref StoragePriority foundPriority, IntVec3 dest) {
                IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
                    var t = new Transpiler(_codes, __originalMethod);

                    var noReturn_Label = generator.DefineLabel();
                    t.TryInsertCodes(
                        3,
                        (i, codes) => codes[i].Is(OpCodes.Ldarg_S, 4),
                        // if (slotGroup == null)
                        //     break;
                        (i, codes) => {
                            var getThingSlotGroupMethod = AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.GetSlotGroup), new[] {typeof(Thing)});
                            return new List<CodeInstruction> {
                                new CodeInstruction(OpCodes.Ldarg_0),                           // if (th
                                new CodeInstruction(OpCodes.Call, getThingSlotGroupMethod),     //       .GetSlotGroup()
                                new CodeInstruction(OpCodes.Ldarg_S, 4),                        //                          slotGroup)
                                new CodeInstruction(OpCodes.Bne_Un_S, noReturn_Label),          //                       !=
                                                                                                //     goto NoReturn;
                                new CodeInstruction(OpCodes.Ret),                               // return;
                            };                                                                  //
                        }, true);                                                               //
                    t.codes[t.InsertIdx].labels.Add(noReturn_Label);                            // NoReturn:

                    // position of Thing
                    var sequence = new List<CodeInstruction> {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Thing), nameof(Thing.SpawnedOrAnyParentSpawned))),
                        new CodeInstruction(OpCodes.Brtrue_S),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Thing), nameof(Thing.PositionHeld))),
                        new CodeInstruction(OpCodes.Br_S),
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredPropertyGetter(typeof(Thing), nameof(Thing.PositionHeld))),
                        new CodeInstruction(OpCodes.Stloc_0),
                    };

                    t.TryInsertCodes(
                        0,
                        (i, codes) => t.TrySequenceEqual(i, sequence),
                        (i, codes) => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Ldarg_S, 9),    // position of dest...
                            new CodeInstruction(OpCodes.Stloc_0),       //
                        }, true);                                       //
                    t.codes.RemoveRange(t.MatchIdx, sequence.Count);    //                 ...instead of Thing

                    return t.GetFinalCodes();
                }

                // make compiler happy
                var _ = Transpiler(default, default, default);
            }
        }
    }
}
