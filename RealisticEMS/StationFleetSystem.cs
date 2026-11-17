using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;
using PoliceCar = Game.Vehicles.PoliceCar;
using PoliceStation = Game.Buildings.PoliceStation;
using Color = UnityEngine.Color;
using FireEngine = Game.Vehicles.FireEngine;
using FireStation = Game.Buildings.FireStation;
using Helicopter = Game.Vehicles.Helicopter;
using Hospital = Game.Buildings.Hospital;

namespace RealisticEMS
{
    /// <summary>Player-set ambulance count of a fire station or depot (building panel slider). Saved with the city.</summary>
    public struct StationFleet : IComponentData, ISerializable
    {
        public int m_Ambulances;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(m_Ambulances);
        public void Deserialize<TReader>(TReader reader) where TReader : IReader => reader.Read(out m_Ambulances);
    }

    /// <summary>Player-set limits (building panel sliders), -1 = not set (default): hospital beds, hospital doctor cars (NEF), police patrol cars. Saved with the city.</summary>
    public struct StationCaps : IComponentData, ISerializable
    {
        public int m_Beds, m_Nef, m_Cars;
        public static StationCaps Unset => new StationCaps { m_Beds = -1, m_Nef = -1, m_Cars = -1 };
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter { writer.Write(m_Beds); writer.Write(m_Nef); writer.Write(m_Cars); }
        public void Deserialize<TReader>(TReader reader) where TReader : IReader { reader.Read(out m_Beds); reader.Read(out m_Nef); reader.Read(out m_Cars); }
    }

    /// <summary>Fire station option (building panel): its ambulances get the red fire brigade livery. Presence = on. Saved with the city.</summary>
    public struct FireLivery : IComponentData, ISerializable
    {
        public byte m_On;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(m_On);
        public void Deserialize<TReader>(TReader reader) where TReader : IReader => reader.Read(out m_On);
    }

