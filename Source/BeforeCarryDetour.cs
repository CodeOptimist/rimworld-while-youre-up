using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CodeOptimist;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
// ReSharper disable once RedundantUsingDirective
using Debug = System.Diagnostics.Debug;

namespace WhileYoureUp;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
partial class Mod
{
    // [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), nameof(WorkGiver_ConstructDeliverResources.HasJobOnThing))]
    // `WorkGiver_ConstructDeliverResources` inherits `HasJobOnThing()`, so we must patch the parent
    [HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing))]
    static class WorkGiver_Scanner__HasJobOnThing_Patch
    {
        [HarmonyPostfix]
        // clear this so our code that ran for `HasJobOnThing()` can re-run for `JobOnThing()`
        static void ClearTempDetour(Pawn pawn) => detours.GetValueSafe(pawn)?.Deactivate();
    }

    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    static class WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> BeforeSupplyDetour(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod) {
            var t = new Transpiler(_codes, __originalMethod);

            var afterNearbyIdx =
                t.TryFindCodeIndex(code => code.Calls(AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources), "FindAvailableNearbyResources")));
            afterNearbyIdx += 1;
            var foundResIdx = t.TryFindCodeLastIndex(afterNearbyIdx, code => code.opcode == OpCodes.Brfalse) + 1; // backward search
            var afterNearby = generator.DefineLabel();
            t.codes[afterNearbyIdx].labels.Add(afterNearby);

            var needField           = AccessTools.FindIncludingInnerTypes(typeof(WorkGiver_ConstructDeliverResources), type => AccessTools.DeclaredField(type, "need"));
            var needDeclaringObjIdx = t.TryFindCodeIndex(code => code.Is(OpCodes.Newobj, AccessTools.DeclaredConstructor(needField.DeclaringType)));

            var leaveJobIdx = t.TryFindCodeIndex(afterNearbyIdx, code => code.opcode == OpCodes.Leave);
            var jobVar      = t.codes[leaveJobIdx - 1].operand;

            t.TryInsertCodes(
                offset: 0,
                (i, codes) => i == afterNearbyIdx,
                (i, codes) => new List<CodeInstruction> {
                    // job = BeforeSupplyDetourJob(pawn, need, (Thing) c, foundRes);
                    new(OpCodes.Ldarg_1),                       // Pawn pawn
                    new(codes[needDeclaringObjIdx + 2].opcode), // ThingDefCountClass <>c__DisplayClass9_1
                    new(OpCodes.Ldfld, needField),              //                                        .need
                    new(OpCodes.Ldarg_2),                       // IConstructible c
                    new(OpCodes.Castclass, typeof(Thing)),      // (Thing) c
                    codes[foundResIdx + 1].Clone(),             // Thing foundRes
                    codes[foundResIdx + 2].Clone(),             // Thing foundRes
                    new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch), nameof(BeforeSupplyDetour_Job))),
                    new(OpCodes.Stloc_S, jobVar),

                    // if (job is not null) return job;
                    new(OpCodes.Ldloc_S, jobVar),
                    new(OpCodes.Brfalse_S, afterNearby),
                    new(OpCodes.Ldloc_S, jobVar),
                    new(OpCodes.Ret),
                });

            return t.GetFinalCodes();
        }

        static Job BeforeSupplyDetour_Job(Pawn pawn, ThingDefCountClass need, Thing constructible, Thing th) {
            if (!settings.Enabled || !settings.HaulBeforeCarry_Supplies || AlreadyHauling(pawn)) return null;
            if (pawn.WorkTagIsDisabled(WorkTags.ManualDumb | WorkTags.Hauling | WorkTags.AllWork)) return null; // like vanilla `TryOpportunisticJob()`

            var mostThing = WorkGiver_ConstructDeliverResources.resourcesAvailable.DefaultIfEmpty().MaxBy(x => x.stackCount);
            if (!havePuah || !settings.UsePickUpAndHaulPlus) { // too difficult to know in advance if there are no extras for PUAH
                if (mostThing.stackCount <= need.count)
                    return null; // there are no extras
            }

            var puahOrHtcJob = BeforeCarryDetour_Job(pawn, constructible.Position, mostThing ?? th); // :BeforeSupplyDetour
            return JobUtility__TryStartErrorRecoverJob_Patch.CatchStanding_Job(pawn, puahOrHtcJob);
        }
    }

    // :BeforeBillDetour :BeforeSupplyDetour
    static Job BeforeCarryDetour_Job(Pawn pawn, LocalTargetInfo carryTarget, Thing thing) {
        if (thing.ParentHolder is Pawn_InventoryTracker) return null;

        // try to avoid haul-before-carry when there are no extras to grab
        // proper way is to recheck after grabbing everything, but here's a simple hack to at least avoid it with stone chunks
        if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 2)) return null; // already going for 1, so 2 to check for another

        if (!TryFindBestBetterStoreCellFor_MidwayToTarget(
                thing, LocalTargetInfo.Invalid, carryTarget, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var storeCell, true)) return null;

        var fromHereSquared  = thing.Position.DistanceToSquared(carryTarget.Cell);
        var fromStoreSquared = storeCell.DistanceToSquared(carryTarget.Cell);
        Debug.WriteLine($"Carry from here: {thing.Position.DistanceTo(carryTarget.Cell)};carry from store: {storeCell.DistanceTo(carryTarget.Cell)}");

        if (fromStoreSquared < fromHereSquared) {
            Debug.WriteLine(
                $"'{pawn}' prefixed job with haul for '{thing.Label}'"
                + $" because '{storeCell.GetSlotGroup(pawn.Map)}' is closer to original destination '{carryTarget} {carryTarget.Cell}'.");

            if (DebugViewSettings.drawOpportunisticJobs) {
                for (var _ = 0; _ < 3; _++) {
                    var duration = 600;
                    pawn.Map.debugDrawer.FlashCell(thing.Position,   0.62f, pawn.Name.ToStringShort, duration); // cyan
                    pawn.Map.debugDrawer.FlashCell(storeCell,        0.22f, pawn.Name.ToStringShort, duration); // orange
                    pawn.Map.debugDrawer.FlashCell(carryTarget.Cell, 0.0f,  pawn.Name.ToStringShort, duration); // red

                    // magenta: shorter old; cyan: longer new
                    pawn.Map.debugDrawer.FlashLine(thing.Position, carryTarget.Cell, duration, SimpleColor.Magenta);
                    pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell,        duration, SimpleColor.Cyan);
                    pawn.Map.debugDrawer.FlashLine(storeCell,      carryTarget.Cell, duration, SimpleColor.Cyan);
                }
            }

            if (havePuah && settings.UsePickUpAndHaulPlus) {
                var detour = SetOrAddDetour(pawn, DetourType.PuahBeforeCarry, storeCell: storeCell, carryTarget: carryTarget);
                detour.TrackPuahThing(thing, storeCell);
                var puahJob = PuahJob(pawn, thing);
                if (puahJob is not null) return puahJob;
            }

            SetOrAddDetour(pawn, DetourType.HtcBeforeCarry, carryTarget: carryTarget);
            var htcJob = HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
            return htcJob;
        }

        return null;
    }
}
