using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    partial class Mod
    {
    #region PUAH call stack
        // so our StoreUtility code can know from where within Pick Up And Haul it's executing
        static readonly List<MethodBase> puahCallStack = new();

        static void PushMethod(MethodBase method) => puahCallStack.Add(method);

        static void PopMethod() {
            // shouldn't happen unless another mod skipped one of our Prefix PushMethods (breaking our mod)
            if (!puahCallStack.Any()) return;

            puahCallStack.Pop();
            if (!puahCallStack.Any())
                puahStoreCellCache.Clear(); // Clear at the end of PUAH's `WorkGiver` job check/assignment. #Cache
        }

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__HasJobOnThing_Patch
        {
            static bool       Prepare()                           => havePuah;
            static MethodBase TargetMethod()                      => PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing;
            static void       Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);
            static void       Postfix()                           => PopMethod();
        }

        [HarmonyPatch]
        static partial class Puah_WorkGiver_HaulToInventory__JobOnThing_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_JobOnThing;

            // priority to order correctly with our other Prefix/Postfix
            [HarmonyPriority(Priority.HigherThanNormal)]
            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);

            [HarmonyPriority(Priority.LowerThanNormal)]
            static void Postfix() => PopMethod();
        }

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__AllocateThingAtCell_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt;

            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);
            static void Postfix()                           => PopMethod();
        }
    #endregion

    #region same-priority storage feature
        static StorageSettings reducedPriorityStore;

        [HarmonyPatch]
        static class Puah_StorageSettings_Priority_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredPropertyGetter(typeof(StorageSettings), nameof(StorageSettings.Priority));

            [HarmonyPostfix]
            static void GetReducedPriority(StorageSettings __instance, ref StoragePriority __result) {
                // least disruptive way to support hauling between stores of equal priority
                if (__instance == reducedPriorityStore && __result > StoragePriority.Unstored) // #ReducedPriority
                    __result -= 1;
            }
        }

        static readonly List<Thing> thingsInReducedPriorityStore = new();

        [HarmonyPatch]
        static partial class Puah_WorkGiver_HaulToInventory__JobOnThing_Patch
        {
            // This feature is currently PUAH only.
            // The implementation is optimized more for simplicity than performance, so I'm not perfectly happy.
            [HarmonyPrefix]
            static void HaulToEqualPriority(Pawn pawn, Thing thing) {
                if (!settings.Enabled || !settings.UsePickUpAndHaulPlus || !settings.HaulBeforeCarry_ToEqualPriority) return;
                if (detours.GetValueSafe(pawn)?.type != DetourType.PuahBeforeCarry) return;

                var haulDestination = StoreUtility.CurrentHaulDestinationOf(thing);
                if (haulDestination is null) return;

                reducedPriorityStore = haulDestination.GetStoreSettings(); // #ReducedPriority
                thingsInReducedPriorityStore.AddRange(
                    thing.GetSlotGroup().CellsList.SelectMany(cell => cell.GetThingList(thing.Map).Where(cellThing => cellThing.def.EverHaulable)));
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
        static class Puah_ListerHaulables_ThingsPotentiallyNeedingHauling_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(ListerHaulables), nameof(ListerHaulables.ThingsPotentiallyNeedingHauling));

            [HarmonyPostfix]
            static void IncludeThingsInReducedPriorityStore(ref List<Thing> __result) {
                if (!thingsInReducedPriorityStore.NullOrEmpty())
                    __result.AddRange(thingsInReducedPriorityStore);
            }
        }
    #endregion

    #region unloading
        [HarmonyPatch]
        static class Puah_JobDriver_UnloadYourHauledInventory__FirstUnloadableThing_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing;

            // todo #PatchNeighborCheck
            [HarmonyPrefix]
            static bool DetourAwareFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                if (!settings.Enabled || !settings.UsePickUpAndHaulPlus) return Continue();

                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var carriedThings         = Traverse.Create(hauledToInventoryComp).Method("GetHashSet").GetValue<HashSet<Thing>>(); // Traverse is cached
                if (!carriedThings.Any()) return Halt(__result = default);

                // just loaded game, or half-state from toggling settings, etc.
                var detour = SetOrAddDetour(pawn, DetourType.ExistingElsePuah);

#if false
                Debug.WriteLine($"{pawn}");
                Debug.WriteLine($"{detour.puah_defHauls.Count} Hauls:");
                foreach (var defHaul in detour.puah_defHauls)
                    Debug.WriteLine($"\t{defHaul.Key}");

                Debug.WriteLine($"{detour.puah_defHauls.Count} Unloads:");
                foreach (var haul in detour.puah_defHauls)
                    Debug.WriteLine($"\t{haul.Value.GetSlotGroup(pawn.Map)}");
#endif

                (Thing thing, IntVec3 storeCell) GetDefHaul(Thing thing) {
                    // It's completely possible storage has changed; that's fine. This is just a guess for order.
                    if (detour.puah_defHauls.TryGetValue(thing.def, out var storeCell))
                        return (thing, storeCell);

                    // should only be necessary after loading, because detours aren't saved in game file like CompHauledToInventory
                    if (TryFindBestBetterStoreCellFor_MidwayToTarget(
                            thing, detour.opportunity_jobTarget, detour.beforeCarry_carryTarget,
                            pawn,  pawn.Map,                     StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false)) {
                        // cache for next
                        detour.puah_defHauls.Add(thing.def, storeCell);
                    }
                    return (thing, storeCell);
                }

                var closestHaul = carriedThings.Select(GetDefHaul)
                    .Where(x => x.storeCell.IsValid).DefaultIfEmpty()
                    .MinBy(x => x.storeCell.DistanceTo(pawn.Position));
                var closestSlotGroup = closestHaul.storeCell.IsValid ? closestHaul.storeCell.GetSlotGroup(pawn.Map) : null;

                var firstThingToUnload = closestSlotGroup is null
                    ? closestHaul.thing
                    : carriedThings.Select(GetDefHaul)
                        .Where(x => x.storeCell.IsValid && x.storeCell.GetSlotGroup(pawn.Map) == closestSlotGroup)
                        .DefaultIfEmpty() // should at least find closestHaul, but guard against future changes
                        .MinBy(x => (x.thing.def.FirstThingCategory?.index, x.thing.def.defName)).thing;

                // ReSharper disable once ConvertIfStatementToNullCoalescingExpression
                if (firstThingToUnload is null)
                    firstThingToUnload = carriedThings.MinBy(t => (t.def.FirstThingCategory?.index, t.def.defName));

                if (!carriedThings.Intersect(pawn.inventory.innerContainer).Contains(firstThingToUnload)) {
                    // can't be removed by dropping / delivering, so remove now
                    carriedThings.Remove(firstThingToUnload);

                    // because of merges
                    var thingFoundByDef = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == firstThingToUnload.def);
                    if (thingFoundByDef != default)
                        return Halt(__result = new ThingCount(thingFoundByDef, thingFoundByDef.stackCount));
                }

                return Halt(__result = new ThingCount(firstThingToUnload, firstThingToUnload.stackCount));
            }
        }
    #endregion
    }
}
