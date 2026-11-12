using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Rendering;
using Game.Simulation;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;
using FireEngine = Game.Vehicles.FireEngine;
using FireStation = Game.Buildings.FireStation;
using PoliceStation = Game.Buildings.PoliceStation;
using Random = Unity.Mathematics.Random;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Sends police and fire brigade along with the ambulance, with lights:
    ///  - Police: AmpdsCode.Police cars (psychiatry A 1 .. D/E 4, AED codes 2), plus 1 on a locked door.
    ///  - Fire brigade: 1 engine for AED codes (First Responder) and on a locked door.
    ///  - Locked door: rolled once per call at a home (no Einsatzcode for it).
    /// Spawned like NefDispatchSystem from the nearest station with free vehicles. Police get purpose mask 0 and fire
    /// engines the Empty flag, so vanilla never hands them other jobs. Without a job vanilla ResetPath clears
    /// CarFlags.Emergency; this system sets it again while driving there.
    /// On scene they are held while an ambulance is on its way to or caring for the patient: vanilla Police/FireEngine
    /// AISystem skip vehicles with TripSource (not yet out of their building), so the car gets one with a timer and is
    /// stopped. VehicleSpawnSystem only counts the timer down; when it runs out (e.g. after loading a save) vanilla takes
    /// over again. SidewalkParkingSystem parks held cars with the ambulance group of the call. Afterwards vanilla sends
    /// them home. Never parks: deleted on arrival home.
    /// </summary>
    public partial class ResponderDispatchSystem : GameSystemBase
    {
        private EntityQuery m_Ambulances, m_PoliceStations, m_FireStations, m_PolicePrefabs, m_FirePrefabs;
        private UnitNamingSystem m_Units;
        private Random m_Random = new Random(0x9011);
        private readonly HashSet<Entity> m_Handled = new HashSet<Entity>(); // patients already decided
        private class Call { public Entity Patient, Location; public uint Since; public bool Held, Done; }
        private readonly Dictionary<Entity, Call> m_Cars = new Dictionary<Entity, Call>();
        private SimulationSystem m_Sim;
        private SidewalkParkingSystem m_Parking;

        /// <summary>Police cars and fire engines held on scene -> call location. Read by SidewalkParkingSystem and the dispatch UI.</summary>
        public static readonly Dictionary<Entity, Entity> HeldAt = new Dictionary<Entity, Entity>();

        /// <summary>Patients whose door is locked. Read by UnitNamingSystem.</summary>
        public static readonly HashSet<Entity> LockedDoor = new HashSet<Entity>();

        // ponytail: fixed chance; move to settings if players want to tune it.
        private const float LockedDoorChance = 0.05f;
        /// <summary>Debug tab: patients whose next call gets a locked door regardless of chance or building type.</summary>
        public static readonly HashSet<Entity> ForceLocked = new HashSet<Entity>();

        /// <summary>Call patient of a police car / fire engine sent by this system, else Entity.Null. Read by CallInfoSection.</summary>
        public Entity PatientOf(Entity car) => m_Cars.TryGetValue(car, out var c) ? c.Patient : Entity.Null;
        public IEnumerable<Entity> CarsOf(Entity patient) { foreach (var kv in m_Cars) if (kv.Value.Patient == patient) yield return kv.Key; }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;
        // The frame PoliceCarAISystem runs (FireEngineAISystem: one before), right after their arrival check.
        public override int GetUpdateOffset(SystemUpdatePhase phase) => 5;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Parking = World.GetOrCreateSystemManaged<SidewalkParkingSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_PoliceStations = GetEntityQuery(ComponentType.ReadOnly<PoliceStation>(), ComponentType.ReadOnly<Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_FireStations = GetEntityQuery(ComponentType.ReadOnly<FireStation>(), ComponentType.ReadOnly<Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_PolicePrefabs = GetEntityQuery(ComponentType.ReadOnly<PoliceCarData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
            m_FirePrefabs = GetEntityQuery(ComponentType.ReadOnly<FireEngineData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Handled.Clear(); m_Cars.Clear(); LockedDoor.Clear(); HeldAt.Clear(); ForceLocked.Clear();
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);
            var active = new HashSet<Entity>();
            var awaited = new HashSet<Entity>(); // patients an ambulance is driving to or caring for
            foreach (var v in vehicles)
            {
                if (NefDispatchSystem.IsNef(em, v)) continue;
                var a = em.GetComponentData<Ambulance>(v);
                if (a.m_TargetPatient == Entity.Null || a.m_TargetLocation == Entity.Null) continue;
                active.Add(a.m_TargetPatient);
                if ((a.m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) == 0) awaited.Add(a.m_TargetPatient);
                if (m_Handled.Contains(a.m_TargetPatient) || !m_Units.TryGetCode(v, out var code) || code == null) continue;
                m_Handled.Add(a.m_TargetPatient);

                bool locked = ForceLocked.Remove(a.m_TargetPatient) || em.HasComponent<ResidentialProperty>(a.m_TargetLocation) && m_Random.NextFloat() < LockedDoorChance;
                if (locked) LockedDoor.Add(a.m_TargetPatient);
                int police = math.max(code.Police, locked ? 1 : 0);
                int fire = math.max(code.Fire, locked ? 1 : 0);
                for (int i = 0; i < police; i++) Spawn(a.m_TargetPatient, a.m_TargetLocation, true);
                for (int i = 0; i < fire; i++) Spawn(a.m_TargetPatient, a.m_TargetLocation, false);
                if (police + fire > 0)
                    Mod.log.Info($"Patient {a.m_TargetPatient.Index} ({code} {code.Reason}{(locked ? ", Tür verschlossen" : "")}): {police} Polizei, {fire} Feuerwehr alarmiert");
            }
            LockedDoor.IntersectWith(active); // call over

            uint now = m_Sim.frameIndex;
            uint cap = (uint)((Mod.Setting.NefMaxWaitMinutes + Mod.Setting.OnSceneMinutes + 60) * OnSceneCareSystem.FramesPerGameMinute);
            foreach (var car in new List<Entity>(m_Cars.Keys))
            {
                var call = m_Cars[car];
                if (!em.Exists(car) || em.HasComponent<Deleted>(car)) { m_Cars.Remove(car); HeldAt.Remove(car); continue; }
                if (em.HasComponent<ParkedCar>(car)) { em.AddComponent<Deleted>(car); m_Cars.Remove(car); continue; }
                bool needed = awaited.Contains(call.Patient) && (!call.Held || now - call.Since < cap);
                if (call.Held)
                {
                    if (needed) { Lights(car); continue; }
                    HeldAt.Remove(car);
                    if (m_Parking.IsParked(car)) continue; // SidewalkParkingSystem puts it back onto its lane first
                    Release(car, call);
                    continue;
                }
                if (needed && !call.Done) m_Parking.ArriveIfQueued(car, call.Location); // stuck behind the call's other vehicles
                if (needed && !call.Done && Arrived(car, call)) { Hold(car, call, now, cap); continue; }
                if (call.Done || Returning(car)) { LightsOff(car); continue; } // vanilla never clears the flag we set
                Lights(car);
            }
        }

        private bool Returning(Entity car) => EntityManager.HasComponent<Game.Vehicles.PoliceCar>(car)
            ? (EntityManager.GetComponentData<Game.Vehicles.PoliceCar>(car).m_State & PoliceCarFlags.Returning) != 0
            : (EntityManager.GetComponentData<FireEngine>(car).m_State & FireEngineFlags.Returning) != 0;

        private void Lights(Entity car)
        {
            var em = EntityManager;
            var c = em.GetComponentData<Car>(car);
            if ((c.m_Flags & CarFlags.Emergency) != 0) return;
            c.m_Flags |= CarFlags.Emergency | CarFlags.StayOnRoad;
            em.SetComponentData(car, c);
            if (!em.HasComponent<EffectsUpdated>(car)) em.AddComponent<EffectsUpdated>(car);
        }

        private void LightsOff(Entity car)
        {
            var em = EntityManager;
            var c = em.GetComponentData<Car>(car);
            if ((c.m_Flags & CarFlags.Emergency) == 0) return;
            c.m_Flags &= ~CarFlags.Emergency;
            em.SetComponentData(car, c);
            if (!em.HasComponent<EffectsUpdated>(car)) em.AddComponent<EffectsUpdated>(car);
        }

        /// <summary>Standing at the end of its path near the call, or just sent home from there by vanilla.</summary>
        private bool Arrived(Entity car, Call call)
        {
            var em = EntityManager;
            if (em.HasComponent<TripSource>(car) || !em.HasComponent<Transform>(call.Location)) return false;
            if (!Returning(car) && !VehicleUtils.PathEndReached(em.GetComponentData<CarCurrentLane>(car))) return false;
            if (em.HasComponent<Moving>(car) && math.length(em.GetComponentData<Moving>(car).m_Velocity) > 2f) return false;
            // ponytail: 150 m like OnSceneCareSystem.OnScene; huge lots could be further.
            return math.distance(em.GetComponentData<Transform>(car).m_Position.xz, em.GetComponentData<Transform>(call.Location).m_Position.xz) < 150f;
        }

        /// <summary>Takes the car out of vanilla's AI and stops it, like vanilla PoliceCarAISystem.StopVehicle.</summary>
        private void Hold(Entity car, Call call, uint now, uint cap)
        {
            var em = EntityManager;
            em.AddComponentData(car, new TripSource(em.GetComponentData<Owner>(car).m_Owner, cap));
            em.RemoveComponent<Moving>(car);
            em.RemoveComponent<TransformFrame>(car);
            em.RemoveComponent<InterpolatedTransform>(car);
            em.RemoveComponent<Swaying>(car);
            if (!em.HasComponent<Stopped>(car)) em.AddComponent<Stopped>(car);
            if (!em.HasComponent<Updated>(car)) em.AddComponent<Updated>(car);
            call.Held = true; call.Since = now;
            HeldAt[car] = call.Location;
            Mod.log.Info($"{Name(car)} {car.Index}: bleibt am Einsatzort bei Patient {call.Patient.Index}");
        }

        /// <summary>
        /// Back to vanilla, which starts the stopped car (StartVehicle) and sends it home on a new path from where it
        /// stands now. EndOfPath must go: vanilla deletes a returning car that has reached the end of its path.
        /// </summary>
        private void Release(Entity car, Call call)
        {
            var em = EntityManager;
            call.Held = false; call.Done = true;
            em.RemoveComponent<TripSource>(car);
            var lane = em.GetComponentData<CarCurrentLane>(car);
            lane.m_LaneFlags &= ~Game.Vehicles.CarLaneFlags.EndOfPath;
            em.SetComponentData(car, lane);
            var po = em.GetComponentData<PathOwner>(car);
            po.m_State |= PathFlags.Obsolete; // it may have been moved to the ambulance group
            em.SetComponentData(car, po);
            Mod.log.Info($"{Name(car)} {car.Index}: rückt ein");
        }

        private string Name(Entity car) => EntityManager.HasComponent<Game.Vehicles.PoliceCar>(car) ? "Polizei" : "Feuerwehr";

        private void Spawn(Entity patient, Entity location, bool police)
        {
            var em = EntityManager;
            if (!em.HasComponent<Transform>(location)) return;
            float3 at = em.GetComponentData<Transform>(location).m_Position;
            var station = police
                ? Nearest(m_PoliceStations, at, s => (em.GetComponentData<PoliceStation>(s).m_Flags & PoliceStationFlags.HasAvailablePatrolCars) != 0)
                : Nearest(m_FireStations, at, s => (em.GetComponentData<FireStation>(s).m_Flags & FireStationFlags.HasAvailableFireEngines) != 0);
            // ponytail: first unlocked prefab, ignoring city theme; use Police/FireEngineSelectData if the look matters.
            var prefab = police
                ? First(m_PolicePrefabs, p => p != NefPrefabSystem.NefPrefab && (em.GetComponentData<PoliceCarData>(p).m_PurposeMask & PolicePurpose.Patrol) != 0)
                : First(m_FirePrefabs, p => true);
            if (station == Entity.Null || prefab == Entity.Null) { Mod.log.Warn(police ? "Polizei: keine Wache mit freiem Streifenwagen" : "Feuerwehr: keine Wache mit freiem Fahrzeug"); return; }

            using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
            em.CreateEntity(em.GetComponentData<ObjectData>(prefab).m_Archetype, created);
            var e = created[0];
            em.SetComponentData(e, em.GetComponentData<Transform>(station));
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new PseudoRandomSeed(ref m_Random));
            em.AddComponentData(e, new TripSource(station));
            em.AddComponentData(e, default(Unspawned));
            em.AddComponentData(e, new Owner(station));
            if (police) em.SetComponentData(e, new Game.Vehicles.PoliceCar(PoliceCarFlags.Empty, 0, 0));
            else em.SetComponentData(e, new FireEngine(FireEngineFlags.Empty, 0, 0f, 1f));
            em.SetComponentData(e, new Target(location));
            m_Cars[e] = new Call { Patient = patient, Location = location };
        }

        // ponytail: straight-line distance and no per-station vehicle count; all cars may come from the same station.
        private Entity Nearest(EntityQuery stations, float3 at, Func<Entity, bool> ok)
        {
            var em = EntityManager;
            using var all = stations.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null; float bestDist = float.MaxValue;
            foreach (var s in all)
            {
                if (!ok(s)) continue;
                float d = math.distance(at, em.GetComponentData<Transform>(s).m_Position);
                if (d < bestDist) { bestDist = d; best = s; }
            }
            return best;
        }

        private static Entity First(EntityQuery prefabs, Func<Entity, bool> ok)
        {
            using var all = prefabs.ToEntityArray(Allocator.Temp);
            foreach (var p in all) if (ok(p)) return p;
            return Entity.Null;
        }
    }
}
