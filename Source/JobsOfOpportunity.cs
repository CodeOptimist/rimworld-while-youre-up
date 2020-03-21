using System;
using System.Collections.Generic;
using HarmonyLib;
using HugsLib;
using HugsLib.Settings;
using Verse;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity : ModBase
    {
        static string modIdentifier;

        static SettingHandle<HaulProximities> haulProximities;
        static SettingHandle<bool> showVanillaParameters;
        static SettingHandle<float> maxStartToThing, maxStartToThingPctOrigTrip, maxStoreToJob, maxStoreToJobPctOrigTrip, maxTotalTripPctOrigTrip, maxNewLegsPctOrigTrip;

        public override void DefsLoaded() {
            modIdentifier = ModContentPack.PackageIdPlayerFacing;

            SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue = default, SettingHandle.ValueIsValid validator = default,
                SettingHandle.ShouldDisplay shouldDisplay = default, string enumPrefix = default) {
                var settingHandle = Settings.GetHandle(
                    settingName, $"{modIdentifier}_SettingTitle_{settingName}".Translate(), $"{modIdentifier}_SettingDesc_{settingName}".Translate(), defaultValue, validator, enumPrefix);
                settingHandle.VisibilityPredicate = shouldDisplay;
                return settingHandle;
            }

            haulProximities = GetSettingHandle("haulProximities", HaulProximities.PreferWithin, default, default, $"{modIdentifier}_SettingTitle_haulProximities_");

            showVanillaParameters = GetSettingHandle("showVanillaParameters", false);
            var ShowVanillaParameters = new SettingHandle.ShouldDisplay(() => showVanillaParameters.Value);

            var floatRangeValidator = Validators.FloatRangeValidator(0f, 999f);
            maxNewLegsPctOrigTrip = GetSettingHandle("maxNewLegsPctOrigTrip", 1.0f, floatRangeValidator, ShowVanillaParameters);
            maxTotalTripPctOrigTrip = GetSettingHandle("maxTotalTripPctOrigTrip", 1.7f, floatRangeValidator, ShowVanillaParameters);

            maxStartToThing = GetSettingHandle("maxStartToThing", 30f, floatRangeValidator, ShowVanillaParameters);
            maxStartToThingPctOrigTrip = GetSettingHandle("maxStartToThingPctOrigTrip", 0.5f, floatRangeValidator, ShowVanillaParameters);
            maxStoreToJob = GetSettingHandle("maxStoreToJob", 50f, floatRangeValidator, ShowVanillaParameters);
            maxStoreToJobPctOrigTrip = GetSettingHandle("maxStoreToJobPctOrigTrip", 0.6f, floatRangeValidator, ShowVanillaParameters);
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

        enum HaulProximities { RequireWithin, PreferWithin, Ignore }
    }
}
