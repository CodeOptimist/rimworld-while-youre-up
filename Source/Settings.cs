using CodeOptimist;
using UnityEngine;
using Verse; // ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        public override void DoSettingsWindowContents(Rect inRect) {
            Gui.modId = modId;
            var list = new Listing_Standard();
            list.Begin(inRect);

            list.DrawBool(ref settings.Enabled, nameof(settings.Enabled));
            if (ModLister.HasActiveModWithName("Pick Up And Haul"))
                list.DrawBool(ref settings.HaulToInventory, nameof(settings.HaulToInventory));
            list.DrawBool(ref settings.DrawOpportunisticJobs, nameof(settings.DrawOpportunisticJobs));

            list.Gap();
            list.DrawEnum(settings.HaulProximities, nameof(settings.HaulProximities), val => { settings.HaulProximities = val; });
            list.DrawBool(ref settings.SkipIfBleeding, nameof(settings.SkipIfBleeding));

            list.DrawBool(ref settings.ShowVanillaParameters, nameof(settings.ShowVanillaParameters));
            if (settings.ShowVanillaParameters) {
                using (new DrawContext { TextAnchor = TextAnchor.MiddleRight }) {
                    list.DrawFloat(ref settings.MaxNewLegsPctOrigTrip,      nameof(settings.MaxNewLegsPctOrigTrip));
                    list.DrawFloat(ref settings.MaxTotalTripPctOrigTrip,    nameof(settings.MaxTotalTripPctOrigTrip));
                    list.DrawFloat(ref settings.MaxStartToThing,            nameof(settings.MaxStartToThing));
                    list.DrawFloat(ref settings.MaxStartToThingPctOrigTrip, nameof(settings.MaxStartToThingPctOrigTrip));
                    list.DrawInt(ref settings.MaxStartToThingRegionLookCount, nameof(settings.MaxStartToThingRegionLookCount));
                    list.DrawFloat(ref settings.MaxStoreToJob,            nameof(settings.MaxStoreToJob));
                    list.DrawFloat(ref settings.MaxStoreToJobPctOrigTrip, nameof(settings.MaxStoreToJobPctOrigTrip));
                    list.DrawInt(ref settings.MaxStoreToJobRegionLookCount, nameof(settings.MaxStoreToJobRegionLookCount));
                }
            }

            list.Gap();
            list.DrawBool(ref settings.HaulBeforeSupply,    nameof(settings.HaulBeforeSupply));
            list.DrawBool(ref settings.HaulBeforeBill,      nameof(settings.HaulBeforeBill));
            list.DrawBool(ref settings.StockpilesOnly,      nameof(settings.StockpilesOnly));
            list.DrawBool(ref settings.HaulToEqualPriority, nameof(settings.HaulToEqualPriority));

            list.Gap(12f * 4);
            if (Widgets.ButtonText(list.GetRect(30f).LeftPart(0.25f), "RestoreToDefaultSettings".Translate()))
                settings.ExposeData();

            list.End();
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        class Settings : ModSettings
        {
            public bool Enabled, StockpilesOnly, HaulToInventory, HaulBeforeSupply, HaulBeforeBill, HaulBeforeBill_NeedsInitForCs, HaulToEqualPriority, SkipIfBleeding,
                DrawOpportunisticJobs;

            public Hauling.HaulProximities HaulProximities;
            public bool                    ShowVanillaParameters;
            public float                   MaxStartToThing, MaxStartToThingPctOrigTrip, MaxStoreToJob, MaxStoreToJobPctOrigTrip, MaxTotalTripPctOrigTrip, MaxNewLegsPctOrigTrip;
            public int                     MaxStartToThingRegionLookCount, MaxStoreToJobRegionLookCount;

            public override void ExposeData() {
                foundConfig = true;

                void Look<T>(ref T value, string label, T defaultValue) {
                    if (Scribe.mode == LoadSaveMode.Inactive)
                        value = defaultValue;

                    Scribe_Values.Look(ref value, label, defaultValue);
                }

                Look(ref Enabled,                        nameof(Enabled),                        true);
                Look(ref StockpilesOnly,                 nameof(StockpilesOnly),                 true);
                Look(ref HaulToInventory,                nameof(HaulToInventory),                true);
                Look(ref HaulBeforeSupply,               nameof(HaulBeforeSupply),               true);
                Look(ref HaulBeforeBill,                 nameof(HaulBeforeBill),                 true);
                Look(ref HaulBeforeBill_NeedsInitForCs,  nameof(HaulBeforeBill_NeedsInitForCs),  true);
                Look(ref HaulToEqualPriority,            nameof(HaulToEqualPriority) + "_2.1.0", true);
                Look(ref SkipIfBleeding,                 nameof(SkipIfBleeding),                 true);
                Look(ref HaulProximities,                nameof(HaulProximities),                Hauling.HaulProximities.Ignored);
                Look(ref DrawOpportunisticJobs,          nameof(DrawOpportunisticJobs),          false);
                Look(ref ShowVanillaParameters,          nameof(ShowVanillaParameters),          false);
                Look(ref MaxStartToThing,                nameof(MaxStartToThing),                30f);
                Look(ref MaxStartToThingPctOrigTrip,     nameof(MaxStartToThingPctOrigTrip),     0.5f);
                Look(ref MaxStoreToJob,                  nameof(MaxStoreToJob),                  50f);
                Look(ref MaxStoreToJobPctOrigTrip,       nameof(MaxStoreToJobPctOrigTrip),       0.6f);
                Look(ref MaxTotalTripPctOrigTrip,        nameof(MaxTotalTripPctOrigTrip),        1.7f);
                Look(ref MaxNewLegsPctOrigTrip,          nameof(MaxNewLegsPctOrigTrip),          1.0f);
                Look(ref MaxStartToThingRegionLookCount, nameof(MaxStartToThingRegionLookCount), 25);
                Look(ref MaxStoreToJobRegionLookCount,   nameof(MaxStoreToJobRegionLookCount),   25);

                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.Saving)
                    DebugViewSettings.drawOpportunisticJobs = DrawOpportunisticJobs;

                if (Scribe.mode == LoadSaveMode.LoadingVars) {
                    if (haveCommonSense) {
                        if (HaulBeforeBill_NeedsInitForCs) {
                            CsHaulingOverBillsSetting.SetValue(null, false);
                            HaulBeforeBill = true;
                            HaulBeforeBill_NeedsInitForCs = false;
                        } else if ((bool)CsHaulingOverBillsSetting.GetValue(null))
                            HaulBeforeBill = false;
                    }
                }
            }
        }
    }
}
