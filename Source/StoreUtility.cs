// TryFindBestBetterStorageFor() looks for both slot group and non-slot group (twice the search), returns preference for non-slot group

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
partial class Mod
{
    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once FieldCanBeMadeReadOnly.Global
    [TweakValue("WhileYoureUp.Unloading")] public static bool DumpIfStoreFilledAndAltsInopportune = true;

    // So long as we are within a given job check/assignment of PUAH's `WorkGiver_HaulToInventory`
    //  we can cache store cell by thing and reuse it since pawn, distance, etc. will remain the same. :Cache
    static readonly Dictionary<Thing, IntVec3> puahStoreCellCache = new(64);

    [HarmonyPatch]
    static class Puah_WorkGiver_HaulToInventory__TryFindBestBetterStoreCellFor_Patch
    {
        static bool Prepare() => havePuah;

        // PUAH's version of this adds `skipCells`, but PUAH *also* calls the vanilla version (indirectly).
        // To keep things simple we just point PUAH's version back toward vanilla's and only patch vanilla's (w/`skipCells` support).
        static MethodBase TargetMethod() => PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor;

        // todo :PatchNeighborCheck
        [HarmonyPrefix]
        static bool Use_DetourAware_TryFindStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction,
            ref IntVec3 foundCell) {
            if (!settings.Enabled || !settings.UsePickUpAndHaulPlus) return Continue();
            __result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell);
            return Halt();
        }
    }

    [HarmonyPriority(Priority.HigherThanNormal)]
    [HarmonyPatch]
    static class StoreUtility__TryFindBestBetterStoreCellFor_Patch
    {
        // We patch `TryFindBestBetterStoreCellFor()` to implement our detour logic into PUAH.
        // This isn't needed for the `htcOpportunity` and `htcBeforeCarry` detours since they're a simple `HaulToCellStorageJob()` call.
        static bool       Prepare()      => havePuah;
        static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor));

        // todo :PatchNeighborCheck
        [HarmonyPrefix]
        static bool DetourAware_TryFindStore(ref bool __result, Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
            Faction faction, out IntVec3 foundCell, bool needAccurateResult) {
            foundCell = IntVec3.Invalid;
            if (carrier is null || !settings.Enabled || !settings.UsePickUpAndHaulPlus) return Continue();
            var isUnloadJob = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
            if (!puahToInventoryCallStack.Any() && !isUnloadJob) return Continue();

            var canCache      = !isUnloadJob; // unload job happens over multiple ticks
            var usesSkipCells = puahToInventoryCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt);
            var skipCells     = usesSkipCells ? (HashSet<IntVec3>)PuahField_WorkGiver_HaulToInventory_SkipCells.GetValue(null) : null;
            if (canCache) {
                if (puahStoreCellCache.Count == 0) {
                    // Opportunity detours clear their cache immediately after, so this is only non-empty
                    //  if we're executing within an opportunity detour, which makes it safe to use.
                    // This is the source for the majority of our cache hits, but see below. :Cache
                    puahStoreCellCache.AddRange(opportunityStoreCellCache);
                }
                if (!puahStoreCellCache.TryGetValue(t, out foundCell))
                    foundCell = IntVec3.Invalid;
                else
                    Debug.WriteLine($"{RealTime.frameCount} {carrier} Cache hit! (Size: {puahStoreCellCache.Count}) {MethodBase.GetCurrentMethod()!.Name}");

                // we reproduce PUAH's `skipCells` within `…MidwayToTarget()` below but we also need it here with caching
                if (foundCell.IsValid && skipCells is not null) {
                    if (skipCells.Contains(foundCell))
                        foundCell = IntVec3.Invalid; // cache is no good; skipCells will be used below
                    else                             // successful cache hit
                        skipCells.Add(foundCell);    // not used below, but the next call of this method, like PUAH
                }
            }

            var detour = detours.GetValueSafe(carrier);
            Debug.WriteLine($"Carrier: {carrier} {detour?.type}");
            var jobTarget   = detour?.opportunity.jobTarget ?? LocalTargetInfo.Invalid;
            var carryTarget = detour?.beforeCarry.carryTarget ?? LocalTargetInfo.Invalid;

            if (!foundCell.IsValid && !TryFindBestBetterStoreCellFor_MidwayToTarget( // call our own
                    t, jobTarget, carryTarget, carrier, map, currentPriority, faction, out foundCell,
                    // True here may give us a shorter path, giving detours a better chance.
                    needAccurateResult: !puahToInventoryCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing)
                                        && (jobTarget.IsValid || carryTarget.IsValid)
                                        && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                    skipCells))
                return Halt(__result = false);

            // PUAH can repeat a store lookup on its own outside the context of `Opportunity_Job()`
            // —both from dedicated "Haul" labor, or from `BeforeCarry_Job()`, but it's seldom. :Cache
            if (canCache)
                puahStoreCellCache.SetOrAdd(t, foundCell);

            // Since unloading occurs many ticks later than loading, circumstances may have changed.
            // If we're only hauling because it was opportune, and the goal posts have been moved,
            //  let's check if they've moved farther than we're willing to tolerate.
            if (isUnloadJob) {
                if (!DumpIfStoreFilledAndAltsInopportune && !DebugViewSettings.drawOpportunisticJobs) return Halt(__result = true);
                if (detour?.type != DetourType.PuahOpportunity) return Halt(__result = true);
                if (!detour.puah.defHauls.TryGetValue(t.def, out var storeCell)) return Halt(__result = true);
                if (foundCell.GetSlotGroup(map) == storeCell.GetSlotGroup(map)) return Halt(__result = true);

                var newStoreCell = foundCell; // "cannot use 'out' parameter 'foundCell' inside local function declaration"
                bool IsNewStoreOpportune() {
                    // For these checks let's take it as a given that our unloading pawn is at `originalFoundCell` by now.
                    //  This isn't necessarily true?, but since we found the distance acceptable if we did make it there,
                    //  let's use that as our basis and check for a detour from that original detour.
                    // Obviously a detour from a detour can be longer than we originally allowed,
                    //  but we would prefer the pawn to unload rather than dump their items.
                    var storeToNewStoreSquared = storeCell.DistanceToSquared(newStoreCell); // :Sqrt
                    var storeToJobSquared      = storeCell.DistanceToSquared(detour.opportunity.jobTarget.Cell);
                    if (storeToNewStoreSquared > storeToJobSquared * settings.Opportunity_MaxNewLegsPctOrigTrip.Squared()) return false; // :MaxNewLeg

                    var storeToNewStore = storeCell.DistanceTo(newStoreCell);
                    var newStoreToJob   = newStoreCell.DistanceTo(detour.opportunity.jobTarget.Cell);
                    var storeToJob      = storeCell.DistanceTo(detour.opportunity.jobTarget.Cell);
                    if (storeToNewStore + newStoreToJob > storeToJob * settings.Opportunity_MaxTotalTripPctOrigTrip) return false; // :MaxTotalTrip
                    return true;
                }

                if (!DumpIfStoreFilledAndAltsInopportune || IsNewStoreOpportune())
                    return Halt(__result = true);

                if (DebugViewSettings.drawOpportunisticJobs) {
                    for (var _ = 0; _ < 3; _++) {
                        var duration = 600;
                        map.debugDrawer.FlashCell(foundCell,                         0.26f, carrier.Name.ToStringShort, duration); // yellow
                        map.debugDrawer.FlashCell(storeCell,                         0.22f, carrier.Name.ToStringShort, duration); // orange
                        map.debugDrawer.FlashCell(detour.opportunity.jobTarget.Cell, 0.0f,  carrier.Name.ToStringShort, duration); // red

                        // yellow: longer new; green: shorter old (longer is worse in this case, hence swapped colors)
                        map.debugDrawer.FlashLine(storeCell, foundCell,                         duration, SimpleColor.Yellow);
                        map.debugDrawer.FlashLine(foundCell, detour.opportunity.jobTarget.Cell, duration, SimpleColor.Yellow);
                        map.debugDrawer.FlashLine(storeCell, detour.opportunity.jobTarget.Cell, duration, SimpleColor.Green);
                    }

                    MoteMaker.ThrowText(storeCell.ToVector3(), carrier.Map, "Debug_CellOccupied".ModTranslate(), new Color(0.94f, 0.85f, 0f)); // orange
                    MoteMaker.ThrowText(foundCell.ToVector3(), carrier.Map, "Debug_TooFar".ModTranslate(),       Color.yellow);
                    MoteMaker.ThrowText(carrier.DrawPos,       carrier.Map, "Debug_Dropping".ModTranslate(),     Color.green);
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
                if (foundCellGroup != detour.beforeCarry.puah.storeCell.GetSlotGroup(map))
                    return Halt(__result = false);

                // Debug.WriteLine($"{t} is destined for same storage {foundCellGroup} {foundCell}");

                if (foundCellGroup.Settings.Priority == t.Position.GetSlotGroup(map)?.Settings?.Priority) {
                    if (carrier.CurJobDef == JobDefOf.HaulToContainer && carrier.CurJob.targetC.Thing is Frame frame) {
                        if (!frame.cachedMaterialsNeeded.Select(x => x.thingDef).Contains(t.def))
                            return Halt(__result = false);

                        Debug.WriteLine(
                            $"APPROVED {t} {t.Position} as needed supplies for {detour.beforeCarry.carryTarget}"
                            + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                    }

                    if (carrier.CurJobDef == JobDefOf.DoBill) {
                        if (!carrier.CurJob.targetQueueB.Select(x => x.Thing?.def).Contains(t.def))
                            return Halt(__result = false);

                        Debug.WriteLine(
                            $"APPROVED {t} {t.Position} as ingredients for {detour.beforeCarry.carryTarget}"
                            + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                    }
                }
            }

            if (puahToInventoryCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt)) {
                detour = SetOrAddDetour(carrier, DetourType.ExistingElsePuah);
                detour.TrackPuahThing(t, foundCell);
            }
            return Halt(__result = true);
        }
    }

    static bool TryFindBestBetterStoreCellFor_MidwayToTarget(Thing thing, LocalTargetInfo opportunity, LocalTargetInfo beforeCarry,
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
            if (slotGroup.Settings.Priority == currentPriority && !beforeCarry.IsValid) break; // :ToEqualPriority

            var stockpile       = slotGroup.parent as Zone_Stockpile;
            var buildingStorage = slotGroup.parent as Building_Storage;

            if (opportunity.IsValid) {
                if (stockpile is not null && !settings.Opportunity_ToStockpiles) continue;
                if (buildingStorage is not null && !settings.Opportunity_BuildingFilter.Allows(buildingStorage.def)) continue;
            }

            if (beforeCarry.IsValid) {
                // :ToEqualPriority
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
