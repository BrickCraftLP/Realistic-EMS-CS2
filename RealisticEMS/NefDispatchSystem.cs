using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Ambulance = Game.Vehicles.Ambulance;
using Hospital = Game.Buildings.Hospital;
using Random = Unity.Mathematics.Random;
using Transform = Game.Objects.Transform;
using Color = UnityEngine.Color;

namespace RealisticEMS
{
    /// <summary>
    /// Experimental: dispatches emergency doctor cars (NEF) from hospitals.
    ///  - Sent with the ambulance when the call's code lists NEF (Einsatzcodes), or called in by the ambulance on
    ///    scene with a level-based chance.
    ///  - Drives with lights (AmbulanceFlags.Critical makes vanilla set CarFlags.Emergency), waits on scene while the
    ///    patient is there (it carries the patient's request as ServiceDispatch, so vanilla LoadPatients waits) and
    ///    leaves on its own once the patient has boarded the ambulance. OnSceneCareSystem holds the ambulance.
    ///  - Escort: when the ambulance leaves for hospital, a roll decides whether the NEF follows to the same hospital.
    ///  - Never parks: on arrival home it is deleted (a parked NEF would be used by HospitalAISystem as an ambulance).
    /// </summary>
    public partial class NefDispatchSystem : GameSystemBase
    {
        private EntityQuery m_Ambulances, m_Hospitals;
        private UnitNamingSystem m_Units;
        private Random m_Random = new Random(0x5EF1);

        private class Mission { public Entity Patient, Location, Hospital; public bool Escorting; public int Frames; }
        private readonly Dictionary<Entity, Mission> m_Missions = new Dictionary<Entity, Mission>(); // nef -> mission
        private readonly Dictionary<Entity, (Entity location, int frames)> m_Pending = new Dictionary<Entity, (Entity, int)>(); // patient -> wait
        private readonly HashSet<Entity> m_Handled = new HashSet<Entity>(); // patients already decided (sent or rolled)
        private readonly HashSet<Entity> m_NoStart = new HashSet<Entity>(); // hospitals whose NEF could not leave (no route)

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        public static bool IsNef(EntityManager em, Entity vehicle) =>
            NefDispatchSystem.Prefab != Entity.Null && em.HasComponent<PrefabRef>(vehicle) && em.GetComponentData<PrefabRef>(vehicle).m_Prefab == Prefab;

        private static Entity Prefab => NefPrefabSystem.NefPrefab;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Hospitals = GetEntityQuery(ComponentType.ReadOnly<Hospital>(), ComponentType.ReadOnly<Patient>(), ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Missions.Clear(); m_Pending.Clear(); m_Handled.Clear(); m_NoStart.Clear(); NefByPatient.Clear();
        }

        protected override void OnUpdate()
        {
            if (!Mod.Setting.EnableNef || Prefab == Entity.Null) { NefByPatient.Clear(); return; }
            var em = EntityManager;
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);

            var rtwByPatient = new Dictionary<Entity, Entity>();
            var nefs = new List<Entity>();
            foreach (var v in vehicles)
            {
                if (IsNef(em, v)) { nefs.Add(v); continue; }
                var a = em.GetComponentData<Ambulance>(v);
                if (a.m_TargetPatient == Entity.Null) continue;
                rtwByPatient[a.m_TargetPatient] = v;
                if (m_Handled.Contains(a.m_TargetPatient) || !m_Units.TryGetCode(v, out var code) || code == null) continue;

                if (code.Nef > 0) Request(a.m_TargetPatient, a.m_TargetLocation, $"Code {code} verlangt NEF");
                else if (OnSceneCareSystem.OnScene(em, v)) // not AtTarget: the on-scene hold clears it
                {
                    m_Handled.Add(a.m_TargetPatient); // one roll per patient
                    if (m_Random.NextFloat() < CallInChance(code.Level))
                        Request(a.m_TargetPatient, a.m_TargetLocation, $"{m_Units.CallsignOf(v)} fordert NEF nach ({code})");
                }
            }

            foreach (var nef in nefs) UpdateNef(nef, rtwByPatient);

            foreach (var kv in m_Missions)
                if (!kv.Value.Escorting && rtwByPatient.TryGetValue(kv.Value.Patient, out var rtw)) MaybeEscort(kv.Key, kv.Value, rtw);

