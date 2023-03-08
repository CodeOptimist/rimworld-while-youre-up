using HarmonyLib;
using System.Collections.Generic;

namespace CodeOptimist;

class CodeInstructionComparer : IEqualityComparer<CodeInstruction>
{
  public bool Equals(CodeInstruction x, CodeInstruction y) => x != null && y != null && (x == y || Equals(x.opcode, y.opcode) && (Equals(x.operand, y.operand) || x.operand == null || y.operand == null));

  public int GetHashCode(CodeInstruction obj) => obj.GetHashCode();
}