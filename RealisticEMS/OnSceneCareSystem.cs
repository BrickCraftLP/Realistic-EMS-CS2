using Game;
using Game.Common;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;

namespace RealisticEMS
{
    /// <summary>
    /// On-scene care: an ambulance stays with the patient for OnSceneMinutes (game time) after it arrives, before the
    /// patient may board (CrewHoldSystem keeps AtTarget cleared). With an NEF involved, care starts once both are on
    /// scene; if the NEF is not there within NefMaxWaitMinutes, the crew starts alone. The drive never counts.
    /// </summary>
    public partial class OnSceneCareSystem : GameSystemBase
    {
        public const float FramesPerGameMinute = TimeSystem.kTicksPerDay / 1440f;

        private class Care { public uint Dispatch, Arrive, NefArrive, Start, Transport; }
        private readonly Dictionary<Entity, Care> m_Care = new Dictionary<Entity, Care>(); // patient -> on-scene times
        private EntityQuery m_Ambulances;
        private SimulationSystem m_Sim;
        private UnitNamingSystem m_Units;

        /// <summary>Game frames of a call's milestones (0 = not yet). Read by CallInfoSection.</summary>
        public bool TryGetTimes(Entity patient, out uint dispatch, out uint arrive, out uint nefArrive, out uint start, out uint transport)
        {
            bool ok = m_Care.TryGetValue(patient, out var c);
            dispatch = c?.Dispatch ?? 0; arrive = c?.Arrive ?? 0; nefArrive = c?.NefArrive ?? 0; start = c?.Start ?? 0; transport = c?.Transport ?? 0;
            return ok;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        /// <summary>An ambulance is on its way to or caring for this patient (not yet transporting).</summary>
        public bool Awaited(Entity patient) => m_Care.TryGetValue(patient, out var c) && c.Transport == 0;

        /// <summary>Arrived at the call location, the same test AmbulanceAISystem uses (end of path OR curb parking spot).</summary>
        public static bool OnScene(EntityManager em, Entity vehicle)
        {
            if (!em.HasComponent<CarCurrentLane>(vehicle) || !em.HasComponent<PathOwner>(vehicle)) return false;
            var lane = em.GetComponentData<CarCurrentLane>(vehicle);
            var pathOwner = em.GetComponentData<PathOwner>(vehicle);
            // A freshly dispatched vehicle still shows "end reached" from its parking spot at the station until the
            // new path arrives: require a settled path and being near the call location.
            if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated)) != 0) return false;
            if (!VehicleUtils.PathEndReached(lane) && !VehicleUtils.ParkingSpaceReached(lane, pathOwner)) return false;
            var target = em.HasComponent<Game.Common.Target>(vehicle) ? em.GetComponentData<Game.Common.Target>(vehicle).m_Target : Entity.Null;
            if (!em.HasComponent<Game.Objects.Transform>(target)) return false;
            // ponytail: 150 m covers large lots; huge industrial lots could be further.
            return math.distance(em.GetComponentData<Game.Objects.Transform>(target).m_Position.xz, em.GetComponentData<Game.Objects.Transform>(vehicle).m_Position.xz) < 150f;
        }

        private readonly HashSet<Entity> m_Silenced = new HashSet<Entity>();

        /// <summary>
        /// The siren effect needs CarFlags.Emergency, the flashing lights Emergency or Warning (EffectControlData).
        /// Vanilla only sets Emergency again when a new path starts, i.e. on departure.
        /// </summary>
        private void SilenceSirens(HashSet<Entity> quiet)
        {
            var em = EntityManager;
            foreach (var v in quiet)
            {
                if (!em.HasComponent<Car>(v)) continue;
                var car = em.GetComponentData<Car>(v);
                var flags = (car.m_Flags & ~CarFlags.Emergency) | CarFlags.Warning;
                if (flags != car.m_Flags) { car.m_Flags = flags; em.SetComponentData(v, car); }
                if (m_Silenced.Add(v)) Log.Debug($"unit={m_Units.CallsignOf(v)} siren off, lights on");
            }
            foreach (var v in new List<Entity>(m_Silenced))
            {
                if (quiet.Contains(v)) continue;
                m_Silenced.Remove(v);
                if (!em.Exists(v) || !em.HasComponent<Car>(v)) continue;
                var car = em.GetComponentData<Car>(v);
                car.m_Flags &= ~CarFlags.Warning; // departure path sets Emergency again if needed
                em.SetComponentData(v, car);
            }
        }

        private Game.Notifications.IconCommandSystem m_Icons;
        private EntityQuery m_HealthcareParams;

        /// <summary>
        /// Vanilla HealthProblemSystem counts HealthProblem.m_Timer up while the patient still needs a vehicle and shows
        /// "ambulance has not arrived" when it runs out; it only clears that once the patient boards. With the on-scene
        /// care hold the patient boards minutes after arrival, so the icon appeared with the crew already there.
        /// The ambulance is on scene: reset the timer and remove the icon.
        /// </summary>
        private void ClearWaitIcon(Entity patient)
        {
            var em = EntityManager;
            if (!em.HasComponent<Game.Citizens.HealthProblem>(patient) || m_HealthcareParams.IsEmptyIgnoreFilter) return;
            var hp = em.GetComponentData<Game.Citizens.HealthProblem>(patient);
            if (hp.m_Timer == 0) return;
            hp.m_Timer = 0;
            em.SetComponentData(patient, hp);
            var prefab = m_HealthcareParams.GetSingleton<Game.Prefabs.HealthcareParameterData>().m_AmbulanceNotificationPrefab;
            m_Icons.CreateCommandBuffer().Remove(patient, prefab);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Icons = World.GetOrCreateSystemManaged<Game.Notifications.IconCommandSystem>();
            m_HealthcareParams = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.HealthcareParameterData>());
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<PathOwner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Care.Clear();
            m_Silenced.Clear();
            CrewHoldSystem.Held.Clear();
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            uint now = m_Sim.frameIndex;
            uint care = (uint)(Mod.Setting.OnSceneMinutes * FramesPerGameMinute);
            uint maxWait = Mod.Setting.EnableNef || Mod.Setting.EnableHelicopter ? (uint)(Mod.Setting.NefMaxWaitMinutes * FramesPerGameMinute) : 0;
            uint cap = care + maxWait + (uint)(10 * FramesPerGameMinute);
            var held = new HashSet<Entity>();
            var quiet = new HashSet<Entity>(); // on scene during care: lights on, siren off
            var seen = new HashSet<Entity>();
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);

            foreach (var rtw in vehicles)
            {
                if (NefDispatchSystem.IsNef(em, rtw)) continue;
                var a = em.GetComponentData<Ambulance>(rtw);
                var p = a.m_TargetPatient;
                if (p != Entity.Null && (a.m_State & AmbulanceFlags.Transporting) != 0 && m_Care.TryGetValue(p, out var tc))
                {
                    seen.Add(p); // keep the times for the info panel until the patient is delivered
                    if (tc.Transport == 0) tc.Transport = now;
                    continue;
                }
                if (p == Entity.Null || (a.m_State & AmbulanceFlags.Dispatched) == 0
                    || (a.m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) != 0) continue;
                // Doctor by road (NEF) or by helicopter (police car shuttling the doctor from the landing site).
                bool nefInvolved = NefDispatchSystem.NefByPatient.TryGetValue(p, out var nef) || HelicopterSystem.DoctorByPatient.TryGetValue(p, out nef);
                seen.Add(p);
                if (!m_Care.TryGetValue(p, out var c)) m_Care[p] = c = new Care { Dispatch = now };
                if (nefInvolved && c.NefArrive == 0 && nef != Entity.Null && em.Exists(nef) && OnScene(em, nef)) c.NefArrive = now;
                if (c.Arrive == 0 && now - c.Dispatch > EnRouteCap && em.HasComponent<Car>(rtw) && !OnScene(em, rtw)) { Recall(rtw, a); continue; }
                if (care == 0 && !nefInvolved) continue;
                // Held already on the way: the patient boards in the first frame AtTarget is visible.
                if (!OnScene(em, rtw)) { held.Add(rtw); continue; }

                string unit = m_Units.CallsignOf(rtw);
                if (c.Arrive == 0)
                {
                    c.Arrive = now;
                    if (!nefInvolved) c.Start = now;
                    Log.Debug($"unit={unit} on scene, {(nefInvolved ? "waiting for doctor car" : $"treating patient minutes={Mod.Setting.OnSceneMinutes}")}");
                }
                else if (c.Start == 0 && !nefInvolved) c.Start = now; // NEF mission ended: start alone
                ClearWaitIcon(p);
                if (c.Start == 0 && nefInvolved)
                {
                    bool nefOnScene = nef != Entity.Null && em.Exists(nef) && OnScene(em, nef)
                        && (!em.HasComponent<Ambulance>(nef) || (em.GetComponentData<Ambulance>(nef).m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) == 0);
                    if (nefOnScene) { c.Start = now; Log.Debug($"unit={unit} treating with doctor car minutes={Mod.Setting.OnSceneMinutes}"); }
                    else if (now - c.Arrive >= maxWait) { c.Start = now; Log.Debug($"unit={unit} doctor car late, treating alone"); }
                }

                bool hold = c.Start == 0 || now - c.Start < care;
                if (hold && now - c.Arrive > cap) { hold = false; Log.Warn($"unit={unit} exceeded max on-scene time, loading patient"); }
                if (hold)
                {
                    held.Add(rtw);
                    quiet.Add(rtw);
                    if (nefInvolved && nef != Entity.Null && em.Exists(nef) && OnScene(em, nef)) quiet.Add(nef);
                }
                else if (CrewHoldSystem.Held.Contains(rtw))
                    Log.Debug($"unit={unit} loading patient on_scene_minutes={(now - c.Arrive) / FramesPerGameMinute:0}");
            }

            CrewHoldSystem.Held.Clear();
            CrewHoldSystem.Held.UnionWith(held);
            SilenceSirens(quiet);
            foreach (var p in new List<Entity>(m_Care.Keys)) if (!seen.Contains(p)) m_Care.Remove(p);
        }

        // ponytail: fixed 60 game min en route (4x the 15 min Hilfsfrist); make it a setting if players want it tunable.
        private const uint EnRouteCap = (uint)(60 * FramesPerGameMinute);

        /// <summary>
        /// Stuck in traffic and never arrived: send it home (never delete a driving car) and drop its dispatch so vanilla
        /// hands the patient's request to another unit. Disabled keeps vanilla from giving this one the same call again.
        /// Same recall as HelicopterSystem's ordinary-call recall.
        /// </summary>
        private void Recall(Entity rtw, Ambulance a)
        {
            var em = EntityManager;
            var owner = em.GetComponentData<Owner>(rtw).m_Owner;
            if (!em.Exists(owner)) return;
            Log.Warn($"unit={m_Units.CallsignOf(rtw)} en route minutes={(m_Sim.frameIndex - m_Care[a.m_TargetPatient].Dispatch) / FramesPerGameMinute:0} without arriving, sending home");
            a.m_State = (a.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Critical)) | AmbulanceFlags.Returning | AmbulanceFlags.Disabled;
            a.m_TargetPatient = Entity.Null;
            a.m_TargetLocation = Entity.Null;
            em.SetComponentData(rtw, a);
            if (em.HasBuffer<ServiceDispatch>(rtw)) em.GetBuffer<ServiceDispatch>(rtw).Clear();
            if (em.HasComponent<Game.Objects.TripSource>(rtw)) ResponderDispatchSystem.ReleaseVehicle(em, rtw);
            var po = em.GetComponentData<PathOwner>(rtw);
            var t = em.GetComponentData<Game.Common.Target>(rtw);
            VehicleUtils.SetTarget(ref po, ref t, owner);
            em.SetComponentData(rtw, po);
            em.SetComponentData(rtw, t);
        }

        public IEnumerable<Entity> Patients => m_Care.Keys;
    }

    /// <summary>
    /// Vanilla DeathCheckSystem rolls a recovery for every sick/injured citizen several times a game day and clears
    /// Sick/Injured/RequireTransport on success; HealthProblemSystem then drops the HealthProblem and the ambulance
    /// (LoadPatients) drives home empty. Vanilla patients board within seconds, so it rarely shows there; with the
    /// drive and the on-scene care hold most patients "recovered" before boarding (seen in logs: 51 of 54 lost calls).
    /// Runs right after DeathCheckSystem and gives a patient with an ambulance assigned the flags back. Death still happens.
    /// </summary>
    public partial class PatientKeepSystem : GameSystemBase
    {
        private const Game.Citizens.HealthProblemFlags Need = Game.Citizens.HealthProblemFlags.Sick | Game.Citizens.HealthProblemFlags.Injured | Game.Citizens.HealthProblemFlags.RequireTransport;
        private Dictionary<Entity, Game.Citizens.HealthProblemFlags> m_Flags = new Dictionary<Entity, Game.Citizens.HealthProblemFlags>();
        private OnSceneCareSystem m_Care;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Care = World.GetOrCreateSystemManaged<OnSceneCareSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Flags.Clear();
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            var now = m_Next;
            now.Clear();
            foreach (var p in m_Care.Patients)
            {
                if (!m_Care.Awaited(p) || !em.HasComponent<Game.Citizens.HealthProblem>(p)) continue;
                var hp = em.GetComponentData<Game.Citizens.HealthProblem>(p);
                if ((hp.m_Flags & Game.Citizens.HealthProblemFlags.Dead) != 0) continue;
                if ((hp.m_Flags & Need) == 0 && m_Flags.TryGetValue(p, out var was))
                {
                    hp.m_Flags |= was & Need;
                    em.SetComponentData(p, hp);
                    Log.Debug($"patient={p.Index} recovery blocked, EMS en route or on scene");
                }
                if ((hp.m_Flags & Game.Citizens.HealthProblemFlags.RequireTransport) != 0) now[p] = hp.m_Flags;
            }
            m_Next = m_Flags; // swap: no new dictionary every frame
            m_Flags = now;
        }

        private Dictionary<Entity, Game.Citizens.HealthProblemFlags> m_Next = new Dictionary<Entity, Game.Citizens.HealthProblemFlags>();
    }
}
