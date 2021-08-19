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

            // we can consolidate our code by keeping track of where we are like this
            enum TickContext { None, HaulToInventory_HasJobOnThing, HaulToInventory_JobOnThing, HaulToInventory_JobOnThing_AllocateThingAtCell }

            static void PushTickContext(out TickContext original, TickContext @new) {
                original = tickContext;
                tickContext = @new;
            }

            static void PopTickContext(TickContext state) => tickContext = state;

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
                    return false;
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
                    if (carrier == null || tickContext == TickContext.None || !settings.HaulToInventory || !settings.Enabled) return true;
                    var puah = specialHauls.GetValueSafe(carrier) as PuahWithBetterUnloading;
                    var opportunity = puah as PuahOpportunity;
                    var beforeCarry = puah as PuahBeforeCarry;

                    var skipCells = (HashSet<IntVec3>)AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);

                    if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        t,
                        beforeCarry?.destCell ?? IntVec3.Invalid,
                        carrier, map, currentPriority, faction, out foundCell,
                        tickContext != TickContext.HaulToInventory_HasJobOnThing && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal && (beforeCarry?.destCell.IsValid ?? false),
                        tickContext == TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell ? skipCells : null)) {
                        __result = false;
                        return false;
                    }

                    if (opportunity != null && !Opportunity.TrackPuahThingIfOpportune(opportunity, t, carrier, ref foundCell)) {
                        __result = false;
                        return false;
                    }

                    __result = true;

                    if (tickContext == TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell) {
                        if (puah == null) {
                            puah = new PuahWithBetterUnloading();
                            specialHauls.SetOrAdd(carrier, puah);
                        }
                        puah.TrackThing(t, foundCell);
                    }

                    return false;
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory__FirstUnloadableThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "FirstUnloadableThing");

                [HarmonyPrefix]
                static bool SpecialHaulAwareFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                    if (!settings.HaulToInventory || !settings.Enabled) return true;

                    var hauledToInventoryComp =
                        (ThingComp)AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahCompHauledToInventoryType).Invoke(pawn, null);
                    var carriedThings = Traverse.Create(hauledToInventoryComp).Method("GetHashSet").GetValue<HashSet<Thing>>();

                    IntVec3 GetStoreCell(PuahWithBetterUnloading puah_, Thing thing) {
                        if (puah_.defHauls.TryGetValue(thing.def, out var storeCell))
                            return storeCell;

                        // should only be necessary because specialHauls aren't saved in file like CompHauledToInventory
                        var beforeCarry = puah_ as PuahBeforeCarry;
                        if (TryFindBestBetterStoreCellFor_ClosestToDestCell(
                            thing, beforeCarry?.destCell ?? IntVec3.Invalid, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false))
                            puah_.defHauls.Add(thing.def, storeCell);
                        return storeCell; // IntVec3.Invalid is okay here
                    }

                    Thing firstThingToUnload;
                    if (specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)
                        firstThingToUnload = carriedThings.OrderBy(t => GetStoreCell(puah, t).DistanceTo(pawn.Position)).FirstOrDefault();
                    else
                        firstThingToUnload = carriedThings.FirstOrDefault();

                    if (firstThingToUnload == default) {
                        __result = default;
                        return false;
                    }

                    if (!carriedThings.Intersect(pawn.inventory.innerContainer).Contains(firstThingToUnload)) {
                        // can't be removed from dropping / delivering, so remove now
                        carriedThings.Remove(firstThingToUnload);

                        // because of merges
                        var thingFoundByDef = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == firstThingToUnload.def);
                        if (thingFoundByDef != default) {
                            __result = new ThingCount(thingFoundByDef, thingFoundByDef.stackCount);
                            return false;
                        }
                    }

                    __result = new ThingCount(firstThingToUnload, firstThingToUnload.stackCount);
                    return false;
                }
            }
        }
    }
}
