// Decompiled with JetBrains decompiler
// Type: CodeOptimist.TranspilerHelper
// Assembly: JobsOfOpportunity, Version=3.2.2.1065, Culture=neutral, PublicKeyToken=null
// MVID: 2D726F6D-B465-4BB3-B286-8EB3FFA71395
// Assembly location: F:\SteamLibrary\steamapps\workshop\content\294100\2034960453\1.4\Assemblies\JobsOfOpportunity.dll

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CodeOptimist;

static class TranspilerHelper
{
  public static IEnumerable<CodeInstruction> ReplaceTypes(
    IEnumerable<CodeInstruction> codes,
    Dictionary<Type, Type> subs)
  {
    var list = codes.ToList();
    foreach (var codeInstruction in list)
    {
      var operand = codeInstruction.operand as MethodInfo;
      Type type;
      if ((object) operand != null && subs.TryGetValue(operand.DeclaringType, out type))
      {
        var array = operand.GetParameters().Select(x => x.ParameterType).ToArray();
        var genericArguments = operand.GetGenericArguments();
        var methodInfo = operand.IsGenericMethod ? AccessTools.DeclaredMethod(type, operand.Name, array, genericArguments) : AccessTools.DeclaredMethod(type, operand.Name, array);
        if (methodInfo != null)
          codeInstruction.operand = methodInfo;
      }
    }
    return list.AsEnumerable();
  }

  public static string NameWithType(this MethodBase method, bool withNamespace = true) => (withNamespace ? method.DeclaringType.FullName : method.DeclaringType.FullName.Substring(method.DeclaringType.Namespace.Length + 1)) + "." + method.Name;
}