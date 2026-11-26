using Colossal.Entities;
using Colossal.UI.Binding;
using Game.Common;
using Game.Objects;
using Game.Tools;
using Game.UI.InGame;
using Game.Vehicles;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;
using PoliceCar = Game.Vehicles.PoliceCar;

namespace RealisticEMS
{
    /// <summary>
    /// Extra section in the selected-vehicle panel: Einsatzcode, a detail text and every unit on the same call.
    /// Shown for ambulances, NEF and ResponderDispatchSystem police/fire cars. Rendered by UI/src/mods/call-section.tsx.
    /// Fires (FireAlarmSystem): the same overview on the burning building and on every unit sent there.
    /// </summary>
    public partial class CallInfoSection : InfoSectionBase
    {
        protected override string group => nameof(CallInfoSection);

        private UnitNamingSystem m_Units;
        private ResponderDispatchSystem m_Responders;
        private OnSceneCareSystem m_Care;
        private Game.Simulation.SimulationSystem m_Sim;
        private string m_Phase, m_Times;
        private float m_Progress;
        private EntityQuery m_Ambulances, m_Responders2;
        private AmpdsCode m_Code;
        private string m_Info;
        private readonly List<(Entity e, string callsign, string status)> m_List = new List<(Entity, string, string)>();

        public static void Install(World world, List<ISectionSource> sections) =>
            sections.Insert(0, world.GetOrCreateSystemManaged<CallInfoSection>());

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Responders = World.GetOrCreateSystemManaged<ResponderDispatchSystem>();
            m_Care = World.GetOrCreateSystemManaged<OnSceneCareSystem>();
            m_Sim = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Responders2 = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Target>() },
                Any = new[] { ComponentType.ReadOnly<PoliceCar>(), ComponentType.ReadOnly<FireEngine>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void Reset() { m_Code = null; m_FireCode = null; m_Info = ""; m_Phase = m_Times = ""; m_Progress = -1; m_List.Clear(); }

        private Entity PatientOf(Entity e)
        {
            var em = EntityManager;
            if (em.TryGetComponent<Ambulance>(e, out var a))
            {
                if (a.m_TargetPatient != Entity.Null) return a.m_TargetPatient;
                var viaNef = NefDispatchSystem.NefByPatient.FirstOrDefault(kv => kv.Value == e).Key;
                return viaNef != Entity.Null ? viaNef : HelicopterSystem.PatientOf(e);
            }
            var p = m_Responders.PatientOf(e);
            if (p == Entity.Null) p = HelicopterSystem.PatientOf(e); // doctor shuttle police car
            if (p != Entity.Null || !em.TryGetComponent<Target>(e, out var t) || t.m_Target == Entity.Null) return p;
            // After a load ResponderDispatchSystem forgets its cars: match by call location instead.
            using var amb = m_Ambulances.ToEntityArray(Allocator.Temp);
            foreach (var v in amb)
            {
                var va = em.GetComponentData<Ambulance>(v);
                if (va.m_TargetLocation == t.m_Target && va.m_TargetPatient != Entity.Null) return va.m_TargetPatient;
            }
            return Entity.Null;
        }

        private string StatusOf(Entity e)
        {
            var em = EntityManager;
            if (ResponderDispatchSystem.Standby.Contains(e)) return ResponderDispatchSystem.HeldAt.ContainsKey(e) ? "am Einsatzort" : "Anfahrt";
            if (m_Responders.IsCordon(e)) return ResponderDispatchSystem.HeldAt.ContainsKey(e) ? Mod.T("Strassensperre", "Road block") : Mod.T("Anfahrt Sperre", "En route to road block");
            bool parked = em.HasComponent<ParkedCar>(e);
            if (em.TryGetComponent<Ambulance>(e, out var a)) return DispatchUISystem.Status(em, e, a, parked);
            if (em.TryGetComponent<PoliceCar>(e, out var p)) return DispatchUISystem.PoliceStatus(em, e, p, parked);
            return ResponderDispatchSystem.HeldAt.ContainsKey(e) ? "am Einsatzort" : "Anfahrt"; // fire engine
        }

        /// <summary>Burning building the selection belongs to: the building itself, or a police car / engine sent there.</summary>
        private Entity FireOf(Entity e)
        {
            var em = EntityManager;
            if (FireAlarmSystem.Scenes.ContainsKey(e)) return e;
            var b = m_Responders.FireOf(e);
            if (b != Entity.Null) return FireAlarmSystem.Scenes.ContainsKey(b) ? b : Entity.Null;
            return em.HasComponent<FireEngine>(e) && em.TryGetComponent<Target>(e, out var t) && FireAlarmSystem.Scenes.ContainsKey(t.m_Target) ? t.m_Target : Entity.Null;
        }

        private void UpdateFire(Entity building)
        {
            var em = EntityManager;
            var s = FireAlarmSystem.Scenes[building];
            var units = m_Responders.CarsAtFire(building).Where(em.Exists).ToList();
            using (var cars = m_Responders2.ToEntityArray(Allocator.Temp))
                foreach (var c in cars) if (!units.Contains(c) && em.HasComponent<FireEngine>(c) && em.GetComponentData<Target>(c).m_Target == building) units.Add(c);
            foreach (var e in s.Engines) if (!units.Contains(e) && em.Exists(e) && !em.HasComponent<Deleted>(e) && em.HasComponent<TripSource>(e)) units.Add(e); // fire watch
            // Ambulances/NEFs for patients from the fire: picked up at the building, or hurt by its fire and already outside.
            var fireEvent = em.TryGetComponent<Game.Events.OnFire>(building, out var onFire) ? onFire.m_Event : Entity.Null;
            using (var amb = m_Ambulances.ToEntityArray(Allocator.Temp))
                foreach (var a in amb)
                {
                    if (units.Contains(a)) continue;
                    var va = em.GetComponentData<Ambulance>(a);
                    if (va.m_TargetPatient == Entity.Null) continue;
                    bool fromFire = va.m_TargetLocation == building
                        || fireEvent != Entity.Null && em.TryGetComponent<Game.Citizens.HealthProblem>(va.m_TargetPatient, out var hp) && hp.m_Event == fireEvent;
                    if (fromFire) units.Add(a);
                }
            if (selectedEntity != building && !units.Contains(selectedEntity)) units.Add(selectedEntity);
            units = units.OrderBy(u => em.HasComponent<FireEngine>(u) ? 0 : em.HasComponent<Ambulance>(u) ? 1 : 2).ToList(); // engines, rescue, police

            int engines = units.Count(u => em.HasComponent<FireEngine>(u));
            int nefs = units.Count(u => NefDispatchSystem.IsNef(em, u));
            int rtws = units.Count(u => em.HasComponent<Ambulance>(u)) - nefs;
            int police = units.Count - engines - rtws - nefs;
            m_FireCode = s.Code?.ToString() ?? "";
            m_FireLevel = s.Code != null ? s.Code.Level.ToString()[0].ToString() : "";
            var lines = new List<string>
            {
                s.Code != null ? $"{s.Code.Level}: {s.Code.Complaint}" : "",
                s.Code?.Reason ?? "",
                s.Code != null ? Mod.T("Alarmiert: ", "Dispatched: ") + s.Code.UnitsText() : "",
                Mod.T("Gebäudebrand: ", "Building fire: ") + LevelName(s.Level) + Mod.T($", Alarmstufe {s.Alarm}", $", alarm {s.Alarm}"),
                Mod.T($"Im Einsatz: {engines} Löschfahrzeuge, {rtws} RTW, {nefs} NEF, {police} Polizei", $"Responding: {engines} engines, {rtws} AMB, {nefs} DOC, {police} police"),
            };
            if (s.Closed) lines.Add(Mod.T("Strasse gesperrt, Umleitung aktiv", "Street closed, detour active"));
            m_Info = string.Join("\n", lines.Where(l => l != ""));
            uint now = m_Sim.frameIndex;
            if (s.OutAt == 0) { m_Phase = Mod.T("Brandbekämpfung", "Firefighting"); m_Progress = math.saturate((float)(now - s.Start) / s.MinFrames); }
            else { m_Phase = Mod.T("Nachlöscharbeiten / Brandwache", "Overhaul / fire watch"); m_Progress = math.saturate((float)(now - s.Start) / s.MinFrames); }
            m_Times = Mod.T("Seit Alarmierung: ", "Since dispatch: ") + Min(FireAlarmSystem.FireFrames(now - s.Start));
            m_List.Clear();
            foreach (var u in units) m_List.Add((u, m_Units.CallsignOf(u), StatusOf(u)));
        }

        private static string LevelName(FireAlarmSystem.Level l) => l switch
        {
            FireAlarmSystem.Level.Small => Mod.T("Einfamilienhaus / Kleinbau", "house / small building"),
            FireAlarmSystem.Level.Medium => Mod.T("Mehrfamilienhaus", "medium residential"),
            FireAlarmSystem.Level.Large => Mod.T("Hochhaus / Bürogebäude", "high-rise / office"),
            FireAlarmSystem.Level.Industrial => Mod.T("Industrie", "industrial"),
            _ => Mod.T("Spital / Schule", "hospital / school"),
        };

        private string m_FireCode, m_FireLevel;

        protected override void OnUpdate()
        {
            var em = EntityManager;
            m_FireCode = null;
            var fire = FireOf(selectedEntity);
            if (fire != Entity.Null) { visible = true; UpdateFire(fire); return; }
            var patient = PatientOf(selectedEntity);
            visible = patient != Entity.Null;
            if (!visible) return;

            using var amb = m_Ambulances.ToEntityArray(Allocator.Temp);
            var units = amb.Where(v => em.GetComponentData<Ambulance>(v).m_TargetPatient == patient).ToList();
            m_Code = null;
            foreach (var v in units) if (m_Units.TryGetCode(v, out var c) && c != null) { m_Code = c; break; }
            visible = m_Code != null;
            if (!visible) return;

            if (NefDispatchSystem.NefByPatient.TryGetValue(patient, out var nef) && em.Exists(nef) && !units.Contains(nef)) units.Add(nef);
            // Rescue heli and its doctor shuttle from dispatch on, not only once landed.
            if (HelicopterSystem.HeliByPatient.TryGetValue(patient, out var heli) && em.Exists(heli) && !units.Contains(heli)) units.Add(heli);
            if (HelicopterSystem.ShuttleByPatient.TryGetValue(patient, out var shuttle) && em.Exists(shuttle) && !units.Contains(shuttle)) units.Add(shuttle);
            units.AddRange(m_Responders.CarsOf(patient).Where(em.Exists));
            // Police/fire heading to the same location (covers cars from before a load).
            var location = units.Select(v => em.TryGetComponent<Ambulance>(v, out var va) ? va.m_TargetLocation : Entity.Null).FirstOrDefault(l => l != Entity.Null);
            if (location != Entity.Null)
            {
                using var cars = m_Responders2.ToEntityArray(Allocator.Temp);
                foreach (var c in cars) if (!units.Contains(c) && em.GetComponentData<Target>(c).m_Target == location) units.Add(c);
            }
            if (!units.Contains(selectedEntity)) units.Add(selectedEntity);

            var lines = new List<string>
            {
                $"{m_Code.Level}: {m_Code.Complaint}",
                m_Code.Reason,
                Mod.T("Alarmiert: ", "Dispatched: ") + m_Code.UnitsText(),
            };
            if (ResponderDispatchSystem.LockedDoor.Contains(patient)) lines.Add(Mod.T("Tür verschlossen", "Door locked"));
            m_Info = string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));

