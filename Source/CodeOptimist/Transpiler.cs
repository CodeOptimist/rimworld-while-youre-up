// Decompiled with JetBrains decompiler
// Type: CodeOptimist.Transpiler
// Assembly: JobsOfOpportunity, Version=3.2.2.1065, Culture=neutral, PublicKeyToken=null
// MVID: 2D726F6D-B465-4BB3-B286-8EB3FFA71395
// Assembly location: F:\SteamLibrary\steamapps\workshop\content\294100\2034960453\1.4\Assemblies\JobsOfOpportunity.dll

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;

namespace CodeOptimist;

class Transpiler
{
  public static readonly CodeInstructionComparer comparer = new();
  readonly Dictionary<int, List<List<CodeInstruction>>> indexesInserts = new();
  readonly List<Patch> neighbors = new();
  readonly MethodBase originalMethod;
  readonly MethodBase patchMethod;
  public List<CodeInstruction> codes;

  [MethodImpl(MethodImplOptions.NoInlining)]
  public Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase originalMethod)
  {
    this.originalMethod = originalMethod;
    patchMethod = new StackFrame(1).GetMethod();
    var patchInfo = Harmony.GetPatchInfo(originalMethod);
    if (patchInfo != null)
      neighbors.AddRange(patchInfo.Transpilers);
    codes = instructions.ToList();
  }

  public int MatchIdx { get; private set; }

  public int InsertIdx { get; private set; }

  public int TryFindCodeIndex(Predicate<CodeInstruction> match) => TryFindCodeIndex(0, match);

  public int TryFindCodeIndex(int startIndex, Predicate<CodeInstruction> match) => TryFind(match, () => codes.FindIndex(startIndex, match));

  public int TryFindCodeLastIndex(Predicate<CodeInstruction> match) => TryFindCodeLastIndex(codes.Count - 1, match);

  public int TryFindCodeLastIndex(int startIndex, Predicate<CodeInstruction> match) => TryFind(match, () => codes.FindLastIndex(startIndex, match));

  int TryFind(Predicate<CodeInstruction> match, Func<int> resultFunc)
  {
    int num;
    try
    {
      num = resultFunc();
    }
    catch (Exception ex)
    {
      throw new CodeNotFoundException(match.Method, patchMethod, neighbors);
    }
    return num != -1 ? num : throw new CodeNotFoundException(match.Method, patchMethod, neighbors);
  }

  public bool TrySequenceEqual(int startIndex, List<CodeInstruction> sequence)
  {
    try
    {
      return codes.GetRange(startIndex, sequence.Count).SequenceEqual(sequence, comparer);
    }
    catch (Exception ex)
    {
      throw new CodeNotFoundException(sequence, patchMethod, neighbors);
    }
  }

  public int TryFindCodeSequence(List<CodeInstruction> sequence) => TryFindCodeSequence(0, sequence);

  public int TryFindCodeSequence(int startIndex, List<CodeInstruction> sequence)
  {
    if (sequence.Count > codes.Count)
      return -1;
    try
    {
      return Enumerable.Range(startIndex, codes.Count - sequence.Count + 1).First(i => codes.Skip(i).Take(sequence.Count).SequenceEqual(sequence, comparer));
    }
    catch (InvalidOperationException ex)
    {
      throw new CodeNotFoundException(sequence, patchMethod, neighbors);
    }
  }

  public void TryInsertCodes(
    int offset,
    Func<int, List<CodeInstruction>, bool> match,
    Func<int, List<CodeInstruction>, List<CodeInstruction>> newCodes,
    bool bringLabels = false)
  {
    for (var matchIdx = MatchIdx; matchIdx < codes.Count; ++matchIdx)
    {
      if (match(matchIdx, codes))
      {
        List<List<CodeInstruction>> codeInstructionListList;
        if (!indexesInserts.TryGetValue(matchIdx + offset, out codeInstructionListList))
        {
          codeInstructionListList = new List<List<CodeInstruction>>();
          indexesInserts.Add(matchIdx + offset, codeInstructionListList);
        }
        var codeInstructionList = newCodes(matchIdx, codes);
        if (bringLabels)
        {
          codeInstructionList[0].labels.AddRange(codes[matchIdx + offset].labels);
          codes[matchIdx + offset].labels.Clear();
        }
        codeInstructionListList.Add(codeInstructionList);
        MatchIdx = matchIdx;
        InsertIdx = matchIdx + offset;
        return;
      }
    }
    throw new CodeNotFoundException(match.Method, patchMethod, neighbors);
  }

  public IEnumerable<CodeInstruction> GetFinalCodes(bool debug = false)
  {
    var source = new List<CodeInstruction>();
    for (var index = 0; index < codes.Count; ++index)
    {
      List<List<CodeInstruction>> codeInstructionListList;
      if (indexesInserts.TryGetValue(index, out codeInstructionListList))
      {
        foreach (var collection in codeInstructionListList)
          source.AddRange(collection);
      }
      source.Add(codes[index]);
    }
    return source.AsEnumerable();
  }

  class CodeNotFoundException : Exception
  {
    public CodeNotFoundException(
      List<CodeInstruction> sequence,
      MethodBase patchMethod,
      List<Patch> neighbors)
      : this("Unmatched sequence: " + string.Join(", ", sequence.Select(x => x.ToString())), patchMethod, neighbors)
    {
    }

    public CodeNotFoundException(
      MethodInfo matchMethod,
      MethodBase patchMethod,
      List<Patch> neighbors)
      : this("Unmatched predicate: " + BitConverter.ToString(matchMethod.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>()).Replace("-", ""), patchMethod, neighbors)
    {
    }

    CodeNotFoundException(string message, MethodBase patchMethod, List<Patch> neighbors)
      : base(message)
    {
      var modContentPack = LoadedModManager.RunningModsListForReading.First(x => x.assemblies.loadedAssemblies.Contains(patchMethod.DeclaringType?.Assembly));
      var list = neighbors.Select(n => LoadedModManager.RunningModsListForReading.First(m => m.assemblies.loadedAssemblies.Contains(n.PatchMethod.DeclaringType?.Assembly)).Name).Distinct().ToList();
      Log.Warning("[" + modContentPack.Name + "] You're welcome to 'Share logs' to my Discord: https://discord.gg/pnZGQAN \n");
      if (!list.Any())
        return;
      Log.Error("[" + modContentPack.Name + "] Likely conflict with one of: " + string.Join(", ", list));
    }
  }
}