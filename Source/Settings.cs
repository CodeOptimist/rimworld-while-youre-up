using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Xml;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace WhileYoureUp;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
partial class Mod
{
    [HarmonyPatch]
    static class Dialog_ModSettings__Dialog_ModSettings_Patch
    {
        static MethodBase TargetMethod() {
            if (haveHugs)
                return AccessTools.DeclaredConstructor(HugsType_Dialog_VanillaModSettings, new[] { typeof(Verse.Mod) });
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
                return AccessTools.DeclaredMethod(HugsType_Dialog_VanillaModSettings, "DoWindowContents");
            return AccessTools.DeclaredMethod(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents));
        }

        static Verse.Mod csMod;

        [HarmonyPostfix]
        static void CheckCommonSenseSetting(object __instance) { // :ResolveCsConflict
            if (!haveCommonSense) return;
            if (!settings.HaulBeforeCarry_Bills) return;
            if (!(bool)CsField_Settings_HaulingOverBills.GetValue(null)) return;

            csMod ??= LoadedModManager.GetMod(CsType_CommonSense);

            var curMod = SettingsCurModField.GetValue(__instance);
            if (curMod == mod) {
                CsField_Settings_HaulingOverBills.SetValue(null, false);
                csMod.WriteSettings();
                Messages.Message(
                    $"[{mod.Content.Name}] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)",
                    MessageTypeDefOf.SilentInput, false);
            } else if (curMod == csMod) {
                settings.HaulBeforeCarry_Bills = false;
                //mod.WriteSettings(); // no save because we handle it best on loading
                Messages.Message(
                    $"[{mod.Content.Name}] Unticked setting in While You're Up: \"Haul extra bill ingredients closer\". (Can't use both.)",
                    MessageTypeDefOf.SilentInput, false);
            }
        }
    }

    public class Listing_TreeModFilter : Listing_TreeNonThingFilter
    {
        public Listing_TreeModFilter(ModFilter filter, ModFilter parentFilter, IEnumerable<ThingDef> forceHiddenDefs,
            IEnumerable<SpecialThingFilterDef> forceHiddenFilters, List<ThingDef> suppressSmallVolumeTags, QuickSearchFilter searchFilter) : base(
            filter, parentFilter, forceHiddenDefs, forceHiddenFilters, suppressSmallVolumeTags, searchFilter) {
        }
    }

    public class ModFilter : NonThingFilter
    {
    }

    public override void DoSettingsWindowContents(Rect inRect) => SettingsWindow.DoWindowContents(inRect);

    [StaticConstructorOnStartup]
    // Don't reference this except in DoSettingsWindowContents()! Referencing it early will trigger the static constructor before defs are loaded.
    public static class SettingsWindow
    {
        static          Vector2               opportunityScrollPosition;
        static          Listing_TreeModFilter opportunityTreeFilter;
        static readonly QuickSearchFilter     opportunitySearchFilter = new();
        static readonly QuickSearchWidget     opportunitySearchWidget = new();
        static readonly ModFilter             opportunityDummyFilter  = new();

        static          Vector2               hbcScrollPosition;
        static          Listing_TreeModFilter hbcTreeFilter;
        static readonly QuickSearchFilter     hbcSearchFilter = new();
        static readonly QuickSearchWidget     hbcSearchWidget = new();
        static readonly ModFilter             hbcDummyFilter  = new();

        static readonly ThingCategoryDef storageBuildingCategoryDef;
        static readonly List<TabRecord>  tabsList = new(4);
        static          Tab              tab      = Tab.Opportunity;

        static SettingsWindow() {
            // `ExposeData()` (`LoadingVars`) runs late enough, but only if config file exists
            // so we handle this here; thanks to `[StaticConstructorOnStartup]` mods are loaded
            if (haveCommonSense) { // :ResolveCsConflict
                // we'll fix things here on load, but not actually write the settings
                //  (unless settings dialog is opened and closed)
                if (settings.HaulBeforeCarry_Bills_NeedsInitForCs) {
                    CsField_Settings_HaulingOverBills.SetValue(null, false);
                    settings.HaulBeforeCarry_Bills                = true;
                    settings.HaulBeforeCarry_Bills_NeedsInitForCs = false;
                } else if ((bool)CsField_Settings_HaulingOverBills.GetValue(null))
                    settings.HaulBeforeCarry_Bills = false;
            }

            // thanks to `[StaticConstructorOnStartup]` defs are loaded
            using (var context = new NonThingFilter_LoadingContext()) {
                try {
                    settings.opportunityBuildingFilter = ScribeExtractor.SaveableFromNode<ModFilter>(settings.opportunityBuildingFilterXmlNode, null);
                    settings.hbcBuildingFilter         = ScribeExtractor.SaveableFromNode<ModFilter>(settings.hbcBuildingFilterXmlNode,         null);
                } catch (Exception) {
                    context.Dispose(); // cancel error suppression before exception handling
                    throw;
                }
            }
            hbcSearchWidget.filter = hbcSearchFilter;

            var storageBuildingTypes = typeof(Building_Storage).AllSubclassesNonAbstract();
            storageBuildingTypes.Add(typeof(Building_Storage));
            storageBuildingCategoryDef = new ThingCategoryDef();
            var storageBuildings = DefDatabase<ThingDef>.AllDefsListForReading.Where(x => storageBuildingTypes.Contains(x.thingClass)).ToList();
            foreach (var storageMod in storageBuildings.Select(x => x.modContentPack).Distinct()) {
                if (storageMod is null) continue;

                var modCategoryDef = new ThingCategoryDef { label = storageMod.Name };
                storageBuildingCategoryDef.childCategories.Add(modCategoryDef);
                modCategoryDef.childThingDefs.AddRange(storageBuildings.Where(x => x.modContentPack == storageMod).Select(x => x));
                modCategoryDef.PostLoad();
                modCategoryDef.ResolveReferences();
            }

            storageBuildingCategoryDef.PostLoad();
            storageBuildingCategoryDef.ResolveReferences();

            ResetFilters();

            if (settings.opportunityBuildingFilter is null) {
                settings.opportunityBuildingFilter = new ModFilter();
                settings.opportunityBuildingFilter?.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
            }
            if (settings.hbcBuildingFilter is null) {
                settings.hbcBuildingFilter = new ModFilter();
                settings.hbcBuildingFilter?.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
            }
        }

        enum Tab { Opportunity, OpportunityAdvanced, BeforeCarryDetour, PickUpAndHaul }

        [SuppressMessage("ReSharper", "StringLiteralTypo")]
        [SuppressMessage("ReSharper", "CommentTypo")]
        static void ResetFilters() {
            foreach (var modCategoryDef in storageBuildingCategoryDef.childCategories) {
                var mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(x => x.Name == modCategoryDef.label);

                modCategoryDef.treeNode.SetOpen(1, false);
                switch (mod?.PackageId) {
                    case "ludeon.rimworld": // Core
                        modCategoryDef.treeNode.SetOpen(1, true);
                        break;
                }

                // allow everything for opportunities by default, since it's a core/vanilla feature
                settings.opportunityDefaultBuildingFilter.SetAllow(modCategoryDef, true);
                switch (mod?.PackageId) {
                    case "lwm.deepstorage": // LWM's Deep Storage
                        // deny-listed because storing has a delay, so not really "opportunistic"
                        // todo maybe we can check its settings to see if the delay is enabled though?
                        settings.opportunityDefaultBuildingFilter.SetAllow(modCategoryDef, false);
                        break;
                }

                settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, false);
                switch (mod?.PackageId) {
                    // Most of these are from ZzZombo#9297, blame him for everything. 🙃
                    case "buddy1913.expandedstorageboxes":      // Buddy's Expanded Storage Boxes
                    case "im.skye.rimworld.deepstorageplus":    // Deep Storage Plus
                    case "jangodsoul.simplestorage":            // [JDS] Simple Storage
                    case "jangodsoul.simplestorage.ref":        // [JDS] Simple Storage - Refrigeration
                    case "ludeon.rimworld":                     // Core
                    case "lwm.deepstorage":                     // LWM's Deep Storage
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
                        // determined to be actual containers for storage (and not just repurposing `Building_Storage`)
                        // todo maybe we can automate that by checking subclass or something?
                        // does this from PUAH help us?
                        // `haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null`
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
                    tab = Tab.BeforeCarryDetour;
            } else
                windowTripleStd.Label("PickUpAndHaul_Missing".ModTranslate(), Text.LineHeight, "PickUpAndHaul_Tooltip".ModTranslate());

            tabsList.Clear();
            tabsList.Add(new TabRecord("Opportunity_Tab".ModTranslate(), () => tab = Tab.Opportunity, tab == Tab.Opportunity));
            if (settings.Opportunity_TweakVanilla)
                tabsList.Add(new TabRecord("OpportunityAdvanced_Tab".ModTranslate(), () => tab = Tab.OpportunityAdvanced, tab == Tab.OpportunityAdvanced));
            tabsList.Add(new TabRecord("HaulBeforeCarry_Tab".ModTranslate(), () => tab = Tab.BeforeCarryDetour, tab == Tab.BeforeCarryDetour));
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
                    oDoubleStd.Label("Opportunity_Intro".ModTranslate());
                    oDoubleStd.Gap();

                    using (new DrawContext { LabelPct = 0.25f }) {
                        oDoubleStd.DrawEnum(
                            settings.Opportunity_PathChecker, nameof(settings.Opportunity_PathChecker), val => { settings.Opportunity_PathChecker = val; },
                            Text.LineHeight * 2);
                    }
                    oDoubleStd.Gap();

                    oDoubleStd.DrawBool(ref settings.Opportunity_TweakVanilla, nameof(settings.Opportunity_TweakVanilla));

                    oDoubleStd.NewColumn();
                    oDoubleStd.Label("Opportunity_Tab".ModTranslate());
                    oDoubleStd.GapLine();
                    var invertAuto = !settings.Opportunity_AutoBuildings;
                    oDoubleStd.DrawBool(ref invertAuto, nameof(settings.Opportunity_AutoBuildings));
                    settings.Opportunity_AutoBuildings = !invertAuto;
                    oDoubleStd.Gap(4f);
                    opportunitySearchWidget.OnGUI(oDoubleStd.GetRect(24f));
                    oDoubleStd.Gap(4f);

                    // what we Began on, minus CurHeight, minus 2 lines
                    var filterRect     = oDoubleStd.GetRect(innerTabRect.height - oDoubleStd.CurHeight - Text.LineHeight * 2);
                    var scrollbarWidth = 20f;
                    var filterFullRect = new Rect(0f, 0f, filterRect.width - scrollbarWidth, opportunityTreeFilter?.CurHeight ?? 10000f);
                    Widgets.BeginScrollView(filterRect, ref opportunityScrollPosition, filterFullRect);
                    if (settings.Opportunity_AutoBuildings)
                        opportunityDummyFilter.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
                    opportunityTreeFilter = new Listing_TreeModFilter(
                        settings.Opportunity_AutoBuildings ? opportunityDummyFilter : settings.opportunityBuildingFilter, null, null, null, null,
                        opportunitySearchFilter);
                    opportunityTreeFilter.Begin(filterFullRect);
                    opportunityTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, filterFullRect);
                    opportunityTreeFilter.End();
                    Widgets.EndScrollView();

                    oDoubleStd.GapLine();
                    oDoubleStd.DrawBool(ref settings.Opportunity_ToStockpiles, nameof(settings.Opportunity_ToStockpiles));
                    oDoubleStd.End();
                    break;

                case Tab.OpportunityAdvanced:
                    var labelPct = 0.75f;
                    var advDoubleStd = new Listing_Standard {
                        // ColumnWidth = (float)Math.Round((innerTabRect.width - 17 * 1) / 2),
                    };

                    advDoubleStd.Begin(innerTabRect);

                    advDoubleStd.Label("OpportunityAdvanced_Text1".ModTranslate());
                    using (new DrawContext { TextAnchor = TextAnchor.MiddleRight, LabelPct = labelPct }) {
                        advDoubleStd.DrawPercent(ref settings.Opportunity_MaxNewLegsPctOrigTrip,   nameof(settings.Opportunity_MaxNewLegsPctOrigTrip));
                        advDoubleStd.DrawPercent(ref settings.Opportunity_MaxTotalTripPctOrigTrip, nameof(settings.Opportunity_MaxTotalTripPctOrigTrip));
                    }

                    advDoubleStd.Gap();
                    advDoubleStd.GapLine();
                    advDoubleStd.Gap();

                    advDoubleStd.Label("OpportunityAdvanced_Text2".ModTranslate());
                    using (new DrawContext { TextAnchor = TextAnchor.MiddleRight, LabelPct = labelPct }) {
                        advDoubleStd.DrawFloat(ref settings.Opportunity_MaxStartToThing, nameof(settings.Opportunity_MaxStartToThing));
                        advDoubleStd.DrawFloat(ref settings.Opportunity_MaxStoreToJob,   nameof(settings.Opportunity_MaxStoreToJob));
                        advDoubleStd.DrawPercent(ref settings.Opportunity_MaxStartToThingPctOrigTrip, nameof(settings.Opportunity_MaxStartToThingPctOrigTrip));
                        advDoubleStd.DrawPercent(ref settings.Opportunity_MaxStoreToJobPctOrigTrip,   nameof(settings.Opportunity_MaxStoreToJobPctOrigTrip));
                    }

                    advDoubleStd.Gap();
                    advDoubleStd.GapLine();
                    advDoubleStd.Gap();

                    advDoubleStd.Label("OpportunityAdvanced_Text3".ModTranslate());
                    using (new DrawContext { TextAnchor = TextAnchor.MiddleRight, LabelPct = labelPct }) {
                        advDoubleStd.DrawInt(ref settings.Opportunity_MaxStartToThingRegionLookCount, nameof(settings.Opportunity_MaxStartToThingRegionLookCount));
                        advDoubleStd.DrawInt(ref settings.Opportunity_MaxStoreToJobRegionLookCount,   nameof(settings.Opportunity_MaxStoreToJobRegionLookCount));
                    }

                    advDoubleStd.End();
                    break;


                case Tab.BeforeCarryDetour:
                    var hbcDoubleStd = new Listing_Standard {
                        ColumnWidth = (float)Math.Round((innerTabRect.width - 17 * 1) / 2),
                    };

                    hbcDoubleStd.Begin(innerTabRect);
                    hbcDoubleStd.Label("HaulBeforeCarry_Intro".ModTranslate());
                    hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_Supplies, nameof(settings.HaulBeforeCarry_Supplies));
                    hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_Bills,    nameof(settings.HaulBeforeCarry_Bills));
                    if (havePuah && settings.UsePickUpAndHaulPlus) {
                        hbcDoubleStd.Gap();
                        hbcDoubleStd.Label("HaulBeforeCarry_EqualPriority".ModTranslate());
                        hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_ToEqualPriority, nameof(settings.HaulBeforeCarry_ToEqualPriority));
                    }

                    hbcDoubleStd.NewColumn();
                    hbcDoubleStd.Label("HaulBeforeCarry_Tab".ModTranslate());
                    hbcDoubleStd.GapLine();
                    invertAuto = !settings.HaulBeforeCarry_AutoBuildings;
                    hbcDoubleStd.DrawBool(ref invertAuto, nameof(settings.HaulBeforeCarry_AutoBuildings));
                    settings.HaulBeforeCarry_AutoBuildings = !invertAuto;
                    hbcDoubleStd.Gap(4f);
                    hbcSearchWidget.OnGUI(hbcDoubleStd.GetRect(24f));
                    hbcDoubleStd.Gap(4f);

                    // what we Began on, minus CurHeight, minus 2 lines
                    filterRect     = hbcDoubleStd.GetRect(innerTabRect.height - hbcDoubleStd.CurHeight - Text.LineHeight * 2);
                    scrollbarWidth = 20f;
                    filterFullRect = new Rect(0f, 0f, filterRect.width - scrollbarWidth, hbcTreeFilter?.CurHeight ?? 10000f);
                    Widgets.BeginScrollView(filterRect, ref hbcScrollPosition, filterFullRect);
                    if (settings.HaulBeforeCarry_AutoBuildings)
                        hbcDummyFilter.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
                    hbcTreeFilter = new Listing_TreeModFilter(
                        settings.HaulBeforeCarry_AutoBuildings ? hbcDummyFilter : settings.hbcBuildingFilter, null, null, null, null,
                        hbcSearchFilter);
                    hbcTreeFilter.Begin(filterFullRect);
                    hbcTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, filterFullRect);
                    hbcTreeFilter.End();
                    Widgets.EndScrollView();

                    hbcDoubleStd.GapLine();
                    hbcDoubleStd.DrawBool(ref settings.HaulBeforeCarry_ToStockpiles, nameof(settings.HaulBeforeCarry_ToStockpiles));
                    hbcDoubleStd.End();
                    break;

                case Tab.PickUpAndHaul:
                    var puahDoubleStd = new Listing_Standard {
                        ColumnWidth = (float)Math.Round((innerTabRect.width - 17 * 1) / 2),
                    };

                    puahDoubleStd.Begin(innerTabRect);
                    puahDoubleStd.Label("PickUpAndHaulPlus_Text1".ModTranslate());
                    puahDoubleStd.GapLine();
                    puahDoubleStd.Gap();
                    puahDoubleStd.Label("PickUpAndHaulPlus_Text2".ModTranslate());
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
    internal class Settings : ModSettings
    {
        public enum PathCheckerEnum { Vanilla, Default, Pathfinding }

        public bool Enabled, UsePickUpAndHaulPlus, DrawSpecialHauls;

        public PathCheckerEnum Opportunity_PathChecker;
        public bool            Opportunity_TweakVanilla, Opportunity_ToStockpiles, Opportunity_AutoBuildings;

        public float Opportunity_MaxStartToThing, Opportunity_MaxStartToThingPctOrigTrip, Opportunity_MaxStoreToJob, Opportunity_MaxStoreToJobPctOrigTrip,
            Opportunity_MaxTotalTripPctOrigTrip, Opportunity_MaxNewLegsPctOrigTrip;

        public int Opportunity_MaxStartToThingRegionLookCount, Opportunity_MaxStoreToJobRegionLookCount;

        internal readonly ModFilter opportunityDefaultBuildingFilter = new();
        internal          ModFilter opportunityBuildingFilter;
        internal          XmlNode   opportunityBuildingFilterXmlNode;


        public bool HaulBeforeCarry_Supplies, HaulBeforeCarry_Bills, HaulBeforeCarry_Bills_NeedsInitForCs, HaulBeforeCarry_ToEqualPriority, HaulBeforeCarry_ToStockpiles,
            HaulBeforeCarry_AutoBuildings;

        internal readonly ModFilter hbcDefaultBuildingFilter = new();
        internal          ModFilter hbcBuildingFilter;
        internal          XmlNode   hbcBuildingFilterXmlNode;
        public            ModFilter Opportunity_BuildingFilter     => Opportunity_AutoBuildings ? opportunityDefaultBuildingFilter : opportunityBuildingFilter;
        public            ModFilter HaulBeforeCarry_BuildingFilter => HaulBeforeCarry_AutoBuildings ? hbcDefaultBuildingFilter : hbcBuildingFilter;

        // we also manually call this to restore defaults and to set them before config file exists (Scribe.mode == LoadSaveMode.Inactive)
        public override void ExposeData() {
            if (Scribe.mode != LoadSaveMode.Inactive)
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

            if (Scribe.mode is LoadSaveMode.LoadingVars or LoadSaveMode.Saving)
                DebugViewSettings.drawOpportunisticJobs = DrawSpecialHauls;
        }
    }
}
