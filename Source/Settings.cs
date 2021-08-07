using System;
using System.Linq;
using System.Xml;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
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
            list.DrawBool(ref settings.HaulToEqualPriority, nameof(settings.HaulToEqualPriority));
            list.Gap();

            var leftRect = list.GetRect(0f).LeftHalf();
            var rightRect = list.GetRect(inRect.height - list.CurHeight).RightHalf();
            leftRect.height = rightRect.height;

            var leftList = new Listing_Standard();
            leftList.Begin(leftRect);
            leftList.Gap(leftRect.height - 30f);
            if (Widgets.ButtonText(leftList.GetRect(30f).LeftHalf(), "RestoreToDefaultSettings".Translate())) {
                settings.ExposeData(); // restore defaults
                SettingsWindow.optimizeHaulSearchWidget.Reset();
                SettingsWindow.ResetModFilter(SettingsWindow.optimizeHaulCategoryDef, settings.OptimizeHaul_BuildingFilter);
            }
            leftList.End();

            var rightList = new Listing_Standard();
            rightList.Begin(rightRect);
            rightList.Label($"{modId}_SettingTitle_OptimizeHaulingTo".Translate(), Text.LineHeight, $"{modId}_SettingDesc_OptimizeHaulingTo".Translate());

            var innerList = new Listing_Standard();
            var innerRect = rightList.GetRect(24f);
            innerRect.width -= 25f;
            innerList.Begin(innerRect);
            innerList.DrawBool(ref settings.OptimizeHaul_Auto, nameof(settings.OptimizeHaul_Auto));
            innerList.End();

            rightList.Gap(4f);
            SettingsWindow.optimizeHaulSearchWidget.OnGUI(rightList.GetRect(24f));
            rightList.Gap(4f);

            var outRect = rightList.GetRect(rightRect.height - rightList.CurHeight);
            var viewRect = new Rect(0f, 0f, outRect.width - 20f, SettingsWindow.optimizeHaulTreeFilter?.CurHeight ?? 10000f);
            Widgets.BeginScrollView(outRect, ref SettingsWindow.optimizeHaulScrollPosition, viewRect);
            if (settings.OptimizeHaul_Auto)
                SettingsWindow.optimizeHaulDummyFilter.CopyAllowancesFrom(settings.OptimizeHaulDefaultFilter);
            SettingsWindow.optimizeHaulTreeFilter = new Listing_SettingsTreeThingFilter(
                settings.OptimizeHaul_Auto ? SettingsWindow.optimizeHaulDummyFilter : settings.OptimizeHaul_BuildingFilter, null, null, null, null,
                SettingsWindow.optimizeHaulSearchFilter);
            SettingsWindow.optimizeHaulTreeFilter.Begin(viewRect);
            SettingsWindow.optimizeHaulTreeFilter.ListCategoryChildren(SettingsWindow.optimizeHaulCategoryDef.treeNode, 1, null, viewRect);
            SettingsWindow.optimizeHaulTreeFilter.End();
            Widgets.EndScrollView();
            rightList.End();

            list.End();
        }

        // ReSharper disable UnusedType.Local
        // ReSharper disable UnusedMember.Local
        [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
        static class Log_Error_Patch
        {
            static bool         ignoreLoadReferenceErrors;
            static LoadSaveMode scribeMode;

            public static void SuppressLoadReferenceErrors(Action action) {
                scribeMode = Scribe.mode;
                Scribe.mode = LoadSaveMode.LoadingVars;
                ignoreLoadReferenceErrors = true;

                void Restore() {
                    ignoreLoadReferenceErrors = false;
                    Scribe.mode = scribeMode;
                }

                try {
                    action();
                } catch (Exception) {
                    Restore();
                    throw;
                } finally {
                    Restore();
                }
            }

            [HarmonyPrefix]
            static bool IgnoreCouldNotLoadReferenceOfRemovedModStorageBuildings(string text) {
                if (ignoreLoadReferenceErrors && text.StartsWith("Could not load reference to "))
                    return false;
                return true;
            }
        }
        // ReSharper restore UnusedType.Local
        // ReSharper restore UnusedMember.Local

        // Don't reference this except in DoSettingsWindowContents()! Referencing it early will trigger the static constructor before defs are loaded.
        [StaticConstructorOnStartup]
        public static class SettingsWindow
        {
            public static Vector2                         optimizeHaulScrollPosition;
            public static Listing_SettingsTreeThingFilter optimizeHaulTreeFilter;
            public static QuickSearchFilter               optimizeHaulSearchFilter = new QuickSearchFilter();
            public static QuickSearchWidget               optimizeHaulSearchWidget = new QuickSearchWidget();
            public static ThingCategoryDef                optimizeHaulCategoryDef;

            public static ThingFilter optimizeHaulDummyFilter = new ThingFilter();

            static SettingsWindow() {
                // now that defs are loaded this will work
                Log_Error_Patch.SuppressLoadReferenceErrors(
                    () => settings.OptimizeHaul_BuildingFilter = ScribeExtractor.SaveableFromNode<ThingFilter>(settings.optimizeHaulFilterXmlNode, null));
                optimizeHaulSearchWidget.filter = optimizeHaulSearchFilter;

                var storageBuildingTypes = typeof(Building_Storage).AllSubclassesNonAbstract();
                storageBuildingTypes.Add(typeof(Building_Storage));
                optimizeHaulCategoryDef = new ThingCategoryDef();
                var storageBuildings = DefDatabase<ThingDef>.AllDefsListForReading.Where(x => storageBuildingTypes.Contains(x.thingClass)).ToList();
                foreach (var storageMod in storageBuildings.Select(x => x.modContentPack).Distinct()) {
                    if (storageMod == null) continue;
                    var modCategoryDef = new ThingCategoryDef { label = storageMod.Name };
                    optimizeHaulCategoryDef.childCategories.Add(modCategoryDef);
                    modCategoryDef.childThingDefs.AddRange(storageBuildings.Where(x => x.modContentPack == storageMod).Select(x => x));
                    modCategoryDef.PostLoad();
                    modCategoryDef.ResolveReferences();
                }

                optimizeHaulCategoryDef.PostLoad();
                optimizeHaulCategoryDef.ResolveReferences();

                ResetModFilter(optimizeHaulCategoryDef, settings.OptimizeHaulDefaultFilter);
                if (settings.OptimizeHaul_BuildingFilter == null) {
                    settings.OptimizeHaul_BuildingFilter = new ThingFilter();
                    settings.OptimizeHaul_BuildingFilter?.CopyAllowancesFrom(settings.OptimizeHaulDefaultFilter);
                }
            }

            public static void ResetModFilter(ThingCategoryDef thingCategoryDef, ThingFilter thingFilter) {
                thingFilter.SetDisallowAll();

                foreach (var modCategoryDef in thingCategoryDef.childCategories) {
                    modCategoryDef.treeNode.SetOpen(1, false);

                    var mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(x => x.Name == modCategoryDef.label);
                    Debug.WriteLine($"{mod?.PackageId}, {mod?.Name}");
                    switch (mod?.PackageId) {
                        case "ludeon.rimworld": // Core
                            modCategoryDef.treeNode.SetOpen(1, true);
                            goto case "vanillaexpanded.vfecore";
                        case "vanillaexpanded.vfecore":   // Vanilla Furniture Expanded
                        case "skullywag.extendedstorage": // Extended Storage
                        case "mlie.extendedstorage":      // Extended Storage (Continued)
                        case "lwm.deepstorage":           // LWM's Deep Storage
                        case "rimfridge.kv.rw":           // [KV] RimFridge
                        case "solaris.furniturebase":     // GloomyFurniture
                        case "jangodsoul.simplestorage":  // [JDS] Simple Storage
                        case "sixdd.littlestorage2":      // Little Storage 2
                            thingFilter.SetAllow(modCategoryDef, true);
                            break;
                    }
                }
            }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        class Settings : ModSettings
        {
            public readonly ThingFilter OptimizeHaulDefaultFilter = new ThingFilter();
            public          ThingFilter OptimizeHaul_BuildingFilter;
            internal        XmlNode     optimizeHaulFilterXmlNode;

            public bool                Enabled, HaulToInventory, HaulBeforeSupply, HaulBeforeBill, HaulBeforeBill_NeedsInitForCs, HaulToEqualPriority, SkipIfBleeding,
                DrawOpportunisticJobs, OptimizeHaul_Auto;

            public Hauling.HaulProximities HaulProximities;
            public bool                    ShowVanillaParameters;
            public float                   MaxStartToThing, MaxStartToThingPctOrigTrip, MaxStoreToJob, MaxStoreToJobPctOrigTrip, MaxTotalTripPctOrigTrip, MaxNewLegsPctOrigTrip;
            public int                     MaxStartToThingRegionLookCount, MaxStoreToJobRegionLookCount;

            // we also manually call this to restore defaults and to set them before config file exists (Scribe.mode == LoadSaveMode.Inactive)
            public override void ExposeData() {
                foundConfig = true;

                void Look<T>(ref T value, string label, T defaultValue) {
                    if (Scribe.mode == LoadSaveMode.Inactive)
                        value = defaultValue;

                    Scribe_Values.Look(ref value, label, defaultValue);
                }

                Look(ref Enabled,                        nameof(Enabled),                        true);
                Look(ref HaulToInventory,                nameof(HaulToInventory),                true);
                Look(ref HaulBeforeSupply,               nameof(HaulBeforeSupply),               true);
                Look(ref HaulBeforeBill,                 nameof(HaulBeforeBill),                 true);
                Look(ref HaulBeforeBill_NeedsInitForCs,  nameof(HaulBeforeBill_NeedsInitForCs),  true);
                Look(ref HaulToEqualPriority,            nameof(HaulToEqualPriority) + "_2.1.0", true);
                Look(ref OptimizeHaul_Auto,              nameof(OptimizeHaul_Auto),              true);
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

                if (Scribe.mode == LoadSaveMode.Saving)
                    Scribe_Deep.Look(ref OptimizeHaul_BuildingFilter, nameof(OptimizeHaul_BuildingFilter));
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                    optimizeHaulFilterXmlNode = Scribe.loader.curXmlParent[nameof(OptimizeHaul_BuildingFilter)]; // so we can load later after Defs

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
