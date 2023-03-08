// Decompiled with JetBrains decompiler
// Type: CodeOptimist.Gui
// Assembly: JobsOfOpportunity, Version=3.2.2.1065, Culture=neutral, PublicKeyToken=null
// MVID: 2D726F6D-B465-4BB3-B286-8EB3FFA71395
// Assembly location: F:\SteamLibrary\steamapps\workshop\content\294100\2034960453\1.4\Assemblies\JobsOfOpportunity.dll

using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace CodeOptimist;

static class Gui
{
  public static string modId;

  public static string Title(this string name)
  {
    var taggedString = (modId + "_SettingTitle_" + name).Translate();
    return taggedString.Resolve();
  }

  public static string Desc(this string name)
  {
    var taggedString = (modId + "_SettingDesc_" + name).Translate();
    return taggedString.Resolve();
  }

  public static void DrawBool(this Listing_Standard list, ref bool value, string name) => list.CheckboxLabeled(name.Title(), ref value, name.Desc());

  static void NumberLabel(
    this Listing_Standard list,
    Rect rect,
    float value,
    string format,
    string name,
    out string buffer)
  {
    Widgets.Label(new Rect(rect.x, rect.y, rect.width - 8f, rect.height), name.Title());
    buffer = value.ToString(format);
    list.Gap(list.verticalSpacing);
    string str = name.Desc();
    if (str.NullOrEmpty())
      return;
    if (Mouse.IsOver(rect))
      Widgets.DrawHighlight(rect);
    TooltipHandler.TipRegion(rect, str);
  }

  public static void DrawFloat(this Listing_Standard list, ref float value, string name)
  {
    var rect = list.GetRect(Text.LineHeight);
    string buffer;
    list.NumberLabel(rect.LeftPart(DrawContext.guiLabelPct), value, "f1", name, out buffer);
    Widgets.TextFieldNumeric(rect.RightPart(1f - DrawContext.guiLabelPct), ref value, ref buffer, 0.0f, 999f);
  }

  public static void DrawPercent(this Listing_Standard list, ref float value, string name)
  {
    var rect = list.GetRect(Text.LineHeight);
    string buffer;
    list.NumberLabel(rect.LeftPart(DrawContext.guiLabelPct), value * 100f, "n0", name, out buffer);
    Widgets.TextFieldPercent(rect.RightPart(1f - DrawContext.guiLabelPct), ref value, ref buffer, 0.0f, 10f);
  }

  public static void DrawInt(this Listing_Standard list, ref int value, string name)
  {
    var rect = list.GetRect(Text.LineHeight);
    string buffer;
    list.NumberLabel(rect.LeftPart(DrawContext.guiLabelPct), value, "n0", name, out buffer);
    Widgets.IntEntry(rect.RightPart(1f - DrawContext.guiLabelPct), ref value, ref buffer);
  }

  public static void DrawEnum<T>(
    this Listing_Standard list,
    T value,
    string name,
    Action<T> setValue,
    float height = 30f)
  {
    var rect = list.GetRect(height);
    string str = name.Desc();
    if (!str.NullOrEmpty())
    {
      if (Mouse.IsOver(rect.LeftPart(DrawContext.guiLabelPct)))
        Widgets.DrawHighlight(rect);
      TooltipHandler.TipRegion(rect.LeftPart(DrawContext.guiLabelPct), str);
    }
    Widgets.Label(rect.LeftPart(DrawContext.guiLabelPct), name.Title());
    var name1 = Enum.GetName(typeof (T), value);
    if (Widgets.ButtonText(rect.RightPart(1f - DrawContext.guiLabelPct), (name + "_" + name1).Title()))
    {
      var source = new List<FloatMenuOption>();
      foreach (var obj in Enum.GetValues(typeof (T)).Cast<T>())
      {
        var enumValue = obj;
        var name2 = Enum.GetName(typeof (T), enumValue);
        source.Add(new FloatMenuOption((name + "_" + name2).Title(), () => setValue(enumValue)));
      }
      Find.WindowStack.Add(new FloatMenu(source.ToList()));
    }
    list.Gap(list.verticalSpacing);
  }
}