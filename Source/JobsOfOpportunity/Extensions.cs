// Decompiled with JetBrains decompiler
// Type: JobsOfOpportunity.Extensions
// Assembly: JobsOfOpportunity, Version=3.2.2.1065, Culture=neutral, PublicKeyToken=null
// MVID: 2D726F6D-B465-4BB3-B286-8EB3FFA71395
// Assembly location: F:\SteamLibrary\steamapps\workshop\content\294100\2034960453\1.4\Assemblies\JobsOfOpportunity.dll

using Verse;

namespace JobsOfOpportunity;

static class Extensions
{
    public static string ModTranslate(this string key, params NamedArgument[] args) {
        return ("CodeOptimist.WhileYoureUp_" + key).Translate(args);
    }
}