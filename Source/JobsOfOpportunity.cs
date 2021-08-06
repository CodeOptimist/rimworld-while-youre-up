using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity : Mod
    {
        const           string   modId = "CodeOptimist.JobsOfOpportunity"; // explicit because PackageId may be changed e.g. "__copy__" suffix
        static          Mod      mod;
        static          Settings settings;
        static          bool     foundConfig;
        static readonly Harmony  harmony = new Harmony(modId);

        static readonly Type       PuahCompHauledToInventoryType               = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory");
        static readonly Type       PuahWorkGiver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
        static readonly Type       PuahJobDriver_HaulToInventoryType           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
        static readonly Type       PuahJobDriver_UnloadYourHauledInventoryType = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");
        static readonly MethodInfo PuahJobOnThing                              = AccessTools.DeclaredMethod(PuahWorkGiver_HaulToInventoryType, "JobOnThing");

        static readonly bool havePuah = new List<object>
                { PuahCompHauledToInventoryType, PuahWorkGiver_HaulToInventoryType, PuahJobDriver_HaulToInventoryType, PuahJobDriver_UnloadYourHauledInventoryType, PuahJobOnThing }
            .All(x => x != null);

        static readonly Type HugsDialog_VanillaModSettingsType = GenTypes.GetTypeInAnyAssembly("HugsLib.Settings.Dialog_VanillaModSettings");

        static readonly bool haveHugs = HugsDialog_VanillaModSettingsType != null;

        static readonly Type      CsModType                 = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        static readonly Type      CsSettingsType            = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        static readonly FieldInfo CsHaulingOverBillsSetting = AccessTools.DeclaredField(CsSettingsType, "hauling_over_bills");

        static readonly bool haveCommonSense = new List<object> { CsModType, CsSettingsType, CsHaulingOverBillsSetting }.All(x => x != null);

        static JobsOfOpportunity() {
            Helper.CatchStanding_Initialize(typeof(JobsOfOpportunity), new Harmony("CodeOptimist"));
        }

        public JobsOfOpportunity(ModContentPack content) : base(content) {
            mod = this;
            settings = GetSettings<Settings>();
            if (!foundConfig)
                settings.ExposeData(); // initialize to defaults

            harmony.PatchAll();
        }

        public override string SettingsCategory() {
            return mod.Content.Name;
        }

        // ReSharper disable UnusedType.Local
        // ReSharper disable UnusedMember.Local

        [HarmonyPatch]
        static class Dialog_ModSettings_Dialog_ModSettings_Patch
        {
            static MethodBase TargetMethod() {
                if (haveHugs)
                    return AccessTools.DeclaredConstructor(HugsDialog_VanillaModSettingsType, new[] { typeof(Mod) });
                return AccessTools.DeclaredConstructor(typeof(Dialog_ModSettings));
            }

            [HarmonyPostfix]
            static void SyncDrawSettingToVanilla() => settings.DrawOpportunisticJobs = DebugViewSettings.drawOpportunisticJobs;
        }

        [HarmonyPatch]
        static class Dialog_ModSettings_DoWindowContents_Patch
        {
            static MethodBase TargetMethod() {
                if (haveHugs)
                    return AccessTools.DeclaredMethod(HugsDialog_VanillaModSettingsType, "DoWindowContents");
                return AccessTools.DeclaredMethod(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents));
            }

            [HarmonyPostfix]
            static void CheckCommonSenseSetting(object __instance) {
                var selModField = haveHugs
                    ? AccessTools.DeclaredField(HugsDialog_VanillaModSettingsType, "selectedMod")
                    : AccessTools.DeclaredField(typeof(Dialog_ModSettings),        "selMod");
                var selMod = selModField.GetValue(__instance);

                if (settings.HaulBeforeBill && haveCommonSense && (bool)CsHaulingOverBillsSetting.GetValue(null)) {
                    var csMod = LoadedModManager.GetMod(CsModType);
                    if (selMod == mod) {
                        CsHaulingOverBillsSetting.SetValue(null, false);
                        csMod.WriteSettings();
                        Messages.Message(
                            $"[{mod.Content.Name}] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                    } else if (selMod == csMod) {
                        settings.HaulBeforeBill = false;
                        //mod.WriteSettings(); // no save because we handle it best on loading
                        Messages.Message(
                            $"[{mod.Content.Name}] Unticked setting in Jobs of Opportunity: \"Optimize hauling ingredients\". (Can't use both.)", MessageTypeDefOf.SilentInput,
                            false);
                    }
                }
            }
        }

        // ReSharper restore UnusedType.Local
        // ReSharper restore UnusedMember.Local
    }
}