            Timeline(patient, units);

            m_List.Clear();
            foreach (var u in units) m_List.Add((u, m_Units.CallsignOf(u), StatusOf(u)));
        }

        private static string Min(uint frames) => $"{frames / OnSceneCareSystem.FramesPerGameMinute:0} min";

        // Bar = treatment progress only (-1 hides it in the other phases).
        private void Timeline(Entity patient, List<Entity> units)
        {
            uint now = m_Sim.frameIndex;
            uint care = (uint)(Mod.Setting.OnSceneMinutes * OnSceneCareSystem.FramesPerGameMinute);
            if (!m_Care.TryGetTimes(patient, out var dispatch, out var arrive, out var nefArrive, out var start, out var transport))
            {
                m_Phase = Mod.T("Anfahrt", "En route"); m_Progress = -1; m_Times = ""; return;
            }
            var t = new List<string> { Mod.T("Seit Alarmierung: ", "Since dispatch: ") + Min(now - dispatch) };
            if (arrive != 0) t.Add(Mod.T("RTW vor Ort nach ", "Ambulance on scene after ") + Min(arrive - dispatch));
            if (nefArrive != 0) t.Add(Mod.T("NEF vor Ort nach ", "Doctor on scene after ") + Min(nefArrive - dispatch));
            if (start != 0) t.Add(Mod.T("Versorgung seit ", "Treating for ") + Min((transport != 0 ? transport : now) - start) + (care > 0 ? $" / {Min(care)}" : ""));
            if (transport != 0) t.Add(Mod.T("Transport seit ", "Transporting for ") + Min(now - transport));
            m_Times = string.Join("\n", t);

            if (transport != 0) { m_Phase = Mod.T("Transport ins Krankenhaus", "Transport to hospital"); m_Progress = start != 0 ? 1 : -1; } // treatment over: bar stays full
            else if (start != 0) { m_Phase = Mod.T("Versorgung", "Treatment"); m_Progress = care == 0 ? 1 : math.saturate((float)(now - start) / care); }
            else if (arrive != 0) { m_Phase = Mod.T("Vor Ort, wartet auf NEF", "On scene, waiting for doctor"); m_Progress = -1; }
            else { m_Phase = Mod.T("Anfahrt", "En route"); m_Progress = -1; }
        }

        protected override void OnProcess() { }

        public override void OnWriteProperties(IJsonWriter w)
        {
            w.PropertyName("code"); w.Write(m_FireCode ?? m_Code?.ToString() ?? "");
            w.PropertyName("level"); w.Write(m_FireCode != null ? m_FireLevel : m_Code != null ? m_Code.Level.ToString()[0].ToString() : "");
            w.PropertyName("info"); w.Write(m_Info ?? "");
            w.PropertyName("phase"); w.Write(m_Phase ?? "");
            w.PropertyName("times"); w.Write(m_Times ?? "");
            w.PropertyName("progress"); w.Write(m_Progress);
            w.PropertyName("units"); w.ArrayBegin(m_List.Count);
            foreach (var u in m_List)
            {
                w.TypeBegin("ems.CallUnit");
                w.PropertyName("entity"); w.Write(u.e);
                w.PropertyName("callsign"); w.Write(u.callsign);
                w.PropertyName("status"); w.Write(u.status);
                w.PropertyName("self"); w.Write(u.e == selectedEntity);
                w.TypeEnd();
            }
            w.ArrayEnd();
        }
    }
}
