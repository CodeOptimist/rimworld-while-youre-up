using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            static TickContext tickContext = TickContext.None;

            static readonly Dictionary<Thing, IntVec3> cachedStoreCells = new Dictionary<Thing, IntVec3>();

            enum TickContext { None, HaulToInventory_HasJobOnThing, HaulToInventory_JobOnThing, HaulToInventory_JobOnThing_AllocateThingAtCell }

            static void PushTickContext(out TickContext original, TickContext @new) {
                original = tickContext;
                tickContext = @new;
            }

            static void PopTickContext(TickContext state) {
                tickContext = state;

                if (tickContext == TickContext.None)
                    cachedStoreCells.Clear();
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory__HasJobOnThing_Patch
            {
                // because of PUAH's haulMoreWork toil
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "HasJobOnThing");

                static void Prefix(out TickContext __state) => PushTickContext(out __state, TickContext.HaulToInventory_HasJobOnThing);
                static void Postfix(TickContext __state)    => PopTickContext(__state);
            }

            [HarmonyPatch]
            static partial class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");

                [HarmonyPriority(Priority.High)]
                static void Prefix(out TickContext __state) => PushTickContext(out __state, TickContext.HaulToInventory_JobOnThing);

                [HarmonyPriority(Priority.Low)]
                static void Postfix(TickContext __state) => PopTickContext(__state);
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory__TryFindBestBetterStoreCellFor_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "TryFindBestBetterStoreCellFor");

                [HarmonyPrefix]
                static bool UseSpecialHaulAwareTryFindStore(out bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
                    out IntVec3 foundCell) {
                    // have PUAH use vanilla's to keep our code in one place
                    PushTickContext(out var original, TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell);
                    __result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell); // patched below
                    PopTickContext(original);
                    return Skip();
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
                    Faction faction, ref IntVec3 foundCell, bool needAccurateResult) {
                    if (carrier == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return Original();
                    var isUnloadJob = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
                    if (tickContext == TickContext.None && !isUnloadJob) return Original();

                    var puah = specialHauls.GetValueSafe(carrier) as PuahWithBetterUnloading;
                    var opportunity = puah as PuahOpportunity;
                    var beforeCarry = puah as PuahBeforeCarry;

                    var skipCells = (HashSet<IntVec3>)AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);

                    if (cachedStoreCells.Count == 0)
                        cachedStoreCells.AddRange(Opportunity.cachedStoreCells); // inherit cache if available (will be same tick)

                    var opportunityTarget = opportunity?.jobTarget ?? IntVec3.Invalid;
                    var beforeCarryTarget = beforeCarry?.carryTarget ?? IntVec3.Invalid;
                    if (!cachedStoreCells.TryGetValue(t, out foundCell) && !TryFindBestBetterStoreCellFor_ClosestToTarget(
                        t, opportunityTarget, beforeCarryTarget, carrier, map, currentPriority, faction, out foundCell,
                        // needAccurateResult may give us a shorter path, giving special hauls a better chance
                        tickContext != TickContext.HaulToInventory_HasJobOnThing && (opportunityTarget.IsValid || beforeCarryTarget.IsValid)
                                                                                 && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                        tickContext == TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell ? skipCells : null))
                        return Skip(__result = false);

                    if (isUnloadJob) return Skip(__result = true);

                    // don't use cache with unload, since it's over multiple ticks
                    cachedStoreCells.SetOrAdd(t, foundCell);

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

                    if (tickContext == TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell) {
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

                    IntVec3 GetStoreCell(PuahWithBetterUnloading puah_, Thing thing) {
                        if (puah_.defHauls.TryGetValue(thing.def, out var storeCell))
                            return storeCell;

                        // should only be necessary because specialHauls aren't saved in file like CompHauledToInventory
                        if (TryFindBestBetterStoreCellFor_ClosestToTarget(
                            thing,
                            (puah_ as PuahOpportunity)?.jobTarget ?? IntVec3.Invalid,
                            (puah_ as PuahBeforeCarry)?.carryTarget ?? IntVec3.Invalid,
                            pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false))
                            puah_.defHauls.Add(thing.def, storeCell);
                        return storeCell; // IntVec3.Invalid is okay here
                    }

                    Thing firstThingToUnload;
                    if (specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)
                        firstThingToUnload = carriedThings.OrderBy(t => GetStoreCell(puah, t).DistanceTo(pawn.Position)).FirstOrDefault();
                    else
                        firstThingToUnload = carriedThings.FirstOrDefault();

                    if (firstThingToUnload == default)
                        return Skip(__result = default);

                    if (!carriedThings.Intersect(pawn.inventory.innerContainer).Contains(firstThingToUnload)) {
                        // can't be removed from dropping / delivering, so remove now
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
