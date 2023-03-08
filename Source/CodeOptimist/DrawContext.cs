using System;
using UnityEngine;
using Verse;

namespace CodeOptimist;

class DrawContext : IDisposable
{
  public static float guiLabelPct = 0.5f;
  readonly Color guiColor;
  readonly TextAnchor textAnchor;
  readonly GameFont textFont;
  readonly float labelPct;

  public DrawContext()
  {
    guiColor = GUI.color;
    textFont = Text.Font;
    textAnchor = Text.Anchor;
    labelPct = guiLabelPct;
  }

  public void Dispose()
  {
    GUI.color = guiColor;
    Text.Font = textFont;
    Text.Anchor = textAnchor;
    guiLabelPct = labelPct;
  }

  public Color GuiColor
  {
    set => GUI.color = value;
  }

  public GameFont TextFont
  {
    set => Text.Font = value;
  }

  public TextAnchor TextAnchor
  {
    set => Text.Anchor = value;
  }

  public float LabelPct
  {
    set => guiLabelPct = value;
  }
}