// Decompiled with JetBrains decompiler
// Type: CodeOptimist.Listing_SettingsTreeThingFilter
// Assembly: JobsOfOpportunity, Version=3.2.2.1065, Culture=neutral, PublicKeyToken=null
// MVID: 2D726F6D-B465-4BB3-B286-8EB3FFA71395
// Assembly location: F:\SteamLibrary\steamapps\workshop\content\294100\2034960453\1.4\Assemblies\JobsOfOpportunity.dll

using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace CodeOptimist; 

[HarmonyPatch]
class Listing_SettingsTreeThingFilter : Listing_Tree
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
  static readonly Color NoMatchColor = Color.grey;
  static readonly LRUCache<(TreeNode_ThingCategory, SettingsThingFilter), List<SpecialThingFilterDef>> cachedHiddenSpecialFilters = new(500);
  SettingsThingFilter filter;
  SettingsThingFilter parentFilter;
  List<SpecialThingFilterDef> hiddenSpecialFilters;
  List<ThingDef> forceHiddenDefs;
  List<SpecialThingFilterDef> tempForceHiddenSpecialFilters;
  List<ThingDef> suppressSmallVolumeTags;
  protected QuickSearchFilter searchFilter;
  public int matchCount;
  Rect visibleRect;

  public Listing_SettingsTreeThingFilter(
    SettingsThingFilter filter,
    SettingsThingFilter parentFilter,
    IEnumerable<ThingDef> forceHiddenDefs,
    IEnumerable<SpecialThingFilterDef> forceHiddenFilters,
    List<ThingDef> suppressSmallVolumeTags,
    QuickSearchFilter searchFilter)
  {
    _construct_Listing_TreeThingFilter(this, filter, parentFilter, forceHiddenDefs, forceHiddenFilters, suppressSmallVolumeTags, searchFilter);
  }

  [
    HarmonyPatch(
      typeof(Listing_TreeThingFilter),
      MethodType.Constructor,
      typeof(ThingFilter),
      typeof(ThingFilter),
      typeof(IEnumerable<ThingDef>),
      typeof(IEnumerable<SpecialThingFilterDef>),
      typeof(List<ThingDef>),
      typeof(QuickSearchFilter)
    )
  ]
  [HarmonyReversePatch]
  static void _construct_Listing_TreeThingFilter(
    object instance,
    SettingsThingFilter filter,
    SettingsThingFilter parentFilter,
    IEnumerable<ThingDef> forceHiddenDefs,
    IEnumerable<SpecialThingFilterDef> forceHiddenFilters,
    List<ThingDef> suppressSmallVolumeTags,
    QuickSearchFilter searchFilter)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "DoThingDef")]
  static void OriginalDoThingDef(object instance, ThingDef tDef, int nestLevel, Map map)
  {
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "ListCategoryChildren")]
  public void ListCategoryChildren(
    TreeNode_ThingCategory node,
    int openMask,
    Map map,
    Rect visibleRect)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "DoCategoryChildren")]
  void DoCategoryChildren(
    TreeNode_ThingCategory node,
    int indentLevel,
    int openMask,
    Map map,
    bool subtreeMatchedSearch)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "DoSpecialFilter")]
  [HarmonyReversePatch]
  void DoSpecialFilter(SpecialThingFilterDef sfDef, int nestLevel)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "DoCategory")]
  void DoCategory(
    TreeNode_ThingCategory node,
    int indentLevel,
    int openMask,
    Map map,
    bool subtreeMatchedSearch)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "AllowanceStateOf")]
  [HarmonyReversePatch]
  public MultiCheckboxState AllowanceStateOf(TreeNode_ThingCategory cat)
  {
    Transpiler(null);
    return 0;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "Visible", typeof (ThingDef))]
  [HarmonyReversePatch]
  bool Visible(ThingDef td)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "IsOpen")]
  [HarmonyReversePatch]
  public virtual bool IsOpen(TreeNode node, int openMask)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "ThisOrDescendantsVisibleAndMatchesSearch")]
  bool ThisOrDescendantsVisibleAndMatchesSearch(TreeNode_ThingCategory node)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "CategoryMatches")]
  [HarmonyReversePatch]
  bool CategoryMatches(TreeNode_ThingCategory node)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "Visible", typeof (TreeNode_ThingCategory))]
  [HarmonyReversePatch]
  bool Visible(TreeNode_ThingCategory node)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "Visible", typeof (SpecialThingFilterDef), typeof (TreeNode_ThingCategory))]
  [HarmonyReversePatch]
  bool Visible(SpecialThingFilterDef filter, TreeNode_ThingCategory node)
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "CurrentRowVisibleOnScreen")]
  [HarmonyReversePatch]
  bool CurrentRowVisibleOnScreen()
  {
    Transpiler(null);
    return false;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "CalculateHiddenSpecialFilters", typeof (TreeNode_ThingCategory))]
  void CalculateHiddenSpecialFilters(TreeNode_ThingCategory node)
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "GetCachedHiddenSpecialFilters")]
  static List<SpecialThingFilterDef> GetCachedHiddenSpecialFilters(
    TreeNode_ThingCategory node,
    SettingsThingFilter parentFilter)
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "CalculateHiddenSpecialFilters", typeof (TreeNode_ThingCategory), typeof (ThingFilter))]
  static List<SpecialThingFilterDef> CalculateHiddenSpecialFilters(
    TreeNode_ThingCategory node,
    SettingsThingFilter parentFilter)
  {
    Transpiler(null);
    return null;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyReversePatch]
  [HarmonyPatch(typeof (Listing_TreeThingFilter), "ResetStaticData")]
  public static void ResetStaticData()
  {
    Transpiler(null);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes) => TranspilerHelper.ReplaceTypes(codes, _subs);
  }

  [HarmonyPatch(typeof (Listing_TreeThingFilter), "DoThingDef")]
  static class Listing_TreeThingFilter_DoThingDef_Patch
  {
    [HarmonyPrefix]
    [HarmonyPriority(700)]
    [HarmonyAfter("com.github.automatic1111.recipeicons")]
    static bool RecipeIconsPatchOnly(
      Listing_Tree __instance,
      ThingDef tDef,
      int nestLevel,
      Map map)
    {
      if (!(__instance is Listing_SettingsTreeThingFilter))
        return true;
      OriginalDoThingDef(__instance, tDef, nestLevel, map);
      return false;
    }
  }
}