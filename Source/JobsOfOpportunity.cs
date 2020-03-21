using System;
using System.Collections.Generic;
using HarmonyLib;
using HugsLib;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity : ModBase
    {
        static void InsertCode(ref int i, ref List<CodeInstruction> codes, ref List<CodeInstruction> newCodes, int offset, Func<bool> when, Func<List<CodeInstruction>> what,
            bool bringLabels = false) {
            for (i -= offset; i < codes.Count; i++) {
                if (i >= 0 && when()) {
                    var whatCodes = what();
                    if (bringLabels) {
                        whatCodes[0].labels.AddRange(codes[i + offset].labels);
                        codes[i + offset].labels.Clear();
                    }

                    newCodes.AddRange(whatCodes);
                    if (offset > 0)
                        i += offset;
                    else
                        newCodes.AddRange(codes.GetRange(i + offset, Math.Abs(offset)));
                    break;
                }

                newCodes.Add(codes[i + offset]);
            }
        }
    }
}
