using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [HarmonyPatch(typeof(JobUtility), nameof(JobUtility.TryStartErrorRecoverJob))]
        static class JobUtility__TryStartErrorRecoverJob_Patch
        {
            static int    lastFrameCount;
            static Pawn   lastPawn;
            static string lastCallerName;

            [HarmonyPrefix]
            static void OfferSupport(Pawn pawn) {
                if (RealTime.frameCount == lastFrameCount && pawn == lastPawn) {
                    Log.Warning(
                        $"[{mod.Content.Name}] You're welcome to 'Share logs' to my Discord: https://discord.gg/pnZGQAN \n"
                        + $"Below \"10 jobs in one tick\" error occurred during {lastCallerName}, but could be from several mods.");
                }
            }

            public static Job CatchStanding(Pawn pawn, Job job, [CallerMemberName] string callerName = "") {
                lastPawn = pawn;
                lastFrameCount = RealTime.frameCount;
                lastCallerName = callerName;
                return job;
            }
        }
    }
}
