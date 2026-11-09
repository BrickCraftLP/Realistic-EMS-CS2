using Game;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Keeps ambulances in <see cref="Held"/> on scene: residents only board an ambulance that has
    /// AmbulanceFlags.AtTarget (ResidentAISystem), and AmbulanceAISystem.LoadPatients waits as long as the patient
    /// has not boarded. Clearing AtTarget therefore holds the ambulance with the patient still outside.
    /// OnSceneCareSystem decides who is held (on-scene care, waiting for the NEF).
    /// Two instances run (after AmbulanceAISystem and before ResidentAISystem) so the flag is never visible in between.
    /// </summary>
    public partial class CrewHoldSystem : GameSystemBase
    {
        public static readonly HashSet<Entity> Held = new HashSet<Entity>();

        protected override void OnUpdate() => ClearAtTarget(EntityManager);

        public static void ClearAtTarget(EntityManager em)
        {
            if (Held.Count == 0) return;
            foreach (var e in Held)
            {
                if (!em.Exists(e) || !em.HasComponent<Ambulance>(e)) continue;
                var a = em.GetComponentData<Ambulance>(e);
                if ((a.m_State & AmbulanceFlags.AtTarget) == 0) continue;
                a.m_State &= ~AmbulanceFlags.AtTarget;
                em.SetComponentData(e, a);
            }
        }
    }

    public partial class CrewHoldLateSystem : GameSystemBase
    {
        protected override void OnUpdate() => CrewHoldSystem.ClearAtTarget(EntityManager);
    }
}
