using System;
using System.Diagnostics.CodeAnalysis;
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
    partial class Mod
    {
        public override void DoSettingsWindowContents(Rect inRect) => SettingsWindow.DoWindowContents(inRect);

        [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
        [SuppressMessage("ReSharper", "UnusedMember.Local")]
        static class Log__Error_Patch
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

        // Don't reference this except in DoSettingsWindowContents()! Referencing it early will trigger the static constructor before defs are loaded.
        [StaticConstructorOnStartup]
        public static class SettingsWindow
        {
            public static Vector2                         hbcScrollPosition;
            public static Listing_SettingsTreeThingFilter hbcTreeFilter;
            public static QuickSearchFilter               hbcSearchFilter = new QuickSearchFilter();
            public static QuickSearchWidget               hbcSearchWidget = new QuickSearchWidget();
            public static ThingFilter                     hbcDummyFilter  = new ThingFilter();

            public static ThingCategoryDef storageBuildingCategoryDef;

            static SettingsWindow() {
                // now that defs are loaded this will work
                Log__Error_Patch.SuppressLoadReferenceErrors(
                    () => settings.HaulBeforeCarry_BuildingFilter = ScribeExtractor.SaveableFromNode<ThingFilter>(settings.hbcBuildingFilterXmlNode, null));
                hbcSearchWidget.filter = hbcSearchFilter;

                var storageBuildingTypes = typeof(Building_Storage).AllSubclassesNonAbstract();
                storageBuildingTypes.Add(typeof(Building_Storage));
                storageBuildingCategoryDef = new ThingCategoryDef();
                var storageBuildings = DefDatabase<ThingDef>.AllDefsListForReading.Where(x => storageBuildingTypes.Contains(x.thingClass)).ToList();
                foreach (var storageMod in storageBuildings.Select(x => x.modContentPack).Distinct()) {
                    if (storageMod == null) continue;
                    var modCategoryDef = new ThingCategoryDef { label = storageMod.Name };
                    storageBuildingCategoryDef.childCategories.Add(modCategoryDef);
                    modCategoryDef.childThingDefs.AddRange(storageBuildings.Where(x => x.modContentPack == storageMod).Select(x => x));
                    modCategoryDef.PostLoad();
                    modCategoryDef.ResolveReferences();
                }

                storageBuildingCategoryDef.PostLoad();
                storageBuildingCategoryDef.ResolveReferences();

                ResetModFilter(storageBuildingCategoryDef, settings.HaulBeforeCarry_DefaultBuildingFilter);
                if (settings.HaulBeforeCarry_BuildingFilter == null) {
                    settings.HaulBeforeCarry_BuildingFilter = new ThingFilter();
                    settings.HaulBeforeCarry_BuildingFilter?.CopyAllowancesFrom(settings.HaulBeforeCarry_DefaultBuildingFilter);
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
                        // case "lwm.deepstorage":           // LWM's Deep Storage
                        case "rimfridge.kv.rw":          // [KV] RimFridge
                        case "solaris.furniturebase":    // GloomyFurniture
                        case "jangodsoul.simplestorage": // [JDS] Simple Storage
                        case "sixdd.littlestorage2":     // Little Storage 2
                            thingFilter.SetAllow(modCategoryDef, true);
                            break;
                    }
                }
            }

            public static void DoWindowContents(Rect inRect) {
                Gui.modId = modId;
                var list = new Listing_Standard();
                list.Begin(inRect);

                list.DrawBool(ref settings.Enabled, nameof(settings.Enabled));
                if (ModLister.HasActiveModWithName("Pick Up And Haul"))
                    list.DrawBool(ref settings.UsePickUpAndHaulPlus, nameof(settings.UsePickUpAndHaulPlus));
                list.DrawBool(ref settings.DrawSpecialHauls, nameof(settings.DrawSpecialHauls));
                list.Gap();

                list.DrawEnum(settings.Opportunity_HaulProximities, nameof(settings.Opportunity_HaulProximities), val => { settings.Opportunity_HaulProximities = val; });
                list.DrawBool(ref settings.Opportunity_SkipIfCaravan,  nameof(settings.Opportunity_SkipIfCaravan));
                list.DrawBool(ref settings.Opportunity_SkipIfBleeding, nameof(settings.Opportunity_SkipIfBleeding));

                list.DrawBool(ref settings.Opportunity_TweakVanilla, nameof(settings.Opportunity_TweakVanilla));
                if (settings.Opportunity_TweakVanilla) {
                    using (new DrawContext { TextAnchor = TextAnchor.MiddleRight }) {
                        list.DrawFloat(ref settings.Opportunity_MaxNewLegsPctOrigTrip,      nameof(settings.Opportunity_MaxNewLegsPctOrigTrip));
                        list.DrawFloat(ref settings.Opportunity_MaxTotalTripPctOrigTrip,    nameof(settings.Opportunity_MaxTotalTripPctOrigTrip));
                        list.DrawFloat(ref settings.Opportunity_MaxStartToThing,            nameof(settings.Opportunity_MaxStartToThing));
                        list.DrawFloat(ref settings.Opportunity_MaxStartToThingPctOrigTrip, nameof(settings.Opportunity_MaxStartToThingPctOrigTrip));
                        list.DrawInt(ref settings.Opportunity_MaxStartToThingRegionLookCount, nameof(settings.Opportunity_MaxStartToThingRegionLookCount));
                        list.DrawFloat(ref settings.Opportunity_MaxStoreToJob,            nameof(settings.Opportunity_MaxStoreToJob));
                        list.DrawFloat(ref settings.Opportunity_MaxStoreToJobPctOrigTrip, nameof(settings.Opportunity_MaxStoreToJobPctOrigTrip));
                        list.DrawInt(ref settings.Opportunity_MaxStoreToJobRegionLookCount, nameof(settings.Opportunity_MaxStoreToJobRegionLookCount));
                    }
                }
                list.Gap();

                list.DrawBool(ref settings.HaulBeforeCarry_Supplies,        nameof(settings.HaulBeforeCarry_Supplies));
                list.DrawBool(ref settings.HaulBeforeCarry_Bills,           nameof(settings.HaulBeforeCarry_Bills));
                list.DrawBool(ref settings.HaulBeforeCarry_ToEqualPriority, nameof(settings.HaulBeforeCarry_ToEqualPriority));
                list.Gap();

                var leftRect = list.GetRect(0f).LeftHalf();
                var rightRect = list.GetRect(inRect.height - list.CurHeight).RightHalf();
                leftRect.height = rightRect.height;

                var leftList = new Listing_Standard();
                leftList.Begin(leftRect);
                leftList.Gap(leftRect.height - 30f);
                if (Widgets.ButtonText(leftList.GetRect(30f).LeftHalf(), "RestoreToDefaultSettings".Translate())) {
                    settings.ExposeData(); // restore defaults
                    hbcSearchWidget.Reset();
                    ResetModFilter(storageBuildingCategoryDef, settings.HaulBeforeCarry_BuildingFilter);
                }
                leftList.End();

                var rightList = new Listing_Standard();
                rightList.Begin(rightRect);
                rightList.Label($"{modId}_SettingTitle_OptimizeHaulingTo".Translate(), Text.LineHeight, $"{modId}_SettingDesc_OptimizeHaulingTo".Translate());

                var innerList = new Listing_Standard();
                var innerRect = rightList.GetRect(24f);
                innerRect.width -= 25f;
                innerList.Begin(innerRect);
                innerList.DrawBool(ref settings.HaulBeforeCarry_AutoBuildings, nameof(settings.HaulBeforeCarry_AutoBuildings));
                innerList.End();

                rightList.Gap(4f);
                hbcSearchWidget.OnGUI(rightList.GetRect(24f));
                rightList.Gap(4f);

                var outRect = rightList.GetRect(rightRect.height - rightList.CurHeight);
                var viewRect = new Rect(0f, 0f, outRect.width - 20f, hbcTreeFilter?.CurHeight ?? 10000f);
                Widgets.BeginScrollView(outRect, ref hbcScrollPosition, viewRect);
                if (settings.HaulBeforeCarry_AutoBuildings)
                    hbcDummyFilter.CopyAllowancesFrom(settings.HaulBeforeCarry_DefaultBuildingFilter);
                hbcTreeFilter = new Listing_SettingsTreeThingFilter(
                    settings.HaulBeforeCarry_AutoBuildings ? hbcDummyFilter : settings.HaulBeforeCarry_BuildingFilter, null, null, null, null,
                    hbcSearchFilter);
                hbcTreeFilter.Begin(viewRect);
                hbcTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, viewRect);
                hbcTreeFilter.End();
                Widgets.EndScrollView();
                rightList.End();

                list.End();
            }
        }

        [SuppressMessage("ReSharper", "ClassNeverInstantiated.Local")]
        class Settings : ModSettings
        {
            public bool Enabled, UsePickUpAndHaulPlus, DrawSpecialHauls;


            public enum HaulProximitiesEnum { Both, Either, Ignored }

            public HaulProximitiesEnum Opportunity_HaulProximities;
            public bool                Opportunity_SkipIfCaravan, Opportunity_SkipIfBleeding, Opportunity_TweakVanilla;

            public float Opportunity_MaxStartToThing, Opportunity_MaxStartToThingPctOrigTrip, Opportunity_MaxStoreToJob, Opportunity_MaxStoreToJobPctOrigTrip,
                Opportunity_MaxTotalTripPctOrigTrip, Opportunity_MaxNewLegsPctOrigTrip;

            public int Opportunity_MaxStartToThingRegionLookCount, Opportunity_MaxStoreToJobRegionLookCount;


            public bool HaulBeforeCarry_Supplies, HaulBeforeCarry_Bills, HaulBeforeCarry_Bills_NeedsInitForCs, HaulBeforeCarry_ToEqualPriority, HaulBeforeCarry_AutoBuildings;

            public readonly ThingFilter HaulBeforeCarry_DefaultBuildingFilter = new ThingFilter();
            public          ThingFilter HaulBeforeCarry_BuildingFilter;
            internal        XmlNode     hbcBuildingFilterXmlNode;

            // we also manually call this to restore defaults and to set them before config file exists (Scribe.mode == LoadSaveMode.Inactive)
            public override void ExposeData() {
                foundConfig = true;

                void Look<T>(ref T value, string label, T defaultValue) {
                    if (Scribe.mode == LoadSaveMode.Inactive)
                        value = defaultValue;

                    Scribe_Values.Look(ref value, label, defaultValue);
                }

                Look(ref Enabled,                                    nameof(Enabled),                                    true);
                Look(ref UsePickUpAndHaulPlus,                       nameof(UsePickUpAndHaulPlus),                       true);
                Look(ref DrawSpecialHauls,                           nameof(DrawSpecialHauls),                           false);
                Look(ref Opportunity_SkipIfCaravan,                  nameof(Opportunity_SkipIfCaravan),                  true);
                Look(ref Opportunity_SkipIfBleeding,                 nameof(Opportunity_SkipIfBleeding),                 true);
                Look(ref Opportunity_HaulProximities,                nameof(Opportunity_HaulProximities),                HaulProximitiesEnum.Ignored);
                Look(ref Opportunity_TweakVanilla,                   nameof(Opportunity_TweakVanilla),                   false);
                Look(ref Opportunity_MaxStartToThing,                nameof(Opportunity_MaxStartToThing),                30f);
                Look(ref Opportunity_MaxStartToThingPctOrigTrip,     nameof(Opportunity_MaxStartToThingPctOrigTrip),     0.5f);
                Look(ref Opportunity_MaxStoreToJob,                  nameof(Opportunity_MaxStoreToJob),                  50f);
                Look(ref Opportunity_MaxStoreToJobPctOrigTrip,       nameof(Opportunity_MaxStoreToJobPctOrigTrip),       0.6f);
                Look(ref Opportunity_MaxTotalTripPctOrigTrip,        nameof(Opportunity_MaxTotalTripPctOrigTrip),        1.7f);
                Look(ref Opportunity_MaxNewLegsPctOrigTrip,          nameof(Opportunity_MaxNewLegsPctOrigTrip),          1.0f);
                Look(ref Opportunity_MaxStartToThingRegionLookCount, nameof(Opportunity_MaxStartToThingRegionLookCount), 25);
                Look(ref Opportunity_MaxStoreToJobRegionLookCount,   nameof(Opportunity_MaxStoreToJobRegionLookCount),   25);
                Look(ref HaulBeforeCarry_Supplies,                   nameof(HaulBeforeCarry_Supplies),                   true);
                Look(ref HaulBeforeCarry_Bills,                      nameof(HaulBeforeCarry_Bills),                      true);
                Look(ref HaulBeforeCarry_Bills_NeedsInitForCs,       nameof(HaulBeforeCarry_Bills_NeedsInitForCs),       true);
                Look(ref HaulBeforeCarry_ToEqualPriority,            nameof(HaulBeforeCarry_ToEqualPriority) + "_2.1.0", true);
                Look(ref HaulBeforeCarry_AutoBuildings,              nameof(HaulBeforeCarry_AutoBuildings),              true);

                if (Scribe.mode == LoadSaveMode.Saving)
                    Scribe_Deep.Look(ref HaulBeforeCarry_BuildingFilter, nameof(HaulBeforeCarry_BuildingFilter));
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                    hbcBuildingFilterXmlNode = Scribe.loader.curXmlParent[nameof(HaulBeforeCarry_BuildingFilter)]; // so we can load later after Defs

                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.Saving)
                    DebugViewSettings.drawOpportunisticJobs = DrawSpecialHauls;

                if (Scribe.mode == LoadSaveMode.LoadingVars) {
                    if (haveCommonSense) {
                        if (HaulBeforeCarry_Bills_NeedsInitForCs) {
                            CsHaulingOverBillsSetting.SetValue(null, false);
                            HaulBeforeCarry_Bills = true;
                            HaulBeforeCarry_Bills_NeedsInitForCs = false;
                        } else if ((bool)CsHaulingOverBillsSetting.GetValue(null))
                            HaulBeforeCarry_Bills = false;
                    }
                }
            }
        }
    }
}
