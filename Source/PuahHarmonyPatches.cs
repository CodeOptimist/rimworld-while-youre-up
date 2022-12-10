using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        // so our StoreUtility code can know from where within Pick Up And Haul it's executing
        static readonly List<MethodBase> puahCallStack = new List<MethodBase>();

        static void PushMethod(MethodBase method) => puahCallStack.Add(method);

        static void PopMethod() {
            // shouldn't happen unless another mod skipped one of our Prefix PushMethods (breaking our mod)
            if (!puahCallStack.Any()) return;

            puahCallStack.Pop();
            if (!puahCallStack.Any()) {
                // Todo: Keep the cache until the tick changes; verify at the very end if destination still accepts thing
                // to handle cache going stale within the same tick (uncommon but possible). #CacheTick 
                cachedStoreCells.Clear();
            }
        }

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__HasJobOnThing_Patch
        {
            static bool       Prepare()                           => havePuah;
            static MethodBase TargetMethod()                      => PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing;
            static void       Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);
            static void       Postfix()                           => PopMethod();
        }

        #region
        static StorageSettings reducedPriorityStore;

        [HarmonyPatch]
        static class Puah_StorageSettings_Priority_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredPropertyGetter(typeof(StorageSettings), nameof(StorageSettings.Priority));

            [HarmonyPostfix]
            static void GetReducedPriority(StorageSettings __instance, ref StoragePriority __result) {
                // least disruptive way to support hauling between stores of equal priority
                if (__instance == reducedPriorityStore && __result > StoragePriority.Unstored)
                    __result -= 1;
            }
        }

        static readonly List<Thing> thingsInReducedPriorityStore = new List<Thing>();

        [HarmonyPatch]
        static class Puah_ListerHaulables_ThingsPotentiallyNeedingHauling_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(ListerHaulables), nameof(ListerHaulables.ThingsPotentiallyNeedingHauling));

            [HarmonyPostfix]
            static void IncludeThingsInReducedPriorityStore(ref List<Thing> __result) {
                if (!thingsInReducedPriorityStore.NullOrEmpty()) {
                    // todo does this happen multiple times?
                    Debug.WriteLine($"{RealTime.frameCount} Adding things in reduced priority store.");
                    __result.AddRange(thingsInReducedPriorityStore);
                }
            }
        }
        #endregion

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__JobOnThing_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_JobOnThing;

            [HarmonyPriority(Priority.HigherThanNormal)]
            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);

            [HarmonyPriority(Priority.LowerThanNormal)]
            static void Postfix() => PopMethod();

            // todo I guess this isn't a feature without PUAH; we should change that?
            [HarmonyPrefix]
            static void HaulToEqualPriority(Pawn pawn, Thing thing) {
                if (!settings.HaulBeforeCarry_ToEqualPriority || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return;
                if (!(specialHauls.GetValueSafe(pawn) is PuahBeforeCarry)) return;
                var haulDestination = StoreUtility.CurrentHaulDestinationOf(thing);
                if (haulDestination == null) return;

                reducedPriorityStore = haulDestination.GetStoreSettings(); // mark it
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

            [HarmonyPostfix]
            static void TrackInitialHaul(WorkGiver_Scanner __instance, Job __result, Pawn pawn, Thing thing) {
                if (__result == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return;

                if (!(specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)) {
                    puah = new PuahWithBetterUnloading();
                    specialHauls.SetOrAdd(pawn, puah);
                }
                // thing from parameter because targetA is null because things are in queues instead
                //  https://github.com/Mehni/PickUpAndHaul/blob/af50a05a8ae5ca64d9b95fee8f593cf91f13be3d/Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs#L98
                // JobOnThing() can run additional times (e.g. haulMoreWork toil) so don't assume this is already added if it's an Opportunity or HaulBeforeCarry
                puah.TrackThing(thing, __result.targetB.Cell, prepend: true);
            }
        }

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__AllocateThingAtCell_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt;

            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);
            static void Postfix()                           => PopMethod();
        }

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__TryFindBestBetterStoreCellFor_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor;

            // todo #PatchNeighborCheck
            [HarmonyPrefix]
            static bool UseSpecialHaulAwareTryFindStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
                ref IntVec3 foundCell) {
                if (!settings.UsePickUpAndHaulPlus || !settings.Enabled) return Continue();
                // have PUAH use vanilla's to keep our code in one place
                __result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell); // patched below
                return Halt();
            }
        }

        [HarmonyPatch]
        static class Puah_JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils;

            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
        }

        [HarmonyPatch]
        static class Puah_JobDriver_UnloadYourHauledInventory__FirstUnloadableThing_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing;

            // todo #PatchNeighborCheck
            [HarmonyPrefix]
            static bool SpecialHaulAwareFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                if (!settings.UsePickUpAndHaulPlus || !settings.Enabled) return Continue();
                // Traverse does its own caching

                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var carriedThings         = Traverse.Create(hauledToInventoryComp).Method("GetHashSet").GetValue<HashSet<Thing>>();
                if (!carriedThings.Any()) return Halt(__result = default);

                (Thing thing, IntVec3 storeCell) GetDefHaul(PuahWithBetterUnloading puah_, Thing thing) {
                    // It's completely possible storage has changed; that's fine. This is just a guess for order.
                    if (puah_.defHauls.TryGetValue(thing.def, out var storeCell))
                        return (thing, storeCell);

                    // should only be necessary after loading, because specialHauls aren't saved in game file like CompHauledToInventory
                    if (TryFindBestBetterStoreCellFor_ClosestToTarget(
                            thing,
                            (puah_ as PuahOpportunity)?.jobTarget ?? IntVec3.Invalid,
                            (puah_ as PuahBeforeCarry)?.carryTarget ?? IntVec3.Invalid,
                            pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out storeCell, false)) {
                        // add for next
                        puah_.defHauls.Add(thing.def, storeCell);
                    }
                    return (thing, storeCell);
                }

                // just loaded game, or half-state from toggling settings, etc.
                if (!(specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading puah)) {
                    puah = new PuahWithBetterUnloading();
                    specialHauls.SetOrAdd(pawn, puah);
                }

#if DEBUG
                Debug.WriteLine($"{pawn}");
                Debug.WriteLine($"{puah.defHauls.Count} Hauls:");
                foreach (var defHaul in puah.defHauls)
                    Debug.WriteLine($"\t{defHaul.Key}");

                Debug.WriteLine($"{puah.defHauls.Count} Unloads:");
                foreach (var haul in puah.defHauls)
                    Debug.WriteLine($"\t{haul.Value.GetSlotGroup(pawn.Map)}");
#endif

                var closestHaul = carriedThings.Select(t => GetDefHaul(puah, t))
                    .Where(x => x.storeCell.IsValid).DefaultIfEmpty()
                    .MinBy(x => x.storeCell.DistanceTo(pawn.Position));
                var closestSlotGroup = closestHaul.storeCell.IsValid ? closestHaul.storeCell.GetSlotGroup(pawn.Map) : null;

                var firstThingToUnload = closestSlotGroup == null
                    ? closestHaul.thing
                    : carriedThings.Select(t => GetDefHaul(puah, t))
                        .Where(x => x.storeCell.IsValid && x.storeCell.GetSlotGroup(pawn.Map) == closestSlotGroup)
                        .DefaultIfEmpty() // should at least find closestHaul, but guard against future changes
                        .MinBy(x => (x.thing.def.FirstThingCategory?.index, x.thing.def.defName)).thing;

                if (firstThingToUnload == null)
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
    }
}
