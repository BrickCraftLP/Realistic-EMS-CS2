using Game;
using Game.Citizens;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    public enum CallVolume { Few, Balanced, Many }

    /// <summary>
    /// Scales vanilla sick/injured calls down. Runs before AddHealthProblemSystem applies the events.
    /// Few keeps every 4th new sick/injured event, Balanced every 2nd, Many all (vanilla). Deaths are never touched.
    /// </summary>
    // ponytail: fixed 1/4, 1/2, 1/1 ratios; make it a percentage slider if players want finer control.
    public partial class CallVolumeSystem : GameSystemBase
    {
        private EntityQuery m_Events;
        private uint m_Seen;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Events = GetEntityQuery(ComponentType.ReadOnly<Event>(), ComponentType.ReadOnly<Game.Events.AddHealthProblem>());
            RequireForUpdate(m_Events);
        }

        protected override void OnUpdate()
        {
            uint keep = Mod.Setting.CallVolume switch { CallVolume.Few => 4u, CallVolume.Balanced => 2u, _ => 1u };
            if (keep == 1) return;
            using var events = m_Events.ToEntityArray(Allocator.Temp);
            foreach (var e in events)
            {
                var ev = EntityManager.GetComponentData<Game.Events.AddHealthProblem>(e);
                bool call = (ev.m_Flags & (HealthProblemFlags.Sick | HealthProblemFlags.Injured)) != 0 && (ev.m_Flags & HealthProblemFlags.Dead) == 0;
                if (call && ++m_Seen % keep != 0) EntityManager.DestroyEntity(e);
            }
        }
    }
}