            AssignPending();
            foreach (var gone in new List<Entity>(m_Missions.Keys)) if (!em.Exists(gone) || em.HasComponent<Deleted>(gone)) m_Missions.Remove(gone);

            NefByPatient.Clear();
            foreach (var p in m_Pending.Keys) NefByPatient[p] = Entity.Null;
            foreach (var kv in m_Missions) NefByPatient[kv.Value.Patient] = kv.Key;
        }

        /// <summary>Patients with an NEF (Entity.Null = requested, not yet on the way). Read by OnSceneCareSystem.</summary>
        public static readonly Dictionary<Entity, Entity> NefByPatient = new Dictionary<Entity, Entity>();

        // ponytail: fixed chances; move to settings if players want to tune them.
        private static float CallInChance(AmpdsLevel l) => l == AmpdsLevel.Echo ? 1f : l == AmpdsLevel.Delta ? 0.3f : l == AmpdsLevel.Charlie ? 0.1f : 0.02f;
        private static float EscortChance(AmpdsLevel l) => l == AmpdsLevel.Echo ? 0.8f : l == AmpdsLevel.Delta ? 0.5f : l == AmpdsLevel.Charlie ? 0.15f : 0f;

        private void Request(Entity patient, Entity location, string why)
        {
            m_Handled.Add(patient);
            if (location == Entity.Null || m_Pending.ContainsKey(patient)) return;
            m_Pending[patient] = (location, 0);
            Mod.log.Info($"NEF angefordert für Patient {patient.Index}: {why}");
        }

        private void AssignPending()
        {
            var em = EntityManager;
            foreach (var patient in new List<Entity>(m_Pending.Keys))
            {
                var (location, frames) = m_Pending[patient];
                bool stillNeeded = em.Exists(patient) && em.HasComponent<HealthProblem>(patient)
                    && (em.GetComponentData<HealthProblem>(patient).m_Flags & HealthProblemFlags.Dead) == 0 && em.Exists(location);
                if (!stillNeeded || frames > 3600) { m_Pending.Remove(patient); continue; }
                var hospital = FindHospitalWithFreeNef(location);
                if (hospital == Entity.Null) { m_Pending[patient] = (location, frames + 16); continue; } // stays open
                m_Pending.Remove(patient);
                Spawn(hospital, patient, location);
            }
        }

        private Entity FindHospitalWithFreeNef(Entity location)
        {
            var em = EntityManager;
            if (!em.HasComponent<Transform>(location)) return Entity.Null;
            float3 at = em.GetComponentData<Transform>(location).m_Position;
            var busy = new Dictionary<Entity, int>();
            foreach (var m in m_Missions.Values) busy[m.Hospital] = (busy.TryGetValue(m.Hospital, out var n) ? n : 0) + 1;
            using var hospitals = m_Hospitals.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null; float bestDist = float.MaxValue;
            foreach (var h in hospitals)
            {
                var prefab = em.GetComponentData<PrefabRef>(h).m_Prefab;
                if (m_NoStart.Contains(h) || !em.HasComponent<HospitalData>(prefab)) continue;
                var data = em.GetComponentData<HospitalData>(prefab);
                if (data.m_PatientCapacity <= 0 || data.m_AmbulanceCapacity <= 0) continue; // only hospitals that send vehicles out
                if ((busy.TryGetValue(h, out var used) ? used : 0) >= Mod.Setting.NefPerHospital) continue;
                // ponytail: straight-line distance; the route itself is vanilla pathfinding.
                float d = math.distance(at, em.GetComponentData<Transform>(h).m_Position);
                if (d < bestDist) { bestDist = d; best = h; }
            }
            return best;
        }

