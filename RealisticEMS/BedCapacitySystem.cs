using Game;
using Game.Prefabs;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticEMS
{
    /// <summary>
    /// Scales the patient capacity of every hospital prefab (buildings and extensions) by Setting.BedCapacityPercent,
    /// so hospitals fill up during a rush. Buildings read HospitalData from their prefab, so this applies live.
    /// Over-full hospitals keep their patients and stop admitting (HospitalAISystem clears HasRoomForPatients).
    /// Depots (0 beds) are untouched.
    /// </summary>
    public partial class BedCapacitySystem : GameSystemBase
    {
        private EntityQuery m_Query;
        private readonly Dictionary<Entity, int> m_Original = new Dictionary<Entity, int>();
        private int m_Applied = -1;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadWrite<HospitalData>());
            RequireForUpdate(m_Query);
        }

        /// <summary>Unscaled capacity of a prefab, for the UI.</summary>
        public int OriginalBeds(Entity prefab) => m_Original.TryGetValue(prefab, out var b) ? b : -1;

        protected override void OnUpdate()
        {
            int percent = Mod.Setting.BedCapacityPercent;
            using var prefabs = m_Query.ToEntityArray(Allocator.Temp);
            bool newPrefabs = false;
            foreach (var p in prefabs) if (!m_Original.ContainsKey(p)) { newPrefabs = true; break; }
            if (percent == m_Applied && !newPrefabs) return;

            foreach (var p in prefabs)
            {
                var data = EntityManager.GetComponentData<HospitalData>(p);
                if (!m_Original.TryGetValue(p, out int original)) m_Original[p] = original = data.m_PatientCapacity;
                if (original <= 0) continue;
                // ponytail: one global factor; per-type values (clinic vs. hospital) would need a table in settings.
                data.m_PatientCapacity = math.max(1, (int)math.round(original * percent / 100f));
                EntityManager.SetComponentData(p, data);
            }
            if (percent != m_Applied) Mod.log.Info($"Bettenkapazität auf {percent}% gesetzt ({m_Original.Count} Spitalstypen)");
            m_Applied = percent;
        }
    }
}
