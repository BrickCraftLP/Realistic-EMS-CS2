using Colossal.UI.Binding;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;
using Hospital = Game.Buildings.Hospital;
using PoliceCar = Game.Vehicles.PoliceCar;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Feeds the "Leitstelle" popup (UI/src/mods/ems-panel.tsx). C# builds one JSON snapshot, the UI only renders it.
    /// Bindings: realisticEMS.open (bool), realisticEMS.state (snapshot), realisticEMS.roads (map geometry), triggers realisticEMS.toggle, realisticEMS.debugCall(kind, count).
    /// </summary>
    public partial class DispatchUISystem : UISystemBase
    {
        private const string Group = "realisticEMS";
        private ValueBinding<bool> m_Open;
        private RawValueBinding m_State, m_RoadsBinding, m_Settings;
        private EntityQuery m_Ambulances, m_Hospitals, m_Roads, m_Police, m_Healthy;
        private int m_RoadCount = -1;
        private NameSystem m_NameSystem;
        private UnitNamingSystem m_Units;
        private SimulationSystem m_Sim;
        private int m_Frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Hospitals = GetEntityQuery(ComponentType.ReadOnly<Hospital>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Police = GetEntityQuery(ComponentType.ReadOnly<PoliceCar>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            AddBinding(m_Open = new ValueBinding<bool>(Group, "open", false));
            m_Roads = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Curve>(), ComponentType.ReadOnly<Game.Net.Road>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            AddBinding(m_State = new RawValueBinding(Group, "state", Write));
            AddBinding(m_RoadsBinding = new RawValueBinding(Group, "roads", WriteRoads));
            AddBinding(new TriggerBinding(Group, "toggle", () => { m_Open.Update(!m_Open.value); m_State.Update(); m_RoadCount = -1; }));
            m_Healthy = GetEntityQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<CurrentBuilding>(), ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            AddBinding(new TriggerBinding<string, int>(Group, "debugCall", DebugCall));
            AddBinding(m_Settings = new RawValueBinding(Group, "settings", WriteSettings));
            AddBinding(new TriggerBinding<string, int>(Group, "setSetting", SetSetting));
            VehicleStateSection.Install(World);
        }

        /// <summary>
        /// Debug tab: makes random citizens inside a building sick or injured, the same event vanilla's health system raises.
        /// kind: "sick", "injured", "critical", "door" (locked door), a code like "09E01" or a protocol like "25" (random code of it).
        /// </summary>
        private void DebugCall(string kind, int count)
        {
            var flags = (kind == "injured" ? HealthProblemFlags.Injured : HealthProblemFlags.Sick) | HealthProblemFlags.RequireTransport;
            // Determinant 00 is the "Aufwertung" (override) row, not a real call.
            var codes = kind.Length == 5 ? Ampds.Codes.Where(c => c.ToString().Replace("-", "") == kind).ToList()
                : kind.Length == 2 && int.TryParse(kind, out var protocol) ? Ampds.Codes.Where(c => c.Protocol == protocol && c.Determinant != 0).ToList()
                : null;
            if (codes != null && codes.Count == 0) { Mod.log.Warn($"Debug: no Einsatzcode matches {kind}"); return; }
            using var citizens = m_Healthy.ToEntityArray(Allocator.Temp);
            var rnd = new System.Random();
            for (int i = 0; i < count && citizens.Length > 0; i++)
            {
                var target = citizens[rnd.Next(citizens.Length)];
                if (codes != null) Ampds.Forced[target] = codes[rnd.Next(codes.Count)];
                if (kind == "door") ResponderDispatchSystem.ForceLocked.Add(target);
                if (kind == "critical") // low health -> Classify picks Delta/Echo codes
                {
                    var c = EntityManager.GetComponentData<Citizen>(target);
                    c.m_Health = 5;
                    EntityManager.SetComponentData(target, c);
                }
                var e = EntityManager.CreateEntity(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Game.Events.AddHealthProblem>());
                EntityManager.SetComponentData(e, new Game.Events.AddHealthProblem { m_Event = Entity.Null, m_Target = target, m_Flags = flags });
            }
            Mod.log.Info($"Debug: {count} x {kind} call(s) raised from {citizens.Length} candidates");
        }

        /// <summary>Settings page: every writable bool/int of Setting, with slider bounds. Labels come from the locale IDs.</summary>
        private static IEnumerable<System.Reflection.PropertyInfo> EditableSettings() => typeof(Setting).GetProperties()
            .Where(p => p.CanWrite && p.DeclaringType == typeof(Setting) && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int)));

        private void WriteSettings(IJsonWriter w)
        {
            var s = Mod.Setting;
            var props = EditableSettings().ToList();
            w.ArrayBegin((uint)props.Count);
            foreach (var p in props)
            {
                var slider = (Game.Settings.SettingsUISliderAttribute)System.Attribute.GetCustomAttribute(p, typeof(Game.Settings.SettingsUISliderAttribute));
                w.TypeBegin("Setting");
                w.PropertyName("name"); w.Write(p.Name);
                w.PropertyName("label"); w.Write(s.GetOptionLabelLocaleID(p.Name));
                w.PropertyName("desc"); w.Write(s.GetOptionDescLocaleID(p.Name));
                w.PropertyName("bool"); w.Write(p.PropertyType == typeof(bool));
                w.PropertyName("value"); w.Write(p.PropertyType == typeof(bool) ? ((bool)p.GetValue(s) ? 1 : 0) : (int)p.GetValue(s));
                w.PropertyName("min"); w.Write(slider != null ? (int)slider.min : 0);
                w.PropertyName("max"); w.Write(slider != null ? (int)slider.max : 1);
                w.PropertyName("step"); w.Write(slider != null ? (int)slider.step : 1);
                w.TypeEnd();
            }
            w.ArrayEnd();
        }

        private void SetSetting(string name, int value)
        {
            var p = EditableSettings().FirstOrDefault(x => x.Name == name);
            if (p == null) { Mod.log.Warn($"Settings: unknown option {name}"); return; }
            var slider = (Game.Settings.SettingsUISliderAttribute)System.Attribute.GetCustomAttribute(p, typeof(Game.Settings.SettingsUISliderAttribute));
            if (slider != null) value = math.clamp(value, (int)slider.min, (int)slider.max);
            p.SetValue(Mod.Setting, p.PropertyType == typeof(bool) ? (object)(value != 0) : value);
            Mod.Setting.ApplyAndSave();
            m_Settings.Update();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            uint now = m_Sim.frameIndex;
            if (now - m_LastTrack >= TrackFrames) { m_LastTrack = now; Track(now); }
            if (now - m_LastSample >= SampleFrames) { m_LastSample = now; Sample(); }
            if (!m_Open.value || ++m_Frame % 30 != 0) return;
            m_State.Update();
            // ponytail: road count as change signal; a moved road with same count is missed until the panel is reopened.
            int roads = m_Roads.CalculateEntityCount();
            if (roads != m_RoadCount) { m_RoadCount = roads; m_RoadsBinding.Update(); }
        }

        /// <summary>Flat int array, 8 per road edge: bezier a, b, c, d as x, z (meters). Drawn by KarteTab.</summary>
        private void WriteRoads(IJsonWriter w)
        {
            using var curves = m_Roads.ToComponentDataArray<Game.Net.Curve>(Allocator.Temp);
            w.ArrayBegin((uint)curves.Length * 8);
            foreach (var c in curves)
            {
                var b = c.m_Bezier;
                w.Write((int)b.a.x); w.Write((int)b.a.z); w.Write((int)b.b.x); w.Write((int)b.b.z);
                w.Write((int)b.c.x); w.Write((int)b.c.z); w.Write((int)b.d.x); w.Write((int)b.d.z);
            }
            w.ArrayEnd();
        }

        private void Pos(IJsonWriter w, Entity e)
        {
            var p = EntityManager.HasComponent<Transform>(e) ? EntityManager.GetComponentData<Transform>(e).m_Position : default;
            Prop(w, "x", (int)p.x); Prop(w, "z", (int)p.z);
        }

        private string Name(Entity e) => e == Entity.Null || !EntityManager.Exists(e) ? "-" : m_NameSystem.GetRenderedLabelName(e);

        // AtTarget alone is not enough: the on-scene hold clears it, so also ask OnSceneCareSystem.OnScene.
        internal static string Status(EntityManager em, Entity e, Ambulance a, bool parked)
        {
            if (parked) return (a.m_State & AmbulanceFlags.Disabled) != 0 ? "ausser Dienst" : "frei";
            if ((a.m_State & AmbulanceFlags.Transporting) != 0) return "Transport";
            if ((a.m_State & AmbulanceFlags.Dispatched) != 0) return (a.m_State & AmbulanceFlags.AtTarget) != 0 || OnSceneCareSystem.OnScene(em, e) ? "am Einsatzort" : "Anfahrt";
            if ((a.m_State & AmbulanceFlags.Returning) != 0) return "Rückfahrt";
            return "unterwegs";
        }

        /// <summary>Same status words as Status() where they mean the same, plus "Streife" for patrol.</summary>
        internal static string PoliceStatus(EntityManager em, Entity e, PoliceCar p, bool parked)
        {
            if (parked) return (p.m_State & PoliceCarFlags.Disabled) != 0 ? "ausser Dienst" : "frei";
            if (ResponderDispatchSystem.HeldAt.ContainsKey(e)) return "am Einsatzort"; // vanilla already marked it returning before the hold
            if ((p.m_State & PoliceCarFlags.Returning) != 0) return "Rückfahrt";
            if (p.m_PurposeMask == 0) return ResponderDispatchSystem.HeldAt.ContainsKey(e) ? "am Einsatzort" : "Anfahrt"; // ResponderDispatchSystem car (no vanilla purpose)
            if ((p.m_State & PoliceCarFlags.AccidentTarget) != 0) return (p.m_State & PoliceCarFlags.AtTarget) != 0 ? "am Einsatzort" : "Anfahrt";
            return "Streife";
        }

        private (List<(Entity e, Ambulance a, Entity owner, string status)> rows, List<(Entity e, int used, int beds, bool accepts)> hospitals, List<Entity> depots) Collect()
        {
            var em = EntityManager;
            using var amb = m_Ambulances.ToEntityArray(Allocator.Temp);
            using var hosp = m_Hospitals.ToEntityArray(Allocator.Temp);

            var rows = new List<(Entity e, Ambulance a, Entity owner, string status)>();
            foreach (var e in amb)
            {
                var a = em.GetComponentData<Ambulance>(e);
                rows.Add((e, a, em.GetComponentData<Owner>(e).m_Owner, Status(em, e, a, em.HasComponent<ParkedCar>(e))));
            }
            var hospitals = new List<(Entity e, int used, int beds, bool accepts)>();
            var depots = new List<Entity>();
            foreach (var h in hosp)
            {
                var prefab = em.GetComponentData<PrefabRef>(h).m_Prefab;
                // ponytail: base prefab capacity only; building upgrades (extra wings) are not added.
                int beds = em.HasComponent<HospitalData>(prefab) ? em.GetComponentData<HospitalData>(prefab).m_PatientCapacity : 0;
                if (!em.HasBuffer<Patient>(h)) { if (em.HasBuffer<OwnedVehicle>(h)) depots.Add(h); continue; }
                hospitals.Add((h, em.GetBuffer<Patient>(h, true).Length, beds, HospitalSelectionSystem.CanAcceptPatients(em.GetComponentData<Hospital>(h))));
            }
            return (rows, hospitals, depots);
        }

        private static bool OnCall(string status) => status == "Anfahrt" || status == "am Einsatzort" || status == "Transport";

        private void Write(IJsonWriter w)
        {
            var em = EntityManager;
            var (rows, hospitals, depots) = Collect();
            var rtws = rows.Where(r => !NefDispatchSystem.IsNef(em, r.e)).ToList(); // overview counts ambulances only
            int free = rtws.Count(r => r.status == "frei");
            int onCall = rtws.Count(r => OnCall(r.status));
            int returning = rtws.Count(r => r.status == "Rückfahrt");
            int bedsUsed = hospitals.Sum(h => h.used), bedsTotal = hospitals.Sum(h => h.beds);

            w.TypeBegin("ems.State");
            w.PropertyName("overview"); w.TypeBegin("ems.Overview");
            Prop(w, "total", rtws.Count); Prop(w, "free", free); Prop(w, "onCall", onCall); Prop(w, "returning", returning);
            Prop(w, "bedsUsed", bedsUsed); Prop(w, "bedsTotal", bedsTotal); Prop(w, "hospitals", hospitals.Count); Prop(w, "depots", depots.Count);
            w.TypeEnd();

            w.PropertyName("vehicles"); w.ArrayBegin((uint)rows.Count);
            foreach (var r in rows.OrderBy(r => m_Units.TryGetCallsign(r.e, out var c) ? c : "~"))
            {
                w.TypeBegin("ems.Vehicle");
                Prop(w, "callsign", m_Units.TryGetCallsign(r.e, out var cs) ? cs : $"#{r.e.Index}");
                Prop(w, "status", r.status);
                Prop(w, "station", Name(r.owner));
                bool hasCode = m_Units.TryGetCode(r.e, out var code) && code != null && r.a.m_TargetPatient != Entity.Null;
                Prop(w, "code", hasCode ? code.ToString() : "");
                Prop(w, "level", hasCode ? code.Level.ToString()[0].ToString() : "");
                Prop(w, "reason", hasCode ? code.Reason : "");
                Prop(w, "units", hasCode ? code.UnitsText() : "");
                Prop(w, "destination", r.status == "Transport" ? Name(em.GetComponentData<Target>(r.e).m_Target) : "");
                bool nef = NefDispatchSystem.IsNef(em, r.e);
                // Call id = patient entity index; a NEF is linked through NefDispatchSystem. 0 = no call.
                var patient = r.a.m_TargetPatient;
                if (patient == Entity.Null && nef) patient = NefDispatchSystem.NefByPatient.FirstOrDefault(kv => kv.Value == r.e).Key;
                Prop(w, "call", patient.Index);
                Prop(w, "nef", nef); Pos(w, r.e);
                w.TypeEnd();
            }
            w.ArrayEnd();

            w.PropertyName("hospitals"); w.ArrayBegin((uint)hospitals.Count);
            foreach (var h in hospitals.OrderByDescending(h => h.beds == 0 ? 0 : (float)h.used / h.beds))
            {
                w.TypeBegin("ems.Hospital");
                Prop(w, "name", Name(h.e)); Prop(w, "used", h.used); Prop(w, "beds", h.beds); Prop(w, "accepts", h.accepts);
                Prop(w, "ambulances", rows.Count(r => r.owner == h.e)); Pos(w, h.e);
                w.TypeEnd();
            }
            w.ArrayEnd();

            w.PropertyName("stations"); w.ArrayBegin((uint)depots.Count);
            foreach (var d in depots)
            {
                var own = rows.Where(r => r.owner == d).ToList();
                w.TypeBegin("ems.Station");
                Prop(w, "name", Name(d)); Prop(w, "total", own.Count); Prop(w, "free", own.Count(r => r.status == "frei"));
                Prop(w, "onCall", own.Count(r => r.status != "frei" && r.status != "Rückfahrt" && r.status != "ausser Dienst")); Pos(w, d);
                w.TypeEnd();
            }
            w.ArrayEnd();

            using var police = m_Police.ToEntityArray(Allocator.Temp);
            w.PropertyName("police"); w.ArrayBegin((uint)police.Length);
            foreach (var p in police)
            {
                w.TypeBegin("ems.Police");
                Prop(w, "callsign", m_Units.CallsignOf(p));
                Prop(w, "status", PoliceStatus(em, p, em.GetComponentData<PoliceCar>(p), em.HasComponent<ParkedCar>(p)));
                Prop(w, "station", Name(em.GetComponentData<Owner>(p).m_Owner)); Pos(w, p);
                w.TypeEnd();
            }
            w.ArrayEnd();

            w.PropertyName("stats"); w.TypeBegin("ems.Stats");
            Prop(w, "calls", m_TotalCalls);
            Prop(w, "sampleMinutes", (int)math.round(SampleFrames / OnSceneCareSystem.FramesPerGameMinute));
            w.PropertyName("history"); Ints(w, m_History.SelectMany(x => x).ToList());
            w.PropertyName("levels"); Ints(w, m_CallsByLevel);
            w.PropertyName("response"); Ints(w, m_Response);
            var top = m_Reasons.OrderByDescending(kv => kv.Value).Take(6).ToList();
            w.PropertyName("reasons"); w.ArrayBegin((uint)top.Count);
            foreach (var kv in top) { w.TypeBegin("ems.Reason"); Prop(w, "reason", kv.Key); Prop(w, "n", kv.Value); w.TypeEnd(); }
            w.ArrayEnd();
            w.TypeEnd();
            w.TypeEnd();
        }

        // ---- Statistics: a rolling one-day history plus counters since the city was loaded ----

        private const uint TrackFrames = 64, SampleFrames = 1024; // 256 samples x 1024 frames = one game day
        private const int HistoryLength = 256, ResponseLength = 200;
        private readonly Queue<int[]> m_History = new Queue<int[]>(); // per sample: onCall, returning, total, bedsUsed, bedsTotal, new calls
        private readonly Dictionary<Entity, (Entity patient, uint since, bool counted)> m_Dispatch = new Dictionary<Entity, (Entity, uint, bool)>(); // RTW -> current call
        private readonly int[] m_CallsByLevel = new int[6]; // E D C B A O, the order of the UI
        private readonly Dictionary<string, int> m_Reasons = new Dictionary<string, int>();
        private readonly List<int> m_Response = new List<int>(); // game minutes from alarm to on scene, most recent last
        private int m_TotalCalls, m_SampleCalls;
        private uint m_LastTrack, m_LastSample;

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_History.Clear(); m_Dispatch.Clear(); Ampds.Forced.Clear(); m_Reasons.Clear(); m_Response.Clear();
            System.Array.Clear(m_CallsByLevel, 0, m_CallsByLevel.Length);
            m_TotalCalls = m_SampleCalls = 0;
        }

        /// <summary>Counts each new RTW dispatch and measures the game time until it stands on scene.</summary>
        private void Track(uint now)
        {
            var em = EntityManager;
            using var amb = m_Ambulances.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<Entity>();
            foreach (var e in amb)
            {
                var a = em.GetComponentData<Ambulance>(e);
                if (a.m_TargetPatient == Entity.Null || (a.m_State & AmbulanceFlags.Dispatched) == 0 || NefDispatchSystem.IsNef(em, e)) continue;
                seen.Add(e);
                if (!m_Dispatch.TryGetValue(e, out var d) || d.patient != a.m_TargetPatient)
                {
                    d = (a.m_TargetPatient, now, false);
                    m_TotalCalls++; m_SampleCalls++;
                }
                // UnitNamingSystem assigns the code a few frames after the dispatch.
                if (!d.counted && m_Units.TryGetCode(e, out var code) && code != null)
                {
                    m_CallsByLevel[(int)AmpdsLevel.Echo - (int)code.Level]++;
                    m_Reasons[code.Reason] = (m_Reasons.TryGetValue(code.Reason, out var n) ? n : 0) + 1;
                    d.counted = true;
                }
                if (d.since != 0 && OnSceneCareSystem.OnScene(em, e))
                {
                    m_Response.Add((int)math.round((now - d.since) / OnSceneCareSystem.FramesPerGameMinute));
                    if (m_Response.Count > ResponseLength) m_Response.RemoveAt(0);
                    d.since = 0;
                }
                m_Dispatch[e] = d;
            }
            foreach (var gone in m_Dispatch.Keys.Where(e => !seen.Contains(e)).ToList()) m_Dispatch.Remove(gone);
        }

        private void Sample()
        {
            var em = EntityManager;
            var (rows, hospitals, _) = Collect();
            var rtws = rows.Where(r => !NefDispatchSystem.IsNef(em, r.e)).ToList();
            m_History.Enqueue(new[] { rtws.Count(r => OnCall(r.status)), rtws.Count(r => r.status == "Rückfahrt"), rtws.Count,
                hospitals.Sum(h => h.used), hospitals.Sum(h => h.beds), m_SampleCalls });
            m_SampleCalls = 0;
            while (m_History.Count > HistoryLength) m_History.Dequeue();
        }

        private static void Ints(IJsonWriter w, IList<int> values)
        {
            w.ArrayBegin((uint)values.Count);
            foreach (var v in values) w.Write(v);
            w.ArrayEnd();
        }

        private static void Prop(IJsonWriter w, string n, string v) { w.PropertyName(n); w.Write(v ?? ""); }
        private static void Prop(IJsonWriter w, string n, int v) { w.PropertyName(n); w.Write(v); }
        private static void Prop(IJsonWriter w, string n, bool v) { w.PropertyName(n); w.Write(v); }
    }
}
