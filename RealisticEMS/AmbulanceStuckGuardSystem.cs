using Game;
using Game.Common;
using Game.Pathfind;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace RealisticEMS
{
    /// <summary>
    /// Vanilla StuckMovingObjectSystem flags a vehicle Stuck when its blocker chain loops back to itself (two vehicles
    /// waiting for each other, e.g. halfway through a lane change or in a corridor), and AmbulanceAISystem then deletes
    /// the ambulance, patient and all. Runs before AmbulanceAISystem: clears the flag and lets the ambulance drive
    /// through its blocker instead (vanilla IgnoreBlocker), which breaks the loop. Only when it keeps getting stuck for
    /// ~2 minutes is vanilla allowed to delete it, so a truly trapped ambulance cannot block a road forever.
    /// </summary>
    public partial class AmbulanceStuckGuardSystem : GameSystemBase
    {
        private const int GiveUpFrames = 7200; // ~2 min
        private const int EpisodeGapFrames = 600; // not stuck for ~10 s: the next time is a new episode
        private EntityQuery m_Query;
        private readonly Dictionary<Entity, (int first, int last)> m_Stuck = new Dictionary<Entity, (int, int)>();
        private int m_Frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadWrite<PathOwner>(), ComponentType.ReadWrite<CarCurrentLane>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(m_Query);
        }

        protected override void OnUpdate()
        {
            m_Frame++;
            var em = EntityManager;
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                var po = em.GetComponentData<PathOwner>(e);
                if ((po.m_State & PathFlags.Stuck) == 0) continue;
                bool sameEpisode = m_Stuck.TryGetValue(e, out var s) && m_Frame - s.last < EpisodeGapFrames;
                if (sameEpisode && m_Frame - s.first > GiveUpFrames) continue; // hopeless: vanilla removes it
                m_Stuck[e] = (sameEpisode ? s.first : m_Frame, m_Frame);
                if (!sameEpisode) Mod.log.Info($"Ambulance {e.Index}: blockiert sich mit anderem Fahrzeug, fährt durch statt zu verschwinden");

                po.m_State &= ~PathFlags.Stuck;
                em.SetComponentData(e, po);
                var lane = em.GetComponentData<CarCurrentLane>(e);
                lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
                em.SetComponentData(e, lane);
            }
            if ((m_Frame & 1023) == 0)
                foreach (var e in new List<Entity>(m_Stuck.Keys))
                    if (m_Frame - m_Stuck[e].last > EpisodeGapFrames) m_Stuck.Remove(e);
        }
    }
}
