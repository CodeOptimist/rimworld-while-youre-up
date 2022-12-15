// TryFindBestBetterStorageFor() looks for both slot group and non-slot group (twice the search), returns preference for non-slot group

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [TweakValue("WhileYoureUp.Unloading")] public static bool DumpIfFullStoreAndOthersInopportune = true;

        static readonly Dictionary<Thing, IntVec3> cachedStoreCells = new();

        [HarmonyPatch]
        static class Puah_WorkGiver_HaulToInventory__TryFindBestBetterStoreCellFor_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor;

            // todo #PatchNeighborCheck
            [HarmonyPrefix]
            static bool Use_DetourAware_TryFindStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
                ref IntVec3 foundCell) {
                if (!settings.Enabled || !settings.UsePickUpAndHaulPlus) return Continue();
                // patched below to keep our code in one place
                __result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell);
                return Halt();
            }
        }

        [HarmonyPriority(Priority.HigherThanNormal)]
        [HarmonyPatch]
        static class StoreUtility__TryFindBestBetterStoreCellFor_Patch
        {
            // todo explain havePuah here... I think because features w/o PUAH are implemented elsewhere?
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor));

            // todo #PatchNeighborCheck
            [HarmonyPrefix]
            static bool DetourAware_TryFindStore(ref bool __result, Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
                Faction faction, out IntVec3 foundCell, bool needAccurateResult) {
                foundCell = IntVec3.Invalid;

                if (carrier is null || !settings.Enabled || !settings.UsePickUpAndHaulPlus) return Continue();
                // unload job is ongoing, multiple ticks
                var isUnloadJob = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
                if (!puahCallStack.Any() && !isUnloadJob) return Continue();

                var skipCells    = (HashSet<IntVec3>)PuahField_WorkGiver_HaulToInventory_SkipCells.GetValue(null);
                var hasSkipCells = puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt);

                // unload job happens over multiple ticks
                var canCache = !isUnloadJob;
                if (canCache) { // todo #CacheTick
                    if (cachedStoreCells.Count == 0)
                        cachedStoreCells.AddRange(opportunityCachedStoreCells); // inherit cache if available (will be same tick)
                    if (!cachedStoreCells.TryGetValue(t, out foundCell))
                        foundCell = IntVec3.Invalid;
                    // else
                    //     Debug.WriteLine($"{RealTime.frameCount} Cache hit! (Size: {cachedStoreCells.Count}) {MethodBase.GetCurrentMethod().Name}");

                    // we reproduce PUAH's skipCells in our own TryFindStore but we also need it here with caching
                    if (foundCell.IsValid && hasSkipCells) {
                        if (skipCells.Contains(foundCell))
                            foundCell = IntVec3.Invalid; // cache is no good, skipCells will be used below
                        else                             // successful cache hit
                            skipCells.Add(foundCell);    // not used below, but the next call of this method, like PUAH
                    }
                }

                var detour      = detours.GetValueSafe(carrier);
                var jobTarget   = detour?.opportunity_jobTarget ?? LocalTargetInfo.Invalid;
                var carryTarget = detour?.beforeCarry_carryTarget ?? LocalTargetInfo.Invalid;

                if (!foundCell.IsValid && !TryFindBestBetterStoreCellFor_MidwayToTarget( // call our own
                        t, jobTarget, carryTarget, carrier, map, currentPriority, faction, out foundCell,
                        // True here may give us a shorter path, giving detours a better chance.
                        needAccurateResult: !puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing)
                                            && (jobTarget.IsValid || carryTarget.IsValid)
                                            && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                        hasSkipCells ? skipCells : null))
                    return Halt(__result = false);

                if (canCache)
                    cachedStoreCells.SetOrAdd(t, foundCell);

                // Since unloading occurs many ticks later than loading, circumstances may have changed.
                // If we're only hauling because it was opportune, and the goal posts have been moved,
                //  let's check if they've moved farther than we're willing to tolerate.
                if (isUnloadJob) {
                    if (!DumpIfFullStoreAndOthersInopportune && !DebugViewSettings.drawOpportunisticJobs) return Halt(__result = true);
                    if (detour?.type != DetourType.PuahOpportunity || !detour.puah_defHauls.TryGetValue(t.def, out var originalFoundCell)) return Halt(__result = true);
                    if (foundCell.GetSlotGroup(map) == originalFoundCell.GetSlotGroup(map)) return Halt(__result = true);

                    var newStoreCell = foundCell; // "cannot use 'out' parameter 'foundCell' inside local function declaration"
                    bool IsNewStoreOpportune() {
                        // For these checks let's take it as a given that our unloading pawn is at `originalFoundCell` by now.
                        //  This isn't necessarily true?, but since we found the distance acceptable if we did make it there,
                        //  let's use that as our basis and check for a detour from that original detour.
                        // Obviously a detour from a detour can be longer than we originally allowed, but this is an exceptional
                        //  circumstance; we would really prefer the pawn to unload rather than dump their items.
                        var storeToNewStore = originalFoundCell.DistanceTo(newStoreCell);
                        var storeToJob      = originalFoundCell.DistanceTo(detour.opportunity_jobTarget.Cell);
                        if (storeToNewStore > storeToJob * settings.Opportunity_MaxNewLegsPctOrigTrip) return false;
                        var newStoreToJob = newStoreCell.DistanceTo(detour.opportunity_jobTarget.Cell);
                        if (storeToNewStore + newStoreToJob > storeToJob * settings.Opportunity_MaxTotalTripPctOrigTrip) return false;
                        return true;
                    }

                    if (!DumpIfFullStoreAndOthersInopportune || IsNewStoreOpportune())
                        return Halt(__result = true);

                    if (DebugViewSettings.drawOpportunisticJobs) {
                        for (var _ = 0; _ < 3; _++) {
                            var duration = 600;
                            map.debugDrawer.FlashCell(foundCell,                         0.26f, carrier.Name.ToStringShort, duration);
                            map.debugDrawer.FlashCell(originalFoundCell,                 0.22f, carrier.Name.ToStringShort, duration);
                            map.debugDrawer.FlashCell(detour.opportunity_jobTarget.Cell, 0.0f,  carrier.Name.ToStringShort, duration);

                            map.debugDrawer.FlashLine(originalFoundCell, foundCell,                         duration, SimpleColor.Yellow);
                            map.debugDrawer.FlashLine(foundCell,         detour.opportunity_jobTarget.Cell, duration, SimpleColor.Yellow);
                            map.debugDrawer.FlashLine(originalFoundCell, detour.opportunity_jobTarget.Cell, duration, SimpleColor.Green);
                        }
                    }

                    return Halt(__result = false); // Denied! Find a desperate spot instead.
                }

                if (detour?.type == DetourType.PuahOpportunity) {
                    if (!detour.TrackPuahThingIfOpportune(t, carrier, ref foundCell))
                        return Halt(__result = false);
                }

                if (detour?.type == DetourType.PuahBeforeCarry) {
                    var foundCellGroup = foundCell.GetSlotGroup(map);

                    // only grab extra things going to the same store
                    if (foundCellGroup != detour.beforeCarry_puah_storeCell.GetSlotGroup(map)) return Halt(__result = false);
                    // Debug.WriteLine($"{t} is destined for same storage {foundCellGroup} {foundCell}");

                    if (foundCellGroup.Settings.Priority == t.Position.GetSlotGroup(map)?.Settings?.Priority) {
                        if (carrier.CurJobDef == JobDefOf.HaulToContainer && carrier.CurJob.targetC.Thing is Frame frame) {
                            if (!frame.cachedMaterialsNeeded.Select(x => x.thingDef).Contains(t.def))
                                return Halt(__result = false);
                            Debug.WriteLine(
                                $"APPROVED {t} {t.Position} as needed supplies for {detour.beforeCarry_carryTarget}"
                                + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                        }

                        if (carrier.CurJobDef == JobDefOf.DoBill) {
                            if (!carrier.CurJob.targetQueueB.Select(x => x.Thing?.def).Contains(t.def))
                                return Halt(__result = false);
                            Debug.WriteLine(
                                $"APPROVED {t} {t.Position} as ingredients for {detour.beforeCarry_carryTarget}"
                                + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                        }
                    }
                }

                if (puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt)) {
                    detour = SetOrAddDetour(carrier, DetourType.ExistingElsePuah);
                    detour.TrackPuahThing(t, foundCell);
                }
                return Halt(__result = true);
            }
        }

        public static bool TryFindBestBetterStoreCellFor_MidwayToTarget(Thing thing, LocalTargetInfo opportunity, LocalTargetInfo beforeCarry,
            Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
            out IntVec3 foundCell, bool needAccurateResult, HashSet<IntVec3> skipCells = null) {
            var closestSlot        = IntVec3.Invalid;
            var closestDistSquared = (float)int.MaxValue;
            var foundPriority      = currentPriority;

            foreach (var slotGroup in map.haulDestinationManager.AllGroupsListInPriorityOrder) {
                if (slotGroup.Settings.Priority < foundPriority) break;

                // original: if (slotGroup.Settings.Priority <= currentPriority) break;
                if (slotGroup.Settings.Priority < currentPriority) break;
                if (slotGroup.Settings.Priority == StoragePriority.Unstored) break;
                if (slotGroup.Settings.Priority == currentPriority && !beforeCarry.IsValid) break; // #ToEqualPriority

                var stockpile       = slotGroup.parent as Zone_Stockpile;
                var buildingStorage = slotGroup.parent as Building_Storage;

                if (opportunity.IsValid) {
                    if (stockpile is not null && !settings.Opportunity_ToStockpiles) continue;
                    if (buildingStorage is not null && !settings.Opportunity_BuildingFilter.Allows(buildingStorage.def)) continue;
                }

                if (beforeCarry.IsValid) {
                    // #ToEqualPriority
                    if (!settings.HaulBeforeCarry_ToEqualPriority && slotGroup.Settings.Priority == currentPriority) break;
                    if (settings.HaulBeforeCarry_ToEqualPriority && thing.Position.IsValid && slotGroup == map.haulDestinationManager.SlotGroupAt(thing.Position)) continue;

                    if (stockpile is not null && !settings.HaulBeforeCarry_ToStockpiles) continue;
                    if (buildingStorage is not null) {
                        if (!settings.HaulBeforeCarry_BuildingFilter.Allows(buildingStorage.def)) continue;
                        // if we don't consider it suitable for opportunities (e.g. slow storing) we won't consider it suitable for same-priority delivery
                        if (slotGroup.Settings.Priority == currentPriority && !settings.Opportunity_BuildingFilter.Allows(buildingStorage.def)) continue;
                    }
                }

                if (!slotGroup.parent.Accepts(thing)) continue; // original

                // closest to halfway to target
                var thingPos     = thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : carrier.PositionHeld;
                var detourTarget = opportunity.IsValid ? opportunity.Cell : beforeCarry.IsValid ? beforeCarry.Cell : IntVec3.Invalid;
                var detourMidway = detourTarget.IsValid ? new IntVec3((detourTarget.x + thingPos.x) / 2, detourTarget.y, (detourTarget.z + thingPos.z) / 2) : IntVec3.Invalid;
                var position     = detourMidway.IsValid ? detourMidway : thingPos; // originally just thingPos

                // original block
                var maxCheckedCells = needAccurateResult ? (int)Math.Floor((double)slotGroup.CellsList.Count * Rand.Range(0.005f, 0.018f)) : 0;
                for (var i = 0; i < slotGroup.CellsList.Count; i++) {
                    var cell        = slotGroup.CellsList[i];
                    var distSquared = (float)(position - cell).LengthHorizontalSquared;
                    if (distSquared > closestDistSquared) continue;
                    if (skipCells is not null && skipCells.Contains(cell)) continue; // PUAH addition
                    if (!StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction)) continue;

                    closestSlot        = cell;
                    closestDistSquared = distSquared;
                    foundPriority      = slotGroup.Settings.Priority;

                    if (i >= maxCheckedCells) break;
                }
            }

            foundCell = closestSlot;
            if (foundCell.IsValid && skipCells is not null)
                skipCells.Add(foundCell);
            return foundCell.IsValid;
        }
    }
}
