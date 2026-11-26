using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;
using Hospital = Game.Buildings.Hospital;
using Random = Unity.Mathematics.Random;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Experimental rescue helicopter (RTH), flown by vanilla AmbulanceAISystem (vanilla medical-helicopter prefab).
    ///  - Doctor mission: sent instead of the NEF when faster, or requested on scene. Lands only on a sports field
    ///    near the call (never at the patient); a police car shuttles the doctor field -> patient -> field.
    ///  - Patient flight: when no suitable hospital is reachable by road fast enough (island, clinic without trauma
    ///    care, long drive or heavy traffic), the ambulance brings the patient to the field and hands over; the heli flies to a trauma
    ///    hospital. That ambulance is available again one game minute after the heli lifts off.
    ///  - Landing sites: flat parks with no trees/props within 10 m of the centre; sports fields preferred.
    ///  - Vanilla medical helis never take ordinary calls (also with this feature off), see RecallVanillaHelis.
    ///  - Clinic transfer: a small hospital without road access to a trauma hospital flies out critical patients,
    ///    or one patient when it is full.
    /// Vehicles waiting at a field are held with the TripSource timer (vanilla AI skips them), see ResponderDispatchSystem.
    /// Runs before AmbulanceAISystem so an arriving handover ambulance is held before vanilla unloads at a park.
    /// Missions are not saved: after a load, leftover helis/police fall back to vanilla and go home.
    /// </summary>
    public partial class HelicopterSystem : GameSystemBase
    {
        private enum Kind { Scene, Transfer }
        private enum Phase { FlyIn, OnSite, Flying, Home }

        private class Mission
        {
            public Kind Kind; public Phase Phase;
            public Entity Base, Site, Patient, Location, Police, Rtw, Clinic, Dest;
            public bool DoctorOut, DoctorBack, Carry;
            public uint Since, OnSiteSince;
        }

        private readonly Dictionary<Entity, Mission> m_Missions = new Dictionary<Entity, Mission>(); // heli -> mission
        private readonly HashSet<Entity> m_Decided = new HashSet<Entity>();   // patients whose transport was decided
        private readonly HashSet<Entity> m_Rolled = new HashSet<Entity>();    // patients with an on-scene roll done
        private readonly Dictionary<Entity, Entity> m_Handover = new Dictionary<Entity, Entity>(); // ambulance -> heli
        private readonly Dictionary<(Entity, Entity), bool> m_Reach = new Dictionary<(Entity, Entity), bool>();
        private readonly List<(Entity site, float3 pos)> m_Sites = new List<(Entity, float3)>();
        private uint m_SitesAt = uint.MaxValue;

        private EntityQuery m_Ambulances, m_Hospitals, m_HeliBases, m_MedHelis, m_Parks, m_HeliPrefabs, m_NetChanges, m_Clutter;
        private TerrainSystem m_Terrain;
        private int m_BasesSeen; // hospitals with any helicopter slot, from the last NearestHeliBase (debug message)
        private PrefabSystem m_Prefabs;
        private SimulationSystem m_Sim;
        private UnitNamingSystem m_Units;
        private ResponderDispatchSystem m_Responders;
        private Random m_Random = new Random(0x4E11);

        /// <summary>Patients a heli doctor handles; NefDispatchSystem leaves them alone.</summary>
        public static readonly HashSet<Entity> HeliPatients = new HashSet<Entity>();
        /// <summary>Patient -> shuttle police car once it carries the doctor to the patient (Entity.Null before). Read by OnSceneCareSystem.</summary>
        public static readonly Dictionary<Entity, Entity> DoctorByPatient = new Dictionary<Entity, Entity>();
        /// <summary>Ambulances that handed a patient to the heli: Disabled until parked at their base. Read by AmbulanceReturnSystem.</summary>
        public static readonly HashSet<Entity> HandedOver = new HashSet<Entity>();

        /// <summary>
        /// Patient -> heli / shuttle police car from dispatch on (not only after landing), for the call panels. The heli
        /// has no m_TargetPatient while flying in, so the panels cannot find it through the Ambulance component.
        /// </summary>
        public static readonly Dictionary<Entity, Entity> HeliByPatient = new Dictionary<Entity, Entity>();
        public static readonly Dictionary<Entity, Entity> ShuttleByPatient = new Dictionary<Entity, Entity>();

        /// <summary>Call patient of a heli or shuttle police car of a mission, else Entity.Null.</summary>
        public static Entity PatientOf(Entity vehicle)
        {
            foreach (var kv in HeliByPatient) if (kv.Value == vehicle) return kv.Key;
            foreach (var kv in ShuttleByPatient) if (kv.Value == vehicle) return kv.Key;
            return Entity.Null;
        }

        private void RefreshCallUnits()
        {
            HeliByPatient.Clear(); ShuttleByPatient.Clear();
            foreach (var kv in m_Missions)
            {
                var m = kv.Value;
                if (m.Patient == Entity.Null || m.Phase == Phase.Home) continue;
                HeliByPatient[m.Patient] = kv.Key;
                if (m.Police != Entity.Null) ShuttleByPatient[m.Patient] = m.Police;
            }
        }

        /// <summary>This ambulance is driving its patient to the heli. NefDispatchSystem then never escorts it.</summary>
        public bool IsHandingOver(Entity rtw) => m_Handover.ContainsKey(rtw);

        // ponytail: fixed speeds/radii; tune here or move to settings if players ask.
        private const float HeliSpeed = 60f, CarSpeed = 14f, RoadFactor = 1.4f, SiteRadius = 1500f, NearSite = 150f;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1; // handover holds must beat vanilla's arrival tick

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Responders = World.GetOrCreateSystemManaged<ResponderDispatchSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Hospitals = GetEntityQuery(ComponentType.ReadOnly<Hospital>(), ComponentType.ReadOnly<Patient>(), ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_HeliBases = GetEntityQuery(ComponentType.ReadOnly<Hospital>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_MedHelis = GetEntityQuery(ComponentType.ReadOnly<Helicopter>(), ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Parks = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.Park>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_HeliPrefabs = GetEntityQuery(ComponentType.ReadOnly<AmbulanceData>(), ComponentType.ReadOnly<HelicopterData>(), ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Locked>());
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            // Trees and static props (benches, lamps, goals...) block a landing spot; buildings do not (the park itself).
            m_Clutter = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Transform>() },
                Any = new[] { ComponentType.ReadOnly<Tree>(), ComponentType.ReadOnly<Static>() },
                None = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_NetChanges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Edge>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Missions.Clear(); m_Decided.Clear(); m_Rolled.Clear(); m_Handover.Clear(); m_NoRoute.Clear(); m_Reach.Clear(); m_Sites.Clear();
            m_SitesAt = uint.MaxValue;
            m_LoggedBases.Clear(); m_FreeAt.Clear(); m_Recalled.Clear();
            m_Restore = true;
            HeliByPatient.Clear(); ShuttleByPatient.Clear();
            HeliPatients.Clear(); DoctorByPatient.Clear(); HandedOver.Clear();
        }

        protected override void OnUpdate()
        {
            uint now = m_Sim.frameIndex;
            if (m_Restore) { m_Restore = false; RedirectOrphanedHandovers(); }
            if (now % 16 == 0) RecallVanillaHelis();
            if (!Mod.Setting.EnableHelicopter) { HeliByPatient.Clear(); ShuttleByPatient.Clear(); return; }
            StartNoRouteHandovers();
            HoldArrivingHandovers(); // every frame
            if (now % 16 != 0) return;
            if (!m_NetChanges.IsEmptyIgnoreFilter) m_Reach.Clear();
            if (m_SitesAt == uint.MaxValue || now - m_SitesAt > 4096) RefreshSites(now);

            ScanAmbulances(now);
            foreach (var heli in new List<Entity>(m_Missions.Keys)) UpdateMission(heli, m_Missions[heli], now);
            CleanupHandedOver();
            if (now % 256 == 0) ClinicTransfers(now);
            RefreshCallUnits();
        }

        // ---------------------------------------------------------------- hospitals, sites, roads

        public enum Tier { None, Clinic, Trauma }

        /// <summary>Trauma = treats injuries and has at least half the beds of the city's largest hospital.</summary>
        public static Tier TierOf(int capacity, int maxCapacity, bool treatsInjuries) =>
            capacity <= 0 ? Tier.None : treatsInjuries && capacity * 2 >= maxCapacity ? Tier.Trauma : Tier.Clinic;

        // ponytail: prefab capacity only, building upgrades ignored.
        private Tier TierOf(Entity hospital, int maxCapacity)
        {
            var em = EntityManager;
            if (!em.HasComponent<PrefabRef>(hospital)) return Tier.None;
            var prefab = em.GetComponentData<PrefabRef>(hospital).m_Prefab;
            if (!em.HasComponent<HospitalData>(prefab)) return Tier.None;
            var d = em.GetComponentData<HospitalData>(prefab);
            return TierOf(d.m_PatientCapacity, maxCapacity, d.m_TreatInjuries);
        }

        private int MaxCapacity(NativeArray<Entity> hospitals)
        {
            int max = 0;
            foreach (var h in hospitals)
            {
                var prefab = EntityManager.GetComponentData<PrefabRef>(h).m_Prefab;
                if (EntityManager.HasComponent<HospitalData>(prefab)) max = math.max(max, EntityManager.GetComponentData<HospitalData>(prefab).m_PatientCapacity);
            }
            return max;
        }

        private static readonly string[] SiteWords = { "basketball", "football", "soccer", "sports", "tennis", "court", "pitch" };

        /// <summary>Open, car- and tree-free sports fields by prefab name.</summary>
        // ponytail: the prefab type stands in for "no cars, no trees"; nothing checks objects on the field at runtime.
        public static bool IsLandingName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            var n = prefabName.ToLowerInvariant();
            foreach (var w in SiteWords) if (n.Contains(w)) return true;
            return false;
        }

        private readonly HashSet<string> m_LoggedNames = new HashSet<string>();
        private readonly HashSet<Entity> m_Plain = new HashSet<Entity>(); // landing sites that are plain parks (open grass), not sports fields

        // ponytail: fixed landing-zone size; a CS2 medical heli needs roughly a 25-30 m circle.
        private const float ClearRadius = 10f, MaxSlope = 1.5f, Cell = 64f;

        /// <summary>Flat enough to land: highest minus lowest terrain sample within MaxSlope metres.</summary>
        public static bool IsFlat(float[] heights)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var h in heights) { lo = math.min(lo, h); hi = math.max(hi, h); }
            return hi - lo <= MaxSlope;
        }

        private static int2 CellOf(float3 p) => (int2)math.floor(p.xz / Cell);

        /// <summary>
        /// Parks whose centre is flat and free of trees and props. Sports fields first; plain parks (open grass)
        /// count too, ranked behind. Runs every 4096 frames: objects only change when the player builds.
        /// </summary>
        // ponytail: one pass over all trees/static props bucketed by a 64 m grid, O(objects + sites).
        // Cars parked on the field are not checked.
        private void RefreshSites(uint now)
        {
            m_SitesAt = now;
            m_Sites.Clear(); m_Plain.Clear();
            var em = EntityManager;
            var heights = m_Terrain.GetHeightData();
            var candidates = new List<(Entity site, float3 pos, bool sports)>();
            var grid = new Dictionary<int2, List<int>>();
            using (var parks = m_Parks.ToEntityArray(Allocator.Temp))
                foreach (var p in parks)
                {
                    var pos = em.GetComponentData<Transform>(p).m_Position;
                    var samples = new float[5];
                    int k = 0;
                    foreach (var off in new[] { float2.zero, new float2(ClearRadius, 0), new float2(-ClearRadius, 0), new float2(0, ClearRadius), new float2(0, -ClearRadius) })
                        samples[k++] = TerrainUtils.SampleHeight(ref heights, pos + new float3(off.x, 0, off.y));
                    if (!IsFlat(samples)) continue;
                    bool sports = m_Prefabs.TryGetPrefab(em.GetComponentData<PrefabRef>(p).m_Prefab, out PrefabBase prefab) && IsLandingName(prefab.name);
                    var c = CellOf(pos);
                    if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>();
                    list.Add(candidates.Count);
                    candidates.Add((p, pos, sports));
                }
            if (candidates.Count == 0) return;

            var blocked = new bool[candidates.Count];
            using (var objects = m_Clutter.ToComponentDataArray<Transform>(Allocator.Temp))
            using (var entities = m_Clutter.ToEntityArray(Allocator.Temp))
                for (int o = 0; o < objects.Length; o++)
                {
                    var t = objects[o];
                    // A sports field's own fittings (goals, line decals) belong to it; its trees still block.
                    var owner = em.HasComponent<Owner>(entities[o]) && !em.HasComponent<Tree>(entities[o]) ? em.GetComponentData<Owner>(entities[o]).m_Owner : Entity.Null;
                    var c = CellOf(t.m_Position);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue(c + new int2(dx, dz), out var list)) continue;
                            foreach (var i in list)
                                if (!blocked[i] && !(candidates[i].sports && owner == candidates[i].site) && math.distancesq(t.m_Position.xz, candidates[i].pos.xz) < ClearRadius * ClearRadius) blocked[i] = true;
                        }
                }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (blocked[i]) continue;
                var (site, pos, sports) = candidates[i];
                m_Sites.Add((site, pos));
                if (!sports) m_Plain.Add(site);
            }
            Log.Debug($"landing sites: {m_Sites.Count} usable ({m_Plain.Count} open grass) of {candidates.Count} flat parks");
        }

        private Entity NearestFreeSite(float3 at)
        {
            Entity best = Entity.Null; float bestDist = SiteRadius;
            foreach (var (site, pos) in m_Sites)
            {
                if (!EntityManager.Exists(site) || SiteTaken(site)) continue;
                float d = math.distance(at.xz, pos.xz) + (m_Plain.Contains(site) ? 300f : 0f); // sports fields preferred
                if (d < bestDist) { bestDist = d; best = site; }
            }
            return best;
        }

        private bool SiteTaken(Entity site)
        {
            foreach (var m in m_Missions.Values) if (m.Site == site && m.Phase != Phase.Home) return true;
            return false;
        }

        /// <summary>Road connection between two buildings (BFS over road edges). Detects islands.</summary>
        // ponytail: O(road edges) per new pair, cached until any edge is built or removed.
        private bool Reachable(Entity from, Entity to)
        {
            if (m_Reach.TryGetValue((from, to), out var ok)) return ok;
            var em = EntityManager;
            Entity start = em.HasComponent<Building>(from) ? em.GetComponentData<Building>(from).m_RoadEdge : Entity.Null;
            Entity goal = em.HasComponent<Building>(to) ? em.GetComponentData<Building>(to).m_RoadEdge : Entity.Null;
            ok = false;
            if (start != Entity.Null && goal != Entity.Null)
            {
                var seen = new HashSet<Entity> { start };
                var queue = new Queue<Entity>();
                queue.Enqueue(start);
                while (queue.Count > 0 && !ok)
                {
                    var edge = queue.Dequeue();
                    if (edge == goal) { ok = true; break; }
                    if (!em.HasComponent<Game.Net.Edge>(edge)) continue;
                    var e = em.GetComponentData<Game.Net.Edge>(edge);
                    foreach (var node in new[] { e.m_Start, e.m_End })
                    {
                        if (!em.HasBuffer<Game.Net.ConnectedEdge>(node)) continue;
                        var connected = em.GetBuffer<Game.Net.ConnectedEdge>(node);
                        for (int i = 0; i < connected.Length; i++)
                        {
                            var next = connected[i].m_Edge;
                            if (em.HasComponent<Game.Net.Road>(next) && seen.Add(next)) queue.Enqueue(next);
                        }
                    }
                }
            }
            m_Reach[(from, to)] = m_Reach[(to, from)] = ok;
            return ok;
        }

        /// <summary>Nearest trauma hospital with free beds (any if none has room). traumaOnly false: clinics too, trauma preferred.</summary>
        private Entity NearestTrauma(float3 at, Entity exclude = default, bool traumaOnly = true)
        {
            var em = EntityManager;
            using var hospitals = m_Hospitals.ToEntityArray(Allocator.Temp);
            int max = MaxCapacity(hospitals);
            Entity best = Entity.Null; float bestScore = float.MaxValue;
            foreach (var h in hospitals)
            {
                if (h == exclude) continue;
                var tier = TierOf(h, max);
                if (tier == Tier.None || traumaOnly && tier != Tier.Trauma) continue;
                float score = math.distance(at, em.GetComponentData<Transform>(h).m_Position);
                if (!HospitalSelectionSystem.CanAcceptPatients(em.GetComponentData<Hospital>(h))) score += 100000f;
                if (tier != Tier.Trauma) score += 50000f;
                if (score < bestScore) { bestScore = score; best = h; }
            }
            return best;
        }

        /// <summary>Nearest hospital with a medical-helicopter slot free.</summary>
        private Entity NearestHeliBase(float3 at)
        {
            var em = EntityManager;
            var busy = new Dictionary<Entity, int>();
            foreach (var m in m_Missions.Values) busy[m.Base] = (busy.TryGetValue(m.Base, out var n) ? n : 0) + 1;
            // Buildings that own a medical helicopter count too, whatever their type: some DLC/asset buildings
            // (e.g. Modern Architecture) keep their helicopter outside HospitalData.
            var owned = new Dictionary<Entity, int>();
            using (var helis = m_MedHelis.ToEntityArray(Allocator.Temp))
                foreach (var v in helis)
                {
                    if (m_Missions.ContainsKey(v)) continue; // our own spawns are counted as busy instead
                    var o = em.GetComponentData<Owner>(v).m_Owner;
                    if (!em.Exists(o) || !em.HasComponent<Transform>(o) || !em.HasComponent<PrefabRef>(o)) continue;
                    owned[o] = (owned.TryGetValue(o, out var n) ? n : 0) + 1;
                    // Still flying (e.g. home from a vanilla call): vanilla counts it against the base's capacity and
                    // disables any extra spawn at once, which looped (spawn, disabled, back, spawn again).
                    if (!em.HasComponent<Game.Vehicles.ParkedCar>(v)) busy[o] = (busy.TryGetValue(o, out var b) ? b : 0) + 1;
                    LogBaseOnce(o);
                }
            // All Hospital buildings, not only those with beds: a helicopter base may have none.
            var candidates = new HashSet<Entity>(owned.Keys);
            using (var hospitals = m_HeliBases.ToEntityArray(Allocator.Temp)) foreach (var h in hospitals) candidates.Add(h);
            Entity best = Entity.Null; float bestDist = float.MaxValue;
            m_BasesSeen = 0;
            foreach (var h in candidates)
            {
                // With upgrades (the helipad is often one), or the helicopters it actually owns.
                int slots = math.max(StationFleetSystem.HeliSlots(em, h), owned.TryGetValue(h, out var own) ? own : 0);
                if (slots > 0) m_BasesSeen++;
                if (slots <= 0 || (busy.TryGetValue(h, out var used) ? used : 0) >= slots) continue;
                float d = math.distance(at, em.GetComponentData<Transform>(h).m_Position);
                if (d < bestDist) { bestDist = d; best = h; }
            }
            return best;
        }

        private readonly HashSet<Entity> m_LoggedBases = new HashSet<Entity>();

        /// <summary>Once per building: what owns a medical helicopter, so unusual bases can be checked in the log.</summary>
        private void LogBaseOnce(Entity b)
        {
            if (!m_LoggedBases.Add(b)) return;
            var em = EntityManager;
            string name = m_Prefabs.TryGetPrefab(em.GetComponentData<PrefabRef>(b).m_Prefab, out PrefabBase p) ? p.name : "?";
            Log.Debug($"helicopter base found building={b.Index} prefab={name} is_hospital={em.HasComponent<Hospital>(b)} heli_slots={StationFleetSystem.HeliSlots(em, b)}");
        }

        private float3 Pos(Entity e) => EntityManager.GetComponentData<Transform>(e).m_Position;

        // ---------------------------------------------------------------- doctor dispatch

        /// <summary>
        /// Sends a heli doctor if one is free, a field is near the call and it beats the road doctor (or no NEF exists).
        /// Called by NefDispatchSystem before it requests an NEF, and by this system when NEFs are off.
        /// </summary>
        public bool TryDispatchDoctor(Entity patient, Entity location, string why)
        {
            if (!Mod.Setting.EnableHelicopter) return false;
            if (HeliPatients.Contains(patient)) return true;
            var em = EntityManager;
            if (location == Entity.Null || !em.HasComponent<Transform>(location)) return false;
            float3 at = Pos(location);
            var site = NearestFreeSite(at);
            var heliBase = site == Entity.Null ? Entity.Null : NearestHeliBase(at);
            if (heliBase == Entity.Null) return false;
            if (Mod.Setting.EnableNef)
            {
                // ponytail: straight-line ETAs; the NEF estimate assumes the nearest hospital has one free.
                float heli = math.distance(Pos(heliBase), Pos(site)) / HeliSpeed + math.distance(Pos(site).xz, at.xz) * RoadFactor / CarSpeed;
                float nef = NearestNefDistance(at) * RoadFactor / CarSpeed;
                if (heli >= nef) return false;
            }
            var heliEntity = Spawn(heliBase, site);
            if (heliEntity == Entity.Null) return false;
            var police = m_Responders.CreatePoliceCar(Pos(site), site);
            m_Missions[heliEntity] = new Mission { Kind = Kind.Scene, Base = heliBase, Site = site, Patient = patient, Location = location, Police = police, Since = m_Sim.frameIndex };
            HeliPatients.Add(patient);
            DoctorByPatient[patient] = Entity.Null; // requested; the crew waits for the doctor like for an NEF
            Log.Debug($"heli={heliEntity.Index} dispatched base={heliBase.Index} site={site.Index} patient={patient.Index}: {why}");
            return true;
        }

        private float NearestNefDistance(float3 at)
        {
            var em = EntityManager;
            using var hospitals = m_Hospitals.ToEntityArray(Allocator.Temp);
            float best = float.MaxValue;
            foreach (var h in hospitals)
            {
                var prefab = em.GetComponentData<PrefabRef>(h).m_Prefab;
                if (!em.HasComponent<HospitalData>(prefab)) continue;
                var d = em.GetComponentData<HospitalData>(prefab);
                if (d.m_PatientCapacity > 0 && d.m_AmbulanceCapacity > 0) best = math.min(best, math.distance(at, Pos(h)));
            }
            return best;
        }

        private Entity Spawn(Entity heliBase, Entity target)
        {
            var em = EntityManager;
            using var prefabs = m_HeliPrefabs.ToEntityArray(Allocator.Temp);
            if (prefabs.Length == 0) { Log.Warn("no medical helicopter prefab unlocked"); return Entity.Null; }
            // ponytail: first unlocked prefab, ignoring city theme (same as police in ResponderDispatchSystem).
            var prefab = prefabs[0];
            using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
            em.CreateEntity(em.GetComponentData<ObjectData>(prefab).m_Archetype, created);
            var e = created[0];
            em.SetComponentData(e, em.GetComponentData<Transform>(heliBase));
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new PseudoRandomSeed(ref m_Random));
            em.AddComponentData(e, new TripSource(heliBase));
            em.AddComponentData(e, default(Unspawned));
            em.AddComponentData(e, new Owner(heliBase));
            // No patient and no ServiceDispatch: vanilla flies to the target and would turn home there; we hold it first.
            em.SetComponentData(e, new Ambulance(Entity.Null, target, AmbulanceFlags.Dispatched | AmbulanceFlags.Critical));
            em.SetComponentData(e, new Target(target));
            return e;
        }

        // ---------------------------------------------------------------- per-call scan

        private void ScanAmbulances(uint now)
        {
            var em = EntityManager;
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);
            foreach (var v in vehicles)
            {
                if (m_Missions.ContainsKey(v) || NefDispatchSystem.IsNef(em, v) || em.HasComponent<Helicopter>(v)) continue;
                var a = em.GetComponentData<Ambulance>(v);
                var p = a.m_TargetPatient;
                if (p == Entity.Null || !m_Units.TryGetCode(v, out var code) || code == null) continue;

                // NEFs off: the heli covers doctor codes and on-scene requests itself.
                if (!Mod.Setting.EnableNef && !HeliPatients.Contains(p))
                {
                    if (code.Nef > 0 && m_Rolled.Add(p)) TryDispatchDoctor(p, a.m_TargetLocation, $"code {code} requires doctor");
                    else if (OnSceneCareSystem.OnScene(em, v) && m_Rolled.Add(p) && m_Random.NextFloat() < NefDispatchSystem.CallInChance(code.Level))
                        TryDispatchDoctor(p, a.m_TargetLocation, $"{m_Units.CallsignOf(v)} requested heli (code {code})");
                }

                // Transport decision, once the vanilla hospital path is known.
                if ((a.m_State & AmbulanceFlags.Transporting) != 0 && (a.m_State & (AmbulanceFlags.FindHospital | AmbulanceFlags.Returning)) == 0
                    && !m_Handover.ContainsKey(v) && m_Decided.Add(p) && NeedsFlight(v, a, code, out var dest, out var why))
                    StartHandover(v, p, a.m_TargetLocation, dest, why);
            }
        }

        private bool NeedsFlight(Entity rtw, Ambulance a, AmpdsCode code, out Entity dest, out string why)
        {
            var em = EntityManager;
            why = null;
            var hospital = em.GetComponentData<Target>(rtw).m_Target;
            float3 at = Pos(rtw);
            dest = NearestTrauma(at);
            if (dest == Entity.Null || dest == hospital) return false;
            using var hospitals = m_Hospitals.ToEntityArray(Allocator.Temp);
            bool critical = code.Level >= AmpdsLevel.Delta;
            if (critical && TierOf(hospital, MaxCapacity(hospitals)) == Tier.Clinic) { why = "critical patient, clinic has no trauma bay"; return true; }
            // ponytail: PathInformation is vanilla's route to the chosen hospital (m_Duration includes congestion,
            // distance is the floor if it is unset); straight line for the flight.
            float ground = float.MaxValue;
            if (em.HasComponent<PathInformation>(rtw))
            {
                var pi = em.GetComponentData<PathInformation>(rtw);
                ground = math.max(pi.m_Duration, pi.m_Distance / CarSpeed);
            }
            if (code.Level < AmpdsLevel.Charlie) return false;
            var site = NearestFreeSite(at);
            if (site == Entity.Null) return false;
            float flight = math.distance(at.xz, Pos(site).xz) * RoadFactor / CarSpeed + math.distance(Pos(site), Pos(dest)) / HeliSpeed;
            if (ground > (critical ? 1.5f : 2.5f) * flight) { why = $"ground transport too slow (distance/traffic) ground={ground:0}s flight={flight:0}s"; return true; }
            return false;
        }

        /// <summary>
        /// The vanilla hospital pathfind failed for this ambulance (no road to any hospital that takes the patient,
        /// e.g. an island). Called by AmbulanceReturnSystem; true = the heli takes over, skip the retry/borrow logic.
        /// </summary>
        public bool TryNoRouteHandover(Entity rtw, Entity patient)
        {
            if (!Mod.Setting.EnableHelicopter) return false;
            if (m_Handover.ContainsKey(rtw) || m_NoRoute.ContainsKey(rtw)) return true;
            var em = EntityManager;
            if (patient == Entity.Null || !em.HasComponent<Transform>(rtw)) return false;
            float3 at = Pos(rtw);
            bool heliExists = false;
            foreach (var m in m_Missions.Values) heliExists |= m.Kind == Kind.Scene && m.Patient == patient && m.Phase != Phase.Home;
            if (NearestTrauma(at) == Entity.Null || !heliExists && (NearestFreeSite(at) == Entity.Null || NearestHeliBase(at) == Entity.Null)) return false;
            m_NoRoute[rtw] = patient; // started in OnUpdate: no structural changes inside the caller's loop
            return true;
        }

        private readonly Dictionary<Entity, Entity> m_NoRoute = new Dictionary<Entity, Entity>(); // ambulance -> patient

        private void StartNoRouteHandovers()
        {
            var em = EntityManager;
            foreach (var kv in m_NoRoute)
            {
                var rtw = kv.Key;
                if (!em.Exists(rtw) || em.HasComponent<Deleted>(rtw)) continue;
                var a = em.GetComponentData<Ambulance>(rtw);
                var dest = NearestTrauma(Pos(rtw));
                if (dest == Entity.Null || !StartHandover(rtw, kv.Value, a.m_TargetLocation, dest, "no road connection to any hospital")) continue;
                // Back to a plain transport: it was Returning|Transporting (hospital search failed).
                a = em.GetComponentData<Ambulance>(rtw);
                a.m_State &= ~AmbulanceFlags.Returning;
                em.SetComponentData(rtw, a);
            }
            m_NoRoute.Clear();
        }

        private bool StartHandover(Entity rtw, Entity patient, Entity location, Entity dest, string why)
        {
            var em = EntityManager;
            Mission mission = null; Entity heli = Entity.Null;
            foreach (var kv in m_Missions) if (kv.Value.Kind == Kind.Scene && kv.Value.Patient == patient && kv.Value.Phase != Phase.Home) { heli = kv.Key; mission = kv.Value; }
            if (mission == null)
            {
                var at = Pos(rtw);
                var site = NearestFreeSite(at);
                var heliBase = site == Entity.Null ? Entity.Null : NearestHeliBase(at);
                if (heliBase == Entity.Null) return false;
                heli = Spawn(heliBase, site);
                if (heli == Entity.Null) return false;
                mission = new Mission { Kind = Kind.Scene, Base = heliBase, Site = site, Patient = patient, Location = location, Since = m_Sim.frameIndex };
                m_Missions[heli] = mission;
                Log.Debug($"heli={heli.Index} dispatched for patient flight base={heliBase.Index} site={site.Index}");
            }
            mission.Carry = true; mission.Rtw = rtw; mission.Dest = dest;
            m_Handover[rtw] = heli;
            Retarget(rtw, mission.Site);
            Log.Debug($"unit={m_Units.CallsignOf(rtw)} bringing patient={patient.Index} to heli site={mission.Site.Index}: {why}");
            return true;
        }

        /// <summary>Every frame: a handover ambulance at the field is stopped before vanilla's arrival handling unloads there.</summary>
        private void HoldArrivingHandovers()
        {
            var em = EntityManager;
            foreach (var kv in m_Handover)
            {
                var rtw = kv.Key;
                if (!em.Exists(rtw) || em.HasComponent<TripSource>(rtw) || !m_Missions.TryGetValue(kv.Value, out var m)) continue;
                if (Arrived(rtw, m.Site, out _)) ResponderDispatchSystem.HoldVehicle(em, rtw, Cap());
            }
        }

        // ---------------------------------------------------------------- mission state machine

        private uint Cap() => (uint)((Mod.Setting.NefMaxWaitMinutes + Mod.Setting.OnSceneMinutes + 120) * OnSceneCareSystem.FramesPerGameMinute);

        private void UpdateMission(Entity heli, Mission m, uint now)
        {
            var em = EntityManager;
            if (!em.Exists(heli) || em.HasComponent<Deleted>(heli)) { End(heli, m, "vehicle gone"); return; }
            if (m.Phase != Phase.Home && now - m.Since > Cap()) { SendHome(heli, m, "timeout"); return; }
            var a = em.GetComponentData<Ambulance>(heli);

            switch (m.Phase)
            {
                case Phase.FlyIn:
                    var target = m.Kind == Kind.Transfer ? m.Clinic : m.Site;
                    if (!Arrived(heli, target, out _)) break;
                    ResponderDispatchSystem.HoldVehicle(em, heli, Cap());
                    if (m.Kind == Kind.Scene) Land(heli, m.Site);
                    m.Phase = Phase.OnSite; m.OnSiteSince = now;
                    Log.Debug($"heli={heli.Index} landed at={target.Index}");
                    break;

                case Phase.OnSite:
                    if (m.Kind == Kind.Transfer)
                    {
                        if (now - m.OnSiteSince < OnSceneCareSystem.FramesPerGameMinute) break; // patient boarding
                        Fly(heli, m, m.Dest);
                        break;
                    }
                    UpdateDoctor(m);
                    bool patientDone = !m.Carry || HandOver(heli, m);
                    bool doctorDone = m.Police == Entity.Null || m.DoctorBack;
                    if (!doctorDone || !patientDone) break;
                    if (m.Carry && a.m_TargetPatient != Entity.Null) Fly(heli, m, m.Dest);
                    else if (!m.Carry || !em.Exists(m.Patient)) SendHome(heli, m, "mission over");
                    break;

                case Phase.Flying:
                    // Vanilla UnloadPatients at the hospital, then Returning.
                    if ((a.m_State & AmbulanceFlags.Transporting) != 0) break;
                    if (m.Kind == Kind.Transfer) MovePatientRecord(m);
                    m.Phase = Phase.Home;
                    break;

                case Phase.Home:
                    a.m_State |= AmbulanceFlags.Disabled; // never a regular ambulance on the way back
                    em.SetComponentData(heli, a);
                    if (em.HasComponent<ParkedCar>(heli) || Arrived(heli, m.Base, out _)) { em.AddComponent<Deleted>(heli); End(heli, m, "back at base"); }
                    break;
            }
        }

        /// <summary>Doctor shuttle: police car field -> patient -> field.</summary>
        private void UpdateDoctor(Mission m)
        {
            var em = EntityManager;
            var car = m.Police;
            if (car == Entity.Null || m.DoctorBack) return;
            if (!em.Exists(car) || em.HasComponent<Deleted>(car)) { m.DoctorBack = true; m.Police = Entity.Null; DoctorByPatient.Remove(m.Patient); return; }
            Lights(car);
            if (!m.DoctorOut)
            {
                if (!Arrived(car, m.Site, out _)) return;
                // Doctor boards at the field: on to the patient.
                ResponderDispatchSystem.ReleaseVehicle(em, car);
                Retarget(car, m.Location);
                DoctorByPatient[m.Patient] = car;
                m.DoctorOut = true;
                Log.Debug($"police={car.Index} picked up doctor at site, driving to patient={m.Patient.Index}");
                return;
            }
            bool atPatient = em.Exists(m.Location) && Arrived(car, m.Location, out _);
            bool careOver = !em.Exists(m.Patient) || !World.GetExistingSystemManaged<OnSceneCareSystem>().Awaited(m.Patient);
            var target = em.GetComponentData<Target>(car).m_Target;
            if (target == m.Location)
            {
                if (atPatient && !careOver) { if (!em.HasComponent<TripSource>(car)) ResponderDispatchSystem.HoldVehicle(em, car, Cap()); return; }
                if (!careOver) return;
                // Patient handed to the ambulance: doctor back to the field.
                if (em.HasComponent<TripSource>(car)) ResponderDispatchSystem.ReleaseVehicle(em, car);
                DoctorByPatient.Remove(m.Patient);
                Retarget(car, m.Site);
                Log.Debug($"police={car.Index} driving doctor back to heli");
                return;
            }
            if (!Arrived(car, m.Site, out _)) return;
            m.DoctorBack = true;
            SendPoliceHome(car);
            m.Police = Entity.Null;
            Log.Debug($"doctor back at heli site={m.Site.Index}");
        }

        /// <summary>Ambulance held at the field next to the heli: move the patient over and send the ambulance home.</summary>
        private bool HandOver(Entity heli, Mission m)
        {
            var em = EntityManager;
            if (em.GetComponentData<Ambulance>(heli).m_TargetPatient != Entity.Null) return true; // done
            var rtw = m.Rtw;
            if (rtw == Entity.Null || !em.Exists(rtw)) { m.Carry = false; return true; }
            if (!em.HasComponent<TripSource>(rtw)) return false; // still driving to the field
            var from = em.GetBuffer<Passenger>(rtw);
            var to = em.GetBuffer<Passenger>(heli);
            for (int i = from.Length - 1; i >= 0; i--)
            {
                var creature = from[i].m_Passenger;
                to.Add(new Passenger(creature));
                from.RemoveAt(i);
                if (em.HasComponent<CurrentVehicle>(creature))
                {
                    var cv = em.GetComponentData<CurrentVehicle>(creature);
                    cv.m_Vehicle = heli;
                    em.SetComponentData(creature, cv);
                }
            }
            var h = em.GetComponentData<Ambulance>(heli);
            h.m_TargetPatient = m.Patient;
            em.SetComponentData(heli, h);

            // Ambulance: home, and unavailable until parked there (whatever AvailableOnlyAtDepot says).
            var a = em.GetComponentData<Ambulance>(rtw);
            a.m_State = (a.m_State & ~(AmbulanceFlags.Transporting | AmbulanceFlags.Critical | AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget)) | AmbulanceFlags.Returning | AmbulanceFlags.Disabled;
            a.m_TargetPatient = Entity.Null;
            a.m_TargetLocation = Entity.Null;
            em.SetComponentData(rtw, a);
            ResponderDispatchSystem.ReleaseVehicle(em, rtw);
            Retarget(rtw, em.GetComponentData<Owner>(rtw).m_Owner);
            m_Handover.Remove(rtw);
            HandedOver.Add(rtw);
            Log.Debug($"unit={m_Units.CallsignOf(rtw)} handed patient={m.Patient.Index} to heli={heli.Index}, returning");
            return true;
        }

        private void Fly(Entity heli, Mission m, Entity dest)
        {
            var em = EntityManager;
            var a = em.GetComponentData<Ambulance>(heli);
            a.m_State = (a.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Returning | AmbulanceFlags.Disabled)) | AmbulanceFlags.Transporting | AmbulanceFlags.Critical;
            em.SetComponentData(heli, a);
            ResponderDispatchSystem.ReleaseVehicle(em, heli);
            Retarget(heli, dest);
            m.Phase = Phase.Flying;
            if (m.Rtw != Entity.Null && HandedOver.Contains(m.Rtw)) m_FreeAt[m.Rtw] = m_Sim.frameIndex + (uint)OnSceneCareSystem.FramesPerGameMinute;
            Log.Debug($"heli={heli.Index} flying {(m.Kind == Kind.Transfer ? "transfer" : $"patient={m.Patient.Index}")} to hospital={dest.Index}");
        }

        private void SendHome(Entity heli, Mission m, string why)
        {
            var em = EntityManager;
            if (m.Police != Entity.Null && em.Exists(m.Police)) { if (em.HasComponent<TripSource>(m.Police)) ResponderDispatchSystem.ReleaseVehicle(em, m.Police); SendPoliceHome(m.Police); }
            m.Police = Entity.Null;
            DoctorByPatient.Remove(m.Patient);
            foreach (var kv in new List<KeyValuePair<Entity, Entity>>(m_Handover))
                if (kv.Value == heli) { m_Handover.Remove(kv.Key); if (em.Exists(kv.Key) && em.HasComponent<TripSource>(kv.Key)) ResponderDispatchSystem.ReleaseVehicle(em, kv.Key); }
            if (!em.Exists(heli)) { End(heli, m, why); return; }
            var a = em.GetComponentData<Ambulance>(heli);
            a.m_State = (a.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Critical | AmbulanceFlags.Transporting)) | AmbulanceFlags.Returning | AmbulanceFlags.Disabled;
            a.m_TargetPatient = Entity.Null;
            a.m_TargetLocation = Entity.Null;
            em.SetComponentData(heli, a);
            if (em.HasComponent<TripSource>(heli)) ResponderDispatchSystem.ReleaseVehicle(em, heli);
            Retarget(heli, m.Base);
            m.Phase = Phase.Home;
            Log.Debug($"heli={heli.Index} returning: {why}");
        }

        private void End(Entity heli, Mission m, string why)
        {
            m_Missions.Remove(heli);
            HeliPatients.Remove(m.Patient);
            DoctorByPatient.Remove(m.Patient);
            Log.Debug($"heli={heli.Index} mission ended: {why}");
        }

        private void SendPoliceHome(Entity car)
        {
            var em = EntityManager;
            if (!em.Exists(car) || em.HasComponent<Deleted>(car)) return;
            // Station gone (bulldozed) or car broken: leave it to vanilla. Never Deleted on the road (native crash).
            var owner = em.HasComponent<Owner>(car) ? em.GetComponentData<Owner>(car).m_Owner : Entity.Null;
            if (!em.HasComponent<Game.Vehicles.PoliceCar>(car) || !em.HasComponent<Car>(car) || !em.Exists(owner) || em.HasComponent<Deleted>(owner)) return;
            var p = em.GetComponentData<Game.Vehicles.PoliceCar>(car);
            p.m_State |= PoliceCarFlags.Returning;
            em.SetComponentData(car, p);
            var c = em.GetComponentData<Car>(car);
            c.m_Flags &= ~CarFlags.Emergency;
            em.SetComponentData(car, c);
            Retarget(car, owner);
        }

        private void CleanupHandedOver()
        {
            var em = EntityManager;
            HandedOver.RemoveWhere(e => !em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<ParkedCar>(e));
            // Crew available again shortly after the heli has left with their patient.
            foreach (var kv in new List<KeyValuePair<Entity, uint>>(m_FreeAt))
            {
                if (m_Sim.frameIndex < kv.Value) continue;
                m_FreeAt.Remove(kv.Key);
                if (!HandedOver.Remove(kv.Key) || !em.Exists(kv.Key) || Mod.Setting.AvailableOnlyAtDepot) continue;
                var a = em.GetComponentData<Ambulance>(kv.Key);
                a.m_State &= ~AmbulanceFlags.Disabled;
                em.SetComponentData(kv.Key, a);
                Log.Debug($"unit={m_Units.CallsignOf(kv.Key)} available again after heli departure");
            }
        }

        private readonly Dictionary<Entity, uint> m_FreeAt = new Dictionary<Entity, uint>(); // handed-over ambulance -> frame it becomes available

        private readonly HashSet<Entity> m_Recalled = new HashSet<Entity>();
        private bool m_Restore;

        /// <summary>
        /// After a load (missions are not saved): an ambulance that was driving its patient to a landing field
        /// still targets the park. It searches a hospital by road again instead (vanilla FindHospital, same as
        /// AmbulanceReturnSystem's retry). Runs also with the feature off, for saves made while it was on.
        /// </summary>
        private void RedirectOrphanedHandovers()
        {
            var em = EntityManager;
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);
            foreach (var v in vehicles)
            {
                if (em.HasComponent<Helicopter>(v) || !em.HasComponent<Target>(v) || !em.HasComponent<PathOwner>(v)) continue;
                var a = em.GetComponentData<Ambulance>(v);
                var target = em.GetComponentData<Target>(v).m_Target;
                if ((a.m_State & AmbulanceFlags.Transporting) == 0 || !em.HasComponent<Game.Buildings.Park>(target)) continue;
                if (em.HasComponent<TripSource>(v)) ResponderDispatchSystem.ReleaseVehicle(em, v); // was held at the field
                a.m_State = (a.m_State & ~AmbulanceFlags.Returning) | AmbulanceFlags.FindHospital;
                em.SetComponentData(v, a);
                Retarget(v, target); // keep a valid target until the hospital path arrives (see AmbulanceReturnSystem)
                Log.Debug($"unit={m_Units.CallsignOf(v)} heli handover lost on load, driving patient to hospital by road");
            }
        }

        /// <summary>
        /// Backstop, also with the feature off: a medical heli vanilla dispatched to an ordinary call (outside our
        /// missions) drops it and flies home. The flag hiding in HospitalSelectionSystem prevents most of these.
        /// </summary>
        private void RecallVanillaHelis()
        {
            var em = EntityManager;
            using var helis = m_MedHelis.ToEntityArray(Allocator.Temp);
            foreach (var h in helis)
            {
                if (m_Missions.ContainsKey(h) || em.HasComponent<ParkedCar>(h)) continue;
                var a = em.GetComponentData<Ambulance>(h);
                if (a.m_TargetPatient == Entity.Null && (a.m_State & AmbulanceFlags.Dispatched) == 0) continue;
                if ((a.m_State & AmbulanceFlags.Transporting) != 0) continue; // patient on board: let vanilla deliver
                var owner = em.GetComponentData<Owner>(h).m_Owner;
                if (!em.Exists(owner) || !em.HasComponent<PathOwner>(h)) continue;
                a.m_State = (a.m_State & ~(AmbulanceFlags.Dispatched | AmbulanceFlags.AtTarget | AmbulanceFlags.Critical)) | AmbulanceFlags.Returning | AmbulanceFlags.Disabled;
                a.m_TargetPatient = Entity.Null;
                a.m_TargetLocation = Entity.Null;
                em.SetComponentData(h, a);
                if (em.HasBuffer<ServiceDispatch>(h)) em.GetBuffer<ServiceDispatch>(h).Clear();
                Retarget(h, owner);
                if (m_Recalled.Add(h)) Log.Debug($"heli={h.Index} recalled from ordinary call");
            }
        }

        // ---------------------------------------------------------------- clinic transfers

        private void ClinicTransfers(uint now)
        {
            var em = EntityManager;
            using var hospitals = m_Hospitals.ToEntityArray(Allocator.Temp);
            int max = MaxCapacity(hospitals);
            var busy = new HashSet<Entity>();
            foreach (var m in m_Missions.Values) if (m.Kind == Kind.Transfer && m.Phase != Phase.Home) busy.Add(m.Clinic);
            foreach (var clinic in hospitals)
            {
                if (busy.Contains(clinic) || TierOf(clinic, max) != Tier.Clinic) continue;
                bool island = true;
                foreach (var h in hospitals) if (TierOf(h, max) == Tier.Trauma && Reachable(clinic, h)) { island = false; break; }
                if (!island) continue;

                var patients = em.GetBuffer<Patient>(clinic);
                int capacity = em.GetComponentData<HospitalData>(em.GetComponentData<PrefabRef>(clinic).m_Prefab).m_PatientCapacity;
                Entity pick = Entity.Null; int lowest = int.MaxValue;
                for (int i = 0; i < patients.Length; i++)
                {
                    var c = patients[i].m_Patient;
                    if (!em.HasComponent<Citizen>(c)) continue;
                    int health = em.GetComponentData<Citizen>(c).m_Health;
                    if (health < lowest) { lowest = health; pick = c; }
                }
                // ponytail: health < 25 = critical; tune if transfers feel too frequent.
                bool full = patients.Length >= capacity;
                if (pick == Entity.Null || (lowest >= 25 && !full)) continue;
                StartTransfer(clinic, pick, NearestTrauma(Pos(clinic), clinic), $"island clinic, {(full ? "full" : $"health={lowest}")}");
            }
        }

        /// <summary>Says whether no base was found at all (detection) or all are busy with RTH missions.</summary>
        private string NoBaseMessage() => m_BasesSeen == 0
            ? Mod.T("Kein Spital mit Rettungshelikopter erkannt (auch nicht über Ausbauten)", "No hospital with a medical helicopter found (upgrades included)")
            : Mod.T($"Alle {m_BasesSeen} Helikopter-Basen sind im Einsatz", $"All {m_BasesSeen} helicopter bases are on missions");

        /// <summary>Starts a transfer flight; null on success, else why not (shown by the debug button).</summary>
        private string StartTransfer(Entity clinic, Entity patient, Entity dest, string why)
        {
            if (dest == Entity.Null) return Mod.T("Kein anderes Spital mit Betten", "No other hospital with beds");
            var heliBase = NearestHeliBase(Pos(clinic));
            if (heliBase == Entity.Null) return NoBaseMessage();
            var heli = Spawn(heliBase, clinic);
            if (heli == Entity.Null) return Mod.T("Kein Rettungshelikopter freigeschaltet", "No medical helicopter unlocked");
            m_Missions[heli] = new Mission { Kind = Kind.Transfer, Base = heliBase, Clinic = clinic, Dest = dest, Patient = patient, Since = m_Sim.frameIndex };
            Log.Debug($"heli={heli.Index} transfer patient={patient.Index} from clinic={clinic.Index} to hospital={dest.Index} base={heliBase.Index}: {why}");
            return null;
        }

        /// <summary>
        /// Debug (hospital panel): sets one patient of this hospital to critical health and flies them out now,
        /// skipping the island and clinic-size checks so it can be tested on any map. Returns a status line.
        /// </summary>
        public string DebugCriticalTransfer(Entity hospital)
        {
            var em = EntityManager;
            if (!Mod.Setting.EnableHelicopter) return Mod.T("Rettungshelikopter ist ausgeschaltet", "Rescue helicopter is off");
            if (!em.HasBuffer<Patient>(hospital)) return Mod.T("Kein Spital", "Not a hospital");
            foreach (var m in m_Missions.Values)
                if (m.Kind == Kind.Transfer && m.Clinic == hospital && m.Phase != Phase.Home) return Mod.T("Verlegung läuft bereits", "Transfer already under way");
            var patients = em.GetBuffer<Patient>(hospital);
            Entity pick = Entity.Null;
            for (int i = 0; i < patients.Length && pick == Entity.Null; i++)
                if (em.HasComponent<Citizen>(patients[i].m_Patient)) pick = patients[i].m_Patient;
            if (pick == Entity.Null) return Mod.T("Keine Patienten im Spital", "No patients in this hospital");
            var dest = NearestTrauma(Pos(hospital), hospital);
            if (dest == Entity.Null) dest = NearestTrauma(Pos(hospital), hospital, traumaOnly: false); // testing with one big hospital
            // Check before touching the patient, so a failed test leaves them as they were.
            if (dest == Entity.Null) return Mod.T("Kein anderes Spital mit Betten", "No other hospital with beds");
            if (NearestHeliBase(Pos(hospital)) == Entity.Null) return NoBaseMessage();
            var c = em.GetComponentData<Citizen>(pick);
            c.m_Health = 10;
            em.SetComponentData(pick, c);
            return StartTransfer(hospital, pick, dest, "debug: patient critical") ?? Mod.T($"RTH alarmiert für Patient {pick.Index}", $"Helicopter dispatched for patient {pick.Index}");
        }

        /// <summary>Delivered: the patient now lies in the destination hospital.</summary>
        // ponytail: the record moves on arrival; during the flight the patient is still listed at the clinic.
        private void MovePatientRecord(Mission m)
        {
            var em = EntityManager;
            if (!em.Exists(m.Patient) || !em.HasBuffer<Patient>(m.Clinic) || !em.HasBuffer<Patient>(m.Dest)) return;
            var from = em.GetBuffer<Patient>(m.Clinic);
            bool found = false;
            for (int i = from.Length - 1; i >= 0; i--) if (from[i].m_Patient == m.Patient) { from.RemoveAt(i); found = true; }
            if (!found) { Log.Debug($"transfer cancelled, patient={m.Patient.Index} no longer in clinic={m.Clinic.Index}"); return; }
            em.GetBuffer<Patient>(m.Dest).Add(new Patient(m.Patient));
            if (em.HasComponent<CurrentBuilding>(m.Patient)) em.SetComponentData(m.Patient, new CurrentBuilding(m.Dest));
            Log.Debug($"transferred patient={m.Patient.Index} from clinic={m.Clinic.Index} to hospital={m.Dest.Index}");
        }

        // ---------------------------------------------------------------- vehicle helpers

        /// <summary>Stopped (or nearly) within NearSite of the target building.</summary>
        // ponytail: position + speed only; works for cars and helicopters without reading their lane types.
        private bool Arrived(Entity v, Entity target, out float dist)
        {
            var em = EntityManager;
            dist = float.MaxValue;
            if (!em.Exists(target) || !em.HasComponent<Transform>(target) || !em.HasComponent<Transform>(v)) return false;
            if (em.HasComponent<Unspawned>(v)) return false;
            dist = math.distance(Pos(v).xz, Pos(target).xz);
            if (dist > NearSite) return false;
            return !em.HasComponent<Moving>(v) || math.length(em.GetComponentData<Moving>(v).m_Velocity) < 2f;
        }

        /// <summary>Puts a held heli on the field centre (vanilla may have set it down beside the park).</summary>
        private void Land(Entity heli, Entity site)
        {
            var em = EntityManager;
            var t = em.GetComponentData<Transform>(heli);
            t.m_Position = Pos(site);
            em.SetComponentData(heli, t);
            if (!em.HasComponent<Updated>(heli)) em.AddComponent<Updated>(heli);
        }

        private void Lights(Entity car)
        {
            var em = EntityManager;
            var c = em.GetComponentData<Car>(car);
            if ((c.m_Flags & CarFlags.Emergency) != 0) return;
            c.m_Flags |= CarFlags.Emergency | CarFlags.StayOnRoad;
            em.SetComponentData(car, c);
            if (!em.HasComponent<EffectsUpdated>(car)) em.AddComponent<EffectsUpdated>(car);
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

        // ---------------------------------------------------------------- self-check

        /// <summary>Runs at load; logs an error if tiering or the landing-site filter regresses.</summary>
        public static void SelfCheck()
        {
            void Expect(bool ok, string what) { if (!ok) Log.Error("Helicopter self-check failed: " + what); }
            Expect(TierOf(500, 500, true) == Tier.Trauma, "biggest hospital is trauma");
            Expect(TierOf(100, 500, true) == Tier.Clinic, "small hospital is clinic");
            Expect(TierOf(400, 500, false) == Tier.Clinic, "no injury care is clinic");
            Expect(TierOf(0, 500, true) == Tier.None, "depot is no hospital");
            Expect(IsLandingName("BasketballCourt01") && IsLandingName("Soccer Field") && IsLandingName("SportsPark"), "sports fields land");
            Expect(IsFlat(new[] { 10f, 10.5f, 11f, 10f, 10.2f }) && !IsFlat(new[] { 10f, 12f, 10f, 10f, 10f }), "landing slope limit");
            Expect(!IsLandingName("CityPark01") && !IsLandingName("ParkingLot") && !IsLandingName("TransportPark") && !IsLandingName(null), "plain parks/lots do not");
        }
    }
}
