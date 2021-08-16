using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        public enum SpecialHaulType { None, Opportunity, HaulBeforeCarry }

        public static readonly Dictionary<Pawn, SpecialHaulInfo> specialHauls = new Dictionary<Pawn, SpecialHaulInfo>();

        public class SpecialHaulInfo
        {
            public SpecialHaulType haulType;

            // reminder that storeCell is just *some* cell in our stockpile, actual unload cell is determined at unload
            public List<(Thing thing, IntVec3 storeCell)> hauls;    // for opportune checking (with jobCell)
            public Dictionary<ThingDef, IntVec3>          defHauls; // for unload ordering

            public IntVec3 startCell;
            public IntVec3 jobCell  = IntVec3.Invalid; // when our haul is an opportunity on the way to a job
            public IntVec3 destCell = IntVec3.Invalid; // when store isn't the destination (e.g. bill ingredients, blueprint supplies)

            SpecialHaulInfo() {
            }

            public static SpecialHaulInfo CreateAndAdd(SpecialHaulType haulType, Pawn pawn, IntVec3 cell) {
                var specialHaul = new SpecialHaulInfo { haulType = haulType };

                if (havePuah) {
                    specialHaul.hauls = new List<(Thing thing, IntVec3 storeCell)>();
                    specialHaul.defHauls = new Dictionary<ThingDef, IntVec3>();
                }

                specialHaul.startCell = pawn.Position;
                switch (haulType) {
                    case SpecialHaulType.Opportunity:
                        specialHaul.jobCell = cell;
                        break;
                    case SpecialHaulType.HaulBeforeCarry:
                        specialHaul.destCell = cell;
                        break;
                    case SpecialHaulType.None: break;
                    default:                   throw new ArgumentOutOfRangeException(nameof(haulType), haulType, null);
                }

                specialHauls.SetOrAdd(pawn, specialHaul);
                return specialHaul;
            }

            public string GetJobReportPrefix() {
                switch (haulType) {
                    case SpecialHaulType.Opportunity:     return "Opportunistically ";
                    case SpecialHaulType.HaulBeforeCarry: return "Optimally ";
                    default:                              return "";
                }
            }

            public void Add(Thing thing, IntVec3 storeCell, bool isInitial = false, [CallerMemberName] string callerName = "") {
#if DEBUG
                // make deterministic, but merges and initial hauls will still fluctuate
                storeCell = storeCell.GetSlotGroup(thing.Map).CellsList[0];
#endif

                string verb;
                if (isInitial && hauls.FirstOrDefault().thing == thing) {
                    hauls.RemoveAt(0);
                    verb = "UPDATED on tracker.";
                } else
                    verb = isInitial ? "PREPENDED to tracker." : "Added to tracker.";

                hauls.Insert(isInitial ? 0 : hauls.Count, (thing, storeCell));
                defHauls.SetOrAdd(thing.def, storeCell);

                if (callerName != "TrackPuahThingIfOpportune")
                    Debug.WriteLine($"{RealTime.frameCount} {haulType} {callerName}() {thing} -> {storeCell} {verb}");
            }
        } // ReSharper disable UnusedType.Local
        // ReSharper disable UnusedMember.Local
        // ReSharper disable UnusedParameter.Local

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ClearQueuedJobs))]
        static class Pawn_JobTracker__ClearQueuedJobs_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaul(Pawn ___pawn) => specialHauls.Remove(___pawn);
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
        static class JobDriver_HaulToCell__MakeNewToils_Patch
        {
            [HarmonyPostfix]
            static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
        }

        [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.GetReport))]
        static class JobDriver_HaulToCell__GetReport_Patch
        {
            [HarmonyPostfix]
            static void SpecialHaulJobReport(JobDriver_HaulToCell __instance, ref string __result) {
                if (!specialHauls.TryGetValue(__instance.pawn, out var specialHaul)) return;
                __result = specialHaul.GetJobReportPrefix() + __result;
            }
        }

        static partial class Patch_PUAH
        {
            [HarmonyPatch]
            static class JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(PuahJobDriver_UnloadYourHauledInventoryType, "MakeNewToils");

                [HarmonyPostfix]
                static void ClearSpecialHaulOnFinish(JobDriver __instance) => __instance.AddFinishAction(() => specialHauls.Remove(__instance.pawn));
            }

            [HarmonyPatch]
            static class JobDriver__GetReport_Patch
            {
                static bool       Prepare()      => havePuah;
                static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.GetReport));

                [HarmonyPostfix]
                static void SpecialHaulJobReport(JobDriver __instance, ref string __result) {
                    if (!settings.HaulToInventory || !settings.Enabled) return;
                    if (PuahJobDriver_HaulToInventoryType.IsInstanceOfType(__instance)) {
                        if (!specialHauls.TryGetValue(__instance.pawn, out var specialHaul)) return;
                        __result = specialHaul.GetJobReportPrefix() + __result;
                    } else if (PuahJobDriver_UnloadYourHauledInventoryType.IsInstanceOfType(__instance))
                        __result = "Efficiently " + __result;
                }
            }
        }
    }
}
