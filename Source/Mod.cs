using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodeOptimist;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace JobsOfOpportunity;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
partial class Mod : Verse.Mod
{
#region other mods
    static readonly Type PuahType_CompHauledToInventory               = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory"); // GenTypes has a cache
    static readonly Type PuahType_WorkGiver_HaulToInventory           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");
    static readonly Type PuahType_JobDriver_HaulToInventory           = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");
    static readonly Type PuahType_JobDriver_UnloadYourHauledInventory = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");

    static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "HasJobOnThing");
    static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_JobOnThing    = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "JobOnThing");

    static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor =
        AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "TryFindBestBetterStoreCellFor");

    // todo https://github.com/Mehni/PickUpAndHaul/commit/fd2dd37d48af136600b220b5d9c141957b377e8c
    static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt =
        AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory,    "AllocateThingAtStoreTarget")
        ?? AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "AllocateThingAtCell");

    static readonly MethodInfo PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing =
        AccessTools.DeclaredMethod(PuahType_JobDriver_UnloadYourHauledInventory, "FirstUnloadableThing");

    static readonly MethodInfo PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils =
        AccessTools.DeclaredMethod(PuahType_JobDriver_UnloadYourHauledInventory, "MakeNewToils");

    // todo support changing this to "skipTargets" in the future? https://github.com/Mehni/PickUpAndHaul/commit/d48fdfff9e3c9a072b160871676e813258dee584
    static readonly FieldInfo PuahField_WorkGiver_HaulToInventory_SkipCells = AccessTools.DeclaredField(PuahType_WorkGiver_HaulToInventory, "skipCells");

    static readonly bool havePuah = new List<object> {
            PuahType_CompHauledToInventory, PuahType_WorkGiver_HaulToInventory, PuahType_JobDriver_HaulToInventory, PuahType_JobDriver_UnloadYourHauledInventory,
            PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing, PuahMethod_WorkGiver_HaulToInventory_JobOnThing,
            PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor,
            PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt,
            PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing, PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils,
            PuahField_WorkGiver_HaulToInventory_SkipCells,
        }
        .All(x => x is not null);

    static readonly MethodInfo PuahMethod_CompHauledToInventory_GetComp =
        havePuah ? AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahType_CompHauledToInventory) : null;

    static readonly Type HugsType_Dialog_VanillaModSettings = GenTypes.GetTypeInAnyAssembly("HugsLib.Settings.Dialog_VanillaModSettings");
    static readonly bool haveHugs                           = HugsType_Dialog_VanillaModSettings is not null;

    static readonly FieldInfo SettingsCurModField = haveHugs
        ? AccessTools.DeclaredField(HugsType_Dialog_VanillaModSettings, "selectedMod")
        : AccessTools.DeclaredField(typeof(Dialog_ModSettings),         "mod");

    static readonly Type      CsType_CommonSense                = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");
    static readonly Type      CsType_Settings                   = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");
    static readonly FieldInfo CsField_Settings_HaulingOverBills = AccessTools.DeclaredField(CsType_Settings, "hauling_over_bills");

    static readonly bool haveCommonSense = new List<object> { CsType_CommonSense, CsType_Settings, CsField_Settings_HaulingOverBills }.All(x => x is not null);
#endregion

    static Verse.Mod mod;
    static Settings  settings; // static reference since `.modSettings` isn't
    static bool      foundConfig;

    // Prefix for our XML keys (language translations); PackageId may change (e.g. "__copy__" suffix).
    public static readonly string  modId   = "CodeOptimist.WhileYoureUp";
    static readonly        Harmony harmony = new(modId); // just a unique id

    public Mod(ModContentPack content) : base(content) {
        mod       = this;  // static reference to mod for e.g. mod name in log messages
        Gui.modId = modId; // setup for CodeOptimist Gui library

        settings = GetSettings<Settings>(); // read settings from file
        if (!foundConfig)
            settings.ExposeData(); // initialize to defaults

        harmony.PatchAll();
    }

    // Harmony patch syntactic sugar to distinguish result from return behavior e.g. 'return Continue(_result = false)'
    static bool Continue(object _ = null) => true;
    static bool Halt(object _ = null)     => false;

    // name in "Mod options" and top of settings window
    public override string SettingsCategory() => mod.Content.Name;

    [HarmonyPatch(typeof(JobUtility), nameof(JobUtility.TryStartErrorRecoverJob))]
    static class JobUtility__TryStartErrorRecoverJob_Patch
    {
        static Pawn   lastPawn;
        static int    lastFrameCount;
        static string lastCallerName;

        [HarmonyPrefix]
        static void OfferSupport(Pawn pawn) {
            if (RealTime.frameCount == lastFrameCount && pawn == lastPawn) {
                Log.Warning(
                    $"[{mod.Content.Name}] You're welcome to 'Share logs' to my Discord: https://discord.gg/pnZGQAN \n" +
                    $"[{mod.Content.Name}] Below \"10 jobs in one tick\" error occurred during {lastCallerName}, but could be from several mods.");
            }
        }

        public static Job CatchStanding_Job(Pawn pawn, Job job, [CallerMemberName] string callerName = "") {
            lastPawn       = pawn;
            lastFrameCount = RealTime.frameCount;
            lastCallerName = callerName;
            return job;
        }
    }
}

static class Extensions
{
    // `Resolve()` supports colored text e.g. `<Threat>text</Threat>`
    public static string ModTranslate(this string key, params NamedArgument[] args) => $"{Mod.modId}_{key}".Translate(args).Resolve();

    // same as HarmonyLib's `GeneralExtensions.GetValueSafe()` but with `[CanBeNull]` for ReSharper/Rider,
    //  otherwise forgetting the `?` and throwing an NRE is a real concern.
    [CanBeNull]
    public static T GetValueSafe<S, T>(this Dictionary<S, T> dictionary, S key) => dictionary.TryGetValue(key, out var obj) ? obj : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Squared(this float val) => val * val;
}
