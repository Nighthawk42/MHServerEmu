﻿using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Random;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Generators.Population
{
    public class PopulationObject
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        public PrototypeId MarkerRef;
        public PrototypeId MissionRef;
        public GRandom Random;
        public PropertyCollection Properties;
        public SpawnFlags SpawnFlags;
        public PopulationObjectPrototype Object;
        public List<PrototypeId> SpawnAreas;
        public List<PrototypeId> SpawnCells;
        public int Count;       

        public bool SpawnByMarker(Cell cell)
        {
            // TODO add fast check markerRef for cell
            if (Count == 0) return false;
            Region region = cell.GetRegion();
            SpawnMarkerRegistry registry = region.SpawnMarkerRegistry;
            SpawnReservation reservation = registry.ReserveFreeReservation(MarkerRef, Random, cell, SpawnAreas, SpawnCells);
            if (reservation != null)
            {               
                reservation.Object = Object;
                reservation.MissionRef = MissionRef;
                
                //Logger.Warn($"{GameDatabase.GetFormattedPrototypeName(MissionRef)} {GameDatabase.GetFormattedPrototypeName(MarkerRef)} {pos}");
                ClusterGroup clusterGroup = new(region, Random, Object, null, Properties, SpawnFlags);
                clusterGroup.Initialize();
                // set group position
                var pos = reservation.GetRegionPosition();
                clusterGroup.SetParentRelativePosition(pos);
                clusterGroup.SetParentRelativeOrientation(reservation.MarkerRot); // can be random?
                // spawn Entity from Group
                clusterGroup.Spawn();
                Count--;
                return true;
            }
            return false;
        }
    }

    public class PopulationManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        public Game Game { get; }
        public Region Region { get; }
        public GRandom Random { get; }
        public List<PopulationObject> PopulationMarkers { get; }
        public List<PopulationObject> PopulationObjects { get; }

        private int _blackOutId;
        private BlackOutSpatialPartition _blackOutSpatialPartition;
        private Dictionary<int, BlackOutZone> _blackOutZones;
        private int NextBlackOutId() => _blackOutId++;

        public PopulationManager(Game game, Region region)
        {
            Game = game;
            Region = region;
            Random = new(region.RandomSeed);
            PopulationMarkers = new();
            PopulationObjects = new();
            _blackOutZones = new();
            _blackOutId = 1;
        }

        public void MissionRegisty(MissionPrototype missionProto)
        {
            if (missionProto == null) return;

            if (missionProto.PopulationSpawns.HasValue())
            {
                foreach (var entry in missionProto.PopulationSpawns)
                {
                    //if (missionProto.DesignState == DesignWorkflowState.NotInGame) 
                    //    Logger.Debug($"Mission [{missionProto.DesignState}] {GameDatabase.GetFormattedPrototypeName(missionProto.DataRef)} = {missionProto.DataRef} {GameDatabase.GetFormattedPrototypeName(entry.Population.UsePopulationMarker)}");
                    if (entry.RestrictToAreas.HasValue()) // check areas
                    {
                        bool foundArea = false;
                        foreach (var areaRef in entry.RestrictToAreas)
                        {
                            if (Region.GetArea(areaRef) != null)
                            {
                                foundArea = true;
                                break;
                            }
                        }
                        if (foundArea == false) continue;

                        List<PrototypeId> regionAreas = new();
                        foreach (var areaRef in entry.RestrictToAreas)
                            regionAreas.Add(areaRef);
                        // entry.Count; TODO count population
                        //for (var i = 0; i < entry.Count; i++)
                        AddPopulationObject(entry.Population.UsePopulationMarker, entry.Population, (int)entry.Count, regionAreas, AssetsToList(entry.RestrictToCells), missionProto.DataRef);
                    }
                    else if (entry.RestrictToRegions.HasValue()) // No areas but have Region
                    {
                        List<PrototypeId> regionAreas = new ();
                        foreach (var area in Region.IterateAreas())
                            regionAreas.Add(area.PrototypeDataRef);

                        AddPopulationObject(entry.Population.UsePopulationMarker, entry.Population, (int)entry.Count, regionAreas, AssetsToList(entry.RestrictToCells), missionProto.DataRef);
                    }
                }   
            }

        }

        public static List<PrototypeId> AssetsToList(AssetId[] assets)
        {
            if (assets.IsNullOrEmpty()) return new();
            List<PrototypeId> list = new();
            foreach (var asset in assets)
                list.Add(GameDatabase.GetDataRefByAsset(asset));
            return list;
        }

        public PopulationObject AddPopulationObject(PrototypeId populationMarkerRef, PopulationObjectPrototype population, int count, List<PrototypeId> restrictToAreas, List<PrototypeId> restrictToCellsRef, PrototypeId missionRef)
        {
            //Logger.Warn($"SpawnMarker[{count}] {GameDatabase.GetFormattedPrototypeName(populationMarkerRef)}");
            GRandom random = Game.Random;
            PropertyCollection properties = null;
            if (missionRef != PrototypeId.Invalid)
            {
                properties = new PropertyCollection();
                properties[PropertyEnum.MissionPrototype] = missionRef;
            }
            PopulationObject populationObject = new()
            {
                MarkerRef = populationMarkerRef,
                MissionRef = missionRef,
                Random = random,
                Properties = properties,
                SpawnFlags = SpawnFlags.None,
                Object = population,
                SpawnAreas = restrictToAreas,
                SpawnCells = restrictToCellsRef,
                Count = count
            };

            if (populationMarkerRef != PrototypeId.Invalid)
                PopulationMarkers.Add(populationObject);
            else
                PopulationObjects.Add(populationObject);

            return populationObject;
        }

        public void MetaStateRegisty(PrototypeId prototypeId)
        {
            var metastate = GameDatabase.GetPrototype<MetaStatePrototype>(prototypeId);

            if (metastate is MetaStateMissionProgressionPrototype missionProgression) 
            {
                if (missionProgression.StatesProgression.HasValue())
                    MetaStateRegisty(missionProgression.StatesProgression.First());
            } 
            else if (metastate is MetaStateMissionActivatePrototype missionActivate)
            {
                if (missionActivate.SubStates.HasValue())
                    foreach (var state in missionActivate.SubStates)
                        MetaStateRegisty(state);

                Logger.Debug($"State [{GameDatabase.GetFormattedPrototypeName(missionActivate.DataRef)}][{missionActivate.PopulationObjects.Length}]");
                AddRequiredObjects(missionActivate.PopulationObjects, missionActivate.PopulationAreaRestriction, null);
            }
            else if (metastate is MetaStateMissionSequencerPrototype missionSequencer)
            {
                if (missionSequencer.Sequence.HasValue())
                    foreach (var missionEntry in missionSequencer.Sequence)
                    {
                        Logger.Debug($"State [{GameDatabase.GetFormattedPrototypeName(metastate.DataRef)}][{missionEntry.PopulationObjects.Length}]");
                        AddRequiredObjects(missionEntry.PopulationObjects, missionEntry.PopulationAreaRestriction, null);
                    }
            }
            else if (metastate is MetaStateWaveInstancePrototype waveInstance)
            {
                if (waveInstance.States.HasValue())
                    foreach (var state in waveInstance.States)
                        MetaStateRegisty(state);               
            }
            else if (metastate is MetaStatePopulationMaintainPrototype popProto && popProto.PopulationObjects.HasValue())
            {               
                Logger.Debug($"State [{GameDatabase.GetFormattedPrototypeName(popProto.DataRef)}][{popProto.PopulationObjects.Length}]");
                var areas = popProto.RestrictToAreas;
                if (popProto.DataRef == (PrototypeId)7730041682554854878 && Region.PrototypeId == RegionPrototypeId.CH0402UpperEastRegion) areas = null; // Hack for Moloids
                AddRequiredObjects(popProto.PopulationObjects, areas, popProto.RestrictToCells);
            }
        }

        private void AddRequiredObjects(PopulationRequiredObjectPrototype[] populationObjects, PrototypeId[] restrictToAreas, AssetId[] restrictToCells)
        {
            List<PrototypeId> regionAreas = new();
            List<PrototypeId> regionCell;
            if (restrictToAreas.IsNullOrEmpty())
            {
                foreach (var area in Region.IterateAreas())
                    regionAreas.Add(area.PrototypeDataRef);
                regionCell = new();
                foreach (var cell in Region.Cells)
                    if (cell.Area.IsDynamicArea() == false)
                        regionCell.Add(cell.PrototypeId);
            }
            else
            {
                foreach (var areaRef in restrictToAreas)
                    regionAreas.Add(areaRef);
                regionCell = AssetsToList(restrictToCells);
            }

            Picker<PopulationRequiredObjectPrototype> popPicker = new(Game.Random);

            foreach (var popObject in populationObjects)
                popPicker.Add(popObject);

            if (popPicker.Empty() == false)
            {
                while (popPicker.PickRemove(out var popObject))
                {
                    int count = popObject.Count;
                    if (RegionManager.PatrolRegions.Contains(Region.PrototypeId)) count = 1;
                    var objectProto = popObject.GetPopObject();
                    AddPopulationObject(objectProto.UsePopulationMarker, objectProto, count, regionAreas, regionCell, PrototypeId.Invalid);
                }
            }
        }

        public int SpawnBlackOutZone(Vector3 position, float radius, PrototypeId missionRef)
        {
            var id = NextBlackOutId();
            BlackOutZone zone = new(id, position, radius, missionRef);
            _blackOutZones[id] = zone;
            _blackOutSpatialPartition.Insert(zone);
            // TODO BlackOutZonesRebuild
            return id;
        }

        public IEnumerable<BlackOutZone> IterateBlackOutZoneInVolume<B>(B bound) where B : IBounds
        {
            if (_blackOutSpatialPartition != null)
                return _blackOutSpatialPartition.IterateElementsInVolume(bound);
            else
                return Enumerable.Empty<BlackOutZone>();
        }

        public void InitializeSpacialPartition(Aabb bound)
        {
            if (_blackOutSpatialPartition != null) return;
            _blackOutSpatialPartition = new (bound);

            foreach (var zone in _blackOutZones)
                if (zone.Value != null) _blackOutSpatialPartition.Insert(zone.Value);
        }

        public bool InBlackOutZone(Vector3 position, float radius, PrototypeId missionRef)
        {
            Sphere sphere = new(position, radius);
            if (missionRef != PrototypeId.Invalid)
            {
                foreach (var zone in IterateBlackOutZoneInVolume(sphere))
                    if (zone.MissionRef != missionRef) 
                        return false;
            }
            else if (IterateBlackOutZoneInVolume(sphere).Any() == false) 
                return false;

            return true;
        }
    }
}
