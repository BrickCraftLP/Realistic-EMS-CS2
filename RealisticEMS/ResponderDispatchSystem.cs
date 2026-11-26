using Colossal.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Events;
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
    /// Fire at a building: a vanilla call. The building gets a RescueTarget (vanilla only puts those on Destroyed
    /// buildings) plus a FireRescueRequest, so vanilla picks, routes and accounts a real station engine. Vanilla sends it
    /// home on arrival (nothing to clear); the hold below keeps it on scene like the others.
    /// Police at a vanilla traffic accident: vanilla AccidentSiteSystem already sends them; they are only linked to the
    /// call (m_Linked), never held. All other police (and fire at a non-building location) are spawned:
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
        private EntityQuery m_MaintDepots, m_MaintPrefabs, m_Ambulances, m_PoliceStations, m_FireStations, m_PolicePrefabs, m_FirePrefabs, m_AmbulanceSources, m_AmbulancePrefabs;
        private NefDispatchSystem m_Nef;
        private RoadReachSystem m_Reach;

        /// <summary>
        /// Ambulances and NEFs on standby at a fire (FireAlarmSystem): no patient, held at the staging address while the
        /// building burns. Kept until the vehicle is gone, so NefDispatchSystem leaves a released NEF alone on its way home.
        /// </summary>
        public static readonly HashSet<Entity> Standby = new HashSet<Entity>();
        private UnitNamingSystem m_Units;
        private Random m_Random = new Random(0x9011);
        private readonly HashSet<Entity> m_Handled = new HashSet<Entity>(); // patients already decided
        /// <summary>Code of each decided call (patient -> code), so the UI can show it on police/fire rows.</summary>
        public static readonly Dictionary<Entity, AmpdsCode> CodeByPatient = new Dictionary<Entity, AmpdsCode>();
        private class Call { public Entity Patient, Location, Post, CordonEdge, CordonNode, Take, TakeAt; public uint Since, Spawned, Cap; public bool Held, Done, Vanilla, Maint, Retasked; public PolicePurpose OrigPurpose; public float3 Detour, CrashAt, CrashDir; public float Behind; public Entity Stand => Post != Entity.Null ? Post : Location; } // Cap: fire scene hold ceiling (frames), 0 = default. Post: fire cordon address the car stands at // Vanilla: a station's own vehicle, never deleted by us
        // ponytail: fixed safety cap on live police/fire cars, only reached when calls pile up; normal play stays far below.
        private const int MaxCars = 40;
        private readonly Dictionary<Entity, Call> m_Cars = new Dictionary<Entity, Call>();
        private SimulationSystem m_Sim;
        private SidewalkParkingSystem m_Parking;

        private EntityQuery m_FireEngines, m_PoliceCars, m_LeftoverRescue;
        private EntityArchetype m_RescueRequestArchetype;
        private class FireCall { public Entity Patient, Request, Engine; }
        // ponytail: one per location; a second patient at the same address shares the first one's engine and call end.
        private readonly Dictionary<Entity, FireCall> m_FireCalls = new Dictionary<Entity, FireCall>(); // call location -> vanilla fire call
        private readonly Dictionary<Entity, Entity> m_Linked = new Dictionary<Entity, Entity>(); // vanilla accident police car -> patient
        private bool m_LeftoversChecked;

        /// <summary>Police cars and fire engines held on scene -> call location. Read by SidewalkParkingSystem and the dispatch UI.</summary>
        public static readonly Dictionary<Entity, Entity> HeldAt = new Dictionary<Entity, Entity>();

        /// <summary>Patients whose door is locked. Read by UnitNamingSystem.</summary>
        public static readonly HashSet<Entity> LockedDoor = new HashSet<Entity>();

        // ponytail: fixed chance; move to settings if players want to tune it.
        private const float LockedDoorChance = 0.05f;
        /// <summary>Debug tab: patients whose next call gets a locked door regardless of chance or building type.</summary>
        public static readonly HashSet<Entity> ForceLocked = new HashSet<Entity>();

        /// <summary>Call patient of a police car / fire engine sent by this system, else Entity.Null. Read by CallInfoSection.</summary>
        public Entity PatientOf(Entity car) => m_Cars.TryGetValue(car, out var c) ? c.Patient : m_Linked.TryGetValue(car, out var p) ? p : Entity.Null;
        public IEnumerable<Entity> CarsOf(Entity patient)
        {
            foreach (var kv in m_Cars) if (kv.Value.Patient == patient) yield return kv.Key;
            foreach (var kv in m_Linked) if (kv.Value == patient) yield return kv.Key;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;
        // The frame PoliceCarAISystem runs (FireEngineAISystem: one before), right after their arrival check.
        public override int GetUpdateOffset(SystemUpdatePhase phase) => 5;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Parking = World.GetOrCreateSystemManaged<SidewalkParkingSystem>();
            m_Nef = World.GetOrCreateSystemManaged<NefDispatchSystem>();
            m_Reach = World.GetOrCreateSystemManaged<RoadReachSystem>();
            m_AmbulanceSources = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.Hospital>(), ComponentType.ReadOnly<Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_AmbulancePrefabs = GetEntityQuery(ComponentType.ReadOnly<AmbulanceData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_PoliceStations = GetEntityQuery(ComponentType.ReadOnly<PoliceStation>(), ComponentType.ReadOnly<Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_FireStations = GetEntityQuery(ComponentType.ReadOnly<FireStation>(), ComponentType.ReadOnly<Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_PolicePrefabs = GetEntityQuery(ComponentType.ReadOnly<PoliceCarData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
            m_FirePrefabs = GetEntityQuery(ComponentType.ReadOnly<FireEngineData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
            m_MaintDepots = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.MaintenanceDepot>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_MaintPrefabs = GetEntityQuery(ComponentType.ReadOnly<MaintenanceVehicleData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
            // Fire helicopters carry FireEngine too: they may take our request (and just fly home again).
            m_FireEngines = GetEntityQuery(ComponentType.ReadOnly<FireEngine>(), ComponentType.ReadOnly<ServiceDispatch>(),
                ComponentType.Exclude<ParkedCar>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_PoliceCars = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(), ComponentType.ReadOnly<ServiceDispatch>(),
                ComponentType.Exclude<ParkedCar>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_LeftoverRescue = GetEntityQuery(ComponentType.ReadOnly<RescueTarget>(), ComponentType.Exclude<Destroyed>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            // Same archetype as vanilla CollapsedBuildingSystem's rescue requests.
            m_RescueRequestArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<ServiceRequest>(),
                ComponentType.ReadWrite<FireRescueRequest>(), ComponentType.ReadWrite<RequestGroup>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Handled.Clear(); CodeByPatient.Clear(); m_Cars.Clear(); LockedDoor.Clear(); HeldAt.Clear(); ForceLocked.Clear(); Standby.Clear(); FireEngines.Clear();
            m_FireCalls.Clear(); m_Linked.Clear(); m_LeftoversChecked = false; m_Restore = true;
        }

        private bool m_Restore;

        /// <summary>
        /// After a load: calls already running were decided before the save, so they get no new police/fire cars.
        /// Police cars and fire engines still headed to (or standing at) a call are adopted again, so they are held
        /// and released as before. Adopted as Vanilla: they are never deleted by us, since a station car of the same
        /// call cannot be told apart from ours.
        /// </summary>
        private void RestoreCalls(NativeArray<Entity> ambulances)
        {
            var em = EntityManager;
            var byLocation = new Dictionary<Entity, Entity>(); // call location -> patient
            foreach (var v in ambulances)
            {
                var a = em.GetComponentData<Ambulance>(v);
                if (a.m_TargetPatient == Entity.Null || a.m_TargetLocation == Entity.Null) continue;
                m_Handled.Add(a.m_TargetPatient);
                if (m_Units.TryGetCode(v, out var code) && code != null) CodeByPatient[a.m_TargetPatient] = code;
                byLocation[a.m_TargetLocation] = a.m_TargetPatient;
            }
            int adopted = 0;
            foreach (var q in new[] { m_PoliceCars, m_FireEngines })
            {
                using var cars = q.ToEntityArray(Allocator.Temp);
                foreach (var car in cars)
                {
                    if (!em.HasComponent<Target>(car) || em.HasComponent<Helicopter>(car)) continue;
                    var location = em.GetComponentData<Target>(car).m_Target;
                    if (!byLocation.TryGetValue(location, out var patient) || m_Cars.ContainsKey(car)) continue;
                    m_Cars[car] = new Call { Patient = patient, Location = location, Spawned = m_Sim.frameIndex, Vanilla = true };
                    adopted++;
                }
            }
            // Fire units (FireAlarmSystem) are not saved: anything still frozen by our hold that no call adopted would
            // only move again when its long TripSource timer runs out. Let it go now: vanilla sends it home.
            int released = 0;
            foreach (var q in new[] { m_PoliceCars, m_FireEngines, m_Ambulances })
            {
                using var cars = q.ToEntityArray(Allocator.Temp);
                foreach (var car in cars)
                {
                    if (m_Cars.ContainsKey(car) || !em.HasComponent<TripSource>(car) || em.HasComponent<Unspawned>(car) || !em.HasComponent<Stopped>(car)) continue;
                    if (em.HasComponent<Helicopter>(car) || em.HasComponent<ParkedCar>(car)) continue; // HelicopterSystem restores its own
                    bool ours = em.TryGetComponent<Game.Vehicles.PoliceCar>(car, out var p) ? p.m_PurposeMask == 0
                        : em.TryGetComponent<Ambulance>(car, out var a) ? a.m_TargetPatient == Entity.Null
                        : em.HasComponent<FireEngine>(car);
                    if (!ours) continue;
                    ReleaseVehicle(em, car);
                    released++;
                }
            }
            Log.Debug($"restored after load: calls={byLocation.Count} police/fire adopted={adopted}");
            if (released > 0) Log.Info($"restored after load: released held fire units={released}");
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            if (!m_LeftoversChecked) { m_LeftoversChecked = true; RemoveLeftoverRescueTargets(); EnableRescueResponse(); }
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);
            if (m_Restore) { m_Restore = false; RestoreCalls(vehicles); }
            var active = new HashSet<Entity>();
            var awaited = new HashSet<Entity>(); // patients an ambulance is driving to or caring for
            var accidents = new Dictionary<Entity, Entity>(); // vanilla traffic accident event -> patient
            foreach (var v in vehicles)
            {
                if (NefDispatchSystem.IsNef(em, v)) continue;
                var a = em.GetComponentData<Ambulance>(v);
                if (a.m_TargetPatient == Entity.Null || a.m_TargetLocation == Entity.Null) continue;
                active.Add(a.m_TargetPatient);
                if ((a.m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) == 0) awaited.Add(a.m_TargetPatient);
                var accident = AccidentOf(a.m_TargetPatient);
                if (accident != Entity.Null && !accidents.ContainsKey(accident)) accidents[accident] = a.m_TargetPatient;
                if (m_Handled.Contains(a.m_TargetPatient) || !m_Units.TryGetCode(v, out var code) || code == null) continue;
                m_Handled.Add(a.m_TargetPatient);
                CodeByPatient[a.m_TargetPatient] = code;

                bool locked = ForceLocked.Remove(a.m_TargetPatient) || em.HasComponent<ResidentialProperty>(a.m_TargetLocation) && m_Random.NextFloat() < LockedDoorChance;
                if (locked) LockedDoor.Add(a.m_TargetPatient);
                // Vanilla sends its own police to a traffic accident: no extra cars of ours.
                int police = math.max(accident != Entity.Null ? 0 : code.Police, locked ? 1 : 0);
                int fire = math.max(code.Fire, locked ? 1 : 0);
                for (int i = 0; i < police; i++) Spawn(a.m_TargetPatient, a.m_TargetLocation, true);
                if (fire > 0) RequestFire(a.m_TargetPatient, a.m_TargetLocation);
                if (police + fire > 0)
                    Log.Debug($"patient={a.m_TargetPatient.Index} code={code} reason={code.Reason} locked={locked} police={police} fire={math.min(fire, 1)}{(accident != Entity.Null ? " police_via_accident=vanilla" : "")}");
            }
            LockedDoor.IntersectWith(active); // call over
            Standby.RemoveWhere(v => !em.Exists(v) || em.HasComponent<Deleted>(v));
            EngineLights();
            m_Handled.RemoveWhere(p => !em.Exists(p)); // patient gone for good: nothing to remember
            foreach (var p in new List<Entity>(CodeByPatient.Keys)) if (!em.Exists(p)) CodeByPatient.Remove(p);
            UpdateFireCalls(awaited, m_Sim.frameIndex);
            LinkAccidentPolice(accidents);

            uint now = m_Sim.frameIndex;
            uint cap = (uint)((Mod.Setting.NefMaxWaitMinutes + Mod.Setting.OnSceneMinutes + 60) * OnSceneCareSystem.FramesPerGameMinute);
            foreach (var car in new List<Entity>(m_Cars.Keys))
            {
                var call = m_Cars[car];
                try
                {
                    if (!em.Exists(car) || em.HasComponent<Deleted>(car)) { m_Cars.Remove(car); HeldAt.Remove(car); continue; }
                    if (em.HasComponent<ParkedCar>(car)) { if (!call.Vanilla) em.AddComponent<Deleted>(car); m_Cars.Remove(car); continue; }
                    // Safety net: never arrived, or never got home (no route, stuck in traffic). Only on the road, never held.
                    // Sent home the vanilla way and let go; never deleted while on the road (Deleted on a driving car
                    // crashed the game natively, 2026-11-25).
                    if (!call.Held && now - call.Spawned > 2 * (call.Cap != 0 ? call.Cap : cap) && !m_Parking.IsParked(car))
                    {
                        Log.Warn($"{Name(car)}={car.Index} stuck minutes={(now - call.Spawned) / OnSceneCareSystem.FramesPerGameMinute}, sending home");
                        RestorePatrol(car, call);
                        SendHome(car);
                        m_Cars.Remove(car); HeldAt.Remove(car);
                        continue;
                    }
                    uint callCap = call.Cap != 0 ? call.Cap : cap;
                    // Fire scene (no patient): needed while the building still burns.
                    // Fire scene (no patient): standby ambulances/NEFs while it burns, police for the whole scene incl. overhaul.
                    bool wanted = call.Patient != Entity.Null ? awaited.Contains(call.Patient)
                        : Standby.Contains(car) ? em.Exists(call.Location) && em.HasComponent<OnFire>(call.Location)
                        : FireAlarmSystem.Scenes.TryGetValue(call.Location, out var fs) && fs.OutAt == 0 || HighwayCrashSystem.IsCrashLocation(call.Location); // fire out: fire watch is one engine, police go
                    bool needed = wanted && (!call.Held || now - call.Since < callCap);
                    // Fire standby unit that takes a patient: held ones back onto their lane first, ones still in
                    // their station (TripSource) once they are out; on the road at once.
                    if (call.Take != Entity.Null && !call.Done && (call.Held || !em.HasComponent<TripSource>(car)))
                    {
                        if (call.Held) { HeldAt.Remove(car); if (m_Parking.IsParked(car)) continue; }
                        if (HandOver(car, call)) { m_Cars.Remove(car); continue; }
                        call.Take = Entity.Null; // patient gone: stays on standby
                        if (call.Held) HeldAt[car] = call.Stand;
                        continue;
                    }
                    if (call.Held)
                    {
                        if (needed) { if (call.Maint) Board(car, call); else Lights(car); continue; }
                        HeldAt.Remove(car);
                        if (m_Parking.IsParked(car)) continue; // SidewalkParkingSystem puts it back onto its lane first
                        Release(car, call);
                        continue;
                    }
                    if (needed && !call.Done) m_Parking.ArriveIfQueued(car, call.Stand); // stuck behind the call's other vehicles
                    if (needed && !call.Done && Arrived(car, call)) { Hold(car, call, now, callCap); continue; }
                    if (call.Done || Returning(car)) { LightsOff(car); continue; } // vanilla never clears the flag we set
                    Lights(car);
                }
                catch (Exception ex)
                {
                    // One broken vehicle must not stop the others: forget it, vanilla keeps owning it.
                    Log.Warn(ex, $"{Name(car)}={car.Index} no longer tracked");
                    Cordon.Lift(em, car);
                    m_Cars.Remove(car); HeldAt.Remove(car);
                }
            }
        }

        private bool Returning(Entity car)
        {
            var em = EntityManager;
            if (em.HasComponent<Game.Vehicles.PoliceCar>(car)) return (em.GetComponentData<Game.Vehicles.PoliceCar>(car).m_State & PoliceCarFlags.Returning) != 0;
            if (em.HasComponent<Ambulance>(car)) return (em.GetComponentData<Ambulance>(car).m_State & AmbulanceFlags.Returning) != 0;
            if (em.HasComponent<Game.Vehicles.MaintenanceVehicle>(car)) return (em.GetComponentData<Game.Vehicles.MaintenanceVehicle>(car).m_State & MaintenanceVehicleFlags.Returning) != 0;
            return em.HasComponent<FireEngine>(car) && (em.GetComponentData<FireEngine>(car).m_State & FireEngineFlags.Returning) != 0;
        }

        private void Lights(Entity car)
        {
            var em = EntityManager;
            if (!em.HasComponent<Car>(car)) return;
            var c = em.GetComponentData<Car>(car);
            if ((c.m_Flags & CarFlags.Emergency) != 0) return;
            c.m_Flags |= CarFlags.Emergency | CarFlags.StayOnRoad;
            em.SetComponentData(car, c);
            if (!em.HasComponent<EffectsUpdated>(car)) em.AddComponent<EffectsUpdated>(car);
        }

        private void LightsOff(Entity car)
        {
            var em = EntityManager;
            if (!em.HasComponent<Car>(car)) return;
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
            if (em.HasComponent<TripSource>(car) || !em.HasComponent<Transform>(call.Stand) || !em.HasComponent<CarCurrentLane>(car)) return false;
            if (call.Behind > 0f && BehindCrash(car, call)) return true;
            if (!Returning(car) && !VehicleUtils.PathEndReached(em.GetComponentData<CarCurrentLane>(car))) return false;
            if (em.HasComponent<Moving>(car) && math.length(em.GetComponentData<Moving>(car).m_Velocity) > 2f) return false;
            // ponytail: 150 m like OnSceneCareSystem.OnScene; huge lots could be further.
            return math.distance(em.GetComponentData<Transform>(car).m_Position.xz, em.GetComponentData<Transform>(call.Stand).m_Position.xz) < 150f;
        }

        /// <summary>Takes the car out of vanilla's AI and stops it, like vanilla PoliceCarAISystem.StopVehicle.</summary>
        private void Hold(Entity car, Call call, uint now, uint cap)
        {
            HoldVehicle(EntityManager, car, cap);
            call.Held = true; call.Since = now;
            if (call.Behind > 0f) // highway crash: stands where it stopped, on the lane, before the crash
            {
                if (call.Maint) Board(car, call);
                Log.Debug($"{Name(car)}={car.Index} securing {call.Behind:F0} m before the crash");
                return;
            }
            HeldAt[car] = call.Stand; // cordon cars park as their own group at their post
            if (call.Maint) { Board(car, call); return; }
            if (call.CordonNode != Entity.Null && !Cordon.Place(EntityManager, car, call.CordonEdge, call.CordonNode))
                Log.Warn($"Polizei={car.Index} no lane into the street at junction={call.CordonNode.Index}, parking at post instead");
            Log.Debug($"{Name(car)}={car.Index} staying on scene patient={call.Patient.Index}");
        }

        /// <summary>
        /// Back to vanilla, which starts the stopped car (StartVehicle) and sends it home on a new path from where it
        /// stands now. EndOfPath must go: vanilla deletes a returning car that has reached the end of its path.
        /// </summary>
        private void Release(Entity car, Call call)
        {
            call.Held = false; call.Done = true;
            Cordon.Lift(EntityManager, car);
            RestorePatrol(car, call);
            if (call.Maint) BoardOff(car);
            ReleaseVehicle(EntityManager, car);
            Log.Debug($"{Name(car)}={car.Index} returning");
        }

        /// <summary>
        /// Stops a vehicle and keeps vanilla's AI off it: the vanilla vehicle AI systems skip vehicles with TripSource,
        /// and VehicleSpawnSystem only counts its timer down. Also used by HelicopterSystem (cars and helicopters).
        /// </summary>
        public static void HoldVehicle(EntityManager em, Entity v, uint cap)
        {
            if (em.HasComponent<TripSource>(v) || !em.HasComponent<Owner>(v)) return;
            em.AddComponentData(v, new TripSource(em.GetComponentData<Owner>(v).m_Owner, cap));
            // Like vanilla StopVehicle: stop every layout part (trailers too). A trailer left Moving behind a stopped
            // controller makes the Burst trailer-move job read the controller's removed TransformFrame -> native crash.
            if (em.HasBuffer<LayoutElement>(v))
            {
                using var layout = em.GetBuffer<LayoutElement>(v, true).ToNativeArray(Allocator.Temp);
                foreach (var part in layout) StopPart(em, part.m_Vehicle);
            }
            StopPart(em, v); // no-op if the layout already covered it
        }

        private static readonly ComponentTypeSet MovingParts = new ComponentTypeSet(new[]
        {
            ComponentType.ReadWrite<Moving>(), ComponentType.ReadWrite<TransformFrame>(),
            ComponentType.ReadWrite<InterpolatedTransform>(), ComponentType.ReadWrite<Swaying>(),
        });

        private static void StopPart(EntityManager em, Entity p)
        {
            if (p == Entity.Null || !em.Exists(p) || em.HasComponent<Stopped>(p)) return;
            em.RemoveComponent(p, MovingParts);
            em.AddComponent<Stopped>(p);
            if (!em.HasComponent<Updated>(p)) em.AddComponent<Updated>(p);
        }

        /// <summary>
        /// Back to vanilla, which starts the stopped vehicle and paths on from where it stands. EndOfPath must go:
        /// vanilla deletes a returning car that has reached the end of its path.
        /// </summary>
        public static void ReleaseVehicle(EntityManager em, Entity v)
        {
            em.RemoveComponent<TripSource>(v);
            if (em.HasComponent<CarCurrentLane>(v))
            {
                var lane = em.GetComponentData<CarCurrentLane>(v);
                lane.m_LaneFlags &= ~Game.Vehicles.CarLaneFlags.EndOfPath;
                em.SetComponentData(v, lane);
            }
            if (!em.HasComponent<PathOwner>(v)) return;
            var po = em.GetComponentData<PathOwner>(v);
            po.m_State |= PathFlags.Obsolete; // it may have been moved to the ambulance group
            em.SetComponentData(v, po);
        }

        /// <summary>The patient's vanilla traffic accident event (same source as Ampds.Classify), else Entity.Null.</summary>
        private Entity AccidentOf(Entity patient)
        {
            var em = EntityManager;
            if (!em.HasComponent<HealthProblem>(patient)) return Entity.Null;
            var ev = em.GetComponentData<HealthProblem>(patient).m_Event;
            return em.Exists(ev) && em.HasComponent<Game.Events.TrafficAccident>(ev) ? ev : Entity.Null;
        }

        /// <summary>Fire brigade as a vanilla call: RescueTarget + FireRescueRequest on the call's building.</summary>
        private void RequestFire(Entity patient, Entity location)
        {
            var em = EntityManager;
            if (!em.HasComponent<Building>(location)) { Spawn(patient, location, false); return; } // street etc.: no vanilla target possible
            if (m_FireCalls.ContainsKey(location)) return; // same address, engine already called
            if (em.HasComponent<OnFire>(location) || em.HasComponent<RescueTarget>(location))
            {
                Log.Debug($"fire: building={location.Index} already has a vanilla call (fire/collapse)");
                return;
            }
            em.AddComponentData(location, new RescueTarget(Entity.Null));
            m_FireCalls[location] = new FireCall { Patient = patient, Request = CreateRescueRequest(location) };
        }

        /// <summary>
        /// Fire scene (FireAlarmSystem): police held at a burning building until the fire is out or holdFrames run out.
        /// </summary>
        /// Each car drives to its post (a cordon address, Entity.Null = the building itself).
        /// A post with a junction is a cordon car: it stands across the lanes from that junction into the street (Cordon).
        public void DispatchPoliceToFire(Entity building, IList<(Entity post, Entity edge, Entity node)> posts, uint holdFrames)
        {
            foreach (var p in posts)
            {
                var call = Spawn(Entity.Null, building, true, p.post);
                if (call == null) break; // no station / cap reached
                call.Cap = holdFrames; call.CordonEdge = p.edge; call.CordonNode = p.node;
            }
        }

        /// <summary>Police sent to a burning building by FireAlarmSystem (cordon and scene). Read by CallInfoSection.</summary>
        public IEnumerable<Entity> CarsAtFire(Entity building)
        {
            foreach (var kv in m_Cars) if (kv.Value.Patient == Entity.Null && kv.Value.Location == building && !kv.Value.Done) yield return kv.Key;
        }

        /// <summary>Burning building a police car was sent to by FireAlarmSystem, else Entity.Null.</summary>
        public Entity FireOf(Entity car) => m_Cars.TryGetValue(car, out var c) && c.Patient == Entity.Null ? c.Location : Entity.Null;

        public bool IsCordon(Entity car) => Cordon.IsCordon(car) || m_Cars.TryGetValue(car, out var c) && c.CordonNode != Entity.Null;

        /// <summary>Fire engines sent by FireAlarmSystem: lights while driving there (vanilla ResetPath clears them without a request).</summary>
        public static readonly HashSet<Entity> FireEngines = new HashSet<Entity>();

        /// <summary>
        /// Extra engines for a burning building, spawned by us (vanilla accepts only one fire request per burning
        /// building and destroyed the extra ones, so they never came). Taken from the nearest station with free engines,
        /// as many as it has, then the next: the engines of one station leave in the same frame and drive as a column.
        /// Disabled: vanilla's FireEngineAISystem still puts out the fire at its target (BeginExtinguishing only needs
        /// the target to burn), but gives it no other job and sends it home once the fire is out. Returns engines sent.
        /// </summary>
        public int SpawnEngines(Entity building, int count)
        {
            var em = EntityManager;
            if (!em.HasComponent<Transform>(building)) return 0;
            var at = em.GetComponentData<Transform>(building).m_Position;
            var tried = new HashSet<Entity>();
            int sent = 0;
            while (sent < count)
            {
                var station = Nearest(m_FireStations, at, s => !tried.Contains(s) && (em.GetComponentData<FireStation>(s).m_Flags & FireStationFlags.HasAvailableFireEngines) != 0, building);
                if (station == Entity.Null) break;
                tried.Add(station);
                int n = math.min(FreeEngines(station, out var prefab), count - sent);
                if (n <= 0 || prefab == Entity.Null) continue;
                for (int i = 0; i < n; i++)
                {
                    var e = CreateEngine(station, prefab, building);
                    FireEngines.Add(e);
                    sent++;
                }
                Log.Info($"fire_engines building={building.Index} station={station.Index} column={n}");
            }
            return sent;
        }

        /// <summary>Engines the station can still send (prefab capacity minus its engines on the road) and its engine model.</summary>
        private int FreeEngines(Entity station, out Entity prefab)
        {
            var em = EntityManager;
            prefab = Entity.Null;
            var sp = em.GetComponentData<PrefabRef>(station).m_Prefab;
            int capacity = em.HasComponent<FireStationData>(sp) ? em.GetComponentData<FireStationData>(sp).m_FireEngineCapacity : 0;
            int out_ = 0;
            if (em.HasBuffer<OwnedVehicle>(station))
                foreach (var ov in em.GetBuffer<OwnedVehicle>(station, true))
                {
                    if (!em.HasComponent<FireEngine>(ov.m_Vehicle) || !em.HasComponent<Car>(ov.m_Vehicle)) continue;
                    if (prefab == Entity.Null) prefab = em.GetComponentData<PrefabRef>(ov.m_Vehicle).m_Prefab;
                    if (!em.HasComponent<ParkedCar>(ov.m_Vehicle)) out_++;
                }
            if (prefab == Entity.Null) prefab = First(m_FirePrefabs, p => true);
            // ponytail: base prefab capacity, upgrades not counted; HasAvailableFireEngines still gates the station.
            return math.max(1, capacity) - out_;
        }

        private Entity CreateEngine(Entity station, Entity prefab, Entity target)
        {
            var em = EntityManager;
            using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
            em.CreateEntity(em.GetComponentData<ObjectData>(prefab).m_Archetype, created);
            var e = created[0];
            em.SetComponentData(e, em.GetComponentData<Transform>(station));
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new PseudoRandomSeed(ref m_Random));
            em.AddComponentData(e, new TripSource(station));
            em.AddComponentData(e, default(Unspawned));
            em.AddComponentData(e, new Owner(station));
            em.SetComponentData(e, new FireEngine(FireEngineFlags.Disabled, 0, em.GetComponentData<FireEngineData>(prefab).m_ExtinguishingCapacity, 1f));
            em.SetComponentData(e, new Target(target));
            return e;
        }

        private void EngineLights()
        {
            var em = EntityManager;
            foreach (var e in new List<Entity>(FireEngines))
            {
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<ParkedCar>(e)) { FireEngines.Remove(e); continue; }
                if (Returning(e)) { LightsOff(e); FireEngines.Remove(e); continue; }
                Lights(e);
            }
        }

        private Entity CreateRescueRequest(Entity location)
        {
            var em = EntityManager;
            using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
            em.CreateEntity(m_RescueRequestArchetype, created);
            var e = created[0];
            // Disaster type: vanilla FireEngineAISystem drops a "Fire" request whose target is not burning.
            em.SetComponentData(e, new FireRescueRequest(location, 10f, FireRescueRequestType.Disaster));
            em.SetComponentData(e, new RequestGroup(4u));
            return e;
        }

        /// <summary>
        /// Picks up the engine vanilla assigned to each fire call (it then goes through the normal m_Cars flow), asks again
        /// while none came, and takes the RescueTarget off the building once the call is over.
        /// </summary>
        private void UpdateFireCalls(HashSet<Entity> awaited, uint now)
        {
            if (m_FireCalls.Count == 0) return;
            var em = EntityManager;
            NativeArray<Entity> engines = default;
            try
            {
                foreach (var location in new List<Entity>(m_FireCalls.Keys))
                {
                    var fc = m_FireCalls[location];
                    if (!em.Exists(location) || !awaited.Contains(fc.Patient))
                    {
                        ClearRescueTarget(location);
                        m_FireCalls.Remove(location);
                        continue;
                    }
                    // Adopt every engine sent here (vanilla may send several), not just the one answering our request.
                    if (!engines.IsCreated) engines = m_FireEngines.ToEntityArray(Allocator.Temp);
                    foreach (var e in engines)
                    {
                        if (m_Cars.ContainsKey(e)) continue;
                        bool ours = em.HasComponent<Target>(e) && em.GetComponentData<Target>(e).m_Target == location;
                        var dispatches = em.GetBuffer<ServiceDispatch>(e, true);
                        for (int i = 0; i < dispatches.Length && !ours; i++) ours = dispatches[i].m_Request == fc.Request;
                        if (!ours) continue;
                        fc.Engine = e;
                        if (em.HasComponent<Helicopter>(e)) continue; // flies home on arrival; only cars are held on scene
                        m_Cars[e] = new Call { Patient = fc.Patient, Location = location, Spawned = now, Vanilla = true };
                        Log.Debug($"fire_engine={e.Index} taking over call patient={fc.Patient.Index}");
                    }
                    if (fc.Engine != Entity.Null) continue;
                    if (!em.HasComponent<FireRescueRequest>(fc.Request)) fc.Request = CreateRescueRequest(location); // dropped by vanilla: ask again
                }
            }
            finally { if (engines.IsCreated) engines.Dispose(); }
        }

        /// <summary>
        /// Our calls are Disaster-type requests, and vanilla FireStationAISystem.SpawnVehicle silently refuses those at
        /// stations without disaster response capacity (most of them): nothing came. Give every station one per engine.
        /// </summary>
        private void EnableRescueResponse()
        {
            using var q = EntityManager.CreateEntityQuery(ComponentType.ReadWrite<FireStationData>(), ComponentType.ReadOnly<PrefabData>());
            using var prefabs = q.ToEntityArray(Allocator.Temp);
            int n = 0;
            foreach (var p in prefabs)
            {
                var d = EntityManager.GetComponentData<FireStationData>(p);
                if (d.m_DisasterResponseCapacity >= d.m_FireEngineCapacity) continue;
                d.m_DisasterResponseCapacity = d.m_FireEngineCapacity;
                EntityManager.SetComponentData(p, d);
                n++;
            }
            if (n > 0) Log.Info($"fire: {n} fire station prefabs got disaster response capacity for rescue calls");
        }

        private void ClearRescueTarget(Entity building)
        {
            var em = EntityManager;
            if (em.Exists(building) && em.HasComponent<RescueTarget>(building) && !em.HasComponent<Destroyed>(building))
                em.RemoveComponent<RescueTarget>(building);
        }

        /// <summary>After loading: fire calls are not saved, so a RescueTarget on an intact building is one of ours, left over.</summary>
        private void RemoveLeftoverRescueTargets()
        {
            if (m_LeftoverRescue.IsEmptyIgnoreFilter) return;
            using var buildings = m_LeftoverRescue.ToEntityArray(Allocator.Temp);
            EntityManager.RemoveComponent<RescueTarget>(m_LeftoverRescue);
            Log.Info($"removed stale fire call markers from save count={buildings.Length}");
        }

        /// <summary>Vanilla police cars serving a traffic accident of a current call -> that call's patient (display only).</summary>
        private void LinkAccidentPolice(Dictionary<Entity, Entity> accidents)
        {
            m_Linked.Clear();
            if (accidents.Count == 0) return;
            var em = EntityManager;
            using var cars = m_PoliceCars.ToEntityArray(Allocator.Temp);
            foreach (var car in cars)
            {
                var dispatches = em.GetBuffer<ServiceDispatch>(car, true);
                for (int i = 0; i < dispatches.Length; i++)
                {
                    var request = dispatches[i].m_Request;
                    if (!em.HasComponent<PoliceEmergencyRequest>(request)) continue;
                    var site = em.GetComponentData<PoliceEmergencyRequest>(request).m_Site;
                    if (!em.HasComponent<AccidentSite>(site)) continue;
                    if (accidents.TryGetValue(em.GetComponentData<AccidentSite>(site).m_Event, out var patient)) { m_Linked[car] = patient; break; }
                }
            }
        }

        /// <summary>Lights off, Returning, target = own station (like HelicopterSystem.SendPoliceHome). Vanilla drives it from there.</summary>
        private void SendHome(Entity car)
        {
            var em = EntityManager;
            LightsOff(car);
            if (em.HasComponent<Game.Vehicles.PoliceCar>(car))
            {
                var p = em.GetComponentData<Game.Vehicles.PoliceCar>(car);
                p.m_State |= PoliceCarFlags.Returning;
                em.SetComponentData(car, p);
            }
            else if (em.HasComponent<FireEngine>(car))
            {
                var f = em.GetComponentData<FireEngine>(car);
                f.m_State |= FireEngineFlags.Returning;
                em.SetComponentData(car, f);
            }
            else if (em.HasComponent<Ambulance>(car))
            {
                var a = em.GetComponentData<Ambulance>(car);
                a.m_State = (a.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Critical)) | AmbulanceFlags.Returning;
                em.SetComponentData(car, a);
            }
            if (!em.HasComponent<Owner>(car) || !em.HasComponent<PathOwner>(car) || !em.HasComponent<Target>(car)) return;
            var owner = em.GetComponentData<Owner>(car).m_Owner;
            if (!em.Exists(owner)) return;
            var po = em.GetComponentData<PathOwner>(car);
            var t = em.GetComponentData<Target>(car);
            if (t.m_Target == owner) return; // already heading home: a new path would not help
            VehicleUtils.SetTarget(ref po, ref t, owner);
            em.SetComponentData(car, po);
            em.SetComponentData(car, t);
        }

        private string Name(Entity car) => EntityManager.HasComponent<Game.Vehicles.MaintenanceVehicle>(car) ? "Strassenunterhalt"
            : EntityManager.HasComponent<Game.Vehicles.PoliceCar>(car) ? "Polizei"
            : NefDispatchSystem.IsNef(EntityManager, car) ? "NEF" : EntityManager.HasComponent<Ambulance>(car) ? "RTW" : "Feuerwehr";

        /// <summary>
        /// Reserves a free standby unit held at the fire <paramref name="building"/> for <paramref name="patient"/>
        /// (ambulance or NEF). The handover follows once SidewalkParkingSystem has put it back onto its lane.
        /// </summary>
        /// Held units first, then ones still on their way. An ambulance claims the patient's request right away
        /// (<see cref="ClaimRequest"/>), so vanilla sends nobody else meanwhile. <paramref name="at"/>: where the patient is
        /// (the building, or the citizen's creature outside).
        public bool ReserveStandby(Entity building, Entity patient, bool nef, Entity at = default)
        {
            var em = EntityManager;
            Entity pick = Entity.Null;
            foreach (var kv in m_Cars)
            {
                var c = kv.Value;
                if (c.Done || c.Take != Entity.Null || c.Location != building || !Standby.Contains(kv.Key)) continue;
                if (NefDispatchSystem.IsNef(em, kv.Key) != nef) continue;
                if (pick == Entity.Null || c.Held && !m_Cars[pick].Held) pick = kv.Key;
            }
            if (pick == Entity.Null || !nef && !ClaimRequest(pick, patient)) return false;
            m_Cars[pick].Take = patient;
            m_Cars[pick].TakeAt = at != Entity.Null ? at : building;
            Log.Debug($"{Name(pick)}={pick.Index} {(m_Cars[pick].Held ? "on standby" : "en route")} takes patient={patient.Index} at fire={building.Index}");
            return true;
        }

        /// <summary>Standby ambulances/NEFs at the fire not reserved for a patient (on scene or on their way).</summary>
        public int StandbyCount(Entity building, bool nef)
        {
            int n = 0;
            foreach (var kv in m_Cars)
                if (!kv.Value.Done && kv.Value.Take == Entity.Null && kv.Value.Location == building && Standby.Contains(kv.Key)
                    && NefDispatchSystem.IsNef(EntityManager, kv.Key) == nef) n++;
            return n;
        }

        /// <summary>
        /// The patient's request goes to <paramref name="car"/> the way vanilla hands it out (Dispatched + the vehicle's
        /// ServiceDispatch); a station that held it loses it, a search under way is dropped.
        /// </summary>
        private bool ClaimRequest(Entity car, Entity patient)
        {
            var em = EntityManager;
            if (!CanTakeRequest(patient)) return false;
            var req = em.GetComponentData<HealthProblem>(patient).m_HealthcareRequest;
            if (em.TryGetComponent<Dispatched>(req, out var d) && em.HasBuffer<ServiceDispatch>(d.m_Handler))
            {
                var held = em.GetBuffer<ServiceDispatch>(d.m_Handler);
                for (int i = held.Length - 1; i >= 0; i--) if (held[i].m_Request == req) held.RemoveAt(i);
            }
            if (em.HasComponent<Dispatched>(req)) em.SetComponentData(req, new Dispatched(car)); else em.AddComponentData(req, new Dispatched(car));
            if (em.HasComponent<PathInformation>(req)) em.RemoveComponent<PathInformation>(req);
            if (em.HasBuffer<PathElement>(req)) em.RemoveComponent<PathElement>(req);
            var dispatches = em.GetBuffer<ServiceDispatch>(car);
            dispatches.Clear();
            dispatches.Add(new ServiceDispatch(req));
            return true;
        }

        /// <summary>True while the patient's vanilla request has not reached a vehicle yet (a standby ambulance may take it).</summary>
        public bool CanTakeRequest(Entity patient)
        {
            var em = EntityManager;
            if (!em.TryGetComponent<HealthProblem>(patient, out var hp)) return false;
            if ((hp.m_Flags & HealthProblemFlags.RequireTransport) == 0 || (hp.m_Flags & HealthProblemFlags.Dead) != 0) return false;
            var req = hp.m_HealthcareRequest;
            if (req == Entity.Null || !em.Exists(req)) return false;
            return !em.TryGetComponent<Dispatched>(req, out var d) || em.HasComponent<Building>(d.m_Handler); // a station has it: not spawned yet
        }

        /// <summary>
        /// Standby unit becomes a normal call unit. Ambulance: takes the patient's own request the way vanilla
        /// FireRescue/HealthcareDispatch hands it out (Dispatched + the vehicle's ServiceDispatch); a station that held
        /// it loses it. NEF: NefDispatchSystem adopts it as a normal mission. Then it drives up to the building.
        /// </summary>
        private bool HandOver(Entity car, Call call)
        {
            var em = EntityManager;
            var patient = call.Take;
            Standby.Remove(car);
            if (NefDispatchSystem.IsNef(em, car))
            {
                if (!em.Exists(patient) || !em.HasComponent<HealthProblem>(patient)) { Standby.Add(car); return false; }
                m_Nef.AdoptStandby(car, patient, call.TakeAt, call.Held);
                HeldAt.Remove(car);
                return true;
            }
            // Claimed at reservation; still ours if the patient still needs us and nobody took the request since.
            bool ours = em.TryGetComponent<HealthProblem>(patient, out var hp) && (hp.m_Flags & HealthProblemFlags.RequireTransport) != 0
                && (hp.m_Flags & HealthProblemFlags.Dead) == 0;
            if (ours)
            {
                ours = false;
                var dispatches = em.GetBuffer<ServiceDispatch>(car, true);
                for (int i = 0; i < dispatches.Length; i++) ours |= dispatches[i].m_Request == hp.m_HealthcareRequest;
            }
            if (!ours) { Standby.Add(car); em.GetBuffer<ServiceDispatch>(car).Clear(); return false; }
            var a = em.GetComponentData<Ambulance>(car);
            a.m_TargetPatient = patient; a.m_TargetLocation = call.TakeAt;
            a.m_State = (a.m_State & ~(AmbulanceFlags.Returning | AmbulanceFlags.AtTarget | AmbulanceFlags.Disabled | AmbulanceFlags.Transporting)) | AmbulanceFlags.Dispatched | AmbulanceFlags.Critical;
            em.SetComponentData(car, a);
            var po = em.GetComponentData<PathOwner>(car);
            var t = em.GetComponentData<Target>(car);
            VehicleUtils.SetTarget(ref po, ref t, call.TakeAt);
            em.SetComponentData(car, po);
            em.SetComponentData(car, t);
            if (call.Held) ReleaseVehicle(em, car);
            HeldAt.Remove(car);
            Log.Info($"standby ambulance={car.Index} takes patient={patient.Index} at fire={call.Location.Index} {(call.Held ? "from standby" : "on the way")}");
            return true;
        }

        /// <summary>
        /// Fire standby (FireAlarmSystem): ambulances from the nearest depot/hospital with a free one and NEFs, all to the
        /// staging address, held there while the building burns. Returns how many of each actually left.
        /// </summary>
        public (int rtw, int nef) DispatchStandby(Entity building, Entity staging, int rtw, int nef, uint holdFrames)
        {
            var em = EntityManager;
            if (!em.HasComponent<Transform>(staging)) return (0, 0);
            var at = em.GetComponentData<Transform>(staging).m_Position;
            int sentRtw = 0, sentNef = 0;
            for (int i = 0; i < rtw + nef; i++)
            {
                if (m_Cars.Count >= MaxCars) { Log.Warn($"standby: vehicle cap reached max={MaxCars}"); break; }
                bool isNef = i >= rtw;
                var e = isNef ? m_Nef.SpawnStandby(staging) : CreateAmbulance(at, staging);
                if (e == Entity.Null) continue;
                m_Cars[e] = new Call { Patient = Entity.Null, Location = building, Post = staging, Spawned = m_Sim.frameIndex, Cap = holdFrames };
                Standby.Add(e);
                if (isNef) sentNef++; else sentRtw++;
            }
            return (sentRtw, sentNef);
        }

        /// <summary>Ambulance without a patient from the nearest building with a free one (vanilla HasAvailableAmbulances:
        /// depots only while DepotOnlyDispatchSystem is on). Its own fleet's model, else the first ambulance prefab.</summary>
        private Entity CreateAmbulance(float3 at, Entity target)
        {
            var em = EntityManager;
            var station = Nearest(m_AmbulanceSources, at, s => (em.GetComponentData<Game.Buildings.Hospital>(s).m_Flags & HospitalFlags.HasAvailableAmbulances) != 0, target);
            if (station == Entity.Null) { Log.Debug("standby: no depot/hospital with a free ambulance"); return Entity.Null; } // Debug: retried every RescueRetry
            Entity prefab = Entity.Null;
            if (em.HasBuffer<OwnedVehicle>(station))
                foreach (var ov in em.GetBuffer<OwnedVehicle>(station, true))
                    if (em.HasComponent<Ambulance>(ov.m_Vehicle) && em.HasComponent<Car>(ov.m_Vehicle) && !NefDispatchSystem.IsNef(em, ov.m_Vehicle)) { prefab = em.GetComponentData<PrefabRef>(ov.m_Vehicle).m_Prefab; break; }
            if (prefab == Entity.Null) prefab = First(m_AmbulancePrefabs, p => p != NefPrefabSystem.NefPrefab && em.GetComponentData<AmbulanceData>(p).m_PatientCapacity > 0);
            if (prefab == Entity.Null) { Log.Warn("standby: no ambulance prefab"); return Entity.Null; }

            using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
            em.CreateEntity(em.GetComponentData<ObjectData>(prefab).m_Archetype, created);
            var e = created[0];
            em.SetComponentData(e, em.GetComponentData<Transform>(station));
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new PseudoRandomSeed(ref m_Random));
            em.AddComponentData(e, new TripSource(station));
            em.AddComponentData(e, default(Unspawned));
            em.AddComponentData(e, new Owner(station));
            // Dispatched without a request: vanilla drives it to the target and turns it home on arrival (Hold catches it there).
            em.SetComponentData(e, new Ambulance(Entity.Null, target, AmbulanceFlags.Dispatched | AmbulanceFlags.Critical));
            em.SetComponentData(e, new Target(target));
            Log.Debug($"standby ambulance={e.Index} station={station.Index} to={target.Index}");
            return e;
        }

        private Call Spawn(Entity patient, Entity location, bool police, Entity post = default, bool maint = false)
        {
            var em = EntityManager;
            if (!em.HasComponent<Transform>(location)) return null;
            if (m_Cars.Count >= MaxCars) { Log.Warn($"{(police ? "police" : "fire")}: vehicle cap reached max={MaxCars}, not dispatching"); return null; }
            var call = new Call { Patient = patient, Location = location, Post = post, Spawned = m_Sim.frameIndex };
            var at = em.GetComponentData<Transform>(location).m_Position;
            var e = maint ? CreateMaint(at, call.Stand) : police ? PickPolice(at, call) : CreateCar(at, call.Stand, police);
            if (e == Entity.Null) return null;
            call.Maint = maint;
            m_Cars[e] = call;
            return call;
        }

        /// <summary>Police car from the nearest station with free patrol cars to target, not tracked here. Used by HelicopterSystem (doctor shuttle).</summary>
        public Entity CreatePoliceCar(float3 near, Entity target) => CreateCar(near, target, true);

        /// <summary>
        /// Police for a call: the nearer of a station with a free car (a new one leaves there) and a police car already
        /// on the road that is free (patrolling or heading home, not on a call, no prisoners). Without the second, a call
        /// got no police whenever all station cars were out on patrol. A car taken over keeps its patrol duty for later
        /// (RestorePatrol) and, being a station car, is never deleted by us (Vanilla).
        /// </summary>
        private Entity PickPolice(float3 at, Call call)
        {
            var em = EntityManager;
            var target = call.Stand;
            var station = Nearest(m_PoliceStations, at, s => (em.GetComponentData<PoliceStation>(s).m_Flags & PoliceStationFlags.HasAvailablePatrolCars) != 0, target);
            float stationDist = station != Entity.Null ? math.distance(at, em.GetComponentData<Transform>(station).m_Position) : float.MaxValue;
            var reach = World.GetOrCreateSystemManaged<RoadReachSystem>();
            Entity best = Entity.Null; float bestDist = stationDist;
            using (var cars = m_PoliceCars.ToEntityArray(Allocator.Temp))
                foreach (var c in cars)
                {
                    if (m_Cars.ContainsKey(c) || HeldAt.ContainsKey(c) || m_Linked.ContainsKey(c) || HelicopterSystem.ShuttleByPatient.ContainsValue(c)) continue;
                    if (em.HasComponent<TripSource>(c) || em.HasComponent<Helicopter>(c) || !em.HasComponent<Car>(c) || !em.HasComponent<PathOwner>(c) || !em.HasComponent<Owner>(c)) continue;
                    var p = em.GetComponentData<Game.Vehicles.PoliceCar>(c);
                    const PoliceCarFlags busy = PoliceCarFlags.AccidentTarget | PoliceCarFlags.AtTarget | PoliceCarFlags.Disembarking | PoliceCarFlags.Full | PoliceCarFlags.Disabled | PoliceCarFlags.ShiftEnded | PoliceCarFlags.Empty;
                    if ((p.m_State & busy) != 0 || (p.m_PurposeMask & PolicePurpose.Patrol) == 0) continue;
                    if ((em.GetComponentData<Car>(c).m_Flags & CarFlags.Emergency) != 0) continue; // on its way to a vanilla call
                    if (em.TryGetBuffer<Passenger>(c, true, out var pass) && pass.Length > 0) continue;
                    if (!reach.Connected(em.GetComponentData<Owner>(c).m_Owner, target)) continue;
                    float d = math.distance(at, em.GetComponentData<Transform>(c).m_Position);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
            if (best == Entity.Null) return CreateCar(at, target, true);

            var pc = em.GetComponentData<Game.Vehicles.PoliceCar>(best);
            call.Retasked = true; call.Vanilla = true; call.OrigPurpose = pc.m_PurposeMask;
            pc.m_State = PoliceCarFlags.Empty; pc.m_PurposeMask = 0; pc.m_RequestCount = 0; pc.m_TargetRequest = Entity.Null; // like our own cars: no vanilla jobs
            em.SetComponentData(best, pc);
            em.GetBuffer<ServiceDispatch>(best).Clear();
            var po = em.GetComponentData<PathOwner>(best);
            var t = em.GetComponentData<Target>(best);
            VehicleUtils.SetTarget(ref po, ref t, target);
            em.SetComponentData(best, po);
            em.SetComponentData(best, t);
            Log.Info($"police: free car={best.Index} on the road takes the call ({bestDist:0} m, station {(station == Entity.Null ? "none free" : $"{stationDist:0} m")})");
            return best;
        }

        /// <summary>A police car taken over from patrol gets its duty back when we let it go.</summary>
        private void RestorePatrol(Entity car, Call call)
        {
            var em = EntityManager;
            if (!call.Retasked || !em.Exists(car) || !em.HasComponent<Game.Vehicles.PoliceCar>(car)) return;
            call.Retasked = false;
            var pc = em.GetComponentData<Game.Vehicles.PoliceCar>(car);
            pc.m_PurposeMask = call.OrigPurpose;
            pc.m_State &= ~PoliceCarFlags.Empty;
            em.SetComponentData(car, pc);
        }

        private Entity CreateCar(float3 at, Entity target, bool police)
        {
            var em = EntityManager;
            var station = police
                ? Nearest(m_PoliceStations, at, s => (em.GetComponentData<PoliceStation>(s).m_Flags & PoliceStationFlags.HasAvailablePatrolCars) != 0, target)
                : Nearest(m_FireStations, at, s => (em.GetComponentData<FireStation>(s).m_Flags & FireStationFlags.HasAvailableFireEngines) != 0, target);
            // ponytail: first unlocked prefab, ignoring city theme; use Police/FireEngineSelectData if the look matters.
            var prefab = police
                ? First(m_PolicePrefabs, p => p != NefPrefabSystem.NefPrefab && (em.GetComponentData<PoliceCarData>(p).m_PurposeMask & PolicePurpose.Patrol) != 0)
                : First(m_FirePrefabs, p => true);
            if (station == Entity.Null || prefab == Entity.Null) { Log.Warn(police ? "police: no station with a free car" : "fire: no station with a free engine"); return Entity.Null; }

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
            em.SetComponentData(e, new Target(target));
            return e;
        }

        /// <summary>Road maintenance truck from the nearest road depot with free vehicles. RequestCount 2 keeps vanilla's
        /// AI from asking for road work of its own on the way (RequestTargetIfNeeded only runs at 1 or less).</summary>
        private Entity CreateMaint(float3 at, Entity target)
        {
            var em = EntityManager;
            var depot = Nearest(m_MaintDepots, at, d => (em.GetComponentData<Game.Buildings.MaintenanceDepot>(d).m_Flags & MaintenanceDepotFlags.HasAvailableVehicles) != 0
                && em.TryGetComponent<MaintenanceDepotData>(em.GetComponentData<PrefabRef>(d).m_Prefab, out var dd) && (dd.m_MaintenanceType & MaintenanceType.Road) != 0, target);
            var prefab = First(m_MaintPrefabs, p => (em.GetComponentData<MaintenanceVehicleData>(p).m_MaintenanceType & MaintenanceType.Road) != 0);
            if (depot == Entity.Null || prefab == Entity.Null) { Log.Warn("maint: no road depot with a free truck"); return Entity.Null; }
            using var created = new NativeArray<Entity>(1, Allocator.Temp);
            em.CreateEntity(em.GetComponentData<ObjectData>(prefab).m_Archetype, created);
            var e = created[0];
            em.SetComponentData(e, em.GetComponentData<Transform>(depot));
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new PseudoRandomSeed(ref m_Random));
            em.AddComponentData(e, new TripSource(depot));
            em.AddComponentData(e, default(Unspawned));
            em.AddComponentData(e, new Owner(depot));
            em.SetComponentData(e, new Game.Vehicles.MaintenanceVehicle(MaintenanceVehicleFlags.TransformTarget, 2, 1f));
            em.SetComponentData(e, new Target(target));
            return e;
        }

        /// <summary>Road maintenance trucks for fire road blocks, one per post, arrow towards the detour road.</summary>
        public void DispatchMaintToFire(Entity building, IList<(Entity post, float3 detour)> posts, uint holdFrames)
        {
            foreach (var p in posts)
            {
                var call = Spawn(Entity.Null, building, false, p.post, maint: true);
                if (call == null) break;
                call.Cap = holdFrames; call.Detour = p.detour;
            }
        }

        /// <summary>
        /// Highway crash (HighwayCrashSystem): units to the first crashed car, held while the crash is on. Securing
        /// like on a real highway: the first police car ~150 m before the crash, arrow board trucks ~300 m and
        /// ~450 m before it (arrow towards detour), engines ~40 m before it as a shield; other police at the crash.
        /// A unit that never comes along the closed carriageway (e.g. from a ramp ahead) stops at the crash instead.
        /// </summary>
        public void DispatchToCrash(Entity location, float3 at, float3 dir, int police, int engines, IList<float3> boards, uint holdFrames)
        {
            Call Send(bool isPolice, bool maint, float behind)
            {
                var call = Spawn(Entity.Null, location, isPolice, default, maint);
                if (call == null) return null;
                call.Cap = holdFrames; call.CrashAt = at; call.CrashDir = dir; call.Behind = behind;
                return call;
            }
            for (int i = 0; i < police; i++) if (Send(true, false, i == 0 ? 150f : 0f) == null) break;
            for (int i = 0; i < engines; i++) if (Send(false, false, 40f + 15f * i) == null) break;
            for (int i = 0; i < boards.Count; i++) { var c = Send(false, true, 300f + 150f * i); if (c == null) break; c.Detour = boards[i]; }
        }

        /// <summary>On the crash's carriageway, driving its way, about Behind metres before it.</summary>
        private bool BehindCrash(Entity car, Call call)
        {
            var em = EntityManager;
            var p = em.GetComponentData<Transform>(car).m_Position - call.CrashAt; p.y = 0;
            float along = -math.dot(p, call.CrashDir);
            float side = math.abs(math.dot(p, new float3(call.CrashDir.z, 0, -call.CrashDir.x)));
            if (math.abs(along - call.Behind) > 25f || side > 20f) return false;
            var f = math.mul(em.GetComponentData<Transform>(car).m_Rotation, new float3(0, 0, 1));
            return math.dot(f, call.CrashDir) > 0.7f;
        }

        /// <summary>Arrow board on. Tested in game: SignalAnimation1 = arrow left, 2 = arrow right, both = dark.
        /// Left/right as seen from behind the truck: the side the detour road lies on from where it stands.</summary>
        private void Board(Entity car, Call call)
        {
            var em = EntityManager;
            if (!em.TryGetComponent<Car>(car, out var c) || !em.TryGetComponent<Transform>(car, out var t)) return;
            var f = math.mul(t.m_Rotation, new float3(0, 0, 1));
            var v = call.Detour - t.m_Position;
            bool left = f.x * v.z - f.z * v.x > 0f;
            var want = (c.m_Flags & ~(CarFlags.SignalAnimation1 | CarFlags.SignalAnimation2)) | CarFlags.Working | CarFlags.Warning
                | (left ? CarFlags.SignalAnimation1 : CarFlags.SignalAnimation2);
            if (want == c.m_Flags) return;
            if ((c.m_Flags & CarFlags.Working) == 0) Log.Debug($"Strassenunterhalt={car.Index} arrow={(left ? "left" : "right")}");
            c.m_Flags = want;
            em.SetComponentData(car, c);
            if (!em.HasComponent<EffectsUpdated>(car)) em.AddComponent<EffectsUpdated>(car);
        }

        private void BoardOff(Entity car)
        {
            var em = EntityManager;
            if (!em.TryGetComponent<Car>(car, out var c)) return;
            c.m_Flags &= ~(CarFlags.SignalAnimation1 | CarFlags.SignalAnimation2 | CarFlags.Working | CarFlags.Warning);
            em.SetComponentData(car, c);
            if (!em.HasComponent<EffectsUpdated>(car)) em.AddComponent<EffectsUpdated>(car);
        }

        // ponytail: straight-line distance and no per-station vehicle count; all cars may come from the same station.
        /// <param name="to">Where the vehicle goes: stations it cannot reach by road (island, RoadReachSystem) are skipped.</param>
        private Entity Nearest(EntityQuery stations, float3 at, Func<Entity, bool> ok, Entity to = default)
        {
            var em = EntityManager;
            var reach = m_Reach;
            using var all = stations.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null; float bestDist = float.MaxValue;
            foreach (var s in all)
            {
                if (!ok(s) || to != Entity.Null && !reach.Connected(s, to)) continue;
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
