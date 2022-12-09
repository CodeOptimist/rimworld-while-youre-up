using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity
{
    partial class Mod : Verse.Mod
    {
        static readonly Type PuahType_CompHauledToInventory               = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory"); // GenTypes has a cache
        static readonly Type PuahType_WorkGiver_HaulToInventory           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
        static readonly Type PuahType_JobDriver_HaulToInventory           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
        static readonly Type PuahType_JobDriver_UnloadYourHauledInventory = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");

        static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing                 = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "HasJobOnThing");
        static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_JobOnThing                    = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "JobOnThing");
        static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "TryFindBestBetterStoreCellFor");

        // todo https://github.com/Mehni/PickUpAndHaul/commit/fd2dd37d48af136600b220b5d9c141957b377e8c
        static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt =
            AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory,    "AllocateThingAtStoreTarget")
            ?? AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "AllocateThingAtCell");

        static readonly MethodInfo PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing = AccessTools.DeclaredMethod(PuahType_JobDriver_UnloadYourHauledInventory, "FirstUnloadableThing");
        static readonly MethodInfo PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils         = AccessTools.DeclaredMethod(PuahType_JobDriver_UnloadYourHauledInventory, "MakeNewToils");

        // todo support changing this to "skipTargets" in the future? https://github.com/Mehni/PickUpAndHaul/commit/d48fdfff9e3c9a072b160871676e813258dee584
        static readonly FieldInfo PuahField_WorkGiver_HaulToInventory_SkipCells = AccessTools.DeclaredField(PuahType_WorkGiver_HaulToInventory, "skipCells");

        static readonly bool havePuah = new List<object> {
                PuahType_CompHauledToInventory, PuahType_WorkGiver_HaulToInventory, PuahType_JobDriver_HaulToInventory, PuahType_JobDriver_UnloadYourHauledInventory,
                PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing, PuahMethod_WorkGiver_HaulToInventory_JobOnThing, PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor,
                PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt,
                PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing, PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils,
                PuahField_WorkGiver_HaulToInventory_SkipCells,
            }
            .All(x => x != null);

        static readonly MethodInfo PuahMethod_CompHauledToInventory_GetComp =
            havePuah ? AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahType_CompHauledToInventory) : null;

        static readonly Type HugsType_Dialog_VanillaModSettings = GenTypes.GetTypeInAnyAssembly("HugsLib.Settings.Dialog_VanillaModSettings");
        static readonly bool haveHugs                          = HugsType_Dialog_VanillaModSettings != null;

        static readonly FieldInfo SettingsCurModField = haveHugs
            ? AccessTools.DeclaredField(HugsType_Dialog_VanillaModSettings, "selectedMod")
            : AccessTools.DeclaredField(typeof(Dialog_ModSettings),        "mod");

        static readonly Type      CsType_CommonSense                        = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
        static readonly Type      CsType_Settings                   = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
        static readonly FieldInfo CsField_Settings_HaulingOverBills = AccessTools.DeclaredField(CsType_Settings, "hauling_over_bills");

        static readonly bool haveCommonSense = new List<object> { CsType_CommonSense, CsType_Settings, CsField_Settings_HaulingOverBills }.All(x => x != null);

        // Prefix for our XML keys (language translations); PackageId may change (e.g. "__copy__" suffix).
        public const    string    modId = "CodeOptimist.WhileYoureUp";
        static          Verse.Mod mod; // static reference for e.g. mod name in log messages
        static          Settings  settings;
        static          bool      foundConfig;
        static readonly Harmony   harmony = new Harmony(modId); // just a unique id

        public Mod(ModContentPack content) : base(content) {
            mod       = this; // static reference to mod for e.g. mod name in log messages
            Gui.modId = modId; // setup for CodeOptimist Gui library

            settings  = GetSettings<Settings>();
            if (!foundConfig)
                settings.ExposeData(); // initialize to defaults

            harmony.PatchAll();
        }

        // Harmony patch syntactic sugar to visually distinguish these special cases from actual return true/false
        static bool Original(object _ = null) => true;
        static bool Skip(object _ = null)     => false;

        // name in "Mod options" and top of settings window
        public override string SettingsCategory() => mod.Content.Name;

        static Job PuahJob(PuahWithBetterUnloading puah, Pawn pawn, Thing thing, IntVec3 storeCell) {
            if (!settings.Enabled || !havePuah || !settings.UsePickUpAndHaulPlus) return null;
            specialHauls.SetOrAdd(pawn, puah);
            puah.TrackThing(thing, storeCell);
            var puahWorkGiver = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker; // dictionary lookup
            return (Job)PuahMethod_WorkGiver_HaulToInventory_JobOnThing.Invoke(puahWorkGiver, new object[] { pawn, thing, false });
        }

        public static bool AlreadyHauling(Pawn pawn) {
            if (specialHauls.ContainsKey(pawn)) return true;

            // because we may load a game with an incomplete haul
            if (havePuah) {
                var hauledToInventoryComp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
                var takenToInventory      = Traverse.Create(hauledToInventoryComp).Field<HashSet<Thing>>("takenToInventory").Value; // traverse is cached
                if (takenToInventory != null && takenToInventory.Any(t => t != null))
                    return true;
            }

            return false;
        }
    }

    static class Extensions
    {
        public static string ModTranslate(this string key, params NamedArgument[] args) {
            return $"{Mod.modId}_{key}".Translate(args).Resolve();
        }
    }
}
