using System.Collections.Generic;
using System.Reflection;
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
        static class Patch_PUAH
        {
            static bool inPuah;

            [HarmonyPriority(Priority.HigherThanNormal)]
            [HarmonyPatch]
            static class StoreUtility_TryFindBestBetterStoreCellFor_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor));

                [HarmonyPrefix]
                static bool UseJooTryFindBestBetterStoreCellFor_ClosestToDestCell(ref bool __result, Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
                    Faction faction, ref IntVec3 foundCell, bool needAccurateResult) {
                    if (!inPuah) return true;
                    if (!settings.HaulToInventory || !settings.Enabled) return true;

                    if (carrier == null) return true;
                    var haulTracker = haulTrackers.GetValueSafe(carrier);

                    __result = JooStoreUtility.TryFindBestBetterStoreCellFor_ClosestToDestCell(
                        t, haulTracker?.destCell ?? IntVec3.Invalid, carrier, map, currentPriority, faction, out foundCell, haulTracker?.destCell.IsValid ?? false);
                    return false;
                }
            }

            [HarmonyPatch]
            static class JobDriver_GetReport_Patch
            {
                static bool Prepare() => havePuah;

                // always use DeclaredMethod (explicit)
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

                [HarmonyPostfix]
                static void CustomPuahJobReport(JobDriver __instance, ref string __result) {
                    if (!settings.HaulToInventory || !settings.Enabled) return;
                    if (PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance)) {
                        if (!haulTrackers.TryGetValue(__instance.pawn, out var haulTracker)) return;
                        __result = haulTracker.GetJobReportPrefix() + __result;
                    } else if (PuahJobDriver_UnloadYourHauledInventoryType.IsInstanceOfType(__instance))
                        __result = "Efficiently " + __result;
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "TryFindBestBetterStoreCellFor");

                [HarmonyPrefix]
                static bool UseJooPuahAllocateThingAtCell_TryFindBestBetterStoreCellFor(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority,
                    Faction faction,
                    ref IntVec3 foundCell) {
                    if (!settings.HaulToInventory || !settings.Enabled) return true;
                    __result = JooStoreUtility.PuahAllocateThingAtCell_TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell);
                    return false;
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_HasJobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "HasJobOnThing");

                // we need to patch PUAH's use of vanilla TryFindBestBetterStoreCellFor within HasJobOnThing for the haulMoreWork toil
                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> UseJooPuahHasJobOnThing_HasStore(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.DeclaredMethod(typeof(StoreUtility),    nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.DeclaredMethod(typeof(JooStoreUtility), nameof(JooStoreUtility.PuahHasJobOnThing_HasStore)));
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_JobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");

                [HarmonyPrefix]
                static void TempReduceStoragePriorityForHaulBeforeCarry(WorkGiver_Scanner __instance, ref bool __state, Pawn pawn, Thing thing) {
                    inPuah = true;
                    if (!settings.HaulToInventory || !settings.Enabled) return;
                    if (!settings.HaulToEqualPriority) return;

                    if (!haulTrackers.TryGetValue(pawn, out var haulTracker) || haulTracker.haulType != SpecialHaulType.HaulBeforeCarry) return;

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

                    var haulTracker = haulTrackers.GetValueSafe(pawn) ?? HaulTracker.CreateAndAdd(SpecialHaulType.None, pawn, IntVec3.Invalid);
                    // thing from parameter because targetA is null because things are in queues instead
                    //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                    // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if it's an Opportunity or HaulBeforeCarry
                    haulTracker.Add(thing, __result.targetB.Cell, isInitial: true);
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory_FirstUnloadableThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "FirstUnloadableThing");

                [HarmonyPrefix]
                static bool UseJooPuahFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                    if (!settings.HaulToInventory || !settings.Enabled) return true;
                    __result = JooStoreUtility.PuahFirstUnloadableThing(pawn);
                    return false;
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory_MakeNewToils_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "MakeNewToils");

                [HarmonyPostfix]
                static void ClearTrackingAfterUnload(JobDriver __instance) {
                    Debug.WriteLine($"{RealTime.frameCount} {__instance.pawn} STARTED UNLOAD.");

                    __instance.AddFinishAction(
                        () => {
                            haulTrackers.Remove(__instance.pawn);
                            Debug.WriteLine($"{RealTime.frameCount} {__instance.pawn} FINISHED UNLOAD. Wiped tracking.");
                        });
                }
            }

#if DEBUG
            [HarmonyPatch]
            static class CompHauledToInventory_RegisterHauledItem_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahCompHauledToInventoryType, "RegisterHauledItem");

                [HarmonyPostfix]
                static void TrackHauledItem(ThingComp __instance, Thing thing) {
                    var pawn = (Pawn)__instance.parent;
                    Debug.WriteLine($"{RealTime.frameCount} {pawn} GRABBED {thing}");
                }
            }
#endif
        }
    }
}
