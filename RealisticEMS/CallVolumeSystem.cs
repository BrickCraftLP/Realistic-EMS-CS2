using Game;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    public enum CallVolume { Few, Balanced, Many }

    /// <summary>Marks health events raised by CallVolumeSystem itself, so "Many" does not double its own calls.</summary>
    public struct ExtraCall : IComponentData { }

    /// <summary>
    /// Scales vanilla sick/injured calls. Runs before AddHealthProblemSystem applies the events.
    /// Few: drops every second new sick/injured event. Many: raises one extra event on a random healthy citizen for each one.
    /// Deaths are never touched.
    /// </summary>
    // ponytail: halving/doubling is a fixed ratio; make it a percentage slider if players want finer control.
    public partial class CallVolumeSystem : GameSystemBase
    {
        private EntityQuery m_Events, m_Healthy;
        private uint m_Seen;
        private readonly System.Random m_Rnd = new System.Random();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Events = GetEntityQuery(ComponentType.ReadOnly<Event>(), ComponentType.ReadOnly<Game.Events.AddHealthProblem>(), ComponentType.Exclude<ExtraCall>());
            m_Healthy = GetEntityQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<CurrentBuilding>(), ComponentType.Exclude<HealthProblem>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            RequireForUpdate(m_Events);
        }

        protected override void OnUpdate()
        {
            var volume = Mod.Setting.CallVolume;
            if (volume == CallVolume.Balanced) return;
            using var events = m_Events.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> citizens = default;
            try
            {
                foreach (var e in events)
                {
                    var ev = EntityManager.GetComponentData<Game.Events.AddHealthProblem>(e);
                    bool call = (ev.m_Flags & (HealthProblemFlags.Sick | HealthProblemFlags.Injured)) != 0 && (ev.m_Flags & HealthProblemFlags.Dead) == 0;
                    if (!call) continue;
                    if (volume == CallVolume.Few)
                    {
                        if (++m_Seen % 2 == 0) EntityManager.DestroyEntity(e);
                        continue;
                    }
                    if (!citizens.IsCreated) citizens = m_Healthy.ToEntityArray(Allocator.Temp);
                    if (citizens.Length == 0) continue;
                    var extra = EntityManager.CreateEntity(ComponentType.ReadWrite<Event>(), ComponentType.ReadWrite<Game.Events.AddHealthProblem>(), ComponentType.ReadWrite<ExtraCall>());
                    EntityManager.SetComponentData(extra, new Game.Events.AddHealthProblem { m_Event = Entity.Null, m_Target = citizens[m_Rnd.Next(citizens.Length)], m_Flags = ev.m_Flags });
                }
            }
            finally { if (citizens.IsCreated) citizens.Dispose(); }
        }
    }
}
