﻿// TryFindBestBetterStorageFor() looks for both slot group and non-slot group (twice the search), returns preference for non-slot group

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
        [TweakValue("WhileYoureUp")]
        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        public static bool pauseIfPawnUnloadBamboozled;
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

                if (carrier == null || !settings.Enabled || !settings.UsePickUpAndHaulPlus) return Continue();
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

                var detour      = haulDetours.GetValueSafe(carrier);
                var jobTarget   = detour?.opportunity_jobTarget ?? LocalTargetInfo.Invalid;
                var carryTarget = detour?.beforeCarry_carryTarget ?? LocalTargetInfo.Invalid;

                // todo during the UNLOAD job the pawn has moved!
                //  should we really be getting stockpile closest to target??? need to add new pawn travel distance to calculation
                if (!foundCell.IsValid && !TryFindBestBetterStoreCellFor_ClosestToTarget( // call our own
                        t, jobTarget, carryTarget, carrier, map, currentPriority, faction, out foundCell,
                        // True here may give us a shorter path, giving special hauls a better chance
                        needAccurateResult: !puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing)
                                            && (jobTarget.IsValid || carryTarget.IsValid)
                                            && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                        hasSkipCells ? skipCells : null))
                    return Halt(__result = false);

                if (canCache)
                    cachedStoreCells.SetOrAdd(t, foundCell);

                // since unloading occurs many ticks later than loading, circumstances may have changed
                // if we're only hauling because it was opportune, and the goal posts have been moved,
                // let's throw a (figurative) fit and drop everything so we aren't further delayed from the original non-haul job
                if (isUnloadJob) {
                    if (detour?.type == DetourType.PuahOpportunity && detour.puah_defHauls.TryGetValue(t.def, out var originalFoundCell)) {
                        if (foundCell.GetSlotGroup(map) != originalFoundCell.GetSlotGroup(map)) {
                            var distance           = carrier.Position.DistanceTo(foundCell) + foundCell.DistanceTo(detour.opportunity_jobTarget.Cell);
                            var distanceToOriginal = carrier.Position.DistanceTo(originalFoundCell) + originalFoundCell.DistanceTo(detour.opportunity_jobTarget.Cell);
                            if (distance > distanceToOriginal) {
#if DEBUG
                                if (pauseIfPawnUnloadBamboozled) {
                                    map.debugDrawer.debugCells.Clear();
                                    map.debugDrawer.debugLines.Clear();
                                }

                                if (pauseIfPawnUnloadBamboozled || DebugViewSettings.drawOpportunisticJobs) {
                                    for (var _ = 0; _ < 3; _++) {
                                        var duration = 600;
                                        map.debugDrawer.FlashCell(foundCell,                         0.26f, carrier.Name.ToStringShort, duration);
                                        map.debugDrawer.FlashCell(t.Position,                        0.62f, carrier.Name.ToStringShort, duration);
                                        map.debugDrawer.FlashCell(originalFoundCell,                 0.22f, carrier.Name.ToStringShort, duration);
                                        map.debugDrawer.FlashCell(detour.opportunity_jobTarget.Cell, 0.0f,  carrier.Name.ToStringShort, duration);

                                        map.debugDrawer.FlashLine(carrier.Position,  foundCell,                         duration, SimpleColor.Yellow);
                                        map.debugDrawer.FlashLine(foundCell,         detour.opportunity_jobTarget.Cell, duration, SimpleColor.Yellow);
                                        map.debugDrawer.FlashLine(t.Position,        originalFoundCell,                 duration, SimpleColor.Green);
                                        map.debugDrawer.FlashLine(originalFoundCell, detour.opportunity_jobTarget.Cell, duration, SimpleColor.Green);
                                    }
                                }

                                if (pauseIfPawnUnloadBamboozled) {
                                    if (!Find.Selector.AnyPawnSelected) {
                                        CameraJumper.TryJumpAndSelect(carrier);
                                        Find.TickManager.Pause();
                                    }
                                }
#endif
                                return Halt(__result = false);
                            }
                        }
                    }
                    return Halt(__result = true);
                }

                if (detour?.type == DetourType.PuahOpportunity) {
                    if (!detour.TrackPuahThingIfOpportune(t, carrier, ref foundCell))
                        return Halt(__result = false);
                }

                if (detour?.type == DetourType.PuahBeforeCarry) {
                    var foundCellGroup = foundCell.GetSlotGroup(map);

                    // only grab extra things going to the same store
                    if (foundCellGroup != detour.beforeCarry_storeCell.GetSlotGroup(map)) return Halt(__result = false);
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

        public static bool TryFindBestBetterStoreCellFor_ClosestToTarget(Thing thing, LocalTargetInfo opportunity, LocalTargetInfo beforeCarry, Pawn pawn, Map map,
            StoragePriority currentPriority,
            Faction faction, out IntVec3 foundCell, bool needAccurateResult, HashSet<IntVec3> skipCells = null) {
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
                    if (stockpile != null && !settings.Opportunity_ToStockpiles) continue;
                    if (buildingStorage != null && !settings.Opportunity_BuildingFilter.Allows(buildingStorage.def)) continue;
                }

                if (beforeCarry.IsValid) {
                    // #ToEqualPriority
                    if (!settings.HaulBeforeCarry_ToEqualPriority && slotGroup.Settings.Priority == currentPriority) break;
                    if (settings.HaulBeforeCarry_ToEqualPriority && thing.Position.IsValid && slotGroup == map.haulDestinationManager.SlotGroupAt(thing.Position)) continue;

                    if (stockpile != null && !settings.HaulBeforeCarry_ToStockpiles) continue;
                    if (buildingStorage != null) {
                        if (!settings.HaulBeforeCarry_BuildingFilter.Allows(buildingStorage.def)) continue;
                        // if we don't consider it suitable for opportunities (e.g. slow storing) we won't consider it suitable for same-priority delivery
                        if (slotGroup.Settings.Priority == currentPriority && !settings.Opportunity_BuildingFilter.Allows(buildingStorage.def)) continue;
                    }
                }

                if (!slotGroup.parent.Accepts(thing)) continue; // original

                // closest to target
                var position = opportunity.IsValid  ? opportunity.Cell :
                    beforeCarry.IsValid             ? beforeCarry.Cell :
                    thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : pawn.PositionHeld; // original

                // original block
                var maxCheckedCells = needAccurateResult ? (int)Math.Floor((double)slotGroup.CellsList.Count * Rand.Range(0.005f, 0.018f)) : 0;
                for (var i = 0; i < slotGroup.CellsList.Count; i++) {
                    var cell        = slotGroup.CellsList[i];
                    var distSquared = (float)(position - cell).LengthHorizontalSquared;
                    if (distSquared > closestDistSquared) continue;
                    if (skipCells != null && skipCells.Contains(cell)) continue; // PUAH addition
                    if (!StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, faction)) continue;

                    closestSlot        = cell;
                    closestDistSquared = distSquared;
                    foundPriority      = slotGroup.Settings.Priority;

                    if (i >= maxCheckedCells) break;
                }
            }

            foundCell = closestSlot;
            if (foundCell.IsValid && skipCells != null)
                skipCells.Add(foundCell);
            return foundCell.IsValid;
        }
    }
}
