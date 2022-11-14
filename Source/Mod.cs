using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    static class Extensions
    {
        public static string ModTranslate(this string key, params NamedArgument[] args) {
            return $"{Mod.modId}_{key}".Translate(args);
        }
    }

    partial class Mod : Verse.Mod
    {
        public const    string    modId = "CodeOptimist.WhileYoureUp"; // explicit because PackageId may be changed e.g. "__copy__" suffix
        static          Verse.Mod mod;
        static          Settings  settings;
        static          bool      foundConfig;
        static readonly Harmony   harmony = new Harmony(modId);

        static readonly Type PuahCompHauledToInventoryType               = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory");
        static readonly Type PuahWorkGiver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
        static readonly Type PuahJobDriver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
        static readonly Type PuahJobDriver_UnloadYourHauledInventoryType = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");

        static          MethodInfo PuahGetCompHauledToInventory;
        static readonly MethodInfo PuahJobOnThing     = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");
        static readonly FieldInfo  PuahSkipCellsField = AccessTools.DeclaredField(PuahWorkGiver_HaulToInventoryType, "skipCells");

        static readonly bool havePuah = new List<object> {
                PuahCompHauledToInventoryType, PuahWorkGiver_HaulToInventoryType, PuahJobDriver_HaulToInventoryType, PuahJobDriver_UnloadYourHauledInventoryType, PuahJobOnThing,
                PuahSkipCellsField,
            }
            .All(x => x != null);

        static readonly Type HugsDialog_VanillaModSettingsType = GenTypes.GetTypeInAnyAssembly("HugsLib.Settings.Dialog_VanillaModSettings");
        static readonly bool haveHugs                          = HugsDialog_VanillaModSettingsType != null;

        static readonly FieldInfo SelMod = haveHugs
            ? AccessTools.DeclaredField(HugsDialog_VanillaModSettingsType, "selectedMod")
            : AccessTools.DeclaredField(typeof(Dialog_ModSettings),        "selMod");

        static readonly Type      CsModType                 = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        static readonly Type      CsSettingsType            = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        static readonly FieldInfo CsHaulingOverBillsSetting = AccessTools.DeclaredField(CsSettingsType, "hauling_over_bills");

        static readonly bool haveCommonSense = new List<object> { CsModType, CsSettingsType, CsHaulingOverBillsSetting }.All(x => x != null);

        public Mod(ModContentPack content) : base(content) {
            if (havePuah)
                PuahGetCompHauledToInventory = AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahCompHauledToInventoryType);

            mod       = this;
            Gui.modId = modId;
            settings  = GetSettings<Settings>();
            if (!foundConfig)
                settings.ExposeData(); // initialize to defaults
            harmony.PatchAll();
        }

        static bool Original(object _ = null) => true;
        static bool Skip(object _ = null)     => false;

        public override string SettingsCategory() {
            return mod.Content.Name;
        }

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryOpportunisticJob))]
        [SuppressMessage("ReSharper", "UnusedType.Local")]
        [SuppressMessage("ReSharper", "UnusedMember.Local")]
        static class Pawn_JobTracker__TryOpportunisticJob_Patch
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> _TryOpportunisticJob(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
                var t                  = new Transpiler(_codes, __originalMethod);
                var listerHaulablesIdx = t.TryFindCodeIndex(code => code.LoadsField(AccessTools.DeclaredField(typeof(Map), nameof(Map.listerHaulables))));
                var skipMod            = generator.DefineLabel();

                t.TryInsertCodes(
                    -3,
                    (i, codes) => i == listerHaulablesIdx,
                    (i, codes) => new List<CodeInstruction> {
                        new CodeInstruction(OpCodes.Call,      AccessTools.DeclaredMethod(typeof(Pawn_JobTracker__TryOpportunisticJob_Patch), nameof(IsEnabled))),
                        new CodeInstruction(OpCodes.Brfalse_S, skipMod),

                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Pawn_JobTracker__TryOpportunisticJob_Patch), nameof(TryOpportunisticJob))),
                        new CodeInstruction(OpCodes.Ret),
                    }, true);

                t.codes[t.MatchIdx - 3].labels.Add(skipMod);
                return t.GetFinalCodes();
            }

            static bool IsEnabled() {
                return settings.Enabled;
            }

            // vanilla checks for job.def.allowOpportunisticPrefix and lots of other things before this
            // our settings.Enabled check is done prior to this in the transpiler
            static Job TryOpportunisticJob(Pawn_JobTracker jobTracker, Job job) {
                // Debug.WriteLine($"Opportunity checking {job}");
                var pawn = Traverse.Create(jobTracker).Field("pawn").GetValue<Pawn>();
                if (AlreadyHauling(pawn)) return null;
                var jobTarget = job.targetA;

                if (job.def == JobDefOf.DoBill && settings.HaulBeforeCarry_Bills) {
                    // Debug.WriteLine($"Bill: '{job.bill}' label: '{job.bill.Label}'");
                    // Debug.WriteLine($"Recipe: '{job.bill.recipe}' workerClass: '{job.bill.recipe.workerClass}'");
                    for (var i = 0; i < job.targetQueueB.Count; i++) {
                        var ingredient = job.targetQueueB[i];
                        if (ingredient.Thing == null) continue;

                        if (!havePuah || !settings.UsePickUpAndHaulPlus) { // too difficult to know in advance if there are no extras for PUAH
                            if (ingredient.Thing.stackCount <= job.countQueue[i])
                                continue; // there are no extras
                        }

                        if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, ingredient.Thing, false)) continue; // fast check

                        // permitted when bleeding because facilitates whatever bill is important enough to do while bleeding
                        //  may save precious time going back for ingredients... unless we want only 1 medicine ASAP; it's a trade-off

                        var storeJob = HaulBeforeCarry(pawn, jobTarget, ingredient.Thing); // #HaulBeforeBill
                        if (storeJob != null) return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(pawn, storeJob);
                    }
                }

                if (new[] {
                        JobDefOf.PrepareCaravan_CollectAnimals, JobDefOf.PrepareCaravan_GatherAnimals,
                        JobDefOf.PrepareCaravan_GatherDownedPawns, JobDefOf.PrepareCaravan_GatherItems,
                    }.Contains(job.def)) return null;
                if (pawn.health.hediffSet.BleedRateTotal > 0.001f) return null;
                return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding(pawn, Opportunity.TryHaul(pawn, jobTarget));
            }
        }


        static Job PuahJob(PuahWithBetterUnloading puah, Pawn pawn, Thing thing, IntVec3 storeCell) {
            if (!havePuah || !settings.UsePickUpAndHaulPlus || !settings.Enabled) return null;
            specialHauls.SetOrAdd(pawn, puah);
            puah.TrackThing(thing, storeCell);
            var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker;
            return (Job)PuahJobOnThing.Invoke(puahWorkGiver, new object[] { pawn, thing, false });
        }

        public static bool AlreadyHauling(Pawn pawn) {
            if (specialHauls.ContainsKey(pawn)) return true;

            // because we may load a game with an incomplete haul
            if (havePuah) {
                var hauledToInventoryComp = (ThingComp)PuahGetCompHauledToInventory.Invoke(pawn, null);
                var takenToInventory      = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value;
                if (takenToInventory != null && takenToInventory.Any(t => t != null)) return true;
            }

            return false;
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

                // original
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
