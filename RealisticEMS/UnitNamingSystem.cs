using Colossal.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Gives every ambulance a callsign "RTW {station}-{nn}" and, while on a call, appends the call's AMPDS code.
    /// Police cars get "POL {station}-{nn}", fire engines "FW {station}-{nn}" (callsign only, no code).
    /// {station} = the player-set StationCallsign of the owning building, else its number (numbered per vehicle type).
    /// Shown via the vanilla custom-name mechanism, so it appears as the title of the vehicle info panel.
    /// The code is fixed when the ambulance gets a new patient (assigned at call-taking, like a real dispatch centre).
    /// </summary>
    public partial class UnitNamingSystem : GameSystemBase
    {
        private EntityQuery m_Query, m_Police, m_Fire;
        private static readonly HashSet<Entity> s_Renamed = new HashSet<Entity>();

        /// <summary>Station callsign changed: its vehicles get new callsigns on the next update.</summary>
        public static void RenameStation(Entity station) => s_Renamed.Add(station);
        private NameSystem m_NameSystem;
        private readonly Dictionary<Entity, string> m_Callsign = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, (Entity patient, AmpdsCode code)> m_Call = new Dictionary<Entity, (Entity, AmpdsCode)>();
        private readonly Dictionary<Entity, string> m_ShownName = new Dictionary<Entity, string>();

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Police = GetEntityQuery(ComponentType.ReadOnly<PoliceCar>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Fire = GetEntityQuery(ComponentType.ReadOnly<FireEngine>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            RequireAnyForUpdate(m_Query, m_Police, m_Fire);
        }

        /// <summary>Next free "{prefix}{nn}" for a new vehicle.</summary>
        private string NewCallsign(string prefix)
        {
            var used = new HashSet<string>(m_Callsign.Values.Where(s => s.StartsWith(prefix)));
            int n = 1;
            while (used.Contains($"{prefix}{n:00}")) n++;
            return $"{prefix}{n:00}";
        }

        /// <summary>Player-set station callsign, else the station's rank among stations of that vehicle type.</summary>
        private string StationLabel(Entity owner, List<Entity> stations) =>
            StationCallsignSection.Of(EntityManager, owner) ?? (stations.IndexOf(owner) + 1).ToString();

        /// <summary>Callsign only, no code: police "POL x-nn", fire "FW x-nn".</summary>
        private void NameSimple(EntityQuery query, string prefix)
        {
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<Owner>(Allocator.Temp);
            var stations = owners.Select(o => o.m_Owner).Distinct().OrderBy(e => e.Index).ToList(); // same ponytail as RTW numbering
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!m_Callsign.TryGetValue(e, out var callsign))
                    callsign = m_Callsign[e] = NewCallsign($"{prefix} {StationLabel(owners[i].m_Owner, stations)}-");
                if (!m_ShownName.TryGetValue(e, out var shown) || shown != callsign)
                {
                    m_NameSystem.SetCustomName(e, callsign);
                    m_ShownName[e] = callsign;
                }
            }
        }

        public bool TryGetCallsign(Entity ambulance, out string callsign) => m_Callsign.TryGetValue(ambulance, out callsign);
        public string CallsignOf(Entity ambulance) => m_Callsign.TryGetValue(ambulance, out var c) ? c : $"#{ambulance.Index}";

        /// <summary>Current call code of an ambulance, if any (for logging/other systems).</summary>
        public bool TryGetCode(Entity ambulance, out AmpdsCode code)
        {
            bool ok = m_Call.TryGetValue(ambulance, out var call);
            code = call.code;
            return ok;
        }

        protected override void OnUpdate()
        {
            if (!Mod.Setting.ShowUnitNames) return;
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            using var ambulances = m_Query.ToComponentDataArray<Ambulance>(Allocator.Temp);
            using var owners = m_Query.ToComponentDataArray<Owner>(Allocator.Temp);

            // ponytail: station number = rank of the owner's entity index; changes if stations are added/removed.
            // Store a numbered component on the building if stable numbers across sessions matter.
            var stations = owners.Select(o => o.m_Owner).Distinct().OrderBy(e => e.Index).ToList();
            using var police = m_Police.ToEntityArray(Allocator.Temp);
            using var fire = m_Fire.ToEntityArray(Allocator.Temp);
            var alive = new HashSet<Entity>(entities);
            alive.UnionWith(police);
            alive.UnionWith(fire);
            if (s_Renamed.Count > 0)
            {
                foreach (var v in m_Callsign.Keys.Where(v => EntityManager.TryGetComponent<Owner>(v, out var o) && s_Renamed.Contains(o.m_Owner)).ToList()) m_Callsign.Remove(v);
                s_Renamed.Clear();
            }
            foreach (var gone in m_Callsign.Keys.Where(e => !alive.Contains(e)).ToList()) { m_Callsign.Remove(gone); m_Call.Remove(gone); m_ShownName.Remove(gone); }

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var a = ambulances[i];
                if (!m_Callsign.TryGetValue(e, out var callsign))
                {
                    string station = StationLabel(owners[i].m_Owner, stations);
                    callsign = m_Callsign[e] = NewCallsign($"{(NefDispatchSystem.IsNef(EntityManager, e) ? Mod.T("NEF", "DOC") : Mod.T("RTW", "AMB"))} {station}-");
                }

                bool onCall = a.m_TargetPatient != Entity.Null && (a.m_State & (AmbulanceFlags.Dispatched | AmbulanceFlags.Transporting)) != 0;
                string name = callsign;
                if (onCall)
                {
                    if (!m_Call.TryGetValue(e, out var call) || call.patient != a.m_TargetPatient)
                    {
                        call = (a.m_TargetPatient, Ampds.Classify(EntityManager, a.m_TargetPatient));
                        m_Call[e] = call;
                        if (call.code != null)
                            Mod.log.Info($"{callsign} assigned {call.code} {call.code.Complaint} - {call.code.Reason} | needs {call.code.UnitsText()} (raw: {call.code.RawUnits}) {(call.code.Urgent ? "urgent" : "non-urgent")} (patient {a.m_TargetPatient.Index})");
                    }
                    string status = (a.m_State & AmbulanceFlags.Transporting) != 0 ? Mod.T("Transport", "transporting") : Mod.T("Anfahrt", "en route");
                    name = call.code == null ? $"{callsign} | {status}"
                         : $"{callsign} | {call.code} {call.code.Reason} | {call.code.UnitsText()}{(ResponderDispatchSystem.LockedDoor.Contains(a.m_TargetPatient) ? Mod.T(" | Tür verschlossen", " | door locked") : "")} | {status}";
                }
                else m_Call.Remove(e); // call over: just the callsign

                if (!m_ShownName.TryGetValue(e, out var shown) || shown != name)
                {
                    m_NameSystem.SetCustomName(e, name);
                    m_ShownName[e] = name;
                }
            }
            NameSimple(m_Police, "POL");
            NameSimple(m_Fire, Mod.T("FW", "FIRE"));
        }
    }
}
