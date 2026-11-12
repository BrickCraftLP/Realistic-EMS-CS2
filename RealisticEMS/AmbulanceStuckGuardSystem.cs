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
        private const int StandingFrames = 1200; // ~20 s standing on the road outside a call
        private readonly Dictionary<Entity, int> m_Standing = new Dictionary<Entity, int>();

        /// <summary>
        /// Diagnostic: logs once why an ambulance/NEF stands still on the road while it is not on scene
        /// (blocker, lane, path state). Vehicles at a call or parked by SidewalkParkingSystem are skipped.
        /// </summary>
        private void LogStanding(Unity.Collections.NativeArray<Entity> entities)
        {
            var em = EntityManager;
            var alive = new HashSet<Entity>();
            foreach (var e in entities)
            {
                alive.Add(e);
                float speed = em.HasComponent<Game.Objects.Moving>(e) ? Unity.Mathematics.math.length(em.GetComponentData<Game.Objects.Moving>(e).m_Velocity) : 0f;
                var a = em.GetComponentData<Ambulance>(e);
                bool atCall = CrewHoldSystem.Held.Contains(e) || (a.m_State & (AmbulanceFlags.AtTarget | AmbulanceFlags.Disembarking)) != 0
                              || World.GetExistingSystemManaged<SidewalkParkingSystem>().IsParked(e);
                if (speed > 0.5f || atCall) { m_Standing.Remove(e); continue; }
                int n = m_Standing.TryGetValue(e, out var f) ? f + 1 : 1;
                m_Standing[e] = n;
                if (n != StandingFrames) continue;
                var lane = em.GetComponentData<CarCurrentLane>(e);
                var po = em.GetComponentData<PathOwner>(e);
                var blocker = em.HasComponent<Blocker>(e) ? em.GetComponentData<Blocker>(e) : default;
                int pathLen = em.HasBuffer<PathElement>(e) ? em.GetBuffer<PathElement>(e).Length : -1;
                int nav = em.HasBuffer<CarNavigationLane>(e) ? em.GetBuffer<CarNavigationLane>(e).Length : -1;
                var target = em.HasComponent<Target>(e) ? em.GetComponentData<Target>(e).m_Target : Entity.Null;
                var owner = em.HasComponent<Owner>(e) ? em.GetComponentData<Owner>(e).m_Owner : Entity.Null;
                Mod.log.Warn($"STEHT {e.Index}: {a.m_State} | Ziel {target.Index} Depot {owner.Index} | Spur {lane.m_Lane.Index} pos {lane.m_CurvePosition} " +
                             $"Wechsel {lane.m_ChangeLane.Index} Flags {lane.m_LaneFlags} | Blocker {blocker.m_Blocker.Index} {blocker.m_Type} | " +
                             $"Pfad {po.m_State} {po.m_ElementIndex}/{pathLen} nav {nav} | Stopped {em.HasComponent<Game.Objects.Stopped>(e)} " +
                             $"NEF {NefDispatchSystem.NefByPatient.ContainsValue(e)} OnRoad {SidewalkParkingSystem.OnRoad.Contains(e)}");
            }
            if ((m_Frame & 1023) == 0)
                foreach (var e in new List<Entity>(m_Standing.Keys)) if (!alive.Contains(e)) m_Standing.Remove(e);
        }

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
            LogStanding(entities);
            if ((m_Frame & 1023) == 0)
                foreach (var e in new List<Entity>(m_Stuck.Keys))
                    if (m_Frame - m_Stuck[e].last > EpisodeGapFrames) m_Stuck.Remove(e);
        }
    }
}
