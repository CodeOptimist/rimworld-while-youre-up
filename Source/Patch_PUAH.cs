using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_PUAH
        {
            [HarmonyPatch]
            static class JobDriver_GetReport_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.Method(PuahJobDriver_HaulToInventoryType, "GetReport");

                [HarmonyPostfix]
                static void GetPuahOpportunityJobString(JobDriver __instance, ref string __result) {
                    if (!PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance)) return;
                    if (!haulTrackers.TryGetValue(__instance.pawn, out var haulTracker)) return;
                    if (haulTracker.jobCell.IsValid)
                        __result = $"Opportunistically {__result}";
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "TryFindBestBetterStoreCellFor");

                [HarmonyPrefix]
                static bool UsePuahAllocateThingAtCell_GetStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
                    out IntVec3 foundCell) {
                    __result = JooStoreUtility.PuahAllocateThingAtCell_GetStore(thing, carrier, map, currentPriority, faction, out foundCell);
                    return false;
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_HasJobOnThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "HasJobOnThing");

                [HarmonyPrefix]
                static void ClearPawnHaulTracker(Pawn pawn) {
                    // keep for the haulMoreWork toil that extends our path, otherwise clear it for fresh distance calculations
                    if (pawn.CurJobDef?.defName != "HaulToInventory") haulTrackers.Remove(pawn);
                }

                // we need to patch PUAH's use of vanilla TryFindBestBetterStoreCellFor within HasJobOnThing for the haulMoreWork toil
                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> UsePuahHasJobOnThing_HasStore(IEnumerable<CodeInstruction> instructions) {
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

                static bool UseTryFindBestBetterStoreCellFor_ClosestToDestCell(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
                    out IntVec3 foundCell,
                    bool needAccurateResult) {
                    // why not take advantage of our cache here as well
                    return Hauling.cachedOpportunityStoreCell.TryGetValue(t, out foundCell) || JooStoreUtility.TryFindBestBetterStoreCellFor_ClosestToDestCell(
                               t, IntVec3.Invalid, carrier, map, currentPriority, faction, out foundCell, false);
                }

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> _UseTryFindBestBetterStoreCellFor_ClosestToDestCell(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.DeclaredMethod(typeof(StoreUtility),                               nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.DeclaredMethod(typeof(WorkGiver_HaulToInventory_JobOnThing_Patch), nameof(UseTryFindBestBetterStoreCellFor_ClosestToDestCell)));
                }

                [HarmonyPostfix]
                static void AddFirstRegularHaulToTracker(WorkGiver_Scanner __instance, Job __result, Pawn pawn, Thing thing) {
                    if (__result == null) return;

                    // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if there's a jobCell
                    var haulTracker = PuahHaulTracker.GetOrCreate(pawn);
                    // thing from parameter because targetA is null because things are in queues instead
                    //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                    haulTracker.Add(thing, __result.targetB.Cell, false);
                }
            }

            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory_FirstUnloadableThing_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "FirstUnloadableThing");

                [HarmonyPrefix]
                static bool UsePuahFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                    __result = JooStoreUtility.PuahFirstUnloadableThing(pawn);
                    return false;
                }
            }
        }
    }
}