        /// <summary>Same steps as HealthcareVehicleSelectData.CreateVehicle + HospitalAISystem.SpawnVehicle.</summary>
        private void Spawn(Entity hospital, Entity patient, Entity location)
        {
            var em = EntityManager;
            EntityArchetype archetype = em.GetComponentData<ObjectData>(Prefab).m_Archetype;
            // CreateEntity(archetype) does not compile against the game's mscorlib (ReadOnlySpan overload); use the array overload.
            using var created = new NativeArray<Entity>(1, Allocator.Temp);
            em.CreateEntity(archetype, created);
            var e = created[0];
            em.SetComponentData(e, em.GetComponentData<Transform>(hospital));
            em.SetComponentData(e, new PrefabRef(Prefab));
            em.SetComponentData(e, new PseudoRandomSeed(ref m_Random));
            em.AddComponentData(e, new TripSource(hospital));
            em.AddComponentData(e, default(Unspawned));
            em.AddComponentData(e, new Owner(hospital));
            em.SetComponentData(e, new Ambulance(patient, location, AmbulanceFlags.Dispatched | AmbulanceFlags.Critical));
            em.SetComponentData(e, new Target(location));
            // The patient's own request: vanilla LoadPatients then waits on scene while the patient is there and has not
            // boarded another vehicle (without a dispatch it returns home on arrival). Residents only walk to the
            // vehicle that handles their request, so they never board the NEF.
            var request = em.GetComponentData<HealthProblem>(patient).m_HealthcareRequest;
            if (request != Entity.Null) em.GetBuffer<ServiceDispatch>(e).Add(new ServiceDispatch(request));
            m_Missions[e] = new Mission { Patient = patient, Location = location, Hospital = hospital };
            Mod.log.Info($"NEF {e.Index} alarmiert von Spital {hospital.Index} zu Patient {patient.Index}");
        }

        private void UpdateNef(Entity nef, Dictionary<Entity, Entity> rtwByPatient)
        {
            var em = EntityManager;
            if (em.HasComponent<ParkedCar>(nef)) { em.AddComponent<Deleted>(nef); return; } // never parks, see class summary
            var a = em.GetComponentData<Ambulance>(nef);
            var before = a.m_State;
            m_Missions.TryGetValue(nef, out var mission);
            if (mission != null) mission.Frames += 16;

            if ((a.m_State & AmbulanceFlags.Returning) != 0 && mission != null && mission.Frames <= 64
                && rtwByPatient.TryGetValue(mission.Patient, out var waitingRtw)
                && (em.GetComponentData<Ambulance>(waitingRtw).m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) == 0)
            {
                // Sent home right after spawning: vanilla found no route from this hospital. Use another one.
                m_NoStart.Add(mission.Hospital);
                m_Missions.Remove(nef);
                if (!m_Pending.ContainsKey(mission.Patient)) m_Pending[mission.Patient] = (mission.Location, 0);
                em.AddComponent<Deleted>(nef);
                Mod.log.Warn($"NEF {nef.Index}: kann nicht von Spital {mission.Hospital.Index} losfahren (keine Route), versuche anderes Spital");
                return;
            }
            if ((a.m_State & AmbulanceFlags.Returning) != 0)
            {
                // Home without lights, and never usable as a regular ambulance on the way.
                a.m_State = (a.m_State & ~AmbulanceFlags.Critical) | AmbulanceFlags.Disabled;
                // Vanilla sends the NEF home in the tick the patient boards; the ambulance gets its hospital a tick
                // later. Keep the mission until MaybeEscort has decided.
                if (mission == null || mission.Escorting || !rtwByPatient.ContainsKey(mission.Patient)) m_Missions.Remove(nef);
            }
            else
            {
                a.m_State &= ~AmbulanceFlags.Disabled; // HospitalAISystem may disable it when the hospital is "over capacity"
                bool dead = mission != null && em.HasComponent<HealthProblem>(mission.Patient)
                    && (em.GetComponentData<HealthProblem>(mission.Patient).m_Flags & HealthProblemFlags.Dead) != 0;
                bool busy = (a.m_State & (AmbulanceFlags.Dispatched | AmbulanceFlags.Transporting)) != 0;
                // No mission (e.g. NEF from a save; missions are not saved): vanilla would keep dispatching it and we
                // would keep sending it home every tick. It is never a regular ambulance, so remove it.
                if (mission == null) { em.AddComponent<Deleted>(nef); return; }
                // Safety net for a stuck NEF; the normal end is vanilla's "patient gone".
                int timeout = (int)((Mod.Setting.NefMaxWaitMinutes + Mod.Setting.OnSceneMinutes + 120) * OnSceneCareSystem.FramesPerGameMinute);
                if (mission == null || dead || mission.Frames > timeout) SendHome(nef, ref a, dead ? "Patient verstorben" : mission == null ? "Einsatz beendet" : "Zeitüberschreitung");
            }
            if (a.m_State != before) em.SetComponentData(nef, a);
            ApplyColors(nef);
        }

