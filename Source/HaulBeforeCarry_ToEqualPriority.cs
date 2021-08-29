using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

namespace JobsOfOpportunity
{
    partial class Mod
    {
        static partial class Patch_PUAH
        {
            static          StorageSettings reducedPriorityStore;
            static readonly List<Thing>     thingsInReducedPriorityStore = new List<Thing>();

            [HarmonyPatch]
            static partial class WorkGiver_HaulToInventory__JobOnThing_Patch
            {
                [HarmonyPrefix]
                static void HaulToEqualPriority(Pawn pawn, Thing thing) {
                    if (!settings.HaulToEqualPriority || !settings.HaulToInventory || !settings.Enabled) return;
                    if (!specialHauls.TryGetValue(pawn, out var specialHaul) || specialHaul.haulType != SpecialHaulType.HaulBeforeCarry) return;
                    var haulDestination = StoreUtility.CurrentHaulDestinationOf(thing);
                    if (haulDestination == null) return;

                    reducedPriorityStore = haulDestination.GetStoreSettings();
                    thingsInReducedPriorityStore.AddRange(
                        thing.GetSlotGroup().CellsList.SelectMany(
                            cell => cell.GetThingList(thing.Map).Where(cellThing => cellThing.def.alwaysHaulable || cellThing.def.EverHaulable)));
                    thing.Map.haulDestinationManager.Notify_HaulDestinationChangedPriority();
                }

                [HarmonyPostfix]
                static void HaulToEqualPriorityCleanup() {
                    var map = reducedPriorityStore?.HaulDestinationOwner?.Map;
                    reducedPriorityStore = null;
                    thingsInReducedPriorityStore.Clear();
                    map?.haulDestinationManager.Notify_HaulDestinationChangedPriority();
                }
            }

            [HarmonyPatch]
            static class StorageSettings_Priority_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredPropertyGetter(typeof(StorageSettings), nameof(StorageSettings.Priority));

                [HarmonyPostfix]
                static void GetReducedPriority(StorageSettings __instance, ref StoragePriority __result) {
                    if (__instance == reducedPriorityStore && __result > StoragePriority.Unstored)
                        __result -= 1;
                }
            }

            [HarmonyPatch]
            static class ListerHaulables_ThingsPotentiallyNeedingHauling_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(ListerHaulables), nameof(ListerHaulables.ThingsPotentiallyNeedingHauling));

                [HarmonyPostfix]
                static void IncludeThingsInReducedPriorityStore(ref List<Thing> __result) {
                    if (!thingsInReducedPriorityStore.NullOrEmpty())
                        __result.AddRange(thingsInReducedPriorityStore);
                }
            }
        }
    }
}
