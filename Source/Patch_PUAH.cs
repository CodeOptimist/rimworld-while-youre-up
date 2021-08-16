using System.Collections.Generic;
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
            static bool inPuah;

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory__HasJobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "HasJobOnThing");

                // we need to patch PUAH's use of vanilla TryFindBestBetterStoreCellFor within HasJobOnThing for the haulMoreWork toil
                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> UseJooPuahHasJobOnThing_HasStore(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.DeclaredMethod(typeof(Patch_PUAH),   nameof(PuahHasJobOnThing_HasStore)));
                }
            }

            public static bool PuahHasJobOnThing_HasStore(Thing thing, Pawn pawn, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell,
                bool needAccurateResult) {
                if (!settings.HaulToInventory || !settings.Enabled)
                    return StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, map, currentPriority, faction, out foundCell, needAccurateResult);

                var specialHaul = specialHauls.GetValueSafe(pawn);
                // use our version for the haul to equal priority setting
                if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                    thing, specialHaul?.destCell ?? IntVec3.Invalid, pawn, map, currentPriority, faction, out foundCell, false)) return false;
                return specialHaul == null || specialHaul.haulType != SpecialHaulType.Opportunity || Opportunity.TrackPuahThingIfOpportune(specialHaul, thing, pawn, ref foundCell);
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");

                [HarmonyPrefix]
                static void TempReduceStoragePriorityForHaulBeforeCarry(WorkGiver_Scanner __instance, ref bool __state, Pawn pawn, Thing thing) {
                    inPuah = true;
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
                    inPuah = false;
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

                // PUAH uses its own TryFindBestBetterStoreCellFor using skipCells and that doesn't care about distance (gets first valid)
                // we replace it with one that cares about distance to destination cell, else distance to pawn (like vanilla)
                [HarmonyPrefix]
                static bool UseSpecialHaulAwareTryFindStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
                    ref IntVec3 foundCell) {
                    if (carrier == null || !settings.HaulToInventory || !settings.Enabled) return true;
                    var skipCells = (HashSet<IntVec3>)AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells").GetValue(null);
                    var specialHaul = specialHauls.GetValueSafe(carrier) ?? SpecialHaulInfo.CreateAndAdd(SpecialHaulType.None, carrier, IntVec3.Invalid);
                    if (!TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        thing, specialHaul.destCell, carrier, map, currentPriority, faction, out foundCell, specialHaul.destCell.IsValid, skipCells))
                        __result = false;
                    else {
                        skipCells.Add(foundCell);
                        if (specialHaul.haulType == SpecialHaulType.Opportunity)
                            __result = Opportunity.TrackPuahThingIfOpportune(specialHaul, thing, carrier, ref foundCell);
                        else {
                            specialHaul.Add(thing, foundCell);
                            __result = true;
                        }
                    }
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
                    if (!inPuah) return true;
                    if (!settings.HaulToInventory || !settings.Enabled) return true;

                    if (carrier == null) return true;
                    var specialHaul = specialHauls.GetValueSafe(carrier);

                    __result = TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        t, specialHaul?.destCell ?? IntVec3.Invalid, carrier, map, currentPriority, faction, out foundCell, specialHaul?.destCell.IsValid ?? false);
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
