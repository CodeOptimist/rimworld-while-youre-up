using CodeOptimist;
using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity;

class Mod : Verse.Mod {
    public const string modId = "CodeOptimist.WhileYoureUp";
    static Mod mod;
    static Settings settings;
    static bool foundConfig;
    static readonly Harmony harmony = new("CodeOptimist.WhileYoureUp");
    static readonly Type PuahCompHauledToInventoryType =
        GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory");
    static readonly Type PuahWorkGiver_HaulToInventoryType =
        GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
    static readonly Type PuahJobDriver_HaulToInventoryType =
        GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
    static readonly Type PuahJobDriver_UnloadYourHauledInventoryType =
        GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");
    static readonly MethodInfo PuahHasJobOnThing = AccessTools.DeclaredMethod(
        PuahWorkGiver_HaulToInventoryType,
        "HasJobOnThing"
    );
    static readonly MethodInfo PuahJobOnThing = AccessTools.DeclaredMethod(
        PuahWorkGiver_HaulToInventoryType,
        "JobOnThing"
    );
    static readonly MethodInfo PuahTryFindBestBetterStoreCellFor = AccessTools.DeclaredMethod(
        PuahWorkGiver_HaulToInventoryType,
        "TryFindBestBetterStoreCellFor"
    );
    static readonly MethodInfo PuahFirstUnloadableThing = AccessTools.DeclaredMethod(
        PuahJobDriver_UnloadYourHauledInventoryType,
        "FirstUnloadableThing"
    );
    static readonly MethodInfo PuahMakeNewToils = AccessTools.DeclaredMethod(
        PuahJobDriver_UnloadYourHauledInventoryType,
        "MakeNewToils"
    );
    static readonly MethodInfo PuahAllocateThingAt;
    static readonly FieldInfo PuahSkipCellsField;
    static readonly bool havePuah;
    static readonly MethodInfo PuahGetCompHauledToInventory;
    static readonly Type HugsDialog_VanillaModSettingsType;
    static readonly bool haveHugs;
    static readonly FieldInfo SettingsCurMod;
    static readonly Type CsModType;
    static readonly Type CsSettingsType;
    static readonly FieldInfo CsHaulingOverBillsSetting;
    static readonly bool haveCommonSense;
    public static readonly Dictionary<Pawn, SpecialHaul> specialHauls;