        /// <summary>When the ambulance leaves for hospital: roll whether the doctor follows to the same hospital.</summary>
        private void MaybeEscort(Entity nef, Mission m, Entity rtw)
        {
            var em = EntityManager;
            var a = em.GetComponentData<Ambulance>(rtw);
            if ((a.m_State & AmbulanceFlags.Transporting) == 0 || (a.m_State & AmbulanceFlags.FindHospital) != 0) return;
            var hospital = em.GetComponentData<Target>(rtw).m_Target;
            if (hospital == Entity.Null) return;
            m.Escorting = true; // decided once
            bool critical = (a.m_State & AmbulanceFlags.Critical) != 0;
            var level = m_Units.TryGetCode(rtw, out var code) && code != null ? code.Level : AmpdsLevel.Alpha;
            if (!critical && m_Random.NextFloat() >= EscortChance(level))
            {
                Mod.log.Info($"NEF {nef.Index}: Notarzt nicht nötig, frei nach Abfahrt des RTW"); // vanilla sends it home once the patient is gone
                return;
            }
            var n = em.GetComponentData<Ambulance>(nef);
            // Transporting with no passengers: at the hospital UnloadPatients succeeds at once, then it returns home.
            n.m_State = (n.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Disabled | AmbulanceFlags.Returning)) | AmbulanceFlags.Transporting | (critical ? AmbulanceFlags.Critical : 0);
            if (!critical) n.m_State &= ~AmbulanceFlags.Critical;
            n.m_TargetPatient = Entity.Null;
            em.SetComponentData(nef, n);
            Retarget(nef, hospital);
            Mod.log.Info($"NEF {nef.Index}: begleitet {m_Units.CallsignOf(rtw)} ins Spital {hospital.Index}{(critical ? " mit Blaulicht" : "")}");
        }

        private void SendHome(Entity nef, ref Ambulance a, string why)
        {
            var owner = EntityManager.GetComponentData<Owner>(nef).m_Owner;
            a.m_State = (a.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Critical | AmbulanceFlags.Transporting)) | AmbulanceFlags.Returning;
            a.m_TargetPatient = Entity.Null;
            a.m_TargetLocation = Entity.Null;
            Retarget(nef, owner);
            m_Missions.Remove(nef);
            Mod.log.Info($"NEF {nef.Index}: rückt ein ({why})");
        }

        private void Retarget(Entity e, Entity target)
        {
            var em = EntityManager;
            var po = em.GetComponentData<PathOwner>(e);
            var t = em.GetComponentData<Target>(e);
            VehicleUtils.SetTarget(ref po, ref t, target);
            em.SetComponentData(e, po);
            em.SetComponentData(e, t);
        }

        // NEF colours (white body, red stripe channel). Per vehicle, so the shared police mesh is untouched.
        // ponytail: channel meaning depends on the police mesh; adjust here if the colours land on the wrong parts.
        private static readonly ColorSet NefColors = new ColorSet { m_Channel0 = new Color(0.95f, 0.95f, 0.95f), m_Channel1 = new Color(0.85f, 0.08f, 0.08f), m_Channel2 = new Color(0.95f, 0.75f, 0.1f) };

        /// <summary>
        /// Vanilla custom colours: MeshColorSystem uses an enabled CustomMeshColor buffer instead of the random
        /// variation (and it is saved with the vehicle). Writing MeshColor directly gets overwritten.
        /// </summary>
        private void ApplyColors(Entity nef)
        {
            var em = EntityManager;
            if (!em.HasBuffer<CustomMeshColor>(nef))
            {
                if (m_NoTintLogged.Add(nef)) Mod.log.Warn($"NEF {nef.Index}: body supports no custom colours");
                return;
            }
            var custom = em.GetBuffer<CustomMeshColor>(nef);
            bool enabled = em.IsComponentEnabled<CustomMeshColor>(nef);
            if (enabled && custom.Length == 1 && custom[0].m_ColorSet.m_Channel0.Equals(NefColors.m_Channel0)) return;
            custom.Clear();
            custom.Add(new CustomMeshColor { m_ColorSet = NefColors });
            em.SetComponentEnabled<CustomMeshColor>(nef, true);
            if (!em.HasComponent<BatchesUpdated>(nef)) em.AddComponent<BatchesUpdated>(nef);
        }

        private readonly HashSet<Entity> m_NoTintLogged = new HashSet<Entity>();
    }
}
