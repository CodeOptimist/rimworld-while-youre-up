// Decompiled with JetBrains decompiler
// Type: CodeOptimist.SettingsThingFilter
// Assembly: JobsOfOpportunity, Version=3.2.2.1065, Culture=neutral, PublicKeyToken=null
// MVID: 2D726F6D-B465-4BB3-B286-8EB3FFA71395
// Assembly location: F:\SteamLibrary\steamapps\workshop\content\294100\2034960453\1.4\Assemblies\JobsOfOpportunity.dll

using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace CodeOptimist; 

[HarmonyPatch]
class SettingsThingFilter : IExposable
{
  static readonly Dictionary<Type, Type> _subs = new()
  {
    {
      typeof (Listing_TreeThingFilter),
      typeof (Listing_SettingsTreeThingFilter)
    },
    {
      typeof (ThingFilter),
      typeof (SettingsThingFilter)
    }
  };
  [Unsaved()]
  public Action settingsChangedCallback;
  [Unsaved()]
  public TreeNode_ThingCategory displayRootCategoryInt;
  [Unsaved()]
  public HashSet<ThingDef> allowedDefs = new();
  [Unsaved()]
  public List<SpecialThingFilterDef> disallowedSpecialFilters = new();
  public FloatRange allowedHitPointsPercents = FloatRange.ZeroToOne;
  public bool allowedHitPointsConfigurable = true;
  public QualityRange allowedQualities = QualityRange.All;
  public bool allowedQualitiesConfigurable = true;
  [MustTranslate]
  public string customSummary;
  public List<ThingDef> thingDefs;
  [NoTranslate]
  public List<string> categories;
  [NoTranslate]
  public List<string> tradeTagsToAllow;
  [NoTranslate]
  public List<string> tradeTagsToDisallow;
  [NoTranslate]
  public List<string> thingSetMakerTagsToAllow;
  [NoTranslate]
  public List<string> thingSetMakerTagsToDisallow;
  [NoTranslate]
  public List<string> disallowedCategories;
  [NoTranslate]
  public List<string> specialFiltersToAllow;
  [NoTranslate]
  public List<string> specialFiltersToDisallow;
  public List<StuffCategoryDef> stuffCategoriesToAllow;
  public List<ThingDef> allowAllWhoCanMake;
  public FoodPreferability disallowWorsePreferability;
  public bool disallowInedibleByHuman;
  public bool disallowNotEverStorable;
  public Type allowWithComp;
  public Type disallowWithComp;
  public float disallowCheaperThan = float.MinValue;
  public List<ThingDef> disallowedThingDefs;

  public SettingsThingFilter()
  {
  }

  public SettingsThingFilter(Action settingsChangedCallback) => _construct_ThingFilter(this, settingsChangedCallback);

