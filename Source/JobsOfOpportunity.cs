using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodeOptimist;
using HarmonyLib;
using HugsLib.Settings;
using RimWorld;
using Verse; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    [StaticConstructorOnStartup]
    partial class JobsOfOpportunity : Mod
    {
        const  string   modId = "CodeOptimist.JobsOfOpportunity"; // explicit because PackageId may be changed e.g. "__copy__" suffix
        static Mod      mod;
        static Settings settings;

        static readonly Type       PuahCompHauledToInventoryType               = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory");
        static readonly Type       PuahWorkGiver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
        static readonly Type       PuahJobDriver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
        static readonly Type       PuahJobDriver_UnloadYourHauledInventoryType = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");
        static readonly MethodInfo PuahJobOnThing                              = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");

        static readonly bool havePuah = new List<object>
                {PuahCompHauledToInventoryType, PuahWorkGiver_HaulToInventoryType, PuahJobDriver_HaulToInventoryType, PuahJobDriver_UnloadYourHauledInventoryType, PuahJobOnThing}
            .All(x => x != null);

        static readonly Type      CsModType                 = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        static readonly Type      CsSettingsType            = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        static readonly FieldInfo CsHaulingOverBillsSetting = AccessTools.DeclaredField(CsSettingsType, "hauling_over_bills");

        static readonly bool haveCommonSense = new List<object> {CsModType, CsSettingsType, CsHaulingOverBillsSetting}.All(x => x != null);

        static JobsOfOpportunity() {
            Helper.CatchStanding_Initialize(typeof(JobsOfOpportunity), new Harmony("CodeOptimist"));
        }

        public JobsOfOpportunity(ModContentPack content) : base(content) {
            mod = this;
            settings = GetSettings<Settings>();
            if (!settings.exposedData)
                settings.ExposeData();

            var harmony = new Harmony(modId);
            harmony.PatchAll();
        }

        public override string SettingsCategory() {
            return mod.Content.Name;
        }

        // ReSharper disable UnusedType.Local
        // ReSharper disable UnusedMember.Local
        [HarmonyPatch(typeof(Dialog_VanillaModSettings), MethodType.Constructor, typeof(Mod))]
        static class Dialog_VanillaModSettings_Dialog_VanillaModSettings_Patch
        {
            [HarmonyPostfix]
            static void SyncDrawSettingToVanilla(Mod mod) {
                if (mod == JobsOfOpportunity.mod)
                    settings.DrawOpportunisticJobs = DebugViewSettings.drawOpportunisticJobs;
            }
        }

        [HarmonyPatch(typeof(Dialog_VanillaModSettings), nameof(Dialog_VanillaModSettings.PreClose))]
        static class Dialog_VanillaModSettings_PreClose_Patch
        {
            [HarmonyPostfix]
            static void CheckCommonSenseSetting(Mod ___selectedMod) {
                if (settings.HaulBeforeBill && haveCommonSense && (bool) CsHaulingOverBillsSetting.GetValue(null)) {
                    var csMod = LoadedModManager.GetMod(CsModType);
                    if (___selectedMod == mod) {
                        CsHaulingOverBillsSetting.SetValue(null, false);
                        csMod.WriteSettings();
                        Messages.Message(
                            "[Jobs of Opportunity] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                    } else if (___selectedMod == csMod) {
                        settings.HaulBeforeBill = false;
                        //mod.WriteSettings(); // no save because we handle it best on loading
                        Messages.Message("[Jobs of Opportunity] Unticked setting \"Optimize hauling ingredients\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                    }
                }
            }
        }
        // ReSharper restore UnusedType.Local
        // ReSharper restore UnusedMember.Local
    }
}
