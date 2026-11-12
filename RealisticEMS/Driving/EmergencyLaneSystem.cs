using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace RealisticEMS
{
    /// <summary>
    /// Emergency driving for every vehicle with lights (ambulance, NEF, police, fire engine), using only flags and
    /// fields vanilla already understands (no Burst patch). Runs after CarNavigationSystem.Actions every frame.
    /// The vehicle always stays on real lanes; cars on its way stop and are hidden from it (PassThrough, Yielding.cs).
    /// Per vehicle, in this order:
    ///  1. Push blockers immediately (vanilla waits 5 s).                                    PushBlockers
    ///  2. Roundabout: vanilla behaviour, nothing else.
    ///  3. Any road with lanes of 2.7 m or more: Rettungsgasse, every lane shifted as a block
    ///     (Corridor.cs; highways open it in every jam, other roads when the vehicle comes;
    ///     one lane each way: both directions to their kerb). The vehicle drives the
    ///     corridor line without changing lane (Highway.cs) to its end, then turns any way (HighwayExit.cs). EmergencyCorridor
    ///  4. Free lane in our direction: change there, merge back (NormalRoad.cs).            EmergencyLaneChange
    ///  5. Narrow roads, or corridor off: overtake on the closed oncoming lane              OncomingOvertake
    ///     or cars pull to the kerb and the vehicle passes over the centre line.             EmergencyCorridor
    ///  6. Few cars waiting at red ahead: their signal turns green.                          ClearRedLight
    /// Left-hand traffic is mirrored (m_Side).
    /// </summary>
    public partial class EmergencyLaneSystem : GameSystemBase
    {
        private EntityQuery m_Query;
        private Game.City.CityConfigurationSystem m_City;

        /// <summary>Everything this system remembers about one emergency vehicle.</summary>
        private class EvState
        {
            public int Blocked, Stuck;           // frames behind a slower car / standing behind one
            public bool Changing;                // in a lane change we started
            public Entity RouteLane;             // lane we left to overtake, merge back before the junction
            public (Entity lane, Entity oldConnection, Entity newConnection)? Swap; // route lane change: connection to swap in
            public bool InGap;                   // driving in a Rettungsgasse
            public int GapGrace;                 // frames left forming the gap without a slow car close by
            public int GapStill;                 // frames standing in the gap (diagnostic)
            public bool InSqueeze;               // one-lane corridor: cars at the kerb, vehicle over the centre line
            public readonly HashSet<Entity> Kept = new HashSet<Entity>(); // squeeze cars that may be missing from the lane list
            public int Grace, Still, Cooldown, GreenIgnore;
            public float Offset;                 // squeeze offset kept between two gaps in the queue
            public OncomingPass Oncoming;
            public int OncomingRetryFrame;
            public readonly HashSet<Entity> Forced = new HashSet<Entity>(); // vehicles it stood behind in the gap: driven through
            public readonly HashSet<Entity> Pinned = new HashSet<Entity>(); // lanes given FixedLane in the gap (HighwayExit.cs)
            public Entity Retargeted;            // junction lane already chosen at the corridor end
        }
        private readonly Dictionary<Entity, EvState> m_State = new Dictionary<Entity, EvState>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_City = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Car>(), ComponentType.ReadWrite<CarCurrentLane>(), ComponentType.ReadOnly<Blocker>(), ComponentType.ReadOnly<Moving>() },
                Any = new[] { ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(), ComponentType.ReadOnly<Game.Vehicles.FireEngine>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_RoadEdges = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Road>(), ComponentType.ReadOnly<Game.Net.SubLane>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_ChangedEdges = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Updated>());
        }

        protected override void OnUpdate()
        {
            m_Watch.Restart();
            UpdateAll();
            m_Watch.Stop();
            Stats();
        }

        private void UpdateAll()
        {
            m_Frame++;
            var s = Mod.Setting;
            PassOnRoadObstacles();
            var sirenChanged = new List<Entity>();
            if (!s.SirenOnlyWhenNeeded && Quiet.Count > 0) { ReleaseSirens(null, sirenChanged); RefreshEffects(sirenChanged); sirenChanged.Clear(); }
            if (!s.PushBlockers && !s.EmergencyLaneChange && !s.EmergencyCorridor && !s.OncomingOvertake && !s.ClearRedLight && !s.SirenOnlyWhenNeeded
                && m_Held.Count == 0 && m_Hidden.Count == 0 && m_ShiftedAt.Count == 0) return;
            m_Side = m_City.leftHandTraffic ? -1f : 1f;
            var em = EntityManager;
            m_GapEvs.Clear();
            if (s.EmergencyCorridor) ScanJams(); else if (m_ActiveCws.Count > 0 || m_HighwayEdges.Count > 0) ClearCorridors();
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            var lanes = m_Query.ToComponentDataArray<CarCurrentLane>(Allocator.Temp);
            using var cars = m_Query.ToComponentDataArray<Car>(Allocator.Temp);
            using var blockers = m_Query.ToComponentDataArray<Blocker>(Allocator.Temp);
            bool changed = false;
            var sirenSeen = s.SirenOnlyWhenNeeded ? new HashSet<Entity>() : null;

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var lane = lanes[i];
                if ((cars[i].m_Flags & CarFlags.Emergency) == 0 || IsPedestrianStreet(em, lane.m_Lane))
                {
                    // Lights off (returning) or on a pedestrian street: give back the lanes it pinned in the corridor.
                    if (m_State.TryGetValue(e, out var old)) { ReleasePins(e, old, ref lane); if (!lane.Equals(lanes[i])) { lanes[i] = lane; changed = true; } }
                    m_State.Remove(e);
                    continue;
                }
                if (!m_State.TryGetValue(e, out var st)) m_State[e] = st = new EvState();
                var before = lane;
                Drive(e, st, ref lane, blockers[i]);
                if (!lane.Equals(before)) { lanes[i] = lane; changed = true; }
                if (sirenSeen != null) { sirenSeen.Add(e); if (UpdateSiren(e, st, lane)) sirenChanged.Add(e); }
            }
            if (changed) m_Query.CopyFromComponentDataArray(lanes);
            lanes.Dispose();
            if (sirenSeen != null) ReleaseSirens(sirenSeen, sirenChanged); // lights off, parked, gone: vanilla siren again
            RefreshEffects(sirenChanged);

            ApplyCorridors();
            ReleaseHeld();
            RestoreHidden();
            UpdateRerouted();
            CleanupEased();
            if ((m_Frame & 255) == 0)
                foreach (var e in new List<Entity>(m_State.Keys)) if (!em.Exists(e) || em.HasComponent<Deleted>(e)) m_State.Remove(e);
        }

        private void Drive(Entity e, EvState st, ref CarCurrentLane lane, Blocker blocker)
        {
            var em = EntityManager;
            var s = Mod.Setting;
            float speed = Speed(e);
            float limit = em.HasComponent<CarLane>(lane.m_Lane) ? em.GetComponentData<CarLane>(lane.m_Lane).m_SpeedLimit : 14f;
            // Queue = a car limits us and we are clearly below the limit (creeping counts). A trailer counts as its truck.
            var blockerCar = blocker.m_Blocker == Entity.Null ? Entity.Null : Tractor(blocker.m_Blocker);
            bool blockedByCar = blockerCar != Entity.Null && em.HasComponent<Car>(blockerCar) && speed < math.max(3f, 0.6f * limit);
            st.Blocked = blockedByCar ? st.Blocked + 1 : math.min(st.Blocked, 0); // negative = wait after a lane change
            st.Stuck = blockedByCar && speed < 0.5f ? st.Stuck + 1 : 0;
            if (st.Cooldown > 0) st.Cooldown--;
            if (st.GreenIgnore > 0) st.GreenIgnore--;
            FinishRouteLaneSwap(e, st, lane);

            if (s.PushBlockers && blockedByCar) lane.m_LaneFlags |= CarLaneFlags.PushBlockers;

            // During our lane change vanilla still sees the car ahead in the old lane as blocker, and a standing vehicle
            // cannot steer over. Ignore that blocker until the change is done.
            if (lane.m_ChangeLane != Entity.Null && st.Changing) lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
            else if (st.Changing) { st.Changing = false; ReprojectAfterChange(e, ref lane); }

            if (InRoundabout(e, lane)) { st.InGap = false; ReleasePins(e, st, ref lane); EndSqueeze(st); EndOncoming(e, st); return; }

            bool gap = s.EmergencyCorridor && DriveGap(e, st, ref lane);
            if (gap != st.InGap) Mod.log.Info($"Einsatzfahrzeug {e.Index}: Rettungsgasse {(gap ? "gebildet" : "beendet")}");
            st.InGap = gap;
            st.GapStill = gap && speed < 0.3f ? st.GapStill + 1 : 0;
            if (!gap) { st.Forced.Clear(); ReleasePins(e, st, ref lane); }
            if (gap)
            {
                // Vanilla re-plans this vehicle only every 16 frames and then brakes for the first listed car until the
                // next time; a civilian (or a standing vehicle) it braked for is ignored from then on. A moving emergency
                // vehicle ahead stays a blocker: the convoy follows it.
                if (blockerCar != Entity.Null && blockerCar != e && em.HasComponent<Car>(blockerCar) && (IsCivilian(blockerCar) || Speed(blockerCar) < 0.5f))
                    lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
                if (st.GapStill >= 60) Unblock(e, st, ref lane, blocker); // ~1 s standing: drive through what it brakes for
                var tr = em.GetComponentData<Game.Objects.Transform>(e);
                m_GapEvs.Add((tr.m_Position, math.forward(tr.m_Rotation)));
                EndSqueeze(st); st.Oncoming = null; return;
            }

            if (s.EmergencyLaneChange && lane.m_ChangeLane == Entity.Null) LaneChange(e, st, ref lane, blockedByCar);
            SingleLane(e, st, ref lane, blockerCar, speed, blockedByCar);

            if (s.ClearRedLight && blockedByCar && !st.InSqueeze && st.Oncoming == null && RemainingDistance(lane) < 60f) ClearCarsAtRedLight(e, lane);
        }

        /// <summary>On a roundabout or about to enter one: vanilla behaviour (no gap, no squeeze, nobody stopped).</summary>
        // ponytail: only roundabouts built as a node upgrade (Game.Net.Roundabout); a ring of one-way roads counts as normal road.
        private bool InRoundabout(Entity e, CarCurrentLane lane)
        {
            if (IsRoundabout(lane.m_Lane)) return true;
            return RemainingDistance(lane) < 50f && IsRoundabout(NextNodeLane(e));
        }

        private bool IsRoundabout(Entity lane) =>
            lane != Entity.Null && EntityManager.HasComponent<Owner>(lane)
            && EntityManager.HasComponent<Game.Net.Roundabout>(EntityManager.GetComponentData<Owner>(lane).m_Owner);

        private void LogStanding(Entity e, CarCurrentLane lane, Blocker blocker)
        {
            var em = EntityManager;
            var b = blocker.m_Blocker;
            string on = b != Entity.Null && em.HasComponent<CarCurrentLane>(b) ? $"auf Spur {em.GetComponentData<CarCurrentLane>(b).m_Lane.Index}" : "ohne Spur";
            bool listed = false;
            if (em.HasBuffer<LaneObject>(lane.m_Lane)) foreach (var o in em.GetBuffer<LaneObject>(lane.m_Lane, true)) listed |= o.m_LaneObject == b;
            Mod.log.Info($"Einsatzfahrzeug {e.Index}: steht seit 1 s in der Rettungsgasse (Spur {lane.m_Lane.Index}, Wechsel {lane.m_ChangeLane.Index}), " +
                $"Blocker {b.Index} {blocker.m_Type} {on}, angehalten {m_Held.ContainsKey(b)}, ausgeblendet {m_Hidden.ContainsKey(Tractor(b))}, " +
                $"in eigener Spurliste {listed}: fährt durch");
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // A new world: entities from the old one are gone.
            m_State.Clear(); m_Held.Clear(); m_Rerouted.Clear(); m_Eased.Clear(); m_Slide.Clear(); m_ObstaclePass.Clear();
            m_Cw.Clear(); ClearCorridors(); m_ShiftedAt.Clear(); m_Settled.Clear(); m_Sliding.Clear(); m_Hidden.Clear(); m_GapEvs.Clear();
            ForcedGreen.Clear();
            Quiet.Clear(); m_SirenHold.Clear(); SirenMuteSystem.Masked.Clear();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (mode == GameMode.Game) SweepOrphanLanes();
        }
    }
}
