using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HugsLib;
using HugsLib.Settings;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;
using Dialog_ModSettings = HugsLib.Settings.Dialog_ModSettings;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity : ModBase
    {
        static SettingHandle<bool> enabled, showVanillaParameters, haulToInventory, haulBeforeSupply, haulBeforeBill, skipIfBleeding, drawOpportunisticJobs;
        static SettingHandle<Hauling.HaulProximities> haulProximities;
        static SettingHandle<float> maxStartToThing, maxStartToThingPctOrigTrip, maxStoreToJob, maxStoreToJobPctOrigTrip, maxTotalTripPctOrigTrip, maxNewLegsPctOrigTrip;
        static SettingHandle<int> maxStartToThingRegionLookCount, maxStoreToJobRegionLookCount;
        static readonly SettingHandle.ShouldDisplay HavePuah = ModLister.HasActiveModWithName("Pick Up And Haul") ? new SettingHandle.ShouldDisplay(() => true) : () => false;

        static readonly Type PuahWorkGiver_HaulToInventoryType = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
        static readonly MethodInfo PuahJobOnThing = AccessTools.Method(PuahWorkGiver_HaulToInventoryType, "JobOnThing");
        static WorkGiver puahWorkGiver;

        static readonly Type CsModType = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        static readonly FieldInfo CsSettings = AccessTools.Field(CsModType, "Settings");
        static readonly Type CsSettingsType = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        static readonly FieldInfo CsHaulingOverBillsSetting = AccessTools.Field(CsSettingsType, "hauling_over_bills");
        static readonly bool haveCommonSense = new List<object> {CsModType, CsSettings, CsSettingsType, CsHaulingOverBillsSetting}.All(x => x != null);
        static ModSettings csSettings;

        public override void DefsLoaded() {
            puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamedSilentFail("HaulToInventory")?.Worker;
            csSettings = (ModSettings) CsSettings?.GetValue(LoadedModManager.GetMod(CsModType));
            var modIdentifier = ModContentPack.PackageIdPlayerFacing;

            SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue = default, SettingHandle.ValueIsValid validator = default,
                SettingHandle.ShouldDisplay shouldDisplay = default, string enumPrefix = default) {
                var settingHandle = Settings.GetHandle(
                    settingName, $"{modIdentifier}_SettingTitle_{settingName}".Translate(), $"{modIdentifier}_SettingDesc_{settingName}".Translate(), defaultValue, validator, enumPrefix);
                settingHandle.VisibilityPredicate = shouldDisplay;
                return settingHandle;
            }

            enabled = GetSettingHandle("enabled", true);
            haulToInventory = GetSettingHandle("haulToInventory", true, default, HavePuah);
            haulBeforeSupply = GetSettingHandle("haulBeforeSupply", true);

            var haulBeforeBill_needsInitForCs = GetSettingHandle("haulBeforeBill_needsInitForCs", true, default, () => false);
            haulBeforeBill = GetSettingHandle("haulBeforeBill", true);
            if (haveCommonSense) {
                if (haulBeforeBill_needsInitForCs.Value) {
                    CsHaulingOverBillsSetting.SetValue(csSettings, false);
                    haulBeforeBill.Value = true;
                    haulBeforeBill_needsInitForCs.Value = false;
                } else if ((bool) CsHaulingOverBillsSetting.GetValue(csSettings))
                    haulBeforeBill.Value = false;

                Settings.SaveChanges();

                haulBeforeBill.OnValueChanged += value => {
                    if (value)
                        CsHaulingOverBillsSetting.SetValue(csSettings, false);
                };
            }

            skipIfBleeding = GetSettingHandle("skipIfBleeding", true);

            haulProximities = GetSettingHandle("haulProximities", Hauling.HaulProximities.Ignored, default, default, $"{modIdentifier}_SettingTitle_haulProximities_");

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

        [HarmonyPatch(typeof(Dialog_ModSettings), "PopulateControlInfo")]
        static class Dialog_ModSettings_PopulateControlInfo_Patch
        {
            [HarmonyPrefix]
            static void UpdateDynamicSettings() {
                drawOpportunisticJobs.Value = DebugViewSettings.drawOpportunisticJobs;
                if (haveCommonSense && (bool) CsHaulingOverBillsSetting.GetValue(csSettings))
                    haulBeforeBill.Value = false; // will save on close
            }
        }
    }
}
