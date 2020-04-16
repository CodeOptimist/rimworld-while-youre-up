using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HugsLib;
using HugsLib.Settings;
using RimWorld;
using Verse;
using Dialog_ModSettings = HugsLib.Settings.Dialog_ModSettings;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity : ModBase
    {
        static string modIdentifier;

        static SettingHandle<bool> showVanillaParameters, haulToInventory, haulBeforeSupply, skipIfBleeding, drawOpportunisticJobs;
        static SettingHandle<Hauling.HaulProximities> haulProximities;
        static SettingHandle<float> maxStartToThing, maxStartToThingPctOrigTrip, maxStoreToJob, maxStoreToJobPctOrigTrip, maxTotalTripPctOrigTrip, maxNewLegsPctOrigTrip;
        static SettingHandle<int> maxStartToThingRegionLookCount, maxStoreToJobRegionLookCount;
        static readonly SettingHandle.ShouldDisplay HavePuah = ModLister.HasActiveModWithName("Pick Up And Haul") ? new SettingHandle.ShouldDisplay(() => true) : () => false;

        // Pick Up And Haul
        static readonly List<Assembly> puahAssemblies = LoadedModManager.RunningMods.SingleOrDefault(x => x.PackageIdPlayerFacing == "Mehni.PickUpAndHaul")?.assemblies.loadedAssemblies;
        static readonly Type PuahWorkGiver_HaulToInventory_Type = puahAssemblies?.Select(x => x.GetType("PickUpAndHaul.WorkGiver_HaulToInventory")).SingleOrDefault(x => x != null);
        static WorkGiver puahWorkGiver;

        public override void DefsLoaded() {
            puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamedSilentFail("HaulToInventory")?.Worker;
            modIdentifier = ModContentPack.PackageIdPlayerFacing;

            SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue = default, SettingHandle.ValueIsValid validator = default,
                SettingHandle.ShouldDisplay shouldDisplay = default, string enumPrefix = default) {
                var settingHandle = Settings.GetHandle(
                    settingName, $"{modIdentifier}_SettingTitle_{settingName}".Translate(), $"{modIdentifier}_SettingDesc_{settingName}".Translate(), defaultValue, validator, enumPrefix);
                settingHandle.VisibilityPredicate = shouldDisplay;
                return settingHandle;
            }

            haulToInventory = GetSettingHandle("haulToInventory", true, default, HavePuah);
            haulBeforeSupply = GetSettingHandle("haulBeforeSupply", true, default, HavePuah);
            skipIfBleeding = GetSettingHandle("skipIfBleeding", true);

            haulProximities = GetSettingHandle("haulProximities", Hauling.HaulProximities.BothThenEitherThenIgnored, default, default, $"{modIdentifier}_SettingTitle_haulProximities_");

            drawOpportunisticJobs = GetSettingHandle("drawOpportunisticJobs", DebugViewSettings.drawOpportunisticJobs);
            drawOpportunisticJobs.Unsaved = true;
            drawOpportunisticJobs.OnValueChanged += value => DebugViewSettings.drawOpportunisticJobs = value;

            showVanillaParameters = GetSettingHandle("showVanillaParameters", false);
            var ShowVanillaParameters = new SettingHandle.ShouldDisplay(() => showVanillaParameters.Value);

            var floatRangeValidator = Validators.FloatRangeValidator(0f, 999f);
            maxNewLegsPctOrigTrip = GetSettingHandle("maxNewLegsPctOrigTrip", 1.0f, floatRangeValidator, ShowVanillaParameters);
            maxTotalTripPctOrigTrip = GetSettingHandle("maxTotalTripPctOrigTrip", 1.7f, floatRangeValidator, ShowVanillaParameters);

            maxStartToThing = GetSettingHandle("maxStartToThing", 30f, floatRangeValidator, ShowVanillaParameters);
            maxStartToThingPctOrigTrip = GetSettingHandle("maxStartToThingPctOrigTrip", 0.5f, floatRangeValidator, ShowVanillaParameters);
            maxStartToThingRegionLookCount = GetSettingHandle("maxStartToThingRegionLookCount", 25, Validators.IntRangeValidator(0, 999), ShowVanillaParameters);
            maxStoreToJob = GetSettingHandle("maxStoreToJob", 50f, floatRangeValidator, ShowVanillaParameters);
            maxStoreToJobPctOrigTrip = GetSettingHandle("maxStoreToJobPctOrigTrip", 0.6f, floatRangeValidator, ShowVanillaParameters);
            maxStoreToJobRegionLookCount = GetSettingHandle("maxStoreToJobRegionLookCount", 25, Validators.IntRangeValidator(0, 999), ShowVanillaParameters);
        }

        static void InsertCode(ref int i, ref List<CodeInstruction> codes, ref List<CodeInstruction> newCodes, int offset, Func<bool> when, Func<List<CodeInstruction>> what,
            bool bringLabels = false) {
            for (i -= offset; i < codes.Count; i++) {
                if (i >= 0 && when()) {
                    var whatCodes = what();
                    if (bringLabels) {
                        whatCodes[0].labels.AddRange(codes[i + offset].labels);
                        codes[i + offset].labels.Clear();
                    }

                    newCodes.AddRange(whatCodes);
                    if (offset > 0)
                        i += offset;
                    else
                        newCodes.AddRange(codes.GetRange(i + offset, Math.Abs(offset)));
                    break;
                }

                newCodes.Add(codes[i + offset]);
            }
        }

        [HarmonyPatch(typeof(Dialog_ModSettings), "PopulateControlInfo")]
        static class Dialog_ModSettings_PopulateControlInfo_Patch
        {
            [HarmonyPrefix]
            static void UpdateDynamicSettings() {
                drawOpportunisticJobs.Value = DebugViewSettings.drawOpportunisticJobs;
            }
        }
    }
}
