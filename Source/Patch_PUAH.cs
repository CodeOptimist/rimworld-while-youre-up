using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace JobsOfOpportunity
{
    partial class Mod
    {
        static partial class Patch_PUAH
        {
            static readonly List<MethodBase> callStack = new List<MethodBase>();

            static readonly Dictionary<Thing, IntVec3> cachedStoreCells          = new Dictionary<Thing, IntVec3>();
            static readonly MethodInfo                 hasJobOnThingMethod       = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "HasJobOnThing");
            static readonly MethodInfo                 jobOnThingMethod          = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");
            static readonly MethodInfo                 allocateThingAtCellMethod = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "AllocateThingAtCell");

            static readonly MethodInfo allocateThingFirstStoreMethod = AccessTools.DeclaredMethod(
                typeof(WorkGiver_HaulToInventory__AllocateThingAtCell_Patch), nameof(WorkGiver_HaulToInventory__AllocateThingAtCell_Patch.FirstTryFindStore));

            static readonly MethodInfo allocateThingSecondStoreMethod = AccessTools.DeclaredMethod(
                typeof(WorkGiver_HaulToInventory__AllocateThingAtCell_Patch), nameof(WorkGiver_HaulToInventory__AllocateThingAtCell_Patch.SecondTryFindStore));

            static void PushMethod(MethodBase method) => callStack.Add(method);

            static void PopMethod() {
                // shouldn't happen unless another mod skipped one of our Prefix PushMethods (breaking our mod)
                if (!callStack.Any()) return;

                callStack.Pop();
                if (!callStack.Any())
                    cachedStoreCells.Clear();
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory__HasJobOnThing_Patch
            {
                // because of PUAH's haulMoreWork toil
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => hasJobOnThingMethod;

                static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);
                static void Postfix()                           => PopMethod();
            }

            [HarmonyPatch]
            static partial class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => jobOnThingMethod;

                [HarmonyPriority(Priority.High)]
                static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);

                [HarmonyPriority(Priority.Low)]
                static void Postfix() => PopMethod();
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory__AllocateThingAtCell_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => allocateThingAtCellMethod;

                static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);
                static void Postfix()                           => PopMethod();

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> _AllocateThingAtCell(IEnumerable<CodeInstruction> _codes, MethodBase __originalMethod) {
                    var t = new Transpiler(_codes, __originalMethod);
                    var puahTryFindStore = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "TryFindBestBetterStoreCellFor");
                    var firstCallIdx = t.TryFindCodeIndex(code => code.Calls(puahTryFindStore));

                    t.TryInsertCodes(
                        0,
                        (i, codes) => i == firstCallIdx,
                        (i, codes) => new List<CodeInstruction> {
                            new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(WorkGiver_HaulToInventory__AllocateThingAtCell_Patch), nameof(FirstTryFindStore))),
                        }, true);
                    t.codes.RemoveAt(t.MatchIdx);

                    return t.GetFinalCodes().MethodReplacer(
                        puahTryFindStore, AccessTools.DeclaredMethod(typeof(WorkGiver_HaulToInventory__AllocateThingAtCell_Patch), nameof(SecondTryFindStore)));
                }

                internal static bool FirstTryFindStore(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell) {
                    PushMethod(MethodBase.GetCurrentMethod());
                    var result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell); // patched below
                    PopMethod();
                    return result;
                }

                internal static bool SecondTryFindStore(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell) {
                    PushMethod(MethodBase.GetCurrentMethod());
                    var result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell); // patched below
                    PopMethod();
                    return result;
                }
            }

            [HarmonyPriority(Priority.HigherThanNormal)]
            [HarmonyPatch]
            static class StoreUtility__TryFindBestBetterStoreCellFor_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor));

                [HarmonyPrefix]
                static bool SpecialHaulAwareTryFindStore(ref bool __result, Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
                    Faction faction, out IntVec3 foundCell, bool needAccurateResult) {
                    foundCell = IntVec3.Invalid;

                    if (carrier == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return Original();
                    var isUnloadJob = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
                    if (!callStack.Any() && !isUnloadJob) return Original();

                    // unload job happens over multiple ticks, and the second TryFindStore within AllocateThingAtCell is in a while loop
                    var canCache = !isUnloadJob && !callStack.Contains(allocateThingSecondStoreMethod);
                    if (canCache) {
                        if (cachedStoreCells.Count == 0)
                            cachedStoreCells.AddRange(Opportunity.cachedStoreCells); // inherit cache if available (will be same tick)
                        if (!cachedStoreCells.TryGetValue(t, out foundCell))
                            foundCell = IntVec3.Invalid;
                    }

                    var puah = specialHauls.GetValueSafe(carrier) as PuahWithBetterUnloading;
                    var opportunity = puah as PuahOpportunity;
                    var beforeCarry = puah as PuahBeforeCarry;

                    var skipCells = (HashSet<IntVec3>)AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);

                    var opportunityTarget = opportunity?.jobTarget ?? IntVec3.Invalid;
                    var beforeCarryTarget = beforeCarry?.carryTarget ?? IntVec3.Invalid;
                    if (foundCell == IntVec3.Invalid && !TryFindBestBetterStoreCellFor_ClosestToTarget(
                        t, opportunityTarget, beforeCarryTarget, carrier, map, currentPriority, faction, out foundCell,
                        // needAccurateResult may give us a shorter path, giving special hauls a better chance
                        !callStack.Contains(hasJobOnThingMethod) && (opportunityTarget.IsValid || beforeCarryTarget.IsValid) && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                        callStack.Contains(allocateThingAtCellMethod) ? skipCells : null))
                        return Skip(__result = false);

                    if (canCache)
                        cachedStoreCells.SetOrAdd(t, foundCell);

                    if (isUnloadJob)
                        return Skip(__result = true);

                    if (opportunity != null && !opportunity.TrackThingIfOpportune(t, carrier, ref foundCell))
                        return Skip(__result = false);

                    if (beforeCarry != null) {
                        var foundCellGroup = foundCell.GetSlotGroup(map);

                        // only grab extra things going to the same store
                        if (foundCellGroup != beforeCarry.storeCell.GetSlotGroup(map)) return Skip(__result = false);
                        // Debug.WriteLine($"{t} is destined for same storage {foundCellGroup} {foundCell}");

                        Debug.Assert(foundCellGroup.Settings.Priority > StoragePriority.Unstored);
                        if (foundCellGroup.Settings.Priority == t.Position.GetSlotGroup(map)?.Settings?.Priority) {
                            if (carrier.CurJobDef == JobDefOf.HaulToContainer && carrier.CurJob.targetC.Thing is Frame frame) {
                                if (!frame.cachedMaterialsNeeded.Select(x => x.thingDef).Contains(t.def))
                                    return Skip(__result = false);
                                Debug.WriteLine(
                                    $"APPROVED {t} {t.Position} as needed supplies for {beforeCarry.carryTarget}"
                                    + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                            }

                            if (carrier.CurJobDef == JobDefOf.DoBill) {
                                if (!carrier.CurJob.targetQueueB.Select(x => x.Thing?.def).Contains(t.def))
                                    return Skip(__result = false);
                                Debug.WriteLine(
                                    $"APPROVED {t} {t.Position} as ingredients for {beforeCarry.carryTarget}"
                                    + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                            }
                        }
                    }

                    if (callStack.Contains(allocateThingAtCellMethod)) {
                        if (puah == null) {
                            puah = new PuahWithBetterUnloading();
                            specialHauls.SetOrAdd(carrier, puah);
                        }
                        puah.TrackThing(t, foundCell);
                    }

                    return Skip(__result = true);
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory__FirstUnloadableThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "FirstUnloadableThing");

                [HarmonyPrefix]
                static bool SpecialHaulAwareFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                    if (!settings.UsePickUpAndHaulPlus || !settings.Enabled) return Original();

                    var hauledToInventoryComp =
                        (ThingComp)AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahCompHauledToInventoryType).Invoke(pawn, null);
                    var carriedThings = Traverse.Create(hauledToInventoryComp).Method("GetHashSet").GetValue<HashSet<Thing>>();
                    if (!carriedThings.Any()) return Skip(__result = default);

                    (Thing thing, IntVec3 storeCell) GetDefHaul(PuahWithBetterUnloading puah_, Thing thing) {
                        // It's completely possible storage has changed, that's fine. This is just a guess for order.
                        if (puah_.defHauls.TryGetValue(thing.def, out var storeCell))
                            return (thing, storeCell);

                        // should only be necessary after loading because specialHauls aren't saved in game file like CompHauledToInventory
                        if (TryFindBestBetterStoreCellFor_ClosestToTarget(
                            thing,
                            (puah_ as PuahOpportunity)?.jobTarget ?? IntVec3.Invalid,
                            (puah_ as PuahBeforeCarry)?.carryTarget ?? IntVec3.Invalid,
                            pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false)) {
                            // add for next
                            puah_.defHauls.Add(thing.def, storeCell);
                        }
                        return (thing, storeCell);
                    }

                    // just loaded game, or half-state from toggling settings, etc.
                    if (!(specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)) {
                        puah = new PuahWithBetterUnloading();
                        specialHauls.SetOrAdd(pawn, puah);
                    }

                    Thing firstThingToUnload = null;
                    try {
                        var closestHaul = carriedThings.Select(t => GetDefHaul(puah, t)).Where(x => x.storeCell.IsValid).MinBy(x => x.storeCell.DistanceTo(pawn.Position));
                        var closestSlotGroup = closestHaul.storeCell.GetSlotGroup(pawn.Map);
                        firstThingToUnload = closestSlotGroup == null
                            ? closestHaul.thing
                            : carriedThings.Select(t => GetDefHaul(puah, t)).Where(x => x.storeCell.GetSlotGroup(pawn.Map) == closestSlotGroup)
                                .MinBy(x => (x.thing.def.FirstThingCategory?.index, x.thing.def.defName)).thing;
                    } catch (InvalidOperationException) {
                        // MinBy() empty sequence exception if all cells are invalid; rare but possible
                    }

                    if (firstThingToUnload == null)
                        firstThingToUnload = carriedThings.MinBy(t => (t.def.FirstThingCategory?.index, t.def.defName));

                    if (!carriedThings.Intersect(pawn.inventory.innerContainer).Contains(firstThingToUnload)) {
                        // can't be removed by dropping / delivering, so remove now
                        carriedThings.Remove(firstThingToUnload);

                        // because of merges
                        var thingFoundByDef = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == firstThingToUnload.def);
                        if (thingFoundByDef != default)
                            return Skip(__result = new ThingCount(thingFoundByDef, thingFoundByDef.stackCount));
                    }

                    return Skip(__result = new ThingCount(firstThingToUnload, firstThingToUnload.stackCount));
                }
            }
        }
    }
}
