using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [HarmonyPatch]
        static class Dialog_ModSettings__Dialog_ModSettings_Patch
        {
            static MethodBase TargetMethod() {
                if (haveHugs)
                    return AccessTools.DeclaredConstructor(HugsDialog_VanillaModSettingsType, new[] { typeof(Verse.Mod) });
                return AccessTools.DeclaredConstructor(typeof(Dialog_ModSettings), new[] { typeof(Verse.Mod) });
            }

            [HarmonyPostfix]
            static void SyncDrawSettingToVanilla() => settings.DrawSpecialHauls = DebugViewSettings.drawOpportunisticJobs;
        }

        [HarmonyPatch]
        static class Dialog_ModSettings__DoWindowContents_Patch
        {
            static MethodBase TargetMethod() {
                if (haveHugs)
                    return AccessTools.DeclaredMethod(HugsDialog_VanillaModSettingsType, "DoWindowContents");
                return AccessTools.DeclaredMethod(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents));
            }

            [HarmonyPostfix]
            static void CheckCommonSenseSetting(object __instance) {
                var curMod = SettingsCurMod.GetValue(__instance);

                if (settings.HaulBeforeCarry_Bills && haveCommonSense && (bool)CsHaulingOverBillsSetting.GetValue(null)) {
                    var csMod = LoadedModManager.GetMod(CsModType);
                    if (curMod == mod) {
                        CsHaulingOverBillsSetting.SetValue(null, false);
                        csMod.WriteSettings();
                        Messages.Message(
                            $"[{mod.Content.Name}] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)", MessageTypeDefOf.SilentInput, false);
                    } else if (curMod == csMod) {
                        settings.HaulBeforeCarry_Bills = false;
                        //mod.WriteSettings(); // no save because we handle it best on loading
                        Messages.Message(
                            $"[{mod.Content.Name}] Unticked setting in While You're Up: \"Haul extra bill ingredients closer\". (Can't use both.)",
                            MessageTypeDefOf.SilentInput,
                            false);
                    }
                }
            }
        }
    }
}