    public static Job HaulBeforeCarry(Pawn pawn, LocalTargetInfo carryTarget, Thing thing) {
        if (thing.ParentHolder is Pawn_InventoryTracker)
            return null;
        if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 2))
            return null;
        IntVec3 foundCell;
        if (!TryFindBestBetterStoreCellFor_ClosestToTarget(
                thing,
                IntVec3.Invalid,
                carryTarget,
                pawn,
                pawn.Map,
                StoreUtility.CurrentStoragePriorityOf(thing),
                pawn.Faction,
                out foundCell,
                true
            ))
            return null;
        var num = thing.Position.DistanceTo(carryTarget.Cell);
        if (foundCell.DistanceTo(carryTarget.Cell) >= (double)num)
            return null;
        if (DebugViewSettings.drawOpportunisticJobs) {
            pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, 600);
            pawn.Map.debugDrawer.FlashLine(thing.Position, carryTarget.Cell, 600, (SimpleColor)4);
            pawn.Map.debugDrawer.FlashLine(thing.Position, foundCell, 600, (SimpleColor)6);
            pawn.Map.debugDrawer.FlashLine(foundCell, carryTarget.Cell, 600, (SimpleColor)6);
        }
        var job = PuahJob(new PuahBeforeCarry(carryTarget, foundCell), pawn, thing, foundCell);
        if (job != null)
            return job;
        specialHauls.SetOrAdd(pawn, new SpecialHaul("HaulBeforeCarry_LoadReport", carryTarget));
        return HaulAIUtility.HaulToCellStorageJob(pawn, thing, foundCell, false);
    }

    public Mod(ModContentPack content)
        : base(content) {
        mod = this;
        Gui.modId = "CodeOptimist.WhileYoureUp";
        settings = GetSettings<Settings>();
        if (!foundConfig)
            ((ModSettings)settings).ExposeData();
        harmony.PatchAll();
    }

    static bool Original(object _ = null) => true;

    static bool Skip(object _ = null) => false;

    public override string SettingsCategory() => mod.Content.Name;

    static Job PuahJob(
        PuahWithBetterUnloading puah,
        Pawn pawn,
        Thing thing,
        IntVec3 storeCell
    ) {
        if (!havePuah || !settings.UsePickUpAndHaulPlus || !settings.Enabled)
            return null;
        specialHauls.SetOrAdd(pawn, puah);
        puah.TrackThing(thing, storeCell);
        var worker = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker;
        return (Job)PuahJobOnThing.Invoke(
            worker,
            new object[3]
            {
                pawn,
                thing,
                false
            }
        );
    }

    public static bool AlreadyHauling(Pawn pawn) {
        if (specialHauls.ContainsKey(pawn))
            return true;
        if (havePuah) {
            var source = Traverse.Create((ThingComp)PuahGetCompHauledToInventory.Invoke(pawn, null))
                .Field<HashSet<Thing>>("takenToInventory").Value;
            if (source != null && source.Any(t => t != null))
                return true;
        }
        return false;
    }

    public static bool TryFindBestBetterStoreCellFor_ClosestToTarget(
        Thing thing,
        LocalTargetInfo opportunity,
        LocalTargetInfo beforeCarry,
        Pawn pawn,
        Map map,
        StoragePriority currentPriority,
        Faction faction,
        out IntVec3 foundCell,
        bool needAccurateResult,
        HashSet<IntVec3> skipCells = null
    ) {
        var intVec3_1 = IntVec3.Invalid;
        float num1 = int.MaxValue;
        var storagePriority = currentPriority;
        foreach (var slotGroup in map.haulDestinationManager.AllGroupsListInPriorityOrder) {
            if (slotGroup.Settings.Priority >= storagePriority) {
                if (slotGroup.Settings.Priority >= currentPriority) {
                    if (slotGroup.Settings.Priority != null) {
                        if (slotGroup.Settings.Priority == currentPriority) {
                            if (!beforeCarry.IsValid)
                                break;
                        }
                        var parent1 = slotGroup.parent as Zone_Stockpile;
                        var parent2 = slotGroup.parent as Building_Storage;
                        if (!opportunity.IsValid || (parent1 == null || settings.Opportunity_ToStockpiles) &&
                            (parent2 == null || settings.Opportunity_BuildingFilter.Allows(parent2.def))) {
                            IntVec3 intVec3_2;
                            if (beforeCarry.IsValid) {
                                if (!settings.HaulBeforeCarry_ToEqualPriority) {
                                    if (slotGroup.Settings.Priority == currentPriority)
                                        break;
                                }
                                if (settings.HaulBeforeCarry_ToEqualPriority) {
                                    intVec3_2 = thing.Position;
                                    if (intVec3_2.IsValid &&
                                        slotGroup == map.haulDestinationManager.SlotGroupAt(thing.Position))
                                        continue;
                                }
                                if (parent1 != null && !settings.HaulBeforeCarry_ToStockpiles || parent2 != null &&
                                    (!settings.HaulBeforeCarry_BuildingFilter.Allows(parent2.def) ||
                                        slotGroup.Settings.Priority == currentPriority &&
                                        !settings.Opportunity_BuildingFilter.Allows(parent2.def)))
                                    continue;
                            }
                            if (slotGroup.parent.Accepts(thing)) {
                                IntVec3 intVec3_3 = opportunity.IsValid
                                    ? opportunity.Cell
                                    : (
                                        beforeCarry.IsValid ? beforeCarry.Cell
                                        : thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld
                                        : pawn.PositionHeld
                                    );
                                var num2 = needAccurateResult ? (int)Math.Floor(
                                    slotGroup.CellsList.Count * (double)Rand.Range(0.005f, 0.018f)
                                ) : 0;
                                for (var index = 0; index < slotGroup.CellsList.Count; ++index) {
                                    var cells = slotGroup.CellsList[index];
                                    intVec3_2 = intVec3_3 - cells;
                                    var horizontalSquared = (float)intVec3_2.LengthHorizontalSquared;
                                    if (horizontalSquared <= (double)num1 &&
                                        (skipCells == null || !skipCells.Contains(cells)) &&
                                        StoreUtility.IsGoodStoreCell(cells, map, thing, pawn, faction)) {
                                        intVec3_1 = cells;
                                        num1 = horizontalSquared;
                                        storagePriority = slotGroup.Settings.Priority;
                                        if (index >= num2)
                                            break;
                                    }
                                }
                            }
                        }
                    }
                    else
                        break;
                }
                else
                    break;
            }
            else
                break;
        }
        foundCell = intVec3_1;
        if (foundCell.IsValid && skipCells != null)
            skipCells.Add(foundCell);
        return foundCell.IsValid;
    }

    public override void DoSettingsWindowContents(Rect inRect) => SettingsWindow.DoWindowContents(inRect);

    static Mod() {
        var methodInfo1 = AccessTools.DeclaredMethod(
            PuahWorkGiver_HaulToInventoryType,
            "AllocateThingAtStoreTarget"
        );
        if ((object)methodInfo1 == null)
            methodInfo1 = AccessTools.DeclaredMethod(
                PuahWorkGiver_HaulToInventoryType,
                "AllocateThingAtCell"
            );
        PuahAllocateThingAt = methodInfo1;
        PuahSkipCellsField = AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells");
        havePuah = new List<object>()
        {
            PuahCompHauledToInventoryType,
            PuahWorkGiver_HaulToInventoryType,
            PuahJobDriver_HaulToInventoryType,
            PuahJobDriver_UnloadYourHauledInventoryType,
            PuahHasJobOnThing,
            PuahJobOnThing,
            PuahTryFindBestBetterStoreCellFor,
            PuahFirstUnloadableThing,
            PuahMakeNewToils,
            PuahAllocateThingAt,
            PuahSkipCellsField
        }.All(x => x != null);
        MethodInfo methodInfo2;
        if (!havePuah)
            methodInfo2 = null;
        else
            methodInfo2 = AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp")
                .MakeGenericMethod(PuahCompHauledToInventoryType);
        PuahGetCompHauledToInventory = methodInfo2;
        HugsDialog_VanillaModSettingsType =
            GenTypes.GetTypeInAnyAssembly("HugsLib.Settings.Dialog_VanillaModSettings");
        haveHugs = HugsDialog_VanillaModSettingsType != null;
        SettingsCurMod = haveHugs ? AccessTools.DeclaredField(HugsDialog_VanillaModSettingsType, "selectedMod") :
            AccessTools.DeclaredField(typeof(Dialog_ModSettings), nameof(mod));
        CsModType = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        CsSettingsType = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        CsHaulingOverBillsSetting = AccessTools.DeclaredField(CsSettingsType, "hauling_over_bills");
        haveCommonSense = new List<object>()
        {
            CsModType,
            CsSettingsType,
            CsHaulingOverBillsSetting
        }.All(x => x != null);
        specialHauls = new Dictionary<Pawn, SpecialHaul>();
    }

    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    static class WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> _HaulBeforeSupply(
            IEnumerable<CodeInstruction> _codes,
            ILGenerator generator,
            MethodBase __originalMethod
        ) {
            var transpiler = new Transpiler(_codes, __originalMethod);
            var afterNearbyIdx = transpiler.TryFindCodeIndex(
                code => code.Calls(
                    AccessTools.DeclaredMethod(
                        typeof(WorkGiver_ConstructDeliverResources),
                        "FindAvailableNearbyResources"
                    )
                )
            );
            ++afterNearbyIdx;
            var foundResIdx = transpiler.TryFindCodeLastIndex(afterNearbyIdx, code => code.opcode == OpCodes.Brfalse) +
                1;
            var afterNearby = generator.DefineLabel();
            transpiler.codes[afterNearbyIdx].labels.Add(afterNearby);
            var needField = AccessTools.FindIncludingInnerTypes(
                typeof(WorkGiver_ConstructDeliverResources),
                ty => AccessTools.DeclaredField(ty, "need")
            );
            var resourcesAvailableField = AccessTools.Field(
                typeof(WorkGiver_ConstructDeliverResources),
                "resourcesAvailable"
            );
            var needDeclaringObjIdx = transpiler.TryFindCodeIndex(
                code => code.Is(OpCodes.Newobj, AccessTools.DeclaredConstructor(needField.DeclaringType))
            );
            var codeIndex = transpiler.TryFindCodeIndex(afterNearbyIdx, code => code.opcode == OpCodes.Leave);
            var jobVar = transpiler.codes[codeIndex - 1].operand;
            transpiler.TryInsertCodes(
                0,
                (i, codes) => i == afterNearbyIdx,
                (i, codes) => {
                    var codeInstructionList = new List<CodeInstruction>();
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    codeInstructionList.Add(new CodeInstruction(codes[needDeclaringObjIdx + 2].opcode));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldfld, needField));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldsfld, resourcesAvailableField));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldarg_2));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Castclass, typeof(Thing)));
                    codeInstructionList.Add(codes[foundResIdx + 1].Clone());
                    codeInstructionList.Add(codes[foundResIdx + 2].Clone());
                    codeInstructionList.Add(
                        new CodeInstruction(
                            OpCodes.Call,
                            AccessTools.DeclaredMethod(
                                typeof(WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch),
                                nameof(HaulBeforeSupply)
                            )
                        )
                    );
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Stloc_S, jobVar));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldloc_S, jobVar));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Brfalse_S, afterNearby));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldloc_S, jobVar));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ret));
                    return codeInstructionList;
                }
            );
            return transpiler.GetFinalCodes();
        }

        static Job HaulBeforeSupply(
            Pawn pawn,
            ThingDefCountClass need,
            List<Thing> resourcesAvailable,
            Thing constructible,
            Thing th
        ) {
            if (!settings.HaulBeforeCarry_Supplies || !settings.Enabled || AlreadyHauling(pawn))
                return null;
            if (pawn.WorkTagIsDisabled((WorkTags)1064962))
                return null;
            var thing = resourcesAvailable.DefaultIfEmpty().MaxBy(x => x.stackCount);
            return (!havePuah || !settings.UsePickUpAndHaulPlus) && thing.stackCount <= need.count ? null :
                JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(
                    pawn,
                    HaulBeforeCarry(pawn, constructible.Position, thing ?? th)
                );
        }
    }

    public class PuahBeforeCarry : PuahWithBetterUnloading {
        public LocalTargetInfo carryTarget;
        public IntVec3 storeCell;

        public PuahBeforeCarry(LocalTargetInfo carryTarget, IntVec3 storeCell) {
            this.carryTarget = carryTarget;
            this.storeCell = storeCell;
        }

        public override string GetLoadReport(string text) => "HaulBeforeCarry_LoadReport".ModTranslate(
            text.Named("ORIGINAL"),
            carryTarget.Label.Named("DESTINATION")
        );

        public override string GetUnloadReport(string text) => "HaulBeforeCarry_UnloadReport".ModTranslate(
            text.Named("ORIGINAL"),
            carryTarget.Label.Named("DESTINATION")
        );
    }

    static class Patch_PUAH {
        static StorageSettings reducedPriorityStore;
        static readonly List<Thing> thingsInReducedPriorityStore = new();
        static readonly List<MethodBase> callStack = new();
        static readonly Dictionary<Thing, IntVec3> cachedStoreCells = new();

        static void PushMethod(MethodBase method) => callStack.Add(method);

        static void PopMethod() {
            if (!callStack.Any())
                return;
            callStack.Pop();
            if (callStack.Any())
                return;
            cachedStoreCells.Clear();
        }

        [HarmonyPatch]
        static class StorageSettings_Priority_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => AccessTools.DeclaredPropertyGetter(typeof(StorageSettings), "Priority");

            [HarmonyPostfix]
            static void GetReducedPriority(
                StorageSettings __instance,
                ref StoragePriority __result
            ) {
                if (__instance != reducedPriorityStore || __result <= 0)
                    return;
                __result -= 1;
            }
        }

        [HarmonyPatch]
        static class WorkGiver_HaulToInventory__JobOnThing_Patch {
            [HarmonyPrefix]
            static void HaulToEqualPriority(Pawn pawn, Thing thing) {
                if (!settings.HaulBeforeCarry_ToEqualPriority || !settings.UsePickUpAndHaulPlus || !settings.Enabled ||
                    !(specialHauls.GetValueSafe(pawn) is PuahBeforeCarry))
                    return;
                var ihaulDestination = StoreUtility.CurrentHaulDestinationOf(thing);
                if (ihaulDestination == null)
                    return;
                reducedPriorityStore = ihaulDestination.GetStoreSettings();
                thingsInReducedPriorityStore.AddRange(
                    thing.GetSlotGroup().CellsList.SelectMany(
                        cell => cell.GetThingList(thing.Map).Where(cellThing => cellThing.def.EverHaulable)
                    )
                );
                thing.Map.haulDestinationManager.Notify_HaulDestinationChangedPriority();
            }

            [HarmonyPostfix]
            static void HaulToEqualPriorityCleanup() {
                if (reducedPriorityStore?.owner is IHaulDestination haulDestination) {
                        var map = haulDestination.Map;
                        map?.haulDestinationManager.Notify_HaulDestinationChangedPriority();
                }
                reducedPriorityStore = null;
                thingsInReducedPriorityStore.Clear();
            }

            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => PuahJobOnThing;

            [HarmonyPriority(600)]
            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);

            [HarmonyPriority(200)]
            static void Postfix() => PopMethod();

            [HarmonyPostfix]
            static void TrackInitialHaul(
                WorkGiver_Scanner __instance,
                Job __result,
                Pawn pawn,
                Thing thing
            ) {
                if (__result == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled)
                    return;
                if (!(specialHauls.GetValueSafe(pawn) is PuahWithBetterUnloading withBetterUnloading)) {
                    withBetterUnloading = new PuahWithBetterUnloading();
                    specialHauls.SetOrAdd(pawn, withBetterUnloading);
                }
                withBetterUnloading.TrackThing(thing, __result.targetB.Cell, true, callerName:
                nameof(TrackInitialHaul));
            }
        }

        [HarmonyPatch]
        static class ListerHaulables_ThingsPotentiallyNeedingHauling_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
                typeof(ListerHaulables),
                "ThingsPotentiallyNeedingHauling"
            );

            [HarmonyPostfix]
            static void IncludeThingsInReducedPriorityStore(ref List<Thing> __result) {
                if (thingsInReducedPriorityStore.NullOrEmpty())
                    return;
                __result.AddRange(thingsInReducedPriorityStore);
            }
        }

        [HarmonyPatch]
        static class WorkGiver_HaulToInventory__HasJobOnThing_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => PuahHasJobOnThing;

            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);

            static void Postfix() => PopMethod();
        }

        [HarmonyPatch]
        static class WorkGiver_HaulToInventory__AllocateThingAtCell_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => PuahAllocateThingAt;

            static void Prefix(MethodBase __originalMethod) => PushMethod(__originalMethod);

            static void Postfix() => PopMethod();
        }

        [HarmonyPatch]
        static class WorkGiver_HaulToInventory__TryFindBestBetterStoreCellFor_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => PuahTryFindBestBetterStoreCellFor;

            [HarmonyPrefix]
            static bool UseSpecialHaulAwareTryFindStore(
                ref bool __result,
                Thing thing,
                Pawn carrier,
                Map map,
                StoragePriority currentPriority,
                Faction faction,
                ref IntVec3 foundCell
            ) {
                if (!settings.UsePickUpAndHaulPlus || !settings.Enabled)
                    return Original();
                __result = StoreUtility.TryFindBestBetterStoreCellFor(
                    thing,
                    carrier,
                    map,
                    currentPriority,
                    faction,
                    out foundCell
                );
                return Skip();
            }
        }

        [HarmonyPriority(500)]
        [HarmonyPatch]
        static class StoreUtility__TryFindBestBetterStoreCellFor_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
                typeof(StoreUtility),
                "TryFindBestBetterStoreCellFor"
            );

            static readonly FieldInfo _cachedMaterialsNeeded = AccessTools.Field(
                typeof(Frame),
                "cachedMaterialsNeeded"
            );

            [HarmonyPrefix]
            static bool SpecialHaulAwareTryFindStore(
                ref bool __result,
                Thing t,
                Pawn carrier,
                Map map,
                StoragePriority currentPriority,
                Faction faction,
                out IntVec3 foundCell,
                bool needAccurateResult
            ) {
                foundCell = IntVec3.Invalid;
                if (carrier == null || !settings.UsePickUpAndHaulPlus || !settings.Enabled)
                    return Original();
                var flag1 = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
                if (!callStack.Any() && !flag1)
                    return Original();
                var intVec3Set = (HashSet<IntVec3>)PuahSkipCellsField.GetValue(null);
                var flag2 = callStack.Contains(PuahAllocateThingAt);
                var flag3 = !flag1;
                if (flag3) {
                    if (cachedStoreCells.Count == 0)
                        cachedStoreCells.AddRange(Opportunity.cachedStoreCells);
                    if (!cachedStoreCells.TryGetValue(t, out foundCell))
                        foundCell = IntVec3.Invalid;
                    if (foundCell.IsValid & flag2) {
                        if (intVec3Set.Contains(foundCell))
                            foundCell = IntVec3.Invalid;
                        else
                            intVec3Set.Add(foundCell);
                    }
                }
                var withBetterUnloading = specialHauls.GetValueSafe(carrier) as PuahWithBetterUnloading;
                var puahOpportunity = withBetterUnloading as PuahOpportunity;
                var puahBeforeCarry = withBetterUnloading as PuahBeforeCarry;
                var opportunity = puahOpportunity?.jobTarget ?? IntVec3.Invalid;
                var beforeCarry = puahBeforeCarry?.carryTarget ?? IntVec3.Invalid;
                if (!foundCell.IsValid && !TryFindBestBetterStoreCellFor_ClosestToTarget(
                        t,
                        opportunity,
                        beforeCarry,
                        carrier,
                        map,
                        currentPriority,
                        faction,
                        out foundCell,
                        !callStack.Contains(PuahHasJobOnThing) && (opportunity.IsValid || beforeCarry.IsValid) &&
                        Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
                        flag2 ? intVec3Set : null
                    ))
                    return Skip(__result = false);
                if (flag3)
                    cachedStoreCells.SetOrAdd(t, foundCell);
                if (flag1)
                    return Skip(__result = true);
                if (puahOpportunity != null && !puahOpportunity.TrackThingIfOpportune(t, carrier, ref foundCell))
                    return Skip(__result = false);
                if (puahBeforeCarry != null) {
                    var slotGroup = foundCell.GetSlotGroup(map);
                    if (slotGroup != puahBeforeCarry.storeCell.GetSlotGroup(map))
                        return Skip(__result = false);
                    var priority1 = slotGroup.Settings.Priority;
                    var priority2 = t.Position.GetSlotGroup(map)?.Settings?.Priority;
                    var valueOrDefault = priority2.GetValueOrDefault();
                    if (priority1 == valueOrDefault & priority2.HasValue) {
                        if (carrier.CurJobDef == JobDefOf.HaulToContainer &&
                            carrier.CurJob.targetC.Thing is Frame thing &&
                            ((List<ThingDefCountClass>)_cachedMaterialsNeeded.GetValue(thing)).Select(x => x.thingDef).Contains(t.def))
                            return Skip(__result = false);
                        if (carrier.CurJobDef == JobDefOf.DoBill &&
                            !carrier.CurJob.targetQueueB.Select(x => x.Thing?.def).Contains(t.def))
                            return Skip(__result = false);
                    }
                }
                if (callStack.Contains(PuahAllocateThingAt)) {
                    if (withBetterUnloading == null) {
                        withBetterUnloading = new PuahWithBetterUnloading();
                        specialHauls.SetOrAdd(carrier, withBetterUnloading);
                    }
                    withBetterUnloading.TrackThing(t, foundCell);
                }
                return Skip(__result = true);
            }
        }

        [HarmonyPatch]
        static class JobDriver_UnloadYourHauledInventory__FirstUnloadableThing_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => PuahFirstUnloadableThing;

            [HarmonyPrefix]
            static bool SpecialHaulAwareFirstUnloadableThing(ref ThingCount __result, Pawn pawn) {
                if (!settings.UsePickUpAndHaulPlus || !settings.Enabled)
                    return Original();
                var thingSet = Traverse.Create((ThingComp)PuahGetCompHauledToInventory.Invoke(pawn, null))
                    .Method("GetHashSet", Array.Empty<object>()).GetValue<HashSet<Thing>>();
                if (!thingSet.Any()) {
                    ref var local = ref __result;
                    var thingCount1 = new ThingCount();
                    ThingCount thingCount2;
                    var _ = thingCount2 = thingCount1;
                    local = thingCount2;
                    return Skip(_);
                }
                var puah = specialHauls.GetValueSafe(pawn) as PuahWithBetterUnloading;
                if (puah == null) {
                    puah = new PuahWithBetterUnloading();
                    specialHauls.SetOrAdd(pawn, puah);
                }
                // ISSUE: explicit reference operation
                var tuple = thingSet.Select(t => GetDefHaul(puah, t))
                    .Where(x => x.storeCell.IsValid)
                    .DefaultIfEmpty()
                    .MinBy(x => x.storeCell.DistanceTo(pawn.Position));
                // ISSUE: explicit reference operation
                var closestSlotGroup = tuple.Item2.IsValid ? tuple.Item2.GetSlotGroup(pawn.Map) : null;
                // ISSUE: explicit reference operation
                var firstThingToUnload = closestSlotGroup == null
                    ? tuple.Item1
                    : thingSet.Select(t => GetDefHaul(puah, t))
                        .Where(x => x.storeCell.IsValid && x.storeCell.GetSlotGroup(pawn.Map) == closestSlotGroup)
                        .DefaultIfEmpty()
                        .MinBy(x => (x.thing.def.FirstThingCategory?.index, x.thing.def.defName)).Item1;
                if (firstThingToUnload == null)
                    firstThingToUnload =
                        thingSet.MinBy<Thing, (ushort?, string)>(t => (t.def.FirstThingCategory?.index, t.def.defName));
                if (!thingSet.Intersect(pawn.inventory.innerContainer).Contains(firstThingToUnload)) {
                    thingSet.Remove(firstThingToUnload);
                    var thing = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == firstThingToUnload.def);
                    if (thing != null)
                        return Skip(__result = new ThingCount(thing, thing.stackCount));
                }
                return Skip(__result = new ThingCount(firstThingToUnload, firstThingToUnload.stackCount));

                (Thing thing, IntVec3 storeCell) GetDefHaul(
                    PuahWithBetterUnloading puah_,
                    Thing thing
                ) {
                    IntVec3 foundCell;
                    if (puah_.defHauls.TryGetValue(thing.def, out foundCell))
                        return (thing, foundCell);
                    var suitableDefHaul = TryFindBestBetterStoreCellFor_ClosestToTarget(
                        thing,
                        puah_ is PuahOpportunity puahOpportunity
                            ? puahOpportunity.jobTarget
                            : IntVec3.Invalid,
                        puah_ is PuahBeforeCarry puahBeforeCarry
                            ? puahBeforeCarry.carryTarget
                            : IntVec3.Invalid,
                        pawn,
                        pawn.Map,
                        StoreUtility.CurrentStoragePriorityOf(thing),
                        pawn.Faction,
                        out foundCell,
                        false
                    );
                    if (suitableDefHaul)
                        puah_.defHauls.Add(thing.def, foundCell);
                    return (thing, foundCell);
                }
            }
        }

        [HarmonyPatch]
        static class JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => PuahMakeNewToils;

            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) =>
                __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
        }

        [HarmonyPatch]
        static class JobDriver__GetReport_Patch {
            static bool Prepare() => havePuah;

            static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), "GetReport");

            [HarmonyPostfix]
            static void SpecialHaulGetReport(JobDriver __instance, ref string __result) {
                if (!settings.UsePickUpAndHaulPlus || !settings.Enabled)
                    return;
                if (PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance) &&
                    specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading valueSafe1) {
                    ref var local = ref __result;
                    var text = __result.TrimEnd('.');
                    var loadReport = valueSafe1.GetLoadReport(text);
                    local = loadReport;
                }
                if (!PuahJobDriver_UnloadYourHauledInventoryType.IsInstanceOfType(__instance) ||
                    !(specialHauls.GetValueSafe(__instance.pawn) is PuahWithBetterUnloading valueSafe2))
                    return;
                ref var local1 = ref __result;
                var text1 = __result.TrimEnd('.');
                var unloadReport = valueSafe2.GetUnloadReport(text1);
                local1 = unloadReport;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "TryOpportunisticJob")]
    static class Pawn_JobTracker__TryOpportunisticJob_Patch {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> _TryOpportunisticJob(
            IEnumerable<CodeInstruction> _codes,
            ILGenerator generator,
            MethodBase __originalMethod
        ) {
            var transpiler = new Transpiler(_codes, __originalMethod);
            var listerHaulablesIdx = transpiler.TryFindCodeIndex(
                code => code.LoadsField(AccessTools.DeclaredField(typeof(Map), "listerHaulables"))
            );
            var skipMod = generator.DefineLabel();
            transpiler.TryInsertCodes(
                -3,
                (i, codes) => i == listerHaulablesIdx,
                (i, codes) => {
                    var codeInstructionList = new List<CodeInstruction>();
                    codeInstructionList.Add(
                        new CodeInstruction(
                            OpCodes.Call,
                            AccessTools.DeclaredMethod(
                                typeof(Pawn_JobTracker__TryOpportunisticJob_Patch),
                                "IsEnabled"
                            )
                        )
                    );
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Brfalse_S, skipMod));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ldarg_2));
                    codeInstructionList.Add(
                        new CodeInstruction(
                            OpCodes.Call,
                            AccessTools.DeclaredMethod(
                                typeof(Pawn_JobTracker__TryOpportunisticJob_Patch),
                                "TryOpportunisticJob"
                            )
                        )
                    );
                    codeInstructionList.Add(new CodeInstruction(OpCodes.Ret));
                    return codeInstructionList;
                },
                true
            );
            transpiler.codes[transpiler.MatchIdx - 3].labels.Add(skipMod);
            return transpiler.GetFinalCodes();
        }

        static bool IsEnabled() => settings.Enabled;

        static Job TryOpportunisticJob(Pawn_JobTracker jobTracker, Job job) {
            var pawn = Traverse.Create(jobTracker).Field("pawn").GetValue<Pawn>();
            if (AlreadyHauling(pawn))
                return null;
            if (job.def == JobDefOf.DoBill && settings.HaulBeforeCarry_Bills) {
                for (var index = 0; index < job.targetQueueB.Count; ++index) {
                    var localTargetInfo = job.targetQueueB[index];
                    if (localTargetInfo.Thing != null &&
                        (havePuah && settings.UsePickUpAndHaulPlus ||
                            localTargetInfo.Thing.stackCount > job.countQueue[index]) &&
                        HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, localTargetInfo.Thing, false)) {
                        Job job1 = Mod.HaulBeforeCarry(pawn, job.targetA, localTargetInfo.Thing);
                        if (job1 != null)
                            return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(
                                pawn,
                                job1
                            );
                    }
                }
            }
            if (new JobDef[4]
                {
                    JobDefOf.PrepareCaravan_CollectAnimals,
                    JobDefOf.PrepareCaravan_GatherAnimals,
                    JobDefOf.PrepareCaravan_GatherDownedPawns,
                    JobDefOf.PrepareCaravan_GatherItems
                }.Contains(job.def))
                return null;
            if (pawn.health.hediffSet.BleedRateTotal > 1.0 / 1000.0)
                return null;
            LocalTargetInfo localTargetInfo1;
            if (job.def != JobDefOf.DoBill) {
                localTargetInfo1 = job.targetA;
            }
            else {
                var targetQueueB = job.targetQueueB;
                localTargetInfo1 = targetQueueB != null ? targetQueueB.FirstOrDefault() : job.targetA;
            }
            var jobTarget = localTargetInfo1;
            return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(
                pawn,
                Opportunity.TryHaul(pawn, jobTarget)
            );
        }
    }

    static class Opportunity {
        public static readonly Dictionary<Thing, IntVec3> cachedStoreCells = new();

        public static Job TryHaul(Pawn pawn, LocalTargetInfo jobTarget) {
            var job = _TryHaul();
            cachedStoreCells.Clear();
            return job;

            Job _TryHaul() {
                var maxRanges = new MaxRanges()
                {
                    startToThing = settings.Opportunity_MaxStartToThing,
                    startToThingPctOrigTrip = settings.Opportunity_MaxStartToThingPctOrigTrip,
                    storeToJob = settings.Opportunity_MaxStoreToJob,
                    storeToJobPctOrigTrip = settings.Opportunity_MaxStoreToJobPctOrigTrip
                };
                var index = 0;
                var thingList = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                while (thingList.Count > 0) {
                    if (index == thingList.Count) {
                        maxRanges *= 2;
                        index = 0;
                    }
                    var thing = thingList[index];
                    IntVec3 storeCell;
                    switch (CanHaul(pawn, thing, jobTarget, maxRanges, out storeCell)) {
                        case CanHaulResult.RangeFail:
                            if (settings.Opportunity_PathChecker != Settings.PathCheckerEnum.Vanilla) {
                                ++index;
                                continue;
                            }
                            goto case CanHaulResult.HardFail;
                        case CanHaulResult.HardFail:
                            thingList.RemoveAt(index);
                            continue;
                        case CanHaulResult.Success:
                            if (DebugViewSettings.drawOpportunisticJobs) {
                                pawn.Map.debugDrawer.FlashLine(pawn.Position, jobTarget.Cell, 600, (SimpleColor)1);
                                pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, 600, (SimpleColor)2);
                                pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell, 600, (SimpleColor)2);
                                pawn.Map.debugDrawer.FlashLine(storeCell, jobTarget.Cell, 600, (SimpleColor)2);
                            }
                            var job = PuahJob(new PuahOpportunity(pawn, jobTarget), pawn, thing, storeCell);
                            if (job != null)
                                return job;
                            specialHauls.SetOrAdd(pawn, new SpecialHaul("Opportunity_LoadReport", jobTarget));
                            return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                        default:
                            continue;
                    }
                }
                return null;
            }
        }

        static CanHaulResult CanHaul(
            Pawn pawn,
            Thing thing,
            LocalTargetInfo jobTarget,
            MaxRanges maxRanges,
            out IntVec3 storeCell
        ) {
            storeCell = IntVec3.Invalid;
            float num1 = pawn.Position.DistanceTo(jobTarget.Cell);
            var num2 = pawn.Position.DistanceTo(thing.Position);
            if (num2 > (double)maxRanges.startToThing || num2 > num1 * (double)maxRanges.startToThingPctOrigTrip)
                return CanHaulResult.RangeFail;
            float num3 = thing.Position.DistanceTo(jobTarget.Cell);
            if (num2 + (double)num3 > num1 * (double)settings.Opportunity_MaxTotalTripPctOrigTrip ||
                pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) !=
                null || thing.IsForbidden(pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false))
                return CanHaulResult.HardFail;
            var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
            if (!cachedStoreCells.TryGetValue(thing, out storeCell) && !TryFindBestBetterStoreCellFor_ClosestToTarget(
                    thing,
                    jobTarget,
                    IntVec3.Invalid,
                    pawn,
                    pawn.Map,
                    currentPriority,
                    pawn.Faction,
                    out storeCell,
                    maxRanges.expandCount == 0
                ))
                return CanHaulResult.HardFail;
            cachedStoreCells.SetOrAdd(thing, storeCell);
            float num4 = storeCell.DistanceTo(jobTarget.Cell);
            if (num4 > (double)maxRanges.storeToJob || num4 > num1 * (double)maxRanges.storeToJobPctOrigTrip)
                return CanHaulResult.RangeFail;
            if (num2 + (double)num4 > num1 * (double)settings.Opportunity_MaxNewLegsPctOrigTrip)
                return CanHaulResult.HardFail;
            var num5 = thing.Position.DistanceTo(storeCell);
            if (num2 + (double)num5 + num4 > num1 * (double)settings.Opportunity_MaxTotalTripPctOrigTrip)
                return CanHaulResult.HardFail;
            if (settings.Opportunity_PathChecker == Settings.PathCheckerEnum.Pathfinding) {
                var pathCost1 = GetPathCost(pawn.Position, thing, (PathEndMode)3);
                if (pathCost1 == 0.0)
                    return CanHaulResult.HardFail;
                var pathCost2 = GetPathCost(storeCell, jobTarget, (PathEndMode)2);
                if (pathCost2 == 0.0)
                    return CanHaulResult.HardFail;
                var pathCost3 = GetPathCost(pawn.Position, jobTarget, (PathEndMode)2);
                if (pathCost3 == 0.0 || pathCost1 + (double)pathCost2 >
                    pathCost3 * (double)settings.Opportunity_MaxNewLegsPctOrigTrip)
                    return CanHaulResult.HardFail;
                var pathCost4 = GetPathCost(thing.Position, storeCell, (PathEndMode)3);
                if (pathCost4 == 0.0 || pathCost1 + (double)pathCost4 + pathCost2 >
                    pathCost3 * (double)settings.Opportunity_MaxTotalTripPctOrigTrip)
                    return CanHaulResult.HardFail;
            }
            else if (maxRanges.expandCount == 0 &&
                     (!pawn.Position.WithinRegions(
                         thing.Position,
                         pawn.Map,
                         settings.Opportunity_MaxStartToThingRegionLookCount,
                         TraverseParms.For(pawn)
                     ) || !storeCell.WithinRegions(
                         jobTarget.Cell,
                         pawn.Map,
                         settings.Opportunity_MaxStoreToJobRegionLookCount,
                         TraverseParms.For(pawn)
                     )))
                return CanHaulResult.RangeFail;
            return CanHaulResult.Success;

            float GetPathCost(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode) {
                var path = pawn.Map.pathFinder.FindPath(
                    start,
                    dest,
                    TraverseParms.For(pawn),
                    peMode
                );
                var totalCost = path.TotalCost;
                path.ReleaseToPool();
                return totalCost;
            }
        }

        enum CanHaulResult {
            RangeFail,
            HardFail,
            Success,
        }

        struct MaxRanges {
            public int expandCount;
            public float startToThing;
            public float startToThingPctOrigTrip;
            public float storeToJob;
            public float storeToJobPctOrigTrip;

            public static MaxRanges operator *(
                MaxRanges maxRanges,
                int multiplier
            ) {
                ++maxRanges.expandCount;
                maxRanges.startToThing *= multiplier;
                maxRanges.startToThingPctOrigTrip *= multiplier;
                maxRanges.storeToJob *= multiplier;
                maxRanges.storeToJobPctOrigTrip *= multiplier;
                return maxRanges;
            }
        }
    }

    public class PuahOpportunity : PuahWithBetterUnloading {
        public LocalTargetInfo jobTarget;
        public IntVec3 startCell;
        public List<(Thing thing, IntVec3 storeCell)> hauls = new();

        public PuahOpportunity(Pawn pawn, LocalTargetInfo jobTarget) {
            startCell = pawn.Position;
            this.jobTarget = jobTarget;
        }

        public override string GetLoadReport(string text) => "Opportunity_LoadReport".ModTranslate(
            text.Named("ORIGINAL"),
            jobTarget.Label.Named("DESTINATION")
        );

        public override string GetUnloadReport(string text) => "Opportunity_UnloadReport".ModTranslate(
            text.Named("ORIGINAL"),
            jobTarget.Label.Named("DESTINATION")
        );

        public bool TrackThingIfOpportune(Thing thing, Pawn pawn, ref IntVec3 foundCell) {
            var prepend = pawn.carryTracker?.CarriedThing == thing;
            TrackThing(thing, foundCell, prepend, false);
            var intVec3 = startCell;
            var num1 = 0.0f;
            foreach ((var thing1, var _) in hauls) {
                num1 += intVec3.DistanceTo(thing1.Position);
                intVec3 = thing1.Position;
            }
            var byUnloadDistance = GetHaulsByUnloadDistance();
            var num2 = intVec3.DistanceTo(byUnloadDistance.First().storeCell);
            var storeCell = byUnloadDistance.First().storeCell;
            var num3 = 0.0f;
            foreach (var tuple in byUnloadDistance) {
                num3 += storeCell.DistanceTo(tuple.storeCell);
                storeCell = tuple.storeCell;
            }
            float num4 = storeCell.DistanceTo(jobTarget.Cell);
            var num5 = (double)startCell.DistanceTo(jobTarget.Cell);
            if (num1 + num2 + num3 + num4 > (double)((float)num5 * settings.Opportunity_MaxTotalTripPctOrigTrip) |
                num1 + num3 + num4 > (double)((float)num5 * settings.Opportunity_MaxNewLegsPctOrigTrip)) {
                foundCell = IntVec3.Invalid;
                hauls.RemoveAt(prepend ? 0 : hauls.Count - 1);
                return false;
            }
            defHauls.SetOrAdd(thing.def, foundCell);
            return true;

            List<(Thing thing, IntVec3 storeCell)> GetHaulsByUnloadDistance() {
                var valueTupleList = new List<(Thing, IntVec3)>();
                valueTupleList.Add(hauls.First<(Thing, IntVec3)>());
                var ordered = valueTupleList;
                var tupleList = new List<(Thing thing, IntVec3 storeCell)>(hauls.GetRange(1, hauls.Count - 1));
                while (tupleList.Count > 0) {
                    var valueTuple = tupleList.MinBy(
                        x => x.storeCell.DistanceTo(ordered.Last().Item2)
                    );
                    ordered.Add(valueTuple);
                    tupleList.Remove(valueTuple);
                }
                return ordered;
            }
        }
    }

    [HarmonyPatch]
    static class Dialog_ModSettings__Dialog_ModSettings_Patch {
        static MethodBase TargetMethod() => haveHugs ? AccessTools.DeclaredConstructor(
            HugsDialog_VanillaModSettingsType,
            new Type[1]
            {
                typeof(Mod)
            }
        ) : (MethodBase)AccessTools.DeclaredConstructor(
            typeof(Dialog_ModSettings),
            new Type[1]
            {
                typeof(Mod)
            }
        );

        [HarmonyPostfix]
        static void SyncDrawSettingToVanilla() => settings.DrawSpecialHauls = DebugViewSettings.drawOpportunisticJobs;
    }

    [HarmonyPatch]
    static class Dialog_ModSettings__DoWindowContents_Patch {
        static MethodBase TargetMethod() => haveHugs ?
            AccessTools.DeclaredMethod(HugsDialog_VanillaModSettingsType, "DoWindowContents") :
            (MethodBase)AccessTools.DeclaredMethod(typeof(Dialog_ModSettings), "DoWindowContents");

        [HarmonyPostfix]
        static void CheckCommonSenseSetting(object __instance) {
            var obj = SettingsCurMod.GetValue(__instance);
            if (!settings.HaulBeforeCarry_Bills || !haveCommonSense || !(bool)CsHaulingOverBillsSetting.GetValue(null))
                return;
            var mod = LoadedModManager.GetMod(CsModType);
            if (obj == Mod.mod) {
                CsHaulingOverBillsSetting.SetValue(null, false);
                mod.WriteSettings();
                Messages.Message(
                    "[" + Mod.mod.Content.Name +
                    "] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)",
                    MessageTypeDefOf.SilentInput,
                    false
                );
            }
            else {
                if (obj != mod)
                    return;
                settings.HaulBeforeCarry_Bills = false;
                Messages.Message(
                    "[" + Mod.mod.Content.Name +
                    "] Unticked setting in While You're Up: \"Haul extra bill ingredients closer\". (Can't use both.)",
                    MessageTypeDefOf.SilentInput,
                    false
                );
            }
        }
    }

    [HarmonyPatch(typeof(JobUtility), "TryStartErrorRecoverJob")]
    static class JobUtility__TryStartErrorRecoverJob_Patch {
        static int lastFrameCount;
        static Pawn lastPawn;
        static string lastCallerName;

        [HarmonyPrefix]
        static void OfferSupport(Pawn pawn) {
            if (RealTime.frameCount != lastFrameCount || pawn != lastPawn)
                return;
            Log.Warning(
                "[" + mod.Content.Name +
                "] You're welcome to 'Share logs' to my Discord: https://discord.gg/pnZGQAN \n[" + mod.Content.Name +
                "] Below \"10 jobs in one tick\" error occurred during " + lastCallerName +
                ", but could be from several mods."
            );
        }

        public static Job CatchStanding(Pawn pawn, Job job, [CallerMemberName] string callerName = "") {
            lastPawn = pawn;
            lastFrameCount = RealTime.frameCount;
            lastCallerName = callerName;
            return job;
        }
    }

    [HarmonyPatch(typeof(Log), "Error", typeof(string))]
    static class Log__Error_Patch {
        static bool ignoreLoadReferenceErrors;
        static LoadSaveMode scribeMode;

        public static void SuppressLoadReferenceErrors(Action action) {
            scribeMode = Scribe.mode;
            Scribe.mode = (LoadSaveMode)2;
            ignoreLoadReferenceErrors = true;
            try {
                action();
            }
            catch (Exception ex) {
                Restore();
                throw;
            }
            finally {
                Restore();
            }

            static void Restore() {
                ignoreLoadReferenceErrors = false;
                Scribe.mode = scribeMode;
            }
        }

        [HarmonyPrefix]
        static bool IgnoreCouldNotLoadReferenceOfRemovedModStorageBuildings(string text) =>
            ignoreLoadReferenceErrors && text.StartsWith("Could not load reference to ") ? Skip() : Original();
    }

    [StaticConstructorOnStartup]
    public static class SettingsWindow {
        static Vector2 opportunityScrollPosition;
        static Listing_SettingsTreeThingFilter opportunityTreeFilter;
        static readonly QuickSearchFilter opportunitySearchFilter = new();
        static readonly QuickSearchWidget opportunitySearchWidget = new();
        static readonly SettingsThingFilter opportunityDummyFilter = new();
        static Vector2 hbcScrollPosition;
        static Listing_SettingsTreeThingFilter hbcTreeFilter;
        static readonly QuickSearchFilter hbcSearchFilter = new();
        static readonly QuickSearchWidget hbcSearchWidget = new();
        static readonly SettingsThingFilter hbcDummyFilter = new();
        static readonly ThingCategoryDef storageBuildingCategoryDef;
        static readonly List<TabRecord> tabsList = new();
        static Tab tab = Tab.Opportunity;

        static SettingsWindow() {
            Log__Error_Patch.SuppressLoadReferenceErrors(
                () => {
                    settings.opportunityBuildingFilter = ScribeExtractor.SaveableFromNode<SettingsThingFilter>(
                        settings.opportunityBuildingFilterXmlNode,
                        null
                    );
                    settings.hbcBuildingFilter =
                        ScribeExtractor.SaveableFromNode<SettingsThingFilter>(settings.hbcBuildingFilterXmlNode, null);
                }
            );
            hbcSearchWidget.filter = hbcSearchFilter;
            var storageBuildingTypes = typeof(Building_Storage).AllSubclassesNonAbstract();
            storageBuildingTypes.Add(typeof(Building_Storage));
            storageBuildingCategoryDef = new ThingCategoryDef();
            var list = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(x => storageBuildingTypes.Contains(x.thingClass)).ToList();
            foreach (var modContentPack in list.Select(x => x.modContentPack).Distinct()) {
                var storageMod = modContentPack;
                if (storageMod != null) {
                    var thingCategoryDef1 = new ThingCategoryDef
                    {
                        label = storageMod.Name
                    };
                    var thingCategoryDef2 = thingCategoryDef1;
                    storageBuildingCategoryDef.childCategories.Add(thingCategoryDef2);
                    thingCategoryDef2.childThingDefs.AddRange(
                        list.Where(x => x.modContentPack == storageMod).Select(x => x)
                    );
                    thingCategoryDef2.PostLoad();
                    thingCategoryDef2.ResolveReferences();
                }
            }
            storageBuildingCategoryDef.PostLoad();
            storageBuildingCategoryDef.ResolveReferences();
            ResetFilters();
            if (settings.opportunityBuildingFilter == null) {
                settings.opportunityBuildingFilter = new SettingsThingFilter();
                settings.opportunityBuildingFilter?.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
            }
            if (settings.hbcBuildingFilter != null)
                return;
            settings.hbcBuildingFilter = new SettingsThingFilter();
            settings.hbcBuildingFilter?.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
        }

        public static void ResetFilters() {
            settings.opportunityDefaultBuildingFilter.SetAllowAll(null, true);
            settings.hbcDefaultBuildingFilter.SetDisallowAll();
            foreach (var childCategory in storageBuildingCategoryDef.childCategories) {
                var modCategoryDef = childCategory;
                modCategoryDef.treeNode.SetOpen(1, false);
                switch (LoadedModManager.RunningModsListForReading.FirstOrDefault(x => x.Name == modCategoryDef.label)
                            ?.PackageId) {
                    case "buddy1913.expandedstorageboxes":
                    case "im.skye.rimworld.deepstorageplus":
                    case "jangodsoul.simplestorage":
                    case "jangodsoul.simplestorage.ref":
                    case "mlie.displaycases":
                    case "mlie.eggincubator":
                    case "mlie.extendedstorage":
                    case "mlie.fireextinguisher":
                    case "mlie.functionalvanillaexpandedprops":
                    case "mlie.tobesdiningroom":
                    case "ogliss.thewhitecrayon.quarry":
                    case "primitivestorage.velcroboy333":
                    case "proxyer.smallshelf":
                    case "rimfridge.kv.rw":
                    case "sixdd.littlestorage2":
                    case "skullywag.extendedstorage":
                    case "solaris.furniturebase":
                    case "vanillaexpanded.vfeart":
                    case "vanillaexpanded.vfecore":
                    case "vanillaexpanded.vfefarming":
                    case "vanillaexpanded.vfesecurity":
                    case "vanillaexpanded.vfespacer":
                        settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, true);
                        continue;
                    case "ludeon.rimworld":
                        modCategoryDef.treeNode.SetOpen(1, true);
                        goto case "buddy1913.expandedstorageboxes";
                    case "lwm.deepstorage":
                        settings.opportunityDefaultBuildingFilter.SetAllow(modCategoryDef, false);
                        settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, true);
                        continue;
                    default:
                        continue;
                }
            }
        }

        public static void DoWindowContents(Rect windowRect) {
            var listingStandard1 = new Listing_Standard
            {
                ColumnWidth = (float)Math.Round((windowRect.width - 34.0) / 3.0)
            };
            var list1 = listingStandard1;
            list1.Begin(windowRect);
            list1.DrawBool(ref settings.Enabled, "Enabled");
            list1.NewColumn();
            list1.DrawBool(ref settings.DrawSpecialHauls, "DrawSpecialHauls");
            list1.NewColumn();
            if (ModLister.HasActiveModWithName("Pick Up And Haul")) {
                list1.DrawBool(ref settings.UsePickUpAndHaulPlus, "UsePickUpAndHaulPlus");
                if (tab == Tab.PickUpAndHaul && !settings.UsePickUpAndHaulPlus)
                    tab = Tab.HaulBeforeCarry;
            }
            else
                list1.Label(
                    "PickUpAndHaul_Missing".ModTranslate(),
                    Text.LineHeight,
                    "PickUpAndHaul_Tooltip".ModTranslate()
                );
            tabsList.Clear();
            tabsList.Add(
                new TabRecord("Opportunity_Tab".ModTranslate(), () => tab = Tab.Opportunity, tab == Tab.Opportunity)
            );
            tabsList.Add(
                new TabRecord(
                    "HaulBeforeCarry_Tab".ModTranslate(),
                    () => tab = Tab.HaulBeforeCarry,
                    tab == Tab.HaulBeforeCarry
                )
            );
            if (ModLister.HasActiveModWithName("Pick Up And Haul") && settings.UsePickUpAndHaulPlus)
                tabsList.Add(
                    new TabRecord(
                        "PickUpAndHaulPlus_Tab".ModTranslate(),
                        () => tab = Tab.PickUpAndHaul,
                        tab == Tab.PickUpAndHaul
                    )
                );
            var rect1 = windowRect.AtZero();
            ref var local1 = ref rect1;
            local1.yMin += list1.MaxColumnHeightSeen;
            ref var local2 = ref rect1;
            local2.yMin += 42f;
            ref var local3 = ref rect1;
            local3.height -= 42f;
            Widgets.DrawMenuSection(rect1);
            TabDrawer.DrawTabs(rect1, tabsList, 1);
            tabsList.Clear();
            var innerRect = rect1.GetInnerRect();
            switch (tab) {
                case Tab.Opportunity:
                    var listingStandard2 = new Listing_Standard {
                        ColumnWidth = (float)Math.Round((innerRect.width - 17.0) / 2.0)
                    };
                    var list2 = listingStandard2;
                    list2.Begin(innerRect);
                    using (new DrawContext() { GuiColor = Color.grey }) {
                        list2.Label("Opportunity_Intro".ModTranslate());
                    }
                    list2.Gap();
                    using (new DrawContext() { LabelPct = 0.25f }) {
                        list2.DrawEnum(
                            settings.Opportunity_PathChecker,
                            "Opportunity_PathChecker",
                            val => settings.Opportunity_PathChecker = val,
                            Text.LineHeight * 2f
                        );
                    }
                    list2.Gap();
                    list2.DrawBool(ref settings.Opportunity_TweakVanilla, "Opportunity_TweakVanilla");
                    if (settings.Opportunity_TweakVanilla) {
                        using (new DrawContext()
                               {
                                   TextAnchor = (TextAnchor)5,
                                   LabelPct = 0.65f
                               }) {
                            list2.DrawFloat(
                                ref settings.Opportunity_MaxNewLegsPctOrigTrip,
                                "Opportunity_MaxNewLegsPctOrigTrip"
                            );
                            list2.DrawFloat(
                                ref settings.Opportunity_MaxTotalTripPctOrigTrip,
                                "Opportunity_MaxTotalTripPctOrigTrip"
                            );
                            list2.DrawFloat(ref settings.Opportunity_MaxStartToThing, "Opportunity_MaxStartToThing");
                            list2.DrawFloat(
                                ref settings.Opportunity_MaxStartToThingPctOrigTrip,
                                "Opportunity_MaxStartToThingPctOrigTrip"
                            );
                            list2.DrawInt(
                                ref settings.Opportunity_MaxStartToThingRegionLookCount,
                                "Opportunity_MaxStartToThingRegionLookCount"
                            );
                            list2.DrawFloat(ref settings.Opportunity_MaxStoreToJob, "Opportunity_MaxStoreToJob");
                            list2.DrawFloat(
                                ref settings.Opportunity_MaxStoreToJobPctOrigTrip,
                                "Opportunity_MaxStoreToJobPctOrigTrip"
                            );
                            list2.DrawInt(
                                ref settings.Opportunity_MaxStoreToJobRegionLookCount,
                                "Opportunity_MaxStoreToJobRegionLookCount"
                            );
                        }
                    }
                    list2.NewColumn();
                    using (new DrawContext()
                           {
                               GuiColor = Color.grey
                           })
                        list2.Label("Opportunity_Tab".ModTranslate());
                    list2.GapLine();
                    var flag1 = !settings.Opportunity_AutoBuildings;
                    list2.DrawBool(ref flag1, "Opportunity_AutoBuildings");
                    settings.Opportunity_AutoBuildings = !flag1;
                    list2.Gap(4f);
                    opportunitySearchWidget.OnGUI(list2.GetRect(24f));
                    list2.Gap(4f);
                    Rect rect2 = list2.GetRect(
                        (float)(innerRect.height - (double)list2.CurHeight - Text.LineHeight * 2.0)
                    );
                    var num1 = 20f;
                    var num2 = rect2.width - (double)num1;
                    var opportunityTreeFilter = SettingsWindow.opportunityTreeFilter;
                    var num3 = opportunityTreeFilter != null ? opportunityTreeFilter.CurHeight : 10000.0;
                    var visibleRect1 = new Rect(0.0f, 0.0f, (float)num2, (float)num3);
                    Widgets.BeginScrollView(rect2, ref opportunityScrollPosition, visibleRect1);
                    if (settings.Opportunity_AutoBuildings)
                        opportunityDummyFilter.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
                    Mod.SettingsWindow.opportunityTreeFilter = new Listing_SettingsTreeThingFilter(
                        settings.Opportunity_AutoBuildings ? opportunityDummyFilter :
                            settings.opportunityBuildingFilter,
                        null,
                        null,
                        null,
                        null,
                        opportunitySearchFilter
                    );
                    Mod.SettingsWindow.opportunityTreeFilter.Begin(visibleRect1);
                    Mod.SettingsWindow.opportunityTreeFilter.ListCategoryChildren(
                        storageBuildingCategoryDef.treeNode,
                        1,
                        null,
                        visibleRect1
                    );
                    Mod.SettingsWindow.opportunityTreeFilter.End();
                    Widgets.EndScrollView();
                    list2.GapLine();
                    list2.DrawBool(ref settings.Opportunity_ToStockpiles, "Opportunity_ToStockpiles");
                    list2.End();
                    break;
                case Tab.HaulBeforeCarry:
                    var listingStandard3 = new Listing_Standard
                    {
                        ColumnWidth = (float)Math.Round((innerRect.width - 17.0) / 2.0)
                    };
                    var list3 = listingStandard3;
                    list3.Begin(innerRect);
                    using (new DrawContext()
                           {
                               GuiColor = Color.grey
                           })
                        list3.Label("HaulBeforeCarry_Intro".ModTranslate());
                    list3.DrawBool(ref settings.HaulBeforeCarry_Supplies, "HaulBeforeCarry_Supplies");
                    list3.DrawBool(ref settings.HaulBeforeCarry_Bills, "HaulBeforeCarry_Bills");
                    list3.Gap();
                    using (new DrawContext()
                           {
                               GuiColor = Color.grey
                           })
                        list3.Label("HaulBeforeCarry_EqualPriority".ModTranslate());
                    list3.DrawBool(ref settings.HaulBeforeCarry_ToEqualPriority, "HaulBeforeCarry_ToEqualPriority");
                    list3.NewColumn();
                    using (new DrawContext()
                           {
                               GuiColor = Color.grey
                           })
                        list3.Label("HaulBeforeCarry_Tab".ModTranslate());
                    list3.GapLine();
                    var flag2 = !settings.HaulBeforeCarry_AutoBuildings;
                    list3.DrawBool(ref flag2, "HaulBeforeCarry_AutoBuildings");
                    settings.HaulBeforeCarry_AutoBuildings = !flag2;
                    list3.Gap(4f);
                    hbcSearchWidget.OnGUI(list3.GetRect(24f));
                    list3.Gap(4f);
                    Rect rect3 = list3.GetRect(
                        (float)(innerRect.height - (double)list3.CurHeight - Text.LineHeight * 2.0)
                    );
                    var num4 = 20f;
                    var num5 = rect3.width - (double)num4;
                    var hbcTreeFilter = Mod.SettingsWindow.hbcTreeFilter;
                    var num6 = hbcTreeFilter != null ? hbcTreeFilter.CurHeight : 10000.0;
                    var visibleRect2 = new Rect(0.0f, 0.0f, (float)num5, (float)num6);
                    Widgets.BeginScrollView(rect3, ref hbcScrollPosition, visibleRect2);
                    if (settings.HaulBeforeCarry_AutoBuildings)
                        hbcDummyFilter.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
                    Mod.SettingsWindow.hbcTreeFilter = new Listing_SettingsTreeThingFilter(
                        settings.HaulBeforeCarry_AutoBuildings ? hbcDummyFilter : settings.hbcBuildingFilter,
                        null,
                        null,
                        null,
                        null,
                        hbcSearchFilter
                    );
                    Mod.SettingsWindow.hbcTreeFilter.Begin(visibleRect2);
                    Mod.SettingsWindow.hbcTreeFilter.ListCategoryChildren(
                        storageBuildingCategoryDef.treeNode,
                        1,
                        null,
                        visibleRect2
                    );
                    Mod.SettingsWindow.hbcTreeFilter.End();
                    Widgets.EndScrollView();
                    list3.GapLine();
                    list3.DrawBool(ref settings.HaulBeforeCarry_ToStockpiles, "HaulBeforeCarry_ToStockpiles");
                    list3.End();
                    break;
                case Tab.PickUpAndHaul:
                    var listingStandard4 = new Listing_Standard
                    {
                        ColumnWidth = (float)Math.Round((innerRect.width - 17.0) / 2.0)
                    };
                    var listingStandard5 = listingStandard4;
                    listingStandard5.Begin(innerRect);
                    listingStandard5.Label("PickUpAndHaulPlus_UpgradeTitle".ModTranslate());
                    using (new DrawContext()
                           {
                               GuiColor = Color.grey
                           })
                        listingStandard5.Label("PickUpAndHaulPlus_UpgradeText".ModTranslate());
                    listingStandard5.Gap();
                    listingStandard5.Label("PickUpAndHaulPlus_IntegrationTitle".ModTranslate());
                    using (new DrawContext()
                           {
                               GuiColor = Color.grey
                           })
                        listingStandard5.Label("PickUpAndHaulPlus_IntegrationText".ModTranslate());
                    listingStandard5.End();
                    break;
            }
            var rect4 = windowRect.AtZero();
            ref var local6 = ref rect4;
            local6.yMin += rect1.yMax;
            list1.Begin(rect4);
            list1.Gap(6f);
            if (Widgets.ButtonText(
                    list1.GetRect(30f),
                    "RestoreToDefaultSettings".Translate()
                )) {
                ((ModSettings)settings).ExposeData();
                opportunitySearchWidget.Reset();
                hbcSearchWidget.Reset();
                ResetFilters();
            }
            list1.Gap(6f);
            list1.End();
            list1.End();
        }

        enum Tab {
            Opportunity,
            HaulBeforeCarry,
            PickUpAndHaul,
        }
    }

    class Settings : ModSettings {
        public bool Enabled;
        public bool UsePickUpAndHaulPlus;
        public bool DrawSpecialHauls;
        public PathCheckerEnum Opportunity_PathChecker;
        public bool Opportunity_TweakVanilla;
        public bool Opportunity_ToStockpiles;
        public bool Opportunity_AutoBuildings;
        public float Opportunity_MaxStartToThing;
        public float Opportunity_MaxStartToThingPctOrigTrip;
        public float Opportunity_MaxStoreToJob;
        public float Opportunity_MaxStoreToJobPctOrigTrip;
        public float Opportunity_MaxTotalTripPctOrigTrip;
        public float Opportunity_MaxNewLegsPctOrigTrip;
        public int Opportunity_MaxStartToThingRegionLookCount;
        public int Opportunity_MaxStoreToJobRegionLookCount;
        internal readonly SettingsThingFilter opportunityDefaultBuildingFilter = new();
        internal SettingsThingFilter opportunityBuildingFilter;
        internal XmlNode opportunityBuildingFilterXmlNode;
        public bool HaulBeforeCarry_Supplies;
        public bool HaulBeforeCarry_Bills;
        public bool HaulBeforeCarry_Bills_NeedsInitForCs;
        public bool HaulBeforeCarry_ToEqualPriority;
        public bool HaulBeforeCarry_ToStockpiles;
        public bool HaulBeforeCarry_AutoBuildings;
        internal readonly SettingsThingFilter hbcDefaultBuildingFilter = new();
        internal SettingsThingFilter hbcBuildingFilter;
        internal XmlNode hbcBuildingFilterXmlNode;

        public SettingsThingFilter Opportunity_BuildingFilter => !Opportunity_AutoBuildings ?
            opportunityBuildingFilter : opportunityDefaultBuildingFilter;

        public SettingsThingFilter HaulBeforeCarry_BuildingFilter =>
            !HaulBeforeCarry_AutoBuildings ? hbcBuildingFilter : hbcDefaultBuildingFilter;

        public override void ExposeData() {
            foundConfig = true;
            Look(ref Enabled, "Enabled", true);
            Look(ref DrawSpecialHauls, "DrawSpecialHauls", false);
            Look(ref UsePickUpAndHaulPlus, "UsePickUpAndHaulPlus", true);
            Look(ref Opportunity_PathChecker, "Opportunity_PathChecker", PathCheckerEnum.Default);
            Look(ref Opportunity_TweakVanilla, "Opportunity_TweakVanilla", false);
            Look(ref Opportunity_MaxStartToThing, "Opportunity_MaxStartToThing", 30f);
            Look(ref Opportunity_MaxStartToThingPctOrigTrip, "Opportunity_MaxStartToThingPctOrigTrip", 0.5f);
            Look(ref Opportunity_MaxStoreToJob, "Opportunity_MaxStoreToJob", 50f);
            Look(ref Opportunity_MaxStoreToJobPctOrigTrip, "Opportunity_MaxStoreToJobPctOrigTrip", 0.6f);
            Look(ref Opportunity_MaxTotalTripPctOrigTrip, "Opportunity_MaxTotalTripPctOrigTrip", 1.7f);
            Look(ref Opportunity_MaxNewLegsPctOrigTrip, "Opportunity_MaxNewLegsPctOrigTrip", 1f);
            Look(ref Opportunity_MaxStartToThingRegionLookCount, "Opportunity_MaxStartToThingRegionLookCount", 25);
            Look(ref Opportunity_MaxStoreToJobRegionLookCount, "Opportunity_MaxStoreToJobRegionLookCount", 25);
            Look(ref Opportunity_ToStockpiles, "Opportunity_ToStockpiles", true);
            Look(ref Opportunity_AutoBuildings, "Opportunity_AutoBuildings", true);
            Look(ref HaulBeforeCarry_Supplies, "HaulBeforeCarry_Supplies", true);
            Look(ref HaulBeforeCarry_Bills, "HaulBeforeCarry_Bills", true);
            Look(ref HaulBeforeCarry_Bills_NeedsInitForCs, "HaulBeforeCarry_Bills_NeedsInitForCs", true);
            Look(ref HaulBeforeCarry_ToEqualPriority, "HaulBeforeCarry_ToEqualPriority", true);
            Look(ref HaulBeforeCarry_ToStockpiles, "HaulBeforeCarry_ToStockpiles", true);
            Look(ref HaulBeforeCarry_AutoBuildings, "HaulBeforeCarry_AutoBuildings", true);
            if (Scribe.mode == LoadSaveMode.Saving) {
                Scribe_Deep.Look(ref hbcBuildingFilter, "hbcBuildingFilter", Array.Empty<object>());
                Scribe_Deep.Look(ref opportunityBuildingFilter, "opportunityBuildingFilter", Array.Empty<object>());
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars) {
                hbcBuildingFilterXmlNode = Scribe.loader.curXmlParent["hbcBuildingFilter"];
                opportunityBuildingFilterXmlNode = Scribe.loader.curXmlParent["opportunityBuildingFilter"];
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.Saving)
                DebugViewSettings.drawOpportunisticJobs = DrawSpecialHauls;
            if (Scribe.mode != LoadSaveMode.LoadingVars || !haveCommonSense)
                return;
            if (HaulBeforeCarry_Bills_NeedsInitForCs) {
                CsHaulingOverBillsSetting.SetValue(null, false);
                HaulBeforeCarry_Bills = true;
                HaulBeforeCarry_Bills_NeedsInitForCs = false;
            }
            else {
                if (!(bool)CsHaulingOverBillsSetting.GetValue(null))
                    return;
                HaulBeforeCarry_Bills = false;
            }

            static void Look<T>(ref T value, string label, T defaultValue) {
                if (Scribe.mode == null)
                    value = defaultValue;
                Scribe_Values.Look(ref value, label, defaultValue);
            }
        }

        public enum PathCheckerEnum {
            Default,
            Vanilla,
            Pathfinding,
        }
    }

    public class SpecialHaul {
        readonly string reportKey;
        LocalTargetInfo target;

        protected SpecialHaul() {
        }

        public SpecialHaul(string reportKey, LocalTargetInfo target) {
            this.reportKey = reportKey;
            this.target = target;
        }

        public string GetReport(string text) {
            if (this is PuahWithBetterUnloading withBetterUnloading)
                return withBetterUnloading.GetLoadReport(text);
            return reportKey.ModTranslate(text.Named("ORIGINAL"), target.Label.Named("DESTINATION"));
        }
    }

    public class PuahWithBetterUnloading : SpecialHaul {
        public Dictionary<ThingDef, IntVec3> defHauls = new();

        public virtual string GetLoadReport(string text) =>
            "PickUpAndHaulPlus_LoadReport".ModTranslate(text.Named("ORIGINAL"));

        public virtual string GetUnloadReport(string text) =>
            "PickUpAndHaulPlus_UnloadReport".ModTranslate(text.Named("ORIGINAL"));

        public void TrackThing(
            Thing thing,
            IntVec3 storeCell,
            bool prepend = false,
            bool trackDef = true,
            [CallerMemberName] string callerName = ""
        ) {
            if (trackDef)
                defHauls.SetOrAdd(thing.def, storeCell);
            if (this is PuahOpportunity puahOpportunity) {
                if (puahOpportunity.hauls.LastOrDefault().thing == thing)
                    puahOpportunity.hauls.Pop<(Thing, IntVec3)>();
                if (prepend) {
                    if (puahOpportunity.hauls.FirstOrDefault().thing == thing)
                        puahOpportunity.hauls.RemoveAt(0);
                    puahOpportunity.hauls.Insert(0, (thing, storeCell));
                }
                else
                    puahOpportunity.hauls.Add((thing, storeCell));
            }
            var num = callerName != "TrackThingIfOpportune" ? 1 : 0;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Scanner), "HasJobOnThing")]
    static class WorkGiver_Scanner__HasJobOnThing_Patch {
        [HarmonyPrefix]
        static void CheckForSpecialHaul(out bool __state, Pawn pawn) => __state = specialHauls.ContainsKey(pawn);

        [HarmonyPostfix]
        static void ClearTempSpecialHaul(bool __state, Pawn pawn) {
            if (__state || !specialHauls.ContainsKey(pawn))
                return;
            specialHauls.Remove(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "ClearQueuedJobs")]
    static class Pawn_JobTracker__ClearQueuedJobs_Patch {
        [HarmonyPostfix]
        static void ClearSpecialHaul(Pawn ___pawn) {
            if (___pawn == null)
                return;
            specialHauls.Remove(___pawn);
        }
    }

    [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
    static class JobDriver_HaulToCell__MakeNewToils_Patch {
        [HarmonyPostfix]
        static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(
            () => {
                SpecialHaul specialHaul;
                if (!specialHauls.TryGetValue(__instance.pawn, out specialHaul) ||
                    specialHaul is PuahWithBetterUnloading)
                    return;
                specialHauls.Remove(__instance.pawn);
            }
        );
    }

    [HarmonyPatch(typeof(JobDriver_HaulToCell), "GetReport")]
    static class JobDriver_HaulToCell__GetReport_Patch {
        [HarmonyPostfix]
        static void SpecialHaulGetReport(JobDriver_HaulToCell __instance, ref string __result) {
            SpecialHaul specialHaul;
            if (!specialHauls.TryGetValue(__instance.pawn, out specialHaul))
                return;
            __result = specialHaul.GetReport(__result.TrimEnd('.'));
        }
    }
}
