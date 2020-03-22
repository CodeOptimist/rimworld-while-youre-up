using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using RimWorld;
using Verse;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Patch_ResourceDeliverJobFor
        {
            static IConstructible constructible;

            [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
            static class WorkGiver_ConstructDeliverResources_ResourceDeliverJobFor_Patch
            {
                [HarmonyPrefix]
                [SuppressMessage("ReSharper", "UnusedParameter.Local")]
                static bool GetConstructible(Pawn pawn, IConstructible c) {
                    constructible = c;
                    return true;
                }
            }

            [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceValidator")]
            static class WorkGiver_ConstructDeliverResources_ResourceValidator_Patch
            {
                [HarmonyPostfix]
                [SuppressMessage("ReSharper", "UnusedParameter.Local")]
                static void DenySupplyingDistantResources(ref bool __result, Pawn pawn, ThingDefCountClass need, Thing th) {
                    if (!__result) return;
                    if (!HavePuah()) return;
                    if (!haulBeforeSupply.Value) return;
                    if (!(Patch_ResourceDeliverJobFor.constructible is Thing constructible)) return;
                    if (th.IsInValidStorage()) return;
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(th, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out var storeCell, false)) return;

                    var supplyFromHereDist = th.Position.DistanceTo(constructible.Position);
                    var supplyFromStoreDist = storeCell.DistanceTo(constructible.Position);
                    Debug.WriteLine($"Supply from here: {supplyFromStoreDist}; supply from store: {supplyFromStoreDist}");

                    // if it's closer to our constructible once stored, let's exclude it from consideration so that a
                    // Haul (rather than Supply) job will retrieve it *and* any adjacent resources via Mehni's "Pick Up And Haul"
                    // https://steamcommunity.com/sharedfiles/filedetails/?id=1279012058

                    // and with opportunistic hauling,  if our pawn is sufficiently close we'll end up grabbing this *anyway*
                    // simply on our WAY to the stockpile we'd now be supplying from (provided we're headed that way, e.g. it has resources)

                    if (supplyFromStoreDist < supplyFromHereDist) {
                        __result = false;
                        Debug.WriteLine($"'{pawn}' denied supply job for '{th.Label}' because '{storeCell.GetSlotGroup(pawn.Map).parent}' is closer.");
                    }
                }
            }
        }
    }
}
