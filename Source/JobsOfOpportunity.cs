using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodeOptimist;
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
    [StaticConstructorOnStartup]
    partial class JobsOfOpportunity : ModBase
    {
        static SettingHandle<bool> enabled,        showVanillaParameters, haulToInventory, haulBeforeSupply, haulBeforeBill,
            haulToEqualPriority,   skipIfBleeding, drawOpportunisticJobs;

        static SettingHandle<float> maxStartToThing, maxStartToThingPctOrigTrip, maxStoreToJob, maxStoreToJobPctOrigTrip, maxTotalTripPctOrigTrip,
            maxNewLegsPctOrigTrip;

        static SettingHandle<int> maxStartToThingRegionLookCount, maxStoreToJobRegionLookCount;

        static SettingHandle<Hauling.HaulProximities> haulProximities;

        static readonly SettingHandle.ShouldDisplay HavePuah = ModLister.HasActiveModWithName("Pick Up And Haul") || ModLister.HasActiveModWithName("Pick Up And Haul (Continued)")
            ? new SettingHandle.ShouldDisplay(() => true)
            : () => false;

        static readonly Type       PuahCompHauledToInventoryType               = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory");
        static readonly Type       PuahWorkGiver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
        static readonly Type       PuahJobDriver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
        static readonly Type       PuahJobDriver_UnloadYourHauledInventoryType = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");
        static readonly MethodInfo PuahJobOnThing                              = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");
        static          WorkGiver  puahWorkGiver;

        static readonly bool havePuah = new List<object>
                {PuahCompHauledToInventoryType, PuahWorkGiver_HaulToInventoryType, PuahJobDriver_HaulToInventoryType, PuahJobDriver_UnloadYourHauledInventoryType, PuahJobOnThing}
            .All(x => x != null);

        static readonly Type        CsModType                 = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        static readonly Type        CsSettingsType            = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        static readonly FieldInfo   CsSettings                = AccessTools.DeclaredField(CsModType,      "Settings");
        static readonly FieldInfo   CsHaulingOverBillsSetting = AccessTools.DeclaredField(CsSettingsType, "hauling_over_bills");
        static          ModSettings csSettings;
        static readonly bool        haveCommonSense = new List<object> {CsModType, CsSettingsType, CsSettings, CsHaulingOverBillsSetting}.All(x => x != null);

        static Dictionary<SettingHandle, object> settingHandleControlInfo;

        static JobsOfOpportunity() {
            Helper.CatchStanding_Initialize(typeof(JobsOfOpportunity), new Harmony("CodeOptimist"));
        }

        public override void DefsLoaded() {
            puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamedSilentFail("HaulToInventory")?.Worker;
            csSettings = (ModSettings) CsSettings?.GetValue(LoadedModManager.GetMod(CsModType));
            // don't use ModContentPack.PackageId(PlayerFacing) because it can be changed e.g. "_copy_" suffix
            const string settingIdentifier = "CodeOptimist.JobsOfOpportunity";

            var s = new SettingsHelper(Settings, settingIdentifier);
            enabled = s.GetSettingHandle("enabled",                   true);
            haulToInventory = s.GetSettingHandle("haulToInventory",   true, default, HavePuah);
            haulBeforeSupply = s.GetSettingHandle("haulBeforeSupply", true);

            var haulBeforeBill_needsInitForCs = s.GetSettingHandle("haulBeforeBill_needsInitForCs", true, default, () => false);
            haulBeforeBill = s.GetSettingHandle("haulBeforeBill", true);
            if (haveCommonSense) {
                if (haulBeforeBill_needsInitForCs.Value) {
                    CsHaulingOverBillsSetting.SetValue(csSettings, false);
                    haulBeforeBill.Value = true;
                    haulBeforeBill_needsInitForCs.Value = false;
                } else if ((bool) CsHaulingOverBillsSetting.GetValue(csSettings))
                    haulBeforeBill.Value = false;

                Settings.SaveChanges();

                haulBeforeBill.OnValueChanged += value => {
                    if (value && (bool) CsHaulingOverBillsSetting.GetValue(csSettings)) {
                        CsHaulingOverBillsSetting.SetValue(csSettings, false);
                        Messages.Message(
                            "[Jobs of Opportunity] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                    }
                };
            }

            haulToEqualPriority = s.GetSettingHandle("haulToEqualPriority", true);
            skipIfBleeding = s.GetSettingHandle("skipIfBleeding",           true);
            haulProximities = s.GetSettingHandle(
                "haulProximities", Hauling.HaulProximities.Ignored, default, default, $"{settingIdentifier}_SettingTitle_haulProximities_");

            drawOpportunisticJobs = s.GetSettingHandle("drawOpportunisticJobs", DebugViewSettings.drawOpportunisticJobs);
            drawOpportunisticJobs.Unsaved = true;
            drawOpportunisticJobs.OnValueChanged += value => DebugViewSettings.drawOpportunisticJobs = value;

            showVanillaParameters = s.GetSettingHandle("showVanillaParameters", false);
            var ShowVanillaParameters = new SettingHandle.ShouldDisplay(() => showVanillaParameters.Value);

            var floatRangeValidator = Validators.FloatRangeValidator(0f, 999f);
            maxNewLegsPctOrigTrip = s.GetSettingHandle("maxNewLegsPctOrigTrip",     1.0f, floatRangeValidator, ShowVanillaParameters);
            maxTotalTripPctOrigTrip = s.GetSettingHandle("maxTotalTripPctOrigTrip", 1.7f, floatRangeValidator, ShowVanillaParameters);

            maxStartToThing = s.GetSettingHandle("maxStartToThing",                               30f,  floatRangeValidator,                  ShowVanillaParameters);
            maxStartToThingPctOrigTrip = s.GetSettingHandle("maxStartToThingPctOrigTrip",         0.5f, floatRangeValidator,                  ShowVanillaParameters);
            maxStartToThingRegionLookCount = s.GetSettingHandle("maxStartToThingRegionLookCount", 25,   Validators.IntRangeValidator(0, 999), ShowVanillaParameters);
            maxStoreToJob = s.GetSettingHandle("maxStoreToJob",                                   50f,  floatRangeValidator,                  ShowVanillaParameters);
            maxStoreToJobPctOrigTrip = s.GetSettingHandle("maxStoreToJobPctOrigTrip",             0.6f, floatRangeValidator,                  ShowVanillaParameters);
            maxStoreToJobRegionLookCount = s.GetSettingHandle("maxStoreToJobRegionLookCount",     25,   Validators.IntRangeValidator(0, 999), ShowVanillaParameters);
        }

        [HarmonyPatch(typeof(Dialog_ModSettings), "PopulateControlInfo")]
        static class Dialog_ModSettings_PopulateControlInfo_Patch
        {
            [HarmonyPrefix]
            static void DynamicSettings(Dictionary<SettingHandle, object> ___handleControlInfo) {
                settingHandleControlInfo = ___handleControlInfo;

                drawOpportunisticJobs.Value = DebugViewSettings.drawOpportunisticJobs;
            }
        }

        // Patching virtual methods crashes on Linux with Harmony 2.0.2 (e.g. Window.PostClose, Window.PreClose)
        // https://discord.com/channels/131466550938042369/674571535570305060/790008125150330880
        [HarmonyPatch(typeof(Dialog_VanillaModSettings), nameof(Dialog_VanillaModSettings.PreClose))]
        static class Dialog_VanillaModSettings_PreClose_Patch
        {
            [HarmonyPostfix]
            static void CheckCommonSenseSetting(Dialog_VanillaModSettings __instance) {
                if (haulBeforeBill.Value && haveCommonSense && (bool) CsHaulingOverBillsSetting.GetValue(csSettings)) {
                    haulBeforeBill.Value = false;
                    Traverse.Create(settingHandleControlInfo[haulBeforeBill]).Field<string>("inputValue").Value = "False";
                    Messages.Message("[Jobs of Opportunity] Unticked setting \"Optimize hauling ingredients\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                }
            }
        }
    }
}
