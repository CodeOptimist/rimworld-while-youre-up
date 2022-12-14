using System.Linq;
using RimWorld;
using Verse;

namespace JobsOfOpportunity
{
    partial class Mod
    {
        [DebugAction("Autotests", "Make colony (While You're Up)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        static void MakeColonyWYU() {
            var godMode = DebugSettings.godMode;
            DebugSettings.godMode                   = true;
            DebugViewSettings.drawOpportunisticJobs = true;

            Thing.allowDestroyNonDestroyable = true;
            if (Autotests_ColonyMaker.usedCells is null)
                Autotests_ColonyMaker.usedCells = new BoolGrid(Autotests_ColonyMaker.Map);
            else
                Autotests_ColonyMaker.usedCells.ClearAndResizeTo(Autotests_ColonyMaker.Map);
            Autotests_ColonyMaker.overRect = new CellRect(Autotests_ColonyMaker.Map.Center.x - 50, Autotests_ColonyMaker.Map.Center.z - 50, 100, 50);
            Autotests_ColonyMaker.DeleteAllSpawnedPawns();
            GenDebug.ClearArea(Autotests_ColonyMaker.overRect, Find.CurrentMap);

            Autotests_ColonyMaker.TryGetFreeRect(90, 30, out var pawnAndThingRect);
            foreach (var thingDef in from def in DefDatabase<ThingDef>.AllDefs
                     where typeof(Building_WorkTable).IsAssignableFrom(def.thingClass)
                     select def) {
                if (Autotests_ColonyMaker.TryMakeBuilding(thingDef) is Building_WorkTable workTable) {
                    foreach (var recipe in workTable.def.AllRecipes)
                        workTable.billStack.AddBill(recipe.MakeNewBill());
                }
            }

            pawnAndThingRect = pawnAndThingRect.ContractedBy(1);
            var itemDefs = (from def in DefDatabase<ThingDef>.AllDefs
                where DebugThingPlaceHelper.IsDebugSpawnable(def) && def.category == ThingCategory.Item
                select def).ToList();
            foreach (var itemDef in itemDefs)
                DebugThingPlaceHelper.DebugSpawn(itemDef, pawnAndThingRect.RandomCell, -1, true);

            var pawnCount = 30;
            for (var i = 0; i < pawnCount; i++) {
                var pawn = PawnGenerator.GeneratePawn(Faction.OfPlayer.def.basicMemberKind, Faction.OfPlayer);
                foreach (var w in DefDatabase<WorkTypeDef>.AllDefs) {
                    if (!pawn.WorkTypeIsDisabled(w))
                        pawn.workSettings.SetPriority(w, 3);
                }
                pawn.workSettings.Disable(WorkTypeDefOf.Hauling);
                GenSpawn.Spawn(pawn, pawnAndThingRect.RandomCell, Autotests_ColonyMaker.Map);
            }

            var designated = new Designator_ZoneAddStockpile_Resources();
            for (var _ = 0; _ < 6; _++) {
                Autotests_ColonyMaker.TryGetFreeRect(7, 7, out var stockpileRect);
                stockpileRect = stockpileRect.ContractedBy(1);
                designated.DesignateMultiCell(stockpileRect.Cells);
                ((Zone_Stockpile)Autotests_ColonyMaker.Map.zoneManager.ZoneAt(stockpileRect.CenterCell)).settings.Priority = StoragePriority.Normal;
            }

            Autotests_ColonyMaker.ClearAllHomeArea();
            Autotests_ColonyMaker.FillWithHomeArea(Autotests_ColonyMaker.overRect);
            DebugSettings.godMode            = godMode;
            Thing.allowDestroyNonDestroyable = false;
        }
    }
}
