using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Prefabs;
using Unity.Mathematics;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Hospital = Game.Buildings.Hospital;

namespace RealisticEMS
{
    /// <summary>
    /// Vanilla picks the transport target by pathfinding (HealthcarePathfindSetup.SetupHospitalsJob): cheapest
    /// travel cost among hospitals whose CanCure* flag matches the patient. Full hospitals only get a +120 penalty.
    /// We hide hospitals that fail <see cref="CanAcceptPatients"/> by clearing their CanCure* flags after
    /// HospitalAISystem recomputes them, so pathfinding never targets them. No Burst job is patched.
    /// </summary>
    public partial class HospitalSelectionSystem : GameSystemBase
    {
        private const HospitalFlags CureFlags = HospitalFlags.CanCureDisease | HospitalFlags.CanCureInjury;
        private EntityQuery m_Query;
        private bool m_LastAnyRoom = true;
        private int m_SuspendFrames;
        private readonly System.Collections.Generic.Dictionary<Entity, HospitalFlags> m_Cleared = new System.Collections.Generic.Dictionary<Entity, HospitalFlags>();

        /// <summary>
        /// A hospital pathfind failed (no hospital both free and able to treat this patient). Restore the hidden
        /// hospitals and stop filtering for a while so the retried search falls back to vanilla ranking.
        /// </summary>
        // ponytail: global suspension, so other ambulances searching in this window may also pick a full hospital.
        public void Suspend(int frames)
        {
            m_SuspendFrames = frames;
            foreach (var kv in m_Cleared)
            {
                if (!EntityManager.Exists(kv.Key) || !EntityManager.HasComponent<Hospital>(kv.Key)) continue;
                var h = EntityManager.GetComponentData<Hospital>(kv.Key);
                h.m_Flags |= kv.Value;
                EntityManager.SetComponentData(kv.Key, h);
            }
            m_Cleared.Clear();
        }

        /// <summary>Single rule for "may this building receive a patient". Depots (0 beds) never get HasRoomForPatients.</summary>
        public static bool CanAcceptPatients(in Hospital hospital) => (hospital.m_Flags & HospitalFlags.HasRoomForPatients) != 0;

        /// <summary>
        /// Last-resort target when the vanilla hospital pathfind finds nothing (e.g. patient health outside every
        /// hospital's range): nearest building with beds that treats this kind of problem, free beds preferred.
        /// Ignores health range on purpose; HospitalAISystem only evicts on disease/injury mismatch.
        /// </summary>
        // ponytail: straight-line distance, O(hospitals). Fine for city-scale counts.
        public static Entity FindNearestHospital(EntityManager em, Entity patient, float3 from)
        {
            var flags = em.HasComponent<HealthProblem>(patient) ? em.GetComponentData<HealthProblem>(patient).m_Flags : HealthProblemFlags.Sick;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Hospital>(), ComponentType.ReadOnly<Patient>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            using var hospitals = query.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null;
            float bestScore = float.MaxValue;
            foreach (var h in hospitals)
            {
                var prefab = em.GetComponentData<PrefabRef>(h).m_Prefab;
                if (!em.HasComponent<HospitalData>(prefab)) continue;
                var data = em.GetComponentData<HospitalData>(prefab);
                if (data.m_PatientCapacity <= 0) continue;
                if ((flags & HealthProblemFlags.Sick) != 0 && !data.m_TreatDiseases) continue;
                if ((flags & HealthProblemFlags.Injured) != 0 && !data.m_TreatInjuries) continue;
                float score = math.distance(from, em.GetComponentData<Game.Objects.Transform>(h).m_Position);
                if (!CanAcceptPatients(em.GetComponentData<Hospital>(h))) score += 100000f; // full: only if nothing else
                if (score < bestScore) { bestScore = score; best = h; }
            }
            return best;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadWrite<Hospital>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            RequireForUpdate(m_Query);
        }

        protected override void OnUpdate()
        {
            if (!Mod.Setting.RejectFullHospitals || m_SuspendFrames-- > 0) return;
            // ponytail: HasRoomForPatients is refreshed by HospitalAISystem every 256 frames, so a hospital that fills
            // between ticks still accepts until then. Count Patient buffers against combined HospitalData if that matters.
            var hospitals = m_Query.ToComponentDataArray<Hospital>(Allocator.Temp);
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            // If no hospital has a free bed, rejecting all would make the hospital pathfind fail, and vanilla then
            // brings the patient to the ambulance's own building (= the depot). Fall back to vanilla ranking instead.
            bool anyRoom = false;
            for (int i = 0; i < hospitals.Length && !anyRoom; i++) anyRoom = CanAcceptPatients(hospitals[i]);
            if (anyRoom != m_LastAnyRoom) { Mod.log.Info(anyRoom ? "Free beds available: rejecting full hospitals" : "All hospitals full: vanilla ranking"); m_LastAnyRoom = anyRoom; }
            if (!anyRoom) return;
            bool changed = false;
            for (int i = 0; i < hospitals.Length; i++)
            {
                var h = hospitals[i];
                if (CanAcceptPatients(h) || (h.m_Flags & CureFlags) == 0) continue;
                m_Cleared[entities[i]] = h.m_Flags & CureFlags;
                h.m_Flags &= ~CureFlags;
                hospitals[i] = h;
                changed = true;
            }
            if (changed) m_Query.CopyFromComponentDataArray(hospitals);
        }
    }
}
