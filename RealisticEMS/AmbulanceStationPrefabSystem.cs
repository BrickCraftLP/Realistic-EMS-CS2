using Game;
using Game.Prefabs;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Clones the vanilla taxi depot into a small "Ambulance Station": no beds, one vehicle slot (an ambulance, or a NAW
    /// when the player switches the station to it, see StationNaw), listed with the clinic. Taxi logo/sign sub-objects are dropped.
    /// </summary>
    public partial class AmbulanceStationPrefabSystem : GameSystemBase
    {
        public const string StationName = "RealisticEMS_AmbulanceStation";
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Taxi, m_Clinics;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Taxi = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<TransportDepotData>());
            m_Clinics = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<HospitalData>());
        }

        protected override void OnUpdate()
        {
            if (m_Done || m_Taxi.IsEmpty || m_Clinics.IsEmpty) return;
            m_Done = true;

            BuildingPrefab taxi = null, clinic = null;
            using (var depots = m_Taxi.ToEntityArray(Allocator.Temp))
                foreach (var e in depots)
                    if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p is BuildingPrefab b && !b.Has<ServiceUpgrade>()
                        && b.TryGet(out TransportDepot td) && td.m_TransportType == Game.Prefabs.TransportType.Taxi)
                    {
                        if (b.name == StationName) return;
                        if (taxi == null || b.name.StartsWith("TaxiDepot")) taxi = b;
                    }
            using (var hospitals = m_Clinics.ToEntityArray(Allocator.Temp))
                foreach (var e in hospitals)
                    if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p is BuildingPrefab b && b.name == "MedicalClinic01") clinic = b;
            if (taxi == null || clinic == null) { Log.Error($"Ambulance station not added (taxi depot {(taxi == null ? "missing" : "ok")}, clinic {(clinic == null ? "missing" : "ok")})"); return; }

            var station = (BuildingPrefab)taxi.Clone(StationName);
            station.Remove<ObsoleteIdentifiers>();
            station.Remove<ThemeObject>();
            station.Remove<ContentPrerequisite>();
            station.Remove<TransportDepot>();
            Log.Info("Ambulance station components: " + string.Join(", ", station.components.ConvertAll(c => c.GetType().Name)));

            var hospital = station.AddComponent<Hospital>();
            hospital.m_PatientCapacity = 0;
            hospital.m_AmbulanceCapacity = 1;
            hospital.m_MedicalHelicopterCapacity = 0;
            hospital.m_TreatDiseases = false;
            hospital.m_TreatInjuries = false;
            hospital.m_HealthRange = clinic.GetComponent<Hospital>().m_HealthRange;

            // Healthcare service, toolbar group of the clinic.
            if (station.TryGet(out ServiceObject so) && clinic.TryGet(out ServiceObject cso)) so.m_Service = cso.m_Service;
            if (station.TryGet(out UIObject ui) && clinic.TryGet(out UIObject cui))
            {
                ui.m_Group = cui.m_Group;
                ui.m_Priority = cui.m_Priority + 2;
                ui.m_Icon = cui.m_Icon;
                if (FindAmbulanceIcon() is string icon) ui.m_Icon = icon;
            }

            // Taxi logo: drop sub-objects that look like signs/logos. A logo baked into the building texture stays (see log).
            if (station.TryGet(out ObjectSubObjects subs) && subs.m_SubObjects != null)
            {
                bool Logo(ObjectSubObjectInfo s) => s.m_Object != null && new[] { "taxi", "sign", "logo", "billboard" }.Any(k => s.m_Object.name.ToLowerInvariant().Contains(k));
                foreach (var s in subs.m_SubObjects) Log.Debug($"Ambulance station sub-object: {s.m_Object?.name}{(Logo(s) ? " (removed)" : "")}");
                subs.m_SubObjects = subs.m_SubObjects.Where(s => !Logo(s)).ToArray();
            }
            if (station.TryGet(out ServiceConsumption upkeep)) upkeep.m_Upkeep /= 2;

            Log.Info($"AddPrefab({StationName}) = {m_PrefabSystem.AddPrefab(station)}, cloned from {taxi.name}");
            Prefab = station;
            Colors = Livery(station);
            AddGarageUpgrade(taxi, station, so?.m_Service);
        }

        public const string UpgradeName = "RealisticEMS_AmbulanceStationGarage";

        /// <summary>Clones the taxi depot's upgrade into one for the station: +3 ambulances (4 in total).</summary>
        private void AddGarageUpgrade(BuildingPrefab taxi, BuildingPrefab station, ServicePrefab service)
        {
            PrefabBase garage = null;
            using (var all = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ServiceUpgradeData>()).ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                    if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p.TryGet(out ServiceUpgrade su) && su.m_Buildings != null && su.m_Buildings.Contains(taxi))
                    { garage = p; break; }
            if (garage == null) { Log.Warn($"Ambulance station: {taxi.name} has no upgrade to clone, station stays at 1 vehicle"); return; }

            var up = garage.Clone(UpgradeName);
            up.Remove<ObsoleteIdentifiers>();
            up.Remove<ThemeObject>();
            up.Remove<ContentPrerequisite>();
            up.Remove<TransportDepot>();
            up.GetComponent<ServiceUpgrade>().m_Buildings = new[] { station };
            if (service != null && up.TryGet(out ServiceObject uso)) uso.m_Service = service;
            var h = up.AddComponent<Hospital>();
            h.m_AmbulanceCapacity = 3;
            Log.Info($"AddPrefab({UpgradeName}) = {m_PrefabSystem.AddPrefab(up)}, cloned from {garage.name}");
        }

        public static BuildingPrefab Prefab;
        /// <summary>Per-instance colours for placed stations (StationFleetSystem applies them), null = mesh not tintable.</summary>
        public static Game.Rendering.ColorSet? Colors;

        /// <summary>
        /// The taxi depot's first colour variation with every taxi-yellow channel turned rescue red. The mesh prefab is
        /// shared with the real taxi depot, so the colour goes on each placed station (CustomMeshColor), not the mesh.
        /// </summary>
        private static Game.Rendering.ColorSet? Livery(BuildingPrefab b)
        {
            var red = new UnityEngine.Color(0.78f, 0.08f, 0.08f);
            bool Yellow(UnityEngine.Color c) => c.r > 0.5f && c.g > 0.35f && c.b < 0.35f && c.r - c.b > 0.3f;
            foreach (var mi in b.m_Meshes ?? new ObjectMeshInfo[0])
            {
                if (mi?.m_Mesh == null || !mi.m_Mesh.TryGet(out ColorProperties cp) || cp.m_ColorVariations == null || cp.m_ColorVariations.Count == 0) continue;
                for (int v = 0; v < cp.m_ColorVariations.Count; v++)
                    Log.Debug($"Ambulance station mesh {mi.m_Mesh.name} colours {v}: {string.Join(", ", cp.m_ColorVariations[v].m_Colors)}");
                var c = cp.m_ColorVariations[0].m_Colors;
                UnityEngine.Color Pick(int i) => i < c.Length ? (Yellow(c[i]) ? red : c[i]) : UnityEngine.Color.white;
                return new Game.Rendering.ColorSet { m_Channel0 = Pick(0), m_Channel1 = Pick(1), m_Channel2 = Pick(2) };
            }
            Log.Warn("Ambulance station: building mesh has no colour channels, taxi colours are in the texture and stay");
            return null;
        }

        private string FindAmbulanceIcon()
        {
            using var vehicles = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<AmbulanceData>()).ToEntityArray(Allocator.Temp);
            foreach (var e in vehicles)
                if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p.TryGet(out UIObject vui) && !string.IsNullOrEmpty(vui.m_Icon))
                    return vui.m_Icon;
            return null;
        }
    }
}
