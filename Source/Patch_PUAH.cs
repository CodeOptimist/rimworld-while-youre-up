using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_PUAH
        {
            static readonly Type PuahJobDriver_HaulToInventoryType = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");

            [HarmonyPatch]
            static class JobDriver_GetReport_Patch
            {
                static bool Prepare() {
                    return PuahJobDriver_HaulToInventoryType != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahJobDriver_HaulToInventoryType, "GetReport");
                }

                [HarmonyPostfix]
                static void GetJooPuahReportString(JobDriver __instance, ref string __result) {
                    if (!PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance)) return;
                    if (!Hauling.pawnPuah.ContainsKey(__instance.pawn)) return;
                    __result = $"Opportunistically {__result}";
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor_Patch
            {
                static bool Prepare() {
                    return PuahWorkGiver_HaulToInventoryType != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahWorkGiver_HaulToInventoryType, "TryFindBestBetterStoreCellFor");
                }

                [HarmonyPrefix]
                static bool UsePuahAllocateThingAtCell_GetStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell) {
                    __result = JooStoreUtility.PuahAllocateThingAtCell_GetStore(thing, carrier, map, currentPriority, faction, out foundCell);
                    return false;
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_HasJobOnThing_Patch
            {
                static bool Prepare() {
                    return PuahWorkGiver_HaulToInventoryType != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahWorkGiver_HaulToInventoryType, "HasJobOnThing");
                }

                [HarmonyPrefix]
                static void ClearJooDataForNewHaul(Pawn pawn) {
                    // keep for the haulMoreWork toil that extends our path, otherwise clear it for fresh distance calculations
                    if (pawn.CurJobDef?.defName != "HaulToInventory") Hauling.pawnPuah.Remove(pawn);
                }

                // we need to patch PUAH's use of vanilla TryFindBestBetterStoreCellFor within HasJobOnThing for the haulMoreWork toil
                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> UsePuahHasJobOnThing_HasStore(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.Method(typeof(JooStoreUtility), nameof(JooStoreUtility.PuahHasJobOnThing_HasStore)));
                }
            }

            [HarmonyPatch]
            static class WorkGiver_HaulToInventory_JobOnThing_Patch
            {
                static bool Prepare() {
                    return PuahWorkGiver_HaulToInventoryType != null;
                }

                static MethodBase TargetMethod() {
                    return AccessTools.Method(PuahWorkGiver_HaulToInventoryType, "JobOnThing");
                }

                // why not take advantage of our cache here as well
                static bool UseJooCachedStoreCell(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult = true) {
                    return Hauling.cachedStoreCell.TryGetValue(t, out foundCell)
                           || StoreUtility.TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out foundCell, needAccurateResult);
                }

                [HarmonyTranspiler]
                static IEnumerable<CodeInstruction> _UseJooCachedStoreCell(IEnumerable<CodeInstruction> instructions) {
                    return instructions.MethodReplacer(
                        AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
                        AccessTools.Method(typeof(WorkGiver_HaulToInventory_JobOnThing_Patch), nameof(UseJooCachedStoreCell)));
                }
            }
        }
    }
}