    /// <summary>
    /// Ambulances from fire stations (Setting.FireStationAmbulances): every prefab with FireStationData gets HospitalData
    /// (0 beds, ambulance capacity = its fire engine capacity) and the stations get Game.Buildings.Hospital, so vanilla
    /// HospitalAISystem spawns, dispatches and parks ambulances there like at a depot. Off: both are removed again and
    /// ambulances parked at fire stations are deleted. Without the mod the leftover Hospital has capacity 0 and vanilla
    /// deletes the parked ambulances itself.
    /// The station's vehicle slots (fire engine capacity incl. upgrades) are split: N ambulances, the rest fire engines.
    /// Depots with a slider value are capped the same way. Vanilla only knows prefab capacities, so the cap is enforced
    /// every frame: availability flags, Disabled on parked vehicles, and requests beyond the free count are dropped from
    /// ServiceDispatch, exactly as vanilla drops them when no vehicle is free (the dispatcher then tries another station).
    /// </summary>
    public partial class StationFleetSystem : GameSystemBase
    {
        private EntityQuery m_PrefabsToAdd, m_PrefabsToRemove, m_StationsToAdd, m_StationsToRemove, m_Stations, m_Parked, m_Beds, m_Police;
        private SimulationSystem m_Sim;
        private readonly List<Entity> m_ParkedAmb = new List<Entity>(), m_ParkedEng = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_PrefabsToAdd = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<FireStationData>(), ComponentType.Exclude<HospitalData>());
            m_PrefabsToRemove = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<FireStationData>(), ComponentType.ReadOnly<HospitalData>());
            m_StationsToAdd = GetEntityQuery(ComponentType.ReadOnly<FireStation>(), ComponentType.ReadOnly<ServiceDispatch>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Hospital>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_StationsToRemove = GetEntityQuery(ComponentType.ReadOnly<FireStation>(), ComponentType.ReadOnly<Hospital>(), ComponentType.Exclude<Patient>());
            m_Stations = GetEntityQuery(ComponentType.ReadWrite<Hospital>(), ComponentType.ReadOnly<OwnedVehicle>(), ComponentType.ReadWrite<ServiceDispatch>(),
                ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Patient>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Beds = GetEntityQuery(ComponentType.ReadWrite<Hospital>(), ComponentType.ReadOnly<Patient>(), ComponentType.ReadOnly<StationCaps>(), ComponentType.Exclude<Deleted>());
            m_Police = GetEntityQuery(ComponentType.ReadOnly<PoliceStation>(), ComponentType.ReadOnly<OwnedVehicle>(), ComponentType.ReadWrite<ServiceDispatch>(),
                ComponentType.ReadOnly<StationCaps>(), ComponentType.Exclude<Deleted>());
            m_Parked = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<ParkedCar>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>());
        }

        /// <summary>Vehicle slots of a station: fire engine capacity for fire stations, ambulance capacity for depots (incl. upgrades).</summary>
        public static int Slots(EntityManager em, Entity station)
        {
            var prefab = em.GetComponentData<PrefabRef>(station).m_Prefab;
            em.TryGetBuffer(station, true, out DynamicBuffer<InstalledUpgrade> upgrades);
            if (em.HasComponent<FireStation>(station))
            {
                em.TryGetComponent(prefab, out FireStationData f);
                if (upgrades.IsCreated) UpgradeUtils.CombineStats(em, ref f, upgrades);
                return f.m_FireEngineCapacity;
            }
            em.TryGetComponent(prefab, out HospitalData h);
            if (upgrades.IsCreated) UpgradeUtils.CombineStats(em, ref h, upgrades);
            return h.m_AmbulanceCapacity;
        }

        /// <summary>Ambulances the station may run: slider value, else the default (fire stations) or all slots (depots).</summary>
        public static int Ambulances(EntityManager em, Entity station, int slots) =>
            math.clamp(em.TryGetComponent(station, out StationFleet f) ? f.m_Ambulances
                : em.HasComponent<FireStation>(station) ? Mod.Setting.FireStationAmbulanceDefault : slots, 0, slots);

        public const int NefMax = 4; // ponytail: fixed slider range for doctor cars per hospital

        /// <summary>Bed slots of a hospital (prefab capacity incl. upgrades and the bed percentage).</summary>
        public static int BedSlots(EntityManager em, Entity hospital)
        {
            em.TryGetComponent(em.GetComponentData<PrefabRef>(hospital).m_Prefab, out HospitalData h);
            if (em.TryGetBuffer(hospital, true, out DynamicBuffer<InstalledUpgrade> up)) UpgradeUtils.CombineStats(em, ref h, up);
            return h.m_PatientCapacity;
        }

        /// <summary>Slider maximum: 120% of the vanilla (unscaled by Setting.BedCapacityPercent) capacity incl. upgrades.</summary>
        public static int BedMax(EntityManager em, Entity hospital)
        {
            int Orig(Entity prefab) => BedCapacitySystem.Original.TryGetValue(prefab, out int o) ? o : em.TryGetComponent(prefab, out HospitalData d) ? d.m_PatientCapacity : 0;
            int total = Orig(em.GetComponentData<PrefabRef>(hospital).m_Prefab);
            if (em.TryGetBuffer(hospital, true, out DynamicBuffer<InstalledUpgrade> up))
                foreach (var u in up) if (em.TryGetComponent(u.m_Upgrade, out PrefabRef r)) total += Orig(r.m_Prefab);
            return total > 0 ? math.max(1, (int)math.round(total * 1.2f)) : 0;
        }

        /// <summary>Medical helicopters of a hospital incl. upgrades (e.g. a helipad). Read by HelicopterSystem.</summary>
        public static int HeliSlots(EntityManager em, Entity hospital)
        {
            em.TryGetComponent(em.GetComponentData<PrefabRef>(hospital).m_Prefab, out HospitalData h);
            if (em.TryGetBuffer(hospital, true, out DynamicBuffer<InstalledUpgrade> up)) UpgradeUtils.CombineStats(em, ref h, up);
            return h.m_MedicalHelicopterCapacity;
        }

        /// <summary>Patrol car slots of a police station (incl. upgrades).</summary>
        public static int CarSlots(EntityManager em, Entity station)
        {
            em.TryGetComponent(em.GetComponentData<PrefabRef>(station).m_Prefab, out PoliceStationData p);
            if (em.TryGetBuffer(station, true, out DynamicBuffer<InstalledUpgrade> up)) UpgradeUtils.CombineStats(em, ref p, up);
            return p.m_PatrolCarCapacity;
        }

        /// <summary>Doctor cars a hospital may run at once: slider value, else Setting.NefPerHospital.</summary>
        public static int NefLimit(EntityManager em, Entity hospital) =>
            em.TryGetComponent(hospital, out StationCaps c) && c.m_Nef >= 0 ? c.m_Nef : Mod.Setting.NefPerHospital;

        protected override void OnUpdate()
        {
            EnforceBeds();
            EnforcePolice();
            bool on = Mod.Setting.FireStationAmbulances;
            if (on) AddToFireStations();
            else if (!m_StationsToRemove.IsEmpty || !m_PrefabsToRemove.IsEmpty || m_Sim.frameIndex % 256 == 0) RemoveFromFireStations();

            using var stations = m_Stations.ToEntityArray(Allocator.Temp);
            foreach (var e in stations)
            {
                bool fire = EntityManager.HasComponent<FireStation>(e);
                if (!fire && !EntityManager.HasComponent<StationFleet>(e)) continue; // depot without slider value: vanilla
                int slots = Slots(EntityManager, e), amb = Ambulances(EntityManager, e, slots);
                Enforce(e, amb, fire ? slots - amb : -1);
            }
        }

        /// <summary>Slider beds override the prefab capacity (also above it): room for patients iff patients &lt; beds. Patients stay when lowered.</summary>
        private void EnforceBeds()
        {
            using var hospitals = m_Beds.ToEntityArray(Allocator.Temp);
            foreach (var e in hospitals)
            {
                int beds = EntityManager.GetComponentData<StationCaps>(e).m_Beds;
                var h = EntityManager.GetComponentData<Hospital>(e);
                if (beds < 0) continue;
                bool room = EntityManager.GetBuffer<Patient>(e, true).Length < beds;
                if (((h.m_Flags & HospitalFlags.HasRoomForPatients) != 0) == room) continue;
                h.m_Flags ^= HospitalFlags.HasRoomForPatients;
                EntityManager.SetComponentData(e, h);
            }
        }

        /// <summary>Caps a police station at the slider's patrol cars, same mechanism as <see cref="Enforce"/> for ambulances.</summary>
        private void EnforcePolice()
        {
            var em = EntityManager;
            using var stations = m_Police.ToEntityArray(Allocator.Temp);
            foreach (var station in stations)
            {
                int cars = em.GetComponentData<StationCaps>(station).m_Cars;
                if (cars < 0) continue;
                int out_ = 0;
                m_ParkedAmb.Clear();
                foreach (var v in em.GetBuffer<OwnedVehicle>(station, true))
                {
                    var e = v.m_Vehicle;
                    if (!em.HasComponent<PoliceCar>(e) || em.HasComponent<Helicopter>(e)) continue; // ponytail: helicopters not capped
                    if (em.HasComponent<ParkedCar>(e)) m_ParkedAmb.Add(e); else out_++;
                }
                int free = cars - out_;
                for (int i = 0; i < m_ParkedAmb.Count; i++)
                {
                    var p = em.GetComponentData<PoliceCar>(m_ParkedAmb[i]);
                    if (((p.m_State & PoliceCarFlags.Disabled) != 0) == (i >= free)) continue;
                    p.m_State ^= PoliceCarFlags.Disabled;
                    em.SetComponentData(m_ParkedAmb[i], p);
                }
                if (free <= 0)
                {
                    var s = em.GetComponentData<PoliceStation>(station);
                    if ((s.m_Flags & PoliceStationFlags.HasAvailablePatrolCars) != 0) { s.m_Flags &= ~PoliceStationFlags.HasAvailablePatrolCars; em.SetComponentData(station, s); }
                }
                var dispatches = em.GetBuffer<ServiceDispatch>(station);
                int req = 0;
                for (int i = 0; i < dispatches.Length; i++)
                    if ((em.HasComponent<PoliceEmergencyRequest>(dispatches[i].m_Request) || em.HasComponent<PolicePatrolRequest>(dispatches[i].m_Request)) && ++req > free)
                        dispatches.RemoveAt(i--);
            }
        }

        private void AddToFireStations()
        {
            if (!m_PrefabsToAdd.IsEmpty)
            {
                using var prefabs = m_PrefabsToAdd.ToEntityArray(Allocator.Temp);
                foreach (var p in prefabs)
                    EntityManager.AddComponentData(p, new HospitalData
                    {
                        m_AmbulanceCapacity = EntityManager.GetComponentData<FireStationData>(p).m_FireEngineCapacity,
                        m_HealthRange = new int2(0, 100),
                    });
                Mod.log.Info($"Fire station ambulances: {prefabs.Length} fire station prefabs got ambulance slots");
            }
            if (m_StationsToAdd.IsEmpty) return;
            using var stations = m_StationsToAdd.ToEntityArray(Allocator.Temp);
            foreach (var e in stations)
                if (Slots(EntityManager, e) > 0) // helicopter-only depots stay fire-only
                    EntityManager.AddComponentData(e, default(Hospital));
        }

        private void RemoveFromFireStations()
        {
            EntityManager.RemoveComponent<Hospital>(m_StationsToRemove);
            EntityManager.RemoveComponent<HospitalData>(m_PrefabsToRemove);
            // Ambulances still out return and park at the fire station; this sweep (every 256 frames) deletes them.
            using var parked = m_Parked.ToEntityArray(Allocator.Temp);
            using var owners = m_Parked.ToComponentDataArray<Owner>(Allocator.Temp);
            for (int i = 0; i < parked.Length; i++)
                if (EntityManager.HasComponent<FireStation>(owners[i].m_Owner) && !EntityManager.HasComponent<Hospital>(owners[i].m_Owner))
                    EntityManager.AddComponent<Deleted>(parked[i]);
        }

        // ponytail: channel meaning depends on the ambulance mesh (like NefDispatchSystem.NefColors); tweak here if it looks off.
        private static readonly ColorSet FireColors = new ColorSet { m_Channel0 = new Color(0.78f, 0.07f, 0.06f), m_Channel1 = new Color(0.95f, 0.95f, 0.95f), m_Channel2 = new Color(0.95f, 0.75f, 0.1f) };
        private readonly HashSet<Entity> m_NoTintLogged = new HashSet<Entity>();

        /// <summary>Red livery on/off via the vanilla CustomMeshColor buffer (as NefDispatchSystem.ApplyColors); off = back to the random variation.</summary>
        private void Livery(Entity amb, bool red)
        {
            var em = EntityManager;
            if (!em.HasBuffer<CustomMeshColor>(amb))
            {
                if (red && m_NoTintLogged.Add(amb)) Mod.log.Warn($"Ambulance {amb.Index}: body supports no custom colours, fire livery skipped");
                return;
            }
            bool enabled = em.IsComponentEnabled<CustomMeshColor>(amb);
            var custom = em.GetBuffer<CustomMeshColor>(amb, true);
            bool isRed = enabled && custom.Length == 1 && custom[0].m_ColorSet.m_Channel0.Equals(FireColors.m_Channel0);
            if (isRed == red) return;
            if (red)
            {
                var buf = em.GetBuffer<CustomMeshColor>(amb);
                buf.Clear();
                buf.Add(new CustomMeshColor { m_ColorSet = FireColors });
            }
            em.SetComponentEnabled<CustomMeshColor>(amb, red);
            if (!em.HasComponent<BatchesUpdated>(amb)) em.AddComponent<BatchesUpdated>(amb);
        }

        /// <summary>Caps the station at <paramref name="ambulances"/> ambulances and <paramref name="engines"/> fire engines (-1 = not a fire station).</summary>
        private void Enforce(Entity station, int ambulances, int engines)
        {
            var em = EntityManager;
            int ambOut = 0, engOut = 0;
            m_ParkedAmb.Clear(); m_ParkedEng.Clear();
            bool red = em.HasComponent<FireLivery>(station);
            // ponytail: helicopters are skipped and requests are not split by road type; vanilla fire stations have none.
            foreach (var v in em.GetBuffer<OwnedVehicle>(station, true))
            {
                var e = v.m_Vehicle;
                if (em.HasComponent<Helicopter>(e)) continue;
                bool parked = em.HasComponent<ParkedCar>(e);
                if (em.HasComponent<Ambulance>(e)) { if (parked) m_ParkedAmb.Add(e); else ambOut++; if (engines >= 0) Livery(e, red); }
                else if (engines >= 0 && em.HasComponent<FireEngine>(e)) { if (parked) m_ParkedEng.Add(e); else engOut++; }
            }
            int ambFree = ambulances - ambOut, engFree = engines - engOut;

            for (int i = 0; i < m_ParkedAmb.Count; i++)
            {
                var a = em.GetComponentData<Ambulance>(m_ParkedAmb[i]);
                if (((a.m_State & AmbulanceFlags.Disabled) != 0) == (i >= ambFree)) continue;
                a.m_State ^= AmbulanceFlags.Disabled;
                em.SetComponentData(m_ParkedAmb[i], a);
            }
            for (int i = 0; i < m_ParkedEng.Count; i++)
            {
                var f = em.GetComponentData<FireEngine>(m_ParkedEng[i]);
                if (((f.m_State & FireEngineFlags.Disabled) != 0) == (i >= engFree)) continue;
                f.m_State ^= FireEngineFlags.Disabled;
                em.SetComponentData(m_ParkedEng[i], f);
            }

            var h = em.GetComponentData<Hospital>(station);
            if (ambFree <= 0 && (h.m_Flags & HospitalFlags.HasAvailableAmbulances) != 0)
            {
                h.m_Flags &= ~HospitalFlags.HasAvailableAmbulances;
                em.SetComponentData(station, h);
            }
            const FireStationFlags engineFlags = FireStationFlags.HasAvailableFireEngines | FireStationFlags.HasFreeFireEngines;
            if (engines >= 0 && engFree <= 0)
            {
                var fs = em.GetComponentData<FireStation>(station);
                if ((fs.m_Flags & engineFlags) != 0) { fs.m_Flags &= ~engineFlags; em.SetComponentData(station, fs); }
            }

            if (em.GetBuffer<ServiceDispatch>(station, true).Length == 0) return;
            var dispatches = em.GetBuffer<ServiceDispatch>(station);
            int ambReq = 0, engReq = 0;
            for (int i = 0; i < dispatches.Length; i++)
            {
                var r = dispatches[i].m_Request;
                bool drop = em.TryGetComponent(r, out HealthcareRequest hr)
                    ? hr.m_Type == HealthcareRequestType.Ambulance && ++ambReq > ambFree
                    : engines >= 0 && em.HasComponent<FireRescueRequest>(r) && ++engReq > engFree;
                if (drop) dispatches.RemoveAt(i--);
            }
        }
    }
}
