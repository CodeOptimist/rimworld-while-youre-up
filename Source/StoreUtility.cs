// TryFindBestBetterStorageFor() looks for both slot group and non-slot group (twice the search), returns preference for non-slot group

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        public static readonly Dictionary<Pawn, SpecialHaul> specialHauls     = new Dictionary<Pawn, SpecialHaul>();
        static readonly        Dictionary<Thing, IntVec3>    cachedStoreCells = new Dictionary<Thing, IntVec3>();

        public static bool AlreadyHauling(Pawn pawn) {
            if (specialHauls.ContainsKey(pawn)) return true;

            // because we may load a game with an incomplete haul
            if (havePuah) {
                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var takenToInventory      = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value; // traverse is cached
                if (takenToInventory != null && takenToInventory.Any(t => t != null))
                    return true;
            }

            return false;
        }

        static Job PuahJob(PuahWithBetterUnloading puah, Pawn pawn, Thing thing, IntVec3 storeCell) {
            if (!settings.Enabled || !havePuah || !settings.UsePickUpAndHaulPlus) return null;
            specialHauls.SetOrAdd(pawn, puah);
            puah.TrackThing(thing, storeCell);
            var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker; // dictionary lookup
            return (Job)PuahMethod_WorkGiver_HaulToInventory_JobOnThing.Invoke(puahWorkGiver, new object[] { pawn, thing, false });
        }

        public class SpecialHaul
        {
            readonly string reportKey;
            LocalTargetInfo target;

            protected SpecialHaul() {
            }

            public SpecialHaul(string reportKey, LocalTargetInfo target) {
                this.reportKey = reportKey;
                this.target    = target;
            }

            public string GetReport(string text) {
                if (this is PuahWithBetterUnloading puah)
                    return puah.GetLoadReport(text);
                return reportKey.ModTranslate(text.Named("ORIGINAL"), target.Label.Named("DESTINATION"));
            }
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
        static class JobDriver_HaulToCell__GetReport_Patch
        {
            [HarmonyPostfix]
            static void SpecialHaulGetReport(JobDriver_HaulToCell __instance, ref string __result) {
                if (!specialHauls.TryGetValue(__instance.pawn, out var specialHaul)) return;
                __result = specialHaul.GetReport(__result.TrimEnd('.'));
            }
        }

        [HarmonyPatch]
        static class Puah_JobDriver__GetReport_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

            [HarmonyPostfix]
            static void SpecialHaulGetReport(JobDriver __instance, ref string __result) {
                if (!settings.UsePickUpAndHaulPlus || !settings.Enabled) return;
                if (PuahType_JobDriver_HaulToInventory.IsInstanceOfType(__instance)) {
                    if (specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading puah)
                        __result = puah.GetLoadReport(__result.TrimEnd('.'));
                }

                if (PuahType_JobDriver_UnloadYourHauledInventory.IsInstanceOfType(__instance)) {
                    if (specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading puah)
                        __result = puah.GetUnloadReport(__result.TrimEnd('.'));
                }
            }
        }

        public class PuahWithBetterUnloading : SpecialHaul
        {
            public Dictionary<ThingDef, IntVec3> defHauls = new Dictionary<ThingDef, IntVec3>();

            public virtual string GetLoadReport(string text)   => "PickUpAndHaulPlus_LoadReport".ModTranslate(text.Named("ORIGINAL"));
            public virtual string GetUnloadReport(string text) => "PickUpAndHaulPlus_UnloadReport".ModTranslate(text.Named("ORIGINAL"));

            public void TrackThing(Thing thing, IntVec3 storeCell, bool prepend = false, bool trackDef = true, [CallerMemberName] string callerName = "") {
#if DEBUG
                // make deterministic, but merges and initial hauls will still fluctuate
                storeCell = storeCell.GetSlotGroup(thing.Map).CellsList[0];
#endif
                if (trackDef)
                    defHauls.SetOrAdd(thing.def, storeCell);

                if (this is PuahOpportunity opportunity) {
                    // already here because a thing merged into it, or duplicate from HasJobOnThing()
                    // we want to recalculate with the newer store cell since some time has passed
                    if (opportunity.hauls.LastOrDefault().thing == thing)
                        opportunity.hauls.Pop();

                    // special case
                    if (prepend) {
                        if (opportunity.hauls.FirstOrDefault().thing == thing)
                            opportunity.hauls.RemoveAt(0);
                        opportunity.hauls.Insert(0, (thing, storeCell));
                    } else
                        opportunity.hauls.Add((thing, storeCell));
                }

                if (callerName != "TrackThingIfOpportune")
                    Debug.WriteLine($"{RealTime.frameCount} {this} {callerName}: {thing} -> {storeCell}");
            }
        }

        [HarmonyPriority(Priority.HigherThanNormal)]
        [HarmonyPatch]
        static class Puah_StoreUtility__TryFindBestBetterStoreCellFor_Patch
        {
            static bool       Prepare()      => havePuah;
            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor));

            // todo #PatchNeighborCheck
            [HarmonyPrefix]
            static bool SpecialHaulAwareTryFindStore(ref bool __result, Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
                Faction faction, out IntVec3 foundCell, bool needAccurateResult) {
                foundCell = IntVec3.Invalid;

                if (carrier == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return Continue();
                var isUnloadJob = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
                if (!puahCallStack.Any() && !isUnloadJob) return Continue();

                var skipCells    = (HashSet<IntVec3>)PuahField_WorkGiver_HaulToInventory_SkipCells.GetValue(null);
                var hasSkipCells = puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt);

                // unload job happens over multiple ticks
                var canCache = !isUnloadJob;
                if (canCache) {
                    if (cachedStoreCells.Count == 0)
                        cachedStoreCells.AddRange(opportunityCachedStoreCells); // inherit cache if available (will be same tick)
                    if (!cachedStoreCells.TryGetValue(t, out foundCell))
                        foundCell = IntVec3.Invalid;
                    else
                        Debug.WriteLine($"{RealTime.frameCount} Cache hit! (Size: {cachedStoreCells.Count}) SpecialHaulAwareTryFindStore");

                    // we reproduce PUAH's skipCells in our own TryFindStore but we also need it here with caching
                    if (foundCell.IsValid && hasSkipCells) {
                        if (skipCells.Contains(foundCell))
                            foundCell = IntVec3.Invalid; // cache is no good, skipCells will be used below
                        else                             // successful cache hit
                            skipCells.Add(foundCell);    // not used below, but the next SpecialHaulAwareTryFindStore(), like PUAH
                    }
                }

                var puah        = specialHauls.GetValueSafe(carrier) as PuahWithBetterUnloading;
                var opportunity = puah as PuahOpportunity;
                var beforeCarry = puah as PuahBeforeCarry;

                var opportunityTarget = opportunity?.jobTarget ?? IntVec3.Invalid;
                var beforeCarryTarget = beforeCarry?.carryTarget ?? IntVec3.Invalid;
                if (!foundCell.IsValid && !TryFindBestBetterStoreCellFor_ClosestToTarget( // call our own
                        t, opportunityTarget, beforeCarryTarget, carrier, map, currentPriority, faction, out foundCell,
                        // True here may give us a shorter path, giving special hauls a better chance
                        needAccurateResult: !puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing) && (opportunityTarget.IsValid || beforeCarryTarget.IsValid)
                                                                                                                    && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                        hasSkipCells ? skipCells : null))
                    return Halt(__result = false);

                if (canCache)
                    cachedStoreCells.SetOrAdd(t, foundCell);

                if (isUnloadJob)
                    return Halt(__result = true);

                if (opportunity != null && !opportunity.TrackThingIfOpportune(t, carrier, ref foundCell))
                    return Halt(__result = false);

                if (beforeCarry != null) {
                    var foundCellGroup = foundCell.GetSlotGroup(map);

                    // only grab extra things going to the same store
                    if (foundCellGroup != beforeCarry.storeCell.GetSlotGroup(map)) return Halt(__result = false);
                    // Debug.WriteLine($"{t} is destined for same storage {foundCellGroup} {foundCell}");

                    if (foundCellGroup.Settings.Priority == t.Position.GetSlotGroup(map)?.Settings?.Priority) {
                        if (carrier.CurJobDef == JobDefOf.HaulToContainer && carrier.CurJob.targetC.Thing is Frame frame) {
                            if (!frame.cachedMaterialsNeeded.Select(x => x.thingDef).Contains(t.def))
                                return Halt(__result = false);
                            Debug.WriteLine(
                                $"APPROVED {t} {t.Position} as needed supplies for {beforeCarry.carryTarget}"
                                + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                        }

                        if (carrier.CurJobDef == JobDefOf.DoBill) {
                            if (!carrier.CurJob.targetQueueB.Select(x => x.Thing?.def).Contains(t.def))
                                return Halt(__result = false);
                            Debug.WriteLine(
                                $"APPROVED {t} {t.Position} as ingredients for {beforeCarry.carryTarget}"
                                + $" headed to same-priority storage {foundCellGroup} {foundCell}.");
                        }
                    }
                }

                if (puahCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt)) {
                    if (puah == null) {
                        puah = new PuahWithBetterUnloading();
                        specialHauls.SetOrAdd(carrier, puah);
                    }
                    puah.TrackThing(t, foundCell);
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

                // #ClosestToTarget
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
