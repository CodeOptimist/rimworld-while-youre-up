﻿using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
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
            static class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");

                [HarmonyPriority(Priority.High)]
                static void Prefix(out TickContext __state) => PushTickContext(out __state, TickContext.HaulToInventory_JobOnThing);

                [HarmonyPriority(Priority.Low)]
                static void Postfix(TickContext __state) => PopTickContext(__state);

                [HarmonyPrefix]
                static void TempReduceStoragePriorityForHaulBeforeCarry(WorkGiver_Scanner __instance, ref bool __state, Pawn pawn, Thing thing) {
                    if (!settings.HaulToInventory || !settings.Enabled) return;
                    if (!settings.HaulToEqualPriority) return;

                    if (!specialHauls.TryGetValue(pawn, out var specialHaul) || specialHaul.haulType != SpecialHaulType.HaulBeforeCarry) return;

                    var currentHaulDestination = StoreUtility.CurrentHaulDestinationOf(thing);
                    if (currentHaulDestination == null) return;

                    var storeSettings = currentHaulDestination.GetStoreSettings();
                    if (storeSettings.Priority > StoragePriority.Unstored) {
                        storeSettings.Priority -= 1;
                        __state = true;
                    }
                }

                [HarmonyPostfix]
                static void TrackInitialHaul(WorkGiver_Scanner __instance, bool __state, Job __result, Pawn pawn, Thing thing) {
                    // restore storage priority
                    if (__state)
                        StoreUtility.CurrentHaulDestinationOf(thing).GetStoreSettings().Priority += 1;

                    if (__result == null) return;
                    if (!settings.HaulToInventory || !settings.Enabled) return;

                    var specialHaul = specialHauls.GetValueSafe(pawn) ?? SpecialHaulInfo.CreateAndAdd(SpecialHaulType.None, pawn, IntVec3.Invalid);
                    // thing from parameter because targetA is null because things are in queues instead
                    //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                    // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if it's an Opportunity or HaulBeforeCarry
                    specialHaul.Add(thing, __result.targetB.Cell, isInitial: true);
                }
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
                    var specialHaul = specialHauls.GetValueSafe(carrier);
                    var skipCells = (HashSet<IntVec3>)AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);

                    if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        t,
                        specialHaul?.destCell ?? IntVec3.Invalid,
                        carrier, map, currentPriority, faction, out foundCell,
                        tickContext != TickContext.HaulToInventory_HasJobOnThing && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal && (specialHaul?.destCell.IsValid ?? false),
                        tickContext == TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell ? skipCells : null)) {
                        __result = false;
                        return false;
                    }

                    if (specialHaul?.haulType == SpecialHaulType.Opportunity && !Opportunity.TrackPuahThingIfOpportune(specialHaul, t, carrier, ref foundCell)) {
                        __result = false;
                        return false;
                    }

                    __result = true;

                    if (tickContext == TickContext.HaulToInventory_JobOnThing_AllocateThingAtCell) {
                        if (specialHaul == null)
                            SpecialHaulInfo.CreateAndAdd(SpecialHaulType.None, carrier, foundCell);
                        else
                            specialHaul.Add(t, foundCell);
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
                static bool UseJooPuahFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                    if (!settings.HaulToInventory || !settings.Enabled) return true;
                    __result = PuahFirstUnloadableThing(pawn);
                    return false;
                }
            }

            static ThingCount PuahFirstUnloadableThing(Pawn pawn) {
                var hauledToInventoryComp =
                    (ThingComp)AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahCompHauledToInventoryType).Invoke(pawn, null);
                var carriedThings = Traverse.Create(hauledToInventoryComp).Method("GetHashSet").GetValue<HashSet<Thing>>();

                // should only be necessary because specialHauls aren't currently saved in file like CompHauledToInventory
                IntVec3 GetStoreCell(SpecialHaulInfo haulTracker_, Thing thing) {
                    if (haulTracker_.defHauls.TryGetValue(thing.def, out var storeCell))
                        return storeCell;
                    if (TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        thing, haulTracker_.destCell, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false))
                        haulTracker_.defHauls.Add(thing.def, storeCell);
                    return storeCell; // IntVec3.Invalid is okay here
                }

                var firstThingToUnload = carriedThings.FirstOrDefault();
                if (specialHauls.TryGetValue(pawn, out var specialHaul))
                    firstThingToUnload = carriedThings.OrderBy(t => GetStoreCell(specialHaul, t).DistanceTo(pawn.Position)).FirstOrDefault();
                if (firstThingToUnload == default) return default;

                var thingsFound = pawn.inventory.innerContainer.Where(t => carriedThings.Contains(t));
                if (!thingsFound.Contains(firstThingToUnload)) {
                    // can't be removed from dropping / delivering, so remove now
                    carriedThings.Remove(firstThingToUnload);

                    // because of merges
                    var thingFoundByDef = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == firstThingToUnload.def);
                    if (thingFoundByDef != default)
                        return new ThingCount(thingFoundByDef, thingFoundByDef.stackCount);
                }

                return new ThingCount(firstThingToUnload, firstThingToUnload.stackCount);
            }
        }
    }
}
