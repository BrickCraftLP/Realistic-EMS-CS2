using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Clones the smallest vanilla clinic into an "Ambulance Depot": 0 beds, cannot treat, many ambulances, half upkeep.
    /// With 0 beds the building never gets HasRoomForPatients/CanCure*, so pathfinding never picks it as a transport target.
    /// </summary>
    public partial class DepotPrefabSystem : GameSystemBase
    {
        public const string DepotName = "RealisticEMS_AmbulanceDepot";
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Query;
        private bool m_Done;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<HospitalData>());
        }

        protected override void OnUpdate()
        {
            if (m_Depot != null && m_DiagFrames < 600 && ++m_DiagFrames % 300 == 0) LogDiagnostics();
            if (m_Done || m_Query.IsEmpty) return;
            m_Done = true;

            BuildingPrefab clinic = null;
            int beds = int.MaxValue;
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                if (!m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) || !(p is BuildingPrefab b) || b.Has<ServiceUpgrade>() || !b.TryGet(out Hospital h)) continue;
                Mod.log.Info($"Hospital prefab: {b.name} beds={h.m_PatientCapacity} ambulances={h.m_AmbulanceCapacity}");
                if (b.name == DepotName) return; // already added (e.g. world recreated)
                if (b.name == "MedicalClinic01") { clinic = b; beds = -1; } // standard clinic, always in the menu
                else if (h.m_PatientCapacity > 0 && h.m_PatientCapacity < beds) { clinic = b; beds = h.m_PatientCapacity; }
            }
            if (clinic == null) { Mod.log.Error("No clinic prefab found, depot not added"); return; }

            var depot = (BuildingPrefab)clinic.Clone(DepotName);
            depot.Remove<ObsoleteIdentifiers>();
            // Theme/DLC-restricted copies are hidden from the toolbar.
            depot.Remove<ThemeObject>();
            depot.Remove<ContentPrerequisite>();
            Mod.log.Info("Depot components: " + string.Join(", ", depot.components.ConvertAll(c => c.GetType().Name)));
            var hospital = depot.GetComponent<Hospital>();
            hospital.m_PatientCapacity = 0;
            hospital.m_AmbulanceCapacity = Mod.Setting.DepotAmbulances;
            hospital.m_MedicalHelicopterCapacity = 0;
            hospital.m_TreatDiseases = false;
            hospital.m_TreatInjuries = false;
            if (depot.TryGet(out ServiceConsumption upkeep)) upkeep.m_Upkeep /= 2;
            if (depot.TryGet(out UIObject ui))
            {
                ui.m_Priority += 1; // sort right after the clinic
                if (FindAmbulanceIcon() is string icon) ui.m_Icon = icon;
                Mod.log.Info($"Depot UI: group={ui.m_Group?.name ?? "NULL"} icon={ui.m_Icon} priority={ui.m_Priority}");
            }
            bool added = m_PrefabSystem.AddPrefab(depot);
            Mod.log.Info($"AddPrefab({DepotName}) = {added}, cloned from {clinic.name}");
            m_Depot = depot;
        }

        private BuildingPrefab m_Depot;
        private int m_DiagFrames;

        /// <summary>Icon of a vanilla ambulance vehicle, so the depot is distinguishable from the clinic.</summary>
        private string FindAmbulanceIcon()
        {
            using var vehicles = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<AmbulanceData>()).ToEntityArray(Allocator.Temp);
            foreach (var e in vehicles)
                if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p.TryGet(out UIObject vui) && !string.IsNullOrEmpty(vui.m_Icon))
                    return vui.m_Icon;
            return null;
        }

        /// <summary>Logs whether the depot made it into its toolbar group (the toolbar lists the group's UIGroupElement buffer).</summary>
        private void LogDiagnostics()
        {
            if (!m_PrefabSystem.TryGetEntity(m_Depot, out var e)) { Mod.log.Info("Diag: depot has no entity yet"); return; }
            var em = EntityManager;
            string group = "none", inGroup = "n/a";
            if (em.HasComponent<UIObjectData>(e))
            {
                var g = em.GetComponentData<UIObjectData>(e).m_Group;
                group = g == Entity.Null ? "NULL" : m_PrefabSystem.GetPrefabName(g);
                inGroup = "no";
                if (g != Entity.Null && em.HasBuffer<UIGroupElement>(g))
                    foreach (var el in em.GetBuffer<UIGroupElement>(g, true))
                        if (el.m_Prefab == e) { inGroup = "yes"; break; }
            }
            bool locked = em.HasComponent<Locked>(e) && em.IsComponentEnabled<Locked>(e);
            Mod.log.Info($"Diag: depot entity {e.Index} UIObjectData group={group} inGroupBuffer={inGroup} locked={locked}");
        }
    }
}
