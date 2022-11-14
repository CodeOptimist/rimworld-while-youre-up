using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Xml;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
// ReSharper disable once RedundantUsingDirective
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
                scribeMode                = Scribe.mode;
                Scribe.mode               = LoadSaveMode.LoadingVars;
                ignoreLoadReferenceErrors = true;

                void Restore() {
                    ignoreLoadReferenceErrors = false;
                    Scribe.mode               = scribeMode;
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
                    return Skip();
                return Original();
            }
        }

        // Don't reference this except in DoSettingsWindowContents()! Referencing it early will trigger the static constructor before defs are loaded.
        [StaticConstructorOnStartup]
        public static class SettingsWindow
        {
            static          Vector2                         opportunityScrollPosition;
            static          Listing_SettingsTreeThingFilter opportunityTreeFilter;
            static readonly QuickSearchFilter               opportunitySearchFilter = new QuickSearchFilter();
            static readonly QuickSearchWidget               opportunitySearchWidget = new QuickSearchWidget();
            static readonly SettingsThingFilter             opportunityDummyFilter  = new SettingsThingFilter();

            static          Vector2                         hbcScrollPosition;
            static          Listing_SettingsTreeThingFilter hbcTreeFilter;
            static readonly QuickSearchFilter               hbcSearchFilter = new QuickSearchFilter();
            static readonly QuickSearchWidget               hbcSearchWidget = new QuickSearchWidget();
            static readonly SettingsThingFilter             hbcDummyFilter  = new SettingsThingFilter();

            static readonly ThingCategoryDef storageBuildingCategoryDef;
            static readonly List<TabRecord>  tabsList = new List<TabRecord>();
            static          Tab              tab      = Tab.Opportunity;

            static SettingsWindow() {
                // now that defs are loaded this will work
                Log__Error_Patch.SuppressLoadReferenceErrors(
                    () => {
                        settings.opportunityBuildingFilter = ScribeExtractor.SaveableFromNode<SettingsThingFilter>(settings.opportunityBuildingFilterXmlNode, null);
                        settings.hbcBuildingFilter         = ScribeExtractor.SaveableFromNode<SettingsThingFilter>(settings.hbcBuildingFilterXmlNode,         null);
                    });
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

                ResetFilters();

                if (settings.opportunityBuildingFilter == null) {
                    settings.opportunityBuildingFilter = new SettingsThingFilter();
                    settings.opportunityBuildingFilter?.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
                }
                if (settings.hbcBuildingFilter == null) {
                    settings.hbcBuildingFilter = new SettingsThingFilter();
                    settings.hbcBuildingFilter?.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
                }
            }

            enum Tab { Opportunity, HaulBeforeCarry, PickUpAndHaul }

            [SuppressMessage("ReSharper", "StringLiteralTypo")]
            public static void ResetFilters() {
                settings.opportunityDefaultBuildingFilter.SetAllowAll(null, true);
                settings.hbcDefaultBuildingFilter.SetDisallowAll();

                foreach (var modCategoryDef in storageBuildingCategoryDef.childCategories) {
                    modCategoryDef.treeNode.SetOpen(1, false);

                    // todo move to XML? postpone that probably
                    var mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(x => x.Name == modCategoryDef.label);
                    switch (mod?.PackageId) {
                        // most of these are from ZzZombo#9297, blame him for everything. 🙃
                        case "buddy1913.expandedstorageboxes":      // Buddy's Expanded Storage Boxes
                        case "im.skye.rimworld.deepstorageplus":    // Deep Storage Plus
                        case "jangodsoul.simplestorage":            // [JDS] Simple Storage
                        case "jangodsoul.simplestorage.ref":        // [JDS] Simple Storage - Refrigeration
                        case "mlie.displaycases":                   // Display Cases (Continued)
                        case "mlie.eggincubator":                   // Egg Incubator
                        case "mlie.extendedstorage":                // Extended Storage (Continued)
                        case "mlie.fireextinguisher":               // Fire Extinguisher (Continued)
                        case "mlie.functionalvanillaexpandedprops": // Functional Vanilla Expanded Props (Continued)
                        case "mlie.tobesdiningroom":                // Tobe's Dining Room (Continued)
                        case "ogliss.thewhitecrayon.quarry":        // Quarry
                        case "primitivestorage.velcroboy333":       // Primitive Storage
                        case "proxyer.smallshelf":                  // Small Shelf
                        case "rimfridge.kv.rw":                     // [KV] RimFridge
                        case "sixdd.littlestorage2":                // Little Storage 2
                        case "skullywag.extendedstorage":           // Extended Storage
                        case "solaris.furniturebase":               // GloomyFurniture
                        case "vanillaexpanded.vfecore":             // Vanilla Furniture Expanded
                        case "vanillaexpanded.vfeart":              // Vanilla Furniture Expanded - Art
                        case "vanillaexpanded.vfefarming":          // Vanilla Furniture Expanded - Farming
                        case "vanillaexpanded.vfespacer":           // Vanilla Furniture Expanded - Spacer Module
                        case "vanillaexpanded.vfesecurity":         // Vanilla Furniture Expanded - Security
                            settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, true);
                            break;
                        case "ludeon.rimworld": // Core
                            modCategoryDef.treeNode.SetOpen(1, true);
                            goto case "vanillaexpanded.vfecore";
                        case "lwm.deepstorage": // LWM's Deep Storage
                            settings.opportunityDefaultBuildingFilter.SetAllow(modCategoryDef, false);
                            settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, true);
                            break;
                    }
                }
            }

            public static void DoWindowContents(Rect windowRect) {
                var windowTripleStd = new Listing_Standard {
                    ColumnWidth = (float)Math.Round((windowRect.width - 17 * 2) / 3),
                };

                windowTripleStd.Begin(windowRect);
                windowTripleStd.DrawBool(ref settings.Enabled, nameof(settings.Enabled));
                windowTripleStd.NewColumn();
                windowTripleStd.DrawBool(ref settings.DrawSpecialHauls, nameof(settings.DrawSpecialHauls));
                windowTripleStd.NewColumn();
                if (ModLister.HasActiveModWithName("Pick Up And Haul")) {
                    windowTripleStd.DrawBool(ref settings.UsePickUpAndHaulPlus, nameof(settings.UsePickUpAndHaulPlus));
                    if (tab == Tab.PickUpAndHaul && !settings.UsePickUpAndHaulPlus)
                        tab = Tab.HaulBeforeCarry;
                } else
                    windowTripleStd.Label("PickUpAndHaul_Missing".ModTranslate(), Text.LineHeight, "PickUpAndHaul_Tooltip".ModTranslate());

                // todo actually implement Deep Storage defaults
                tabsList.Clear();
                tabsList.Add(new TabRecord("Opportunity_Tab".ModTranslate(),     () => tab = Tab.Opportunity,     tab == Tab.Opportunity));
                tabsList.Add(new TabRecord("HaulBeforeCarry_Tab".ModTranslate(), () => tab = Tab.HaulBeforeCarry, tab == Tab.HaulBeforeCarry));
                if (ModLister.HasActiveModWithName("Pick Up And Haul") && settings.UsePickUpAndHaulPlus)
                    tabsList.Add(new TabRecord("PickUpAndHaulPlus_Tab".ModTranslate(), () => tab = Tab.PickUpAndHaul, tab == Tab.PickUpAndHaul));

                var tabRect = windowRect.AtZero(); // meaning the top left of windowRect, because we're inside windowRect (Begin)
                tabRect.yMin   += windowTripleStd.MaxColumnHeightSeen;
                tabRect.yMin   += 12f + 30f; // gap & room for tab label row
                tabRect.height -= 12f + 30f; // room for bottom gap & restore button
                Widgets.DrawMenuSection(tabRect);
                TabDrawer.DrawTabs(tabRect, tabsList, 1);
                tabsList.Clear();

                var innerTabRect = tabRect.GetInnerRect();
                switch (tab) {
                    case Tab.Opportunity:
                        var oDoubleStd = new Listing_Standard {
                            ColumnWidth = (float)Math.Round((innerTabRect.width - 17 * 1) / 2),
                        };

                        oDoubleStd.Begin(innerTabRect);
                        using (new DrawContext { GuiColor = Color.grey }) {
                            oDoubleStd.Label("Opportunity_Intro".ModTranslate());
                        }
                        oDoubleStd.Gap();

                        using (new DrawContext { LabelPct = 0.25f }) {
                            oDoubleStd.DrawEnum(
                                settings.Opportunity_PathChecker, nameof(settings.Opportunity_PathChecker), val => { settings.Opportunity_PathChecker = val; },
                                Text.LineHeight * 2);
                        }
                        oDoubleStd.Gap();

                        oDoubleStd.DrawBool(ref settings.Opportunity_TweakVanilla, nameof(settings.Opportunity_TweakVanilla));
                        if (settings.Opportunity_TweakVanilla) {
                            using (new DrawContext { TextAnchor = TextAnchor.MiddleRight, LabelPct = 0.65f }) {
                                oDoubleStd.DrawFloat(ref settings.Opportunity_MaxNewLegsPctOrigTrip,      nameof(settings.Opportunity_MaxNewLegsPctOrigTrip));
                                oDoubleStd.DrawFloat(ref settings.Opportunity_MaxTotalTripPctOrigTrip,    nameof(settings.Opportunity_MaxTotalTripPctOrigTrip));
                                oDoubleStd.DrawFloat(ref settings.Opportunity_MaxStartToThing,            nameof(settings.Opportunity_MaxStartToThing));
                                oDoubleStd.DrawFloat(ref settings.Opportunity_MaxStartToThingPctOrigTrip, nameof(settings.Opportunity_MaxStartToThingPctOrigTrip));
                                oDoubleStd.DrawInt(ref settings.Opportunity_MaxStartToThingRegionLookCount, nameof(settings.Opportunity_MaxStartToThingRegionLookCount));
                                oDoubleStd.DrawFloat(ref settings.Opportunity_MaxStoreToJob,            nameof(settings.Opportunity_MaxStoreToJob));
                                oDoubleStd.DrawFloat(ref settings.Opportunity_MaxStoreToJobPctOrigTrip, nameof(settings.Opportunity_MaxStoreToJobPctOrigTrip));
                                oDoubleStd.DrawInt(ref settings.Opportunity_MaxStoreToJobRegionLookCount, nameof(settings.Opportunity_MaxStoreToJobRegionLookCount));
                            }
                        }

                        oDoubleStd.NewColumn();
                        using (new DrawContext { GuiColor = Color.grey }) {
                            oDoubleStd.Label("Opportunity_Tab".ModTranslate());
                        }
                        oDoubleStd.GapLine();
                        oDoubleStd.DrawBool(ref settings.Opportunity_ToStockpiles,  nameof(settings.Opportunity_ToStockpiles));
                        oDoubleStd.DrawBool(ref settings.Opportunity_AutoBuildings, nameof(settings.Opportunity_AutoBuildings));
                        // oDoubleStd.GapLine();
                        oDoubleStd.Gap(4f);
                        opportunitySearchWidget.OnGUI(oDoubleStd.GetRect(24f));
                        oDoubleStd.Gap(4f);

                        var filterRect     = oDoubleStd.GetRect(innerTabRect.height - oDoubleStd.CurHeight); // what we Began on, minus CurHeight
                        var scrollbarWidth = 20f;
                        var filterFullRect = new Rect(0f, 0f, filterRect.width - scrollbarWidth, opportunityTreeFilter?.CurHeight ?? 10000f);
                        Widgets.BeginScrollView(filterRect, ref opportunityScrollPosition, filterFullRect);
                        if (settings.Opportunity_AutoBuildings)
                            opportunityDummyFilter.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
                        opportunityTreeFilter = new Listing_SettingsTreeThingFilter(
                            settings.Opportunity_AutoBuildings ? opportunityDummyFilter : settings.opportunityBuildingFilter, null, null, null, null,
                            opportunitySearchFilter);
                        opportunityTreeFilter.Begin(filterFullRect);
                        opportunityTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, filterFullRect);
                        opportunityTreeFilter.End();
                        Widgets.EndScrollView();

                        oDoubleStd.End();
                        break;

                    case Tab.HaulBeforeCarry:
                        var hbcDoubleStd = new Listing_Standard {
                            ColumnWidth = (float)Math.Round((innerTabRect.width - 17 * 1) / 2),
                        };

                        hbcDoubleStd.Begin(innerTabRect);
                        using (new DrawContext { GuiColor = Color.grey }) {
                            hbcDoubleStd.Label("HaulBeforeCarry_Intro".ModTranslate());
                        }
                        hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_Supplies, nameof(settings.HaulBeforeCarry_Supplies));
                        hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_Bills,    nameof(settings.HaulBeforeCarry_Bills));
                        hbcDoubleStd.Gap();
                        using (new DrawContext { GuiColor = Color.grey }) {
                            hbcDoubleStd.Label("HaulBeforeCarry_EqualPriority".ModTranslate());
                        }
                        hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_ToEqualPriority, nameof(settings.HaulBeforeCarry_ToEqualPriority));

                        hbcDoubleStd.NewColumn();
                        using (new DrawContext { GuiColor = Color.grey }) {
                            hbcDoubleStd.Label("HaulBeforeCarry_Tab".ModTranslate());
                        }
                        hbcDoubleStd.GapLine();
                        hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_ToStockpiles,  nameof(settings.HaulBeforeCarry_ToStockpiles));
                        hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_AutoBuildings, nameof(settings.HaulBeforeCarry_AutoBuildings));
                        hbcDoubleStd.Gap(4f);
                        hbcSearchWidget.OnGUI(hbcDoubleStd.GetRect(24f));
                        hbcDoubleStd.Gap(4f);

                        filterRect     = hbcDoubleStd.GetRect(innerTabRect.height - hbcDoubleStd.CurHeight); // what we Began on, minus CurHeight
                        scrollbarWidth = 20f;
                        filterFullRect = new Rect(0f, 0f, filterRect.width - scrollbarWidth, hbcTreeFilter?.CurHeight ?? 10000f);
                        Widgets.BeginScrollView(filterRect, ref hbcScrollPosition, filterFullRect);
                        if (settings.HaulBeforeCarry_AutoBuildings)
                            hbcDummyFilter.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
                        hbcTreeFilter = new Listing_SettingsTreeThingFilter(
                            settings.HaulBeforeCarry_AutoBuildings ? hbcDummyFilter : settings.hbcBuildingFilter, null, null, null, null,
                            hbcSearchFilter);
                        hbcTreeFilter.Begin(filterFullRect);
                        hbcTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, filterFullRect);
                        hbcTreeFilter.End();
                        Widgets.EndScrollView();
                        hbcDoubleStd.End();
                        break;

                    case Tab.PickUpAndHaul:
                        var puahDoubleStd = new Listing_Standard {
                            ColumnWidth = (float)Math.Round((innerTabRect.width - 17 * 1) / 2),
                        };

                        puahDoubleStd.Begin(innerTabRect);
                        puahDoubleStd.Label("PickUpAndHaulPlus_UpgradeTitle".ModTranslate());
                        using (new DrawContext { GuiColor = Color.grey }) {
                            puahDoubleStd.Label("PickUpAndHaulPlus_UpgradeText".ModTranslate());
                        }
                        puahDoubleStd.Gap();

                        puahDoubleStd.Label("PickUpAndHaulPlus_IntegrationTitle".ModTranslate());
                        using (new DrawContext { GuiColor = Color.grey }) {
                            puahDoubleStd.Label("PickUpAndHaulPlus_IntegrationText".ModTranslate());
                        }
                        puahDoubleStd.End();
                        break;
                }

                var bottomWindowTripleRect = windowRect.AtZero(); // top left of windowRect, since we're inside it (relative x & y)
                bottomWindowTripleRect.yMin += tabRect.yMax;
                windowTripleStd.Begin(bottomWindowTripleRect);
                windowTripleStd.Gap(6f);
                if (Widgets.ButtonText(windowTripleStd.GetRect(30f), "RestoreToDefaultSettings".Translate())) {
                    settings.ExposeData(); // restore defaults
                    opportunitySearchWidget.Reset();
                    hbcSearchWidget.Reset();
                    ResetFilters();
                }
                windowTripleStd.Gap(6f);

                windowTripleStd.End();
                windowTripleStd.End();
            }
        }

        [SuppressMessage("ReSharper", "ClassNeverInstantiated.Local")]
        class Settings : ModSettings
        {
            public enum PathCheckerEnum { Default, Vanilla, Pathfinding }

            public bool Enabled, UsePickUpAndHaulPlus, DrawSpecialHauls;

            public PathCheckerEnum Opportunity_PathChecker;
            public bool            Opportunity_TweakVanilla, Opportunity_ToStockpiles, Opportunity_AutoBuildings;

            public float Opportunity_MaxStartToThing, Opportunity_MaxStartToThingPctOrigTrip, Opportunity_MaxStoreToJob, Opportunity_MaxStoreToJobPctOrigTrip,
                Opportunity_MaxTotalTripPctOrigTrip, Opportunity_MaxNewLegsPctOrigTrip;

            public int Opportunity_MaxStartToThingRegionLookCount, Opportunity_MaxStoreToJobRegionLookCount;

            internal readonly SettingsThingFilter opportunityDefaultBuildingFilter = new SettingsThingFilter();
            internal          SettingsThingFilter opportunityBuildingFilter;
            internal          XmlNode             opportunityBuildingFilterXmlNode;


            public bool HaulBeforeCarry_Supplies, HaulBeforeCarry_Bills, HaulBeforeCarry_Bills_NeedsInitForCs, HaulBeforeCarry_ToEqualPriority, HaulBeforeCarry_ToStockpiles,
                HaulBeforeCarry_AutoBuildings;

            internal readonly SettingsThingFilter hbcDefaultBuildingFilter = new SettingsThingFilter();
            internal          SettingsThingFilter hbcBuildingFilter;
            internal          XmlNode             hbcBuildingFilterXmlNode;
            public            SettingsThingFilter Opportunity_BuildingFilter     => Opportunity_AutoBuildings ? opportunityDefaultBuildingFilter : opportunityBuildingFilter;
            public            SettingsThingFilter HaulBeforeCarry_BuildingFilter => HaulBeforeCarry_AutoBuildings ? hbcDefaultBuildingFilter : hbcBuildingFilter;

            // we also manually call this to restore defaults and to set them before config file exists (Scribe.mode == LoadSaveMode.Inactive)
            public override void ExposeData() {
                foundConfig = true;

                void Look<T>(ref T value, string label, T defaultValue) {
                    if (Scribe.mode == LoadSaveMode.Inactive)
                        value = defaultValue;

                    Scribe_Values.Look(ref value, label, defaultValue);
                }

                Look(ref Enabled,                                    nameof(Enabled),                                    true);
                Look(ref DrawSpecialHauls,                           nameof(DrawSpecialHauls),                           false);
                Look(ref UsePickUpAndHaulPlus,                       nameof(UsePickUpAndHaulPlus),                       true);
                Look(ref Opportunity_PathChecker,                    nameof(Opportunity_PathChecker),                    PathCheckerEnum.Default);
                Look(ref Opportunity_TweakVanilla,                   nameof(Opportunity_TweakVanilla),                   false);
                Look(ref Opportunity_MaxStartToThing,                nameof(Opportunity_MaxStartToThing),                30f);
                Look(ref Opportunity_MaxStartToThingPctOrigTrip,     nameof(Opportunity_MaxStartToThingPctOrigTrip),     0.5f);
                Look(ref Opportunity_MaxStoreToJob,                  nameof(Opportunity_MaxStoreToJob),                  50f);
                Look(ref Opportunity_MaxStoreToJobPctOrigTrip,       nameof(Opportunity_MaxStoreToJobPctOrigTrip),       0.6f);
                Look(ref Opportunity_MaxTotalTripPctOrigTrip,        nameof(Opportunity_MaxTotalTripPctOrigTrip),        1.7f);
                Look(ref Opportunity_MaxNewLegsPctOrigTrip,          nameof(Opportunity_MaxNewLegsPctOrigTrip),          1.0f);
                Look(ref Opportunity_MaxStartToThingRegionLookCount, nameof(Opportunity_MaxStartToThingRegionLookCount), 25);
                Look(ref Opportunity_MaxStoreToJobRegionLookCount,   nameof(Opportunity_MaxStoreToJobRegionLookCount),   25);
                Look(ref Opportunity_ToStockpiles,                   nameof(Opportunity_ToStockpiles),                   true);
                Look(ref Opportunity_AutoBuildings,                  nameof(Opportunity_AutoBuildings),                  true);
                Look(ref HaulBeforeCarry_Supplies,                   nameof(HaulBeforeCarry_Supplies),                   true);
                Look(ref HaulBeforeCarry_Bills,                      nameof(HaulBeforeCarry_Bills),                      true);
                Look(ref HaulBeforeCarry_Bills_NeedsInitForCs,       nameof(HaulBeforeCarry_Bills_NeedsInitForCs),       true);
                Look(ref HaulBeforeCarry_ToEqualPriority,            nameof(HaulBeforeCarry_ToEqualPriority),            true);
                Look(ref HaulBeforeCarry_ToStockpiles,               nameof(HaulBeforeCarry_ToStockpiles),               true);
                Look(ref HaulBeforeCarry_AutoBuildings,              nameof(HaulBeforeCarry_AutoBuildings),              true);

                if (Scribe.mode == LoadSaveMode.Saving) {
                    Scribe_Deep.Look(ref hbcBuildingFilter,         nameof(hbcBuildingFilter));
                    Scribe_Deep.Look(ref opportunityBuildingFilter, nameof(opportunityBuildingFilter));
                }
                if (Scribe.mode == LoadSaveMode.LoadingVars) {
                    // so we can load after Defs
                    hbcBuildingFilterXmlNode         = Scribe.loader.curXmlParent[nameof(hbcBuildingFilter)];
                    opportunityBuildingFilterXmlNode = Scribe.loader.curXmlParent[nameof(opportunityBuildingFilter)];
                }

                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.Saving)
                    DebugViewSettings.drawOpportunisticJobs = DrawSpecialHauls;

                if (Scribe.mode == LoadSaveMode.LoadingVars) {
                    if (haveCommonSense) {
                        if (HaulBeforeCarry_Bills_NeedsInitForCs) {
                            CsHaulingOverBillsSetting.SetValue(null, false);
                            HaulBeforeCarry_Bills                = true;
                            HaulBeforeCarry_Bills_NeedsInitForCs = false;
                        } else if ((bool)CsHaulingOverBillsSetting.GetValue(null))
                            HaulBeforeCarry_Bills = false;
                    }
                }
            }
        }
    }
}