  [HarmonyPatch(typeof (ThingFilter), "ExposeData")]
  [HarmonyReversePatch]
  public virtual void ExposeData()
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof(ThingFilter), MethodType.Constructor)]
  [HarmonyReversePatch]
  static void _construct_ThingFilter(object instance, Action settingsChangedCallback)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public string Summary => _get_Summary(this);

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "Summary", MethodType.Getter)]
  static string _get_Summary(object instance)
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public ThingRequest BestThingRequest => _get_BestThingRequest(this);

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "BestThingRequest", MethodType.Getter)]
  static ThingRequest _get_BestThingRequest(object instance)
  {
    Transpiler(null);
    return new ThingRequest();

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public ThingDef AnyAllowedDef => _get_AnyAllowedDef(this);

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "AnyAllowedDef", MethodType.Getter)]
  static ThingDef _get_AnyAllowedDef(object instance)
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public IEnumerable<ThingDef> AllowedThingDefs => _get_AllowedThingDefs(this);

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "AllowedThingDefs", MethodType.Getter)]
  static IEnumerable<ThingDef> _get_AllowedThingDefs(object instance)
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public static IEnumerable<ThingDef> AllStorableThingDefs => _get_AllStorableThingDefs();

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "AllStorableThingDefs", MethodType.Getter)]
  static IEnumerable<ThingDef> _get_AllStorableThingDefs()
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public int AllowedDefCount => _get_AllowedDefCount(this);

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "AllowedDefCount", MethodType.Getter)]
  static int _get_AllowedDefCount(object instance)
  {
    Transpiler(null);
    return 0;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public FloatRange AllowedHitPointsPercents
  {
    get => _get_AllowedHitPointsPercents(this);
    set => _set_AllowedHitPointsPercents(this, value);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "AllowedHitPointsPercents", MethodType.Getter)]
  static FloatRange _get_AllowedHitPointsPercents(object instance)
  {
    Transpiler(null);
    return new FloatRange();

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof(ThingFilter), "AllowedHitPointsPercents", MethodType.Setter)]
  static void _set_AllowedHitPointsPercents(object instance, FloatRange value)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public QualityRange AllowedQualityLevels
  {
    get => _get_AllowedQualityLevels(this);
    set => _set_AllowedQualityLevels(this, value);
  }

  [HarmonyPatch(typeof(ThingFilter), "AllowedQualityLevels", MethodType.Getter)]
  [HarmonyReversePatch]
  static QualityRange _get_AllowedQualityLevels(object instance)
  {
    Transpiler(null);
    return new QualityRange();

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof(ThingFilter), "AllowedQualityLevels", MethodType.Setter)]
  [HarmonyReversePatch]
  static void _set_AllowedQualityLevels(object instance, QualityRange value)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  public TreeNode_ThingCategory DisplayRootCategory
  {
    get => _get_DisplayRootCategory(this);
    set => _set_DisplayRootCategory(this, value);
  }

  [HarmonyPatch(typeof(ThingFilter), "DisplayRootCategory", MethodType.Getter)]
  [HarmonyReversePatch]
  static TreeNode_ThingCategory _get_DisplayRootCategory(object instance)
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof(ThingFilter), "DisplayRootCategory", MethodType.Setter)]
  [HarmonyPatch]
  static void _set_DisplayRootCategory(object instance, TreeNode_ThingCategory value)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof(ThingFilter), "ResolveReferences")]
  [HarmonyReversePatch]
  public void ResolveReferences()
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof(ThingFilter), "RecalculateDisplayRootCategory")]
  [HarmonyReversePatch]
  public void RecalculateDisplayRootCategory()
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "RecalculateSpecialFilterConfigurability")]
  public void RecalculateSpecialFilterConfigurability()
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "IsAlwaysDisallowedDueToSpecialFilters")]
  public bool IsAlwaysDisallowedDueToSpecialFilters(ThingDef def)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (ThingFilter), "CopyAllowancesFrom")]
  [HarmonyReversePatch]
  public virtual void CopyAllowancesFrom(SettingsThingFilter other)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (ThingFilter), "SetAllow", typeof (ThingDef), typeof (bool))]
  [HarmonyReversePatch]
  public void SetAllow(ThingDef thingDef, bool allow)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "SetAllow", typeof (SpecialThingFilterDef), typeof (bool))]
  public void SetAllow(SpecialThingFilterDef sfDef, bool allow)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "SetAllow", typeof (ThingCategoryDef), typeof (bool), typeof (IEnumerable<ThingDef>), typeof (IEnumerable<SpecialThingFilterDef>))]
  public void SetAllow(
    ThingCategoryDef categoryDef,
    bool allow,
    IEnumerable<ThingDef> exceptedDefs = null,
    IEnumerable<SpecialThingFilterDef> exceptedFilters = null)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (ThingFilter), "SetAllow", typeof (StuffCategoryDef), typeof (bool))]
  [HarmonyReversePatch]
  public void SetAllow(StuffCategoryDef cat, bool allow)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (ThingFilter), "SetAllowAllWhoCanMake")]
  [HarmonyReversePatch]
  public void SetAllowAllWhoCanMake(ThingDef thing)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "SetFromPreset")]
  public void SetFromPreset(StorageSettingsPreset preset)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "SetDisallowAll")]
  public void SetDisallowAll(
    IEnumerable<ThingDef> exceptedDefs = null,
    IEnumerable<SpecialThingFilterDef> exceptedFilters = null)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "SetAllowAll")]
  public void SetAllowAll(SettingsThingFilter parentFilter, bool includeNonStorable = false)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (ThingFilter), "Allows", typeof (Thing))]
  [HarmonyReversePatch]
  public virtual bool Allows(Thing t)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "Allows", typeof (ThingDef))]
  public bool Allows(ThingDef def)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "Allows", typeof (SpecialThingFilterDef))]
  public bool Allows(SpecialThingFilterDef sf)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "GetThingRequest")]
  public ThingRequest GetThingRequest()
  {
    Transpiler(null);
    return new ThingRequest();

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (ThingFilter), "ToString")]
  public override string ToString()
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (ThingFilter), "CreateOnlyEverStorableThingFilter")]
  [HarmonyReversePatch]
  public static SettingsThingFilter CreateOnlyEverStorableThingFilter()
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }
}