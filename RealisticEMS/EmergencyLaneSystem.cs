using Colossal.Mathematics;
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
using SubLane = Game.Net.SubLane;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace RealisticEMS
{
    /// <summary>
    /// Helps ambulances with lights get out of queues, using only flags vanilla already understands
    /// (no Burst patch). Runs after CarNavigationSystem every frame:
    ///  1. PushBlockers: vanilla sets it after 5 s of temporary blocking; we set it immediately.
    ///  2. Lane change: if blocked and a neighbour lane of the same direction (same SlaveLane group) is free
    ///     ~40 m ahead, start a vanilla lane change (m_ChangeLane). ~40 m before the lane ends we change back to
    ///     the route lane, else the path is recalculated.
    ///  3. Emergency corridor: no free lane -> all cars around stop, those ahead move to the right edge of the
    ///     lane, the ambulance to the left edge, and it passes them; they drive on once it has passed.
    ///  4. Drive-through: after N seconds still blocked, IgnoreBlocker (vanilla stuck handling; visibly clips).
    ///  5. Oncoming lane: one lane per direction and trucks in the queue (or slow traffic and the oncoming lane already
    ///     free) -> the oncoming lane is closed at its start node, the ambulance overtakes the whole (frozen) queue on
    ///     it and merges back. Fire engines with lights get only this one.
    /// </summary>
    public partial class EmergencyLaneSystem : GameSystemBase
    {
        private const float FreeDistance = 40f;
        private const float MergeBackDistance = 40f;
        private EntityQuery m_Query;
        private readonly Dictionary<Entity, int> m_BlockedFrames = new Dictionary<Entity, int>();
        private readonly HashSet<Entity> m_Changing = new HashSet<Entity>(); // ambulances in a lane change we started
        private readonly Dictionary<Entity, Entity> m_RouteLane = new Dictionary<Entity, Entity>(); // ambulance -> lane we left

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Car>(), ComponentType.ReadWrite<CarCurrentLane>(), ComponentType.ReadOnly<Blocker>(), ComponentType.ReadOnly<Moving>() },
                // Police with lights uses the same help; fire engines only the oncoming overtake.
                Any = new[] { ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(), ComponentType.ReadOnly<Game.Vehicles.FireEngine>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(m_Query);
        }

        /// <summary>Pedestrian street lane: vanilla marks its car lanes Forbidden, only service vehicles may enter.</summary>
        // ponytail: Forbidden is the only marker; if another road type ever uses it, it is treated as pedestrian too.
        public static bool IsPedestrianStreet(EntityManager em, Entity lane) =>
            em.HasComponent<CarLane>(lane) && (em.GetComponentData<CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0;

        protected override void OnUpdate()
        {
            var s = Mod.Setting;
            if (!s.PushBlockers && !s.EmergencyLaneChange &&!s.EmergencyCorridor && !s.MovingCorridor && !s.OncomingOvertake) return;
            var em = EntityManager;
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            var lanes = m_Query.ToComponentDataArray<CarCurrentLane>(Allocator.Temp);
            using var cars = m_Query.ToComponentDataArray<Car>(Allocator.Temp);
            using var blockers = m_Query.ToComponentDataArray<Blocker>(Allocator.Temp);
            using var moving = m_Query.ToComponentDataArray<Moving>(Allocator.Temp);
            bool changed = false;

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var lane = lanes[i];
                bool fire = em.HasComponent<Game.Vehicles.FireEngine>(e);
                bool emergency = (cars[i].m_Flags & CarFlags.Emergency) != 0
                    && (fire || s.PoliceEmergencyDriving || em.HasComponent<Ambulance>(e));
                if (emergency && IsPedestrianStreet(em, lane.m_Lane)) emergency = false; // no traffic to clear; vanilla lets it through
                if (!emergency) { m_BlockedFrames.Remove(e); m_StuckFrames.Remove(e); m_MovingActive.Remove(e); m_RouteLane.Remove(e); m_PendingSwap.Remove(e); m_Changing.Remove(e); m_Oncoming.Remove(e); EndCorridor(e); continue; }

                var before = lane;
                var blocker = blockers[i];
                float speed = math.length(moving[i].m_Velocity);
                float limit = em.HasComponent<CarLane>(lane.m_Lane) ? em.GetComponentData<CarLane>(lane.m_Lane).m_SpeedLimit : 14f;
                // Queue = a car limits us and we are clearly below the limit (creeping counts, not only standing).
                // Vanilla reports a trailer as the blocker; trailers have no Car, so look at the truck pulling it.
                var blockerCar = blocker.m_Blocker != Entity.Null && em.HasComponent<Controller>(blocker.m_Blocker) ? em.GetComponentData<Controller>(blocker.m_Blocker).m_Controller : blocker.m_Blocker;
                bool blockedByCar = blockerCar != Entity.Null && em.HasComponent<Car>(blockerCar) && speed < math.max(3f, 0.6f * limit);

                int blockedFrames = blockedByCar ? (m_BlockedFrames.TryGetValue(e, out var f) ? f + 1 : 1) : 0;
                m_BlockedFrames[e] = blockedFrames;
                // Really stuck (standing behind a car), not just slowed down: only then the corridor forms.
                int stuckFrames = blockedByCar && speed < 0.5f ? (m_StuckFrames.TryGetValue(e, out var sf) ? sf + 1 : 1) : 0;
                m_StuckFrames[e] = stuckFrames;
                FinishRouteLaneSwap(e, lane);

                if (!fire && s.PushBlockers && blockedByCar) lane.m_LaneFlags |= CarLaneFlags.PushBlockers;

                // During our lane change vanilla still treats the car ahead in the old lane as blocker, so a stopped
                // ambulance can neither move forward nor steer over (steering needs forward motion). Ignore that
                // blocker until the change is done; CheckBlocker drops the flag by itself when the blocker changes.
                if (lane.m_ChangeLane != Entity.Null && m_Changing.Contains(e)) lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
                else if (m_Changing.Remove(e)) ReprojectAfterChange(e, ref lane); // our change just completed

                if (s.EmergencyLaneChange && lane.m_ChangeLane == Entity.Null)
                {
                    if (m_RouteLane.TryGetValue(e, out var route) && route != lane.m_Lane && RemainingDistance(lane) < MergeBackDistance)
                    {
                        // Back onto the route lane before the node, if it is still in our group.
                        var back = ParallelLanes(lane, e).Find(p => p.lane == route);
                        if (back.lane == route && IsFree(route, back.pos, e, math.min(FreeDistance, LaneRemaining(route, back.pos))))
                            StartChange(ref lane, route, e, "zurück auf Routenspur");
                        else if (s.TurnFromAnyLane && CreateTemporaryTurn(e, lane))
                            Mod.log.Info($"Ambulance {e.Index}: biegt über temporäre Spur von Spur {lane.m_Lane.Index} ab");
                        else
                        {
                            var po = em.GetComponentData<Game.Pathfind.PathOwner>(e);
                            po.m_State |= Game.Pathfind.PathFlags.Obsolete; // vanilla repaths from the current lane
                            em.SetComponentData(e, po);
                            Mod.log.Info($"Ambulance {e.Index}: Routenspur besetzt, Route wird neu berechnet");
                        }
                        m_RouteLane.Remove(e);
                    }
                    // Not while in a corridor: its yielding cars are taken out of the lane lists, so other lanes look
                    // free and the ambulance would hop between lanes instead of driving through.
                    else if (blockedByCar && blockedFrames >= 6 && !m_InCorridor.Contains(e))
                        TryOvertake(ref lane, e);
                }

                if (fire)
                {
                    // Too big for a corridor: free-lane overtake above (multi-lane) or the oncoming lane (one lane per direction).
                    if (s.OncomingOvertake && lane.m_ChangeLane == Entity.Null
                        && (m_Oncoming.ContainsKey(e) || (blockedFrames >= 60 && StartOncoming(e, lane, blockerCar, speed)))
                        && !OncomingOvertake(e, ref lane, speed, blockerCar)) EndOncoming(e);
                    if (!lane.Equals(before)) { lanes[i] = lane; changed = true; }
                    continue;
                }

                // Two-lane road with trucks in the queue, or slow traffic and a free oncoming lane: overtake on the
                // closed oncoming lane instead of a corridor.
                bool oncoming = s.OncomingOvertake && lane.m_ChangeLane == Entity.Null
                    && (m_Oncoming.ContainsKey(e) || (blockedFrames >= 60 && StartOncoming(e, lane, blockerCar, speed)));
                if (oncoming && !OncomingOvertake(e, ref lane, speed, blockerCar)) { EndOncoming(e); oncoming = false; }

                // No lane change possible (or none started): form an emergency corridor in the own lane.
                if (m_GreenIgnore.TryGetValue(e, out var gi)) { if (gi > 1) m_GreenIgnore[e] = gi - 1; else m_GreenIgnore.Remove(e); }
                int cooldown = m_CorridorCooldown.TryGetValue(e, out var cd) ? cd - 1 : 0;
                if (cooldown > 0) m_CorridorCooldown[e] = cooldown; else m_CorridorCooldown.Remove(e);
                if (oncoming) { EndCorridor(e); m_MovingActive.Remove(e); }
                else if (s.EmergencyCorridor && lane.m_ChangeLane == Entity.Null && (m_InCorridor.Contains(e) || (stuckFrames >= CorridorStuckFrames && cooldown <= 0)))
                {
                    // The ambulance still cannot get past (something else blocks it): release the cars, try again later.
                    int still = m_InCorridor.Contains(e) && speed < 0.5f ? (m_CorridorStill.TryGetValue(e, out var st) ? st + 1 : 1) : 0;
                    m_CorridorStill[e] = still;
                    if (still > CorridorGiveUpFrames)
                    {
                        EndCorridor(e);
                        m_CorridorCooldown[e] = CorridorCooldownFrames;
                        Mod.log.Info($"Ambulance {e.Index}: kommt nicht durch, Rettungsgasse aufgelöst");
                    }
                    else if (Corridor(e, ref lane, blockerCar)) { if (m_InCorridor.Add(e)) Mod.log.Info($"Ambulance {e.Index}: Rettungsgasse auf Spur {lane.m_Lane.Index}"); }
                    else
                    {
                        if (m_InCorridor.Contains(e)) Mod.log.Info($"Ambulance {e.Index}: Rettungsgasse beendet (vorbei oder niemand mehr vor ihr)");
                        EndCorridor(e);
                    }
                }
                else
                {
                    if (m_InCorridor.Contains(e)) Mod.log.Info($"Ambulance {e.Index}: Rettungsgasse beendet ({(oncoming ? "Gegenfahrbahn" : lane.m_ChangeLane != Entity.Null ? "Spurwechsel" : "Einstellung aus")})");
                    EndCorridor(e);
                    // Still driving: traffic ahead slows down and keeps right. Once started it stays on while the
                    // ambulance slows down, else the cars are released and frozen again in turn (stop-and-go).
                    bool movingCorridor = s.MovingCorridor && lane.m_ChangeLane == Entity.Null && (speed >= 0.5f || m_MovingActive.Contains(e)) && cooldown <= 0
                        && MovingCorridor(e, ref lane, blockerCar, limit);
                    if (movingCorridor) m_MovingActive.Add(e); else m_MovingActive.Remove(e);
                }

                if (s.ClearRedLight && blockedByCar && !m_InCorridor.Contains(e) && RemainingDistance(lane) < 60f) ClearCarsAtRedLight(e, lane);

                if (!lane.Equals(before)) { lanes[i] = lane; changed = true; }
            }
            if (changed) m_Query.CopyFromComponentDataArray(lanes);
            HoldPassedCars();
            UpdateRerouted();
            CleanupTemporaryTurns();
            CleanupEased();
        }

        // ---- Emergency corridor (Rettungsgasse) ----

        // ponytail: cars always go right and the ambulance left (right-hand traffic); mirror for left-hand cities.
        private const float CorridorGap = 0.4f; // metres between a yielding car and the passing ambulance (smaller: less over the middle line)
        private const int CorridorStuckFrames = 30; // ~0.5 s standing behind a car
        private readonly Dictionary<Entity, int> m_StuckFrames = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, float> m_AmbulanceOffset = new Dictionary<Entity, float>(); // metres right of lane centre (negative = left)
        private const float CorridorAhead = 40f;
        private const int CorridorMaxCars = 6;
        private readonly HashSet<Entity> m_InCorridor = new HashSet<Entity>();
        private readonly HashSet<Entity> m_MovingActive = new HashSet<Entity>(); // moving corridor ran last frame
        // Cars that yield to an ambulance, kept until it has passed them: vanilla navigation updates a car only every
        // 16th frame (UpdateFrame), so a car we took out of the lane list is missing from it in between.
        private readonly Dictionary<Entity, HashSet<Entity>> m_CorridorCars = new Dictionary<Entity, HashSet<Entity>>();

        private readonly Dictionary<Entity, int> m_CorridorGrace = new Dictionary<Entity, int>(); // frames without cars ahead
        private const int CorridorGraceFrames = 90; // keep the offset between two gaps in the queue: no left-right swerving

        private const int CorridorGiveUpFrames = 600;    // ~10 s standing in the corridor
        private const int CorridorCooldownFrames = 1200; // ~20 s before the next try
        private readonly Dictionary<Entity, int> m_CorridorStill = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_CorridorCooldown = new Dictionary<Entity, int>();
        private const int GreenIgnoreFrames = 600; // ~10 s: a green that did not dissolve the queue is ignored
        private readonly Dictionary<Entity, int> m_GreenIgnore = new Dictionary<Entity, int>();

        private void EndCorridor(Entity e) { m_InCorridor.Remove(e); m_CorridorCars.Remove(e); m_CorridorGrace.Remove(e); m_AmbulanceOffset.Remove(e); m_CorridorStill.Remove(e); }

        /// <summary>
        /// All cars around the corridor stop (CarNavigation.m_MaxSpeed = 0 before CarMoveSystem): those ahead of the
        /// ambulance until it has passed them, those behind it until the corridor ends.
        /// Cars ahead move to the right edge: vanilla places a car at m_LanePosition * (lane width - car width) right of
        /// the lane centre; a standing car cannot steer, so its transform is also slid sideways each frame.
        /// Vanilla lets a vehicle ignore only one blocker, so a car that has made room is taken out of its lane's
        /// LaneObject list; the ambulance's speed check then no longer sees it. Navigation re-adds it by itself once
        /// we stop doing that (this system runs after CarNavigationSystem.Actions, which applies those updates).
        /// Returns false when there is nothing (left) to pass.
        /// </summary>
        private bool Corridor(Entity e, ref CarCurrentLane lane, Entity blocker)
        {
            var em = EntityManager;
            if (!em.HasComponent<Curve>(lane.m_Lane) || !em.HasBuffer<LaneObject>(lane.m_Lane) || LaneWidth(lane.m_Lane) < 2.5f) return false;
            if (!m_InCorridor.Contains(e) && !MayStartCorridor(e, lane, blocker)) { m_CorridorCooldown[e] = 120; return false; }
            float laneLength = math.max(1f, MathUtils.Length(em.GetComponentData<Curve>(lane.m_Lane).m_Bezier));
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            float me = lane.m_CurvePosition.x;
            var ahead = new List<Entity>();
            var behind = new List<Entity>();
            using (var objects = em.GetBuffer<LaneObject>(lane.m_Lane, true).ToNativeArray(Allocator.Temp))
            {
                foreach (var o in objects)
                {
                    var car = o.m_LaneObject;
                    if (car == e || !Yields(car)) continue;
                    float d = (forward ? o.m_CurvePosition.x - me : me - o.m_CurvePosition.y) * laneLength;
                    if (d > 0f && d <= CorridorAhead && ahead.Count < CorridorMaxCars) ahead.Add(car);
                    else if (d <= 0f && d >= -CorridorBehind) behind.Add(car);
                }
            }
            // The car in front may already stand on the next (junction) lane.
            if (blocker != Entity.Null && !ahead.Contains(blocker) && Yields(blocker)
                && math.distance(em.GetComponentData<Game.Objects.Transform>(blocker).m_Position, em.GetComponentData<Game.Objects.Transform>(e).m_Position) <= CorridorAhead)
                ahead.Add(blocker);
            // Cars already yielding stay frozen until the ambulance is level with them, even while not in the lane list.
            var ambulance = em.GetComponentData<Game.Objects.Transform>(e);
            float3 heading = math.forward(ambulance.m_Rotation);
            if (!m_CorridorCars.TryGetValue(e, out var kept)) m_CorridorCars[e] = kept = new HashSet<Entity>();
            foreach (var car in kept)
            {
                if (ahead.Contains(car) || !em.Exists(car) || em.HasComponent<Deleted>(car) || !Yields(car)) continue;
                float3 offset = em.GetComponentData<Game.Objects.Transform>(car).m_Position - ambulance.m_Position;
                if (math.dot(offset, heading) > 0f && math.length(offset) <= CorridorAhead + 10f) ahead.Add(car);
            }
            kept.Clear();
            kept.UnionWith(ahead); // passed cars drop out here and drive on
            // Light ahead turned green (also our forced green for the first cars): the queue drives off normally and
            // the ambulance follows. The corridor is only for a standing queue.
            // Once per stuck episode: if the queue still stands after a green (jammed junction), the corridor is allowed.
            // Multi-lane: the ambulance passes between the lanes anyway; dropping the corridor on green (often our own
            // forced green) froze and released the cars every second, so they never made room.
            if (!m_GreenIgnore.ContainsKey(e) && ParallelLanes(lane, e).Count < 2 && QueueHasGreen(ahead))
            {
                Mod.log.Info($"Ambulance {e.Index}: Ampel grün, Rettungsgasse aufgelöst");
                m_GreenIgnore[e] = GreenIgnoreFrames;
                m_CorridorCooldown[e] = 120; // let the queue drive off first
                return false;
            }
            // Nothing that could make room (e.g. only another emergency vehicle ahead): no corridor at all.
            if (ahead.Count == 0 && !m_InCorridor.Contains(e)) { m_CorridorCooldown[e] = 120; return false; }
            if (ahead.Count == 0)
            {
                int grace = m_CorridorGrace.TryGetValue(e, out var g) ? g + 1 : 1;
                m_CorridorGrace[e] = grace;
                if (grace > CorridorGraceFrames) { m_CorridorCooldown[e] = 60; return false; }
                SetLateral(e, ref lane, m_AmbulanceOffset.TryGetValue(e, out var keep) ? keep : 0f, slide: false);
                return true;
            }
            m_CorridorGrace.Remove(e);

            if (!YieldAndPass(e, ref lane, ahead, behind)) { RerouteQueue(e, ahead); m_CorridorCooldown[e] = 120; return false; }
            return true;
        }

        private const float CorridorBehind = 30f;

        /// <summary>
        /// Shared by both corridors. Cars ahead stop and glide to the right edge of their own lane (never onto parked
        /// cars next to it); cars behind stop. The emergency vehicle moves left of the widest yielding car, i.e. over
        /// the middle line; while it is (almost) standing it is slid there too, because a standing car cannot steer.
        /// A car leaves the lane list (so the emergency vehicle may pass it) only once both have reached their sides.
        /// Returns false (and touches nothing) when the road is too narrow to pass next to oncoming traffic.
        /// Two or more lanes in our direction: real Rettungsgasse instead, see the multi-lane branch.
        /// </summary>
        private bool YieldAndPass(Entity e, ref CarCurrentLane lane, List<Entity> ahead, List<Entity> behind)
        {
            var em = EntityManager;
            var shifts = new float[ahead.Count];
            float passOffset;
            // Multi-lane: the gap is the line between our lane and its left neighbour (between the two lanes when we
            // are in the leftmost one). Cars left of that line go to the left edge of their lane, cars right of it to
            // the right edge; the emergency vehicle drives on the line.
            var parallel = ParallelLanes(lane, e);
            var ownLane = lane.m_Lane;
            int me = parallel.FindIndex(p => p.lane == ownLane);
            int gapLeft = me <= 0 ? 0 : me - 1;
            bool multi = me >= 0 && parallel.Count >= 2;
            if (multi)
            {
                float side = me == gapLeft ? -1f : 1f; // our own lane's cars move away from the gap
                for (int i = 0; i < ahead.Count; i++)
                    shifts[i] = side * CorridorShift(e, ahead[i]);
                passOffset = (parallel[gapLeft].offset + parallel[gapLeft + 1].offset) * 0.5f;
            }
            else
            {
                float leftmost = 0f;
                for (int i = 0; i < ahead.Count; i++)
                {
                    shifts[i] = EdgeShift(ahead[i]);
                    leftmost = math.min(leftmost, shifts[i] - VehicleWidth(ahead[i]) * 0.5f);
                }
                passOffset = leftmost - CorridorGap - VehicleWidth(e) * 0.5f;
                if (!CorridorFits(e, lane, passOffset)) return false;
            }
            m_AmbulanceOffset[e] = passOffset;
            float speed = em.HasComponent<Moving>(e) ? math.length(em.GetComponentData<Moving>(e).m_Velocity) : 0f;
            float own = SetLateral(e, ref lane, passOffset, slide: speed < 1.5f);
            bool ambulanceReady = math.abs(own - passOffset) <= 0.3f;

            for (int i = 0; i < ahead.Count; i++)
            {
                var car = ahead[i];
                var cl = em.GetComponentData<CarCurrentLane>(car);
                float lateral = SetLateral(car, ref cl, shifts[i], slide: true);
                em.SetComponentData(car, cl);
                Freeze(car);
                m_Held[car] = (e, 0);
                if (math.abs(lateral - shifts[i]) > 0.15f || !ambulanceReady) continue; // not both out of the way yet: it waits behind
                if (em.HasBuffer<LaneObject>(cl.m_Lane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(cl.m_Lane), car);
                if (em.HasBuffer<LaneObject>(cl.m_ChangeLane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(cl.m_ChangeLane), car);
            }
            foreach (var car in behind) Freeze(car); // nobody follows the emergency vehicle through the corridor
            if (multi)
            {
                int other = me == gapLeft ? gapLeft + 1 : gapLeft;
                YieldNeighbourLane(e, parallel[other].lane, other == gapLeft ? -1f : 1f);
            }
            else YieldOncoming(e, lane);
            return true;
        }

        /// <summary>
        /// Multi-lane: metres a car moves away from the line the emergency vehicle drives on. Staying inside its lane
        /// (EdgeShift) leaves only ~1.9 m between two cars on 3 m lanes, too narrow for an ambulance, so the cars go
        /// over their outer lane edge as far as needed, like a real Rettungsgasse.
        /// </summary>
        private float CorridorShift(Entity e, Entity car)
        {
            var lane = EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane;
            float halfGap = VehicleWidth(e) * 0.5f + CorridorGap; // from the line to the car's near side
            return math.max(EdgeShift(car), halfGap + VehicleWidth(car) * 0.5f - LaneWidth(lane) * 0.5f);
        }

        /// <summary>Metres a car can move from its lane centre to the lane edge.</summary>
        private float EdgeShift(Entity car)
        {
            var cl = EntityManager.GetComponentData<CarCurrentLane>(car);
            return math.max(0f, LaneWidth(cl.m_Lane) * 0.5f - VehicleWidth(car) * 0.5f - 0.05f);
        }

        /// <summary>
        /// Cars in the lane on the other side of the gap, from level with our tail to CorridorAhead in front: they stop
        /// and move to the edge of their lane away from the gap (side -1 = left). They are not in our lane list, so
        /// they only need to make room, and are held like passed cars until we are past them.
        /// </summary>
        // ponytail: lanes beyond the two at the gap do not move; on 3+ lanes only the two next to the gap make room.
        private void YieldNeighbourLane(Entity e, Entity neighbour, float side)
        {
            var em = EntityManager;
            if (!em.HasBuffer<LaneObject>(neighbour)) return;
            var tr = em.GetComponentData<Game.Objects.Transform>(e);
            float3 heading = math.forward(tr.m_Rotation);
            float myLength = VehicleLength(e);
            using var objects = em.GetBuffer<LaneObject>(neighbour, true).ToNativeArray(Allocator.Temp);
            foreach (var o in objects)
            {
                var car = o.m_LaneObject;
                if (!em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car) || em.HasComponent<Ambulance>(car)
                    || (em.GetComponentData<Car>(car).m_Flags & CarFlags.Emergency) != 0) continue;
                float along = math.dot(em.GetComponentData<Game.Objects.Transform>(car).m_Position - tr.m_Position, heading);
                if (along < -myLength || along > CorridorAhead) continue;
                Freeze(car);
                m_Held[car] = (e, 0);
                if (HasTrailer(car)) continue; // its trailer cannot be slid aside: it only stops
                var cl = em.GetComponentData<CarCurrentLane>(car);
                SetLateral(car, ref cl, side * CorridorShift(e, car), slide: true);
                em.SetComponentData(car, cl);
            }
        }

        private const float TypicalCarWidth = 2f; // room an oncoming car needs at the right edge of its lane

        /// <summary>
        /// The emergency vehicle at <paramref name="passOffset"/> reaches this far over the middle line. Each oncoming
        /// vehicle in range leaves room next to the middle line: a car pulled to its right edge (lane width - car width),
        /// a vehicle with a trailer stays in the middle (half of that). Trucks are wider than cars, so their real widths
        /// count; TypicalCarWidth stands for one still to come. No oncoming lane: always fits.
        /// </summary>
        private bool CorridorFits(Entity e, CarCurrentLane lane, float passOffset)
        {
            var oncoming = OncomingLane(e, lane);
            if (oncoming == Entity.Null) return true;
            float intrusion = -(passOffset - VehicleWidth(e) * 0.5f) - LaneWidth(lane.m_Lane) * 0.5f;
            float half = LaneWidth(oncoming) * 0.5f;
            float free = half * 2f - TypicalCarWidth - 0.05f;
            foreach (var car in OncomingCars(e, oncoming))
                free = math.min(free, half + (HasTrailer(car) ? 0f : EdgeShift(car)) - VehicleWidth(car) * 0.5f);
            return intrusion + 0.2f <= free;
        }

        /// <summary>Vehicles on the oncoming lane from level with our tail to OncomingAhead in front (no emergency vehicles).</summary>
        private List<Entity> OncomingCars(Entity e, Entity oncoming)
        {
            var em = EntityManager;
            var cars = new List<Entity>();
            if (!em.HasBuffer<LaneObject>(oncoming)) return cars;
            var tr = em.GetComponentData<Game.Objects.Transform>(e);
            float3 heading = math.forward(tr.m_Rotation);
            float myLength = VehicleLength(e);
            using var objects = em.GetBuffer<LaneObject>(oncoming, true).ToNativeArray(Allocator.Temp);
            foreach (var o in objects)
            {
                var car = o.m_LaneObject;
                if (!em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car) || em.HasComponent<Ambulance>(car)
                    || (em.GetComponentData<Car>(car).m_Flags & CarFlags.Emergency) != 0) continue;
                float along = math.dot(em.GetComponentData<Game.Objects.Transform>(car).m_Position - tr.m_Position, heading);
                if (along >= -myLength && along <= OncomingAhead) cars.Add(car);
            }
            return cars;
        }

        // ---- Narrow road: the queue turns off at the next junction instead (never against traffic) ----

        private readonly Dictionary<Entity, (Entity alt, int frames)> m_Rerouted = new Dictionary<Entity, (Entity, int)>();

        /// <summary>
        /// Cars ahead that take the same exit as the emergency vehicle at the next junction get another real connection
        /// from their own lane (the exit farthest from ours, no U-turn), so only legal turns are used. Navigation and
        /// path are cut after that connection; the car repaths once it is on it (UpdateRerouted).
        /// </summary>
        // ponytail: if pathfinding takes longer than driving the connection, the car briefly reaches the end of its path.
        private void RerouteQueue(Entity e, List<Entity> cars)
        {
            var em = EntityManager;
            var ambNode = NextNodeLane(e);
            if (ambNode == Entity.Null) return;
            float3 ambExit = em.GetComponentData<Curve>(ambNode).m_Bezier.d;
            foreach (var car in cars)
            {
                if (m_Rerouted.ContainsKey(car) || !em.HasBuffer<CarNavigationLane>(car) || !em.HasBuffer<Game.Pathfind.PathElement>(car) || !em.HasComponent<Game.Pathfind.PathOwner>(car)) continue;
                var nav = em.GetBuffer<CarNavigationLane>(car);
                int k = -1;
                for (int j = 0; j < nav.Length; j++)
                    if (em.HasComponent<Owner>(nav[j].m_Lane) && em.HasComponent<Node>(em.GetComponentData<Owner>(nav[j].m_Lane).m_Owner)) { k = j; break; }
                if (k < 0 || !em.HasComponent<Curve>(nav[k].m_Lane)) continue;
                if (math.distance(em.GetComponentData<Curve>(nav[k].m_Lane).m_Bezier.d, ambExit) > 8f) continue; // goes elsewhere anyway
                var alt = AlternativeExit(nav[k].m_Lane, ambExit);
                if (alt == Entity.Null) continue;
                var n = nav[k]; n.m_Lane = alt; nav[k] = n;
                nav.RemoveRange(k + 1, nav.Length - k - 1);
                var path = em.GetBuffer<Game.Pathfind.PathElement>(car);
                int from = math.min(em.GetComponentData<Game.Pathfind.PathOwner>(car).m_ElementIndex, path.Length);
                path.RemoveRange(from, path.Length - from); // nothing of the old route gets appended after the turn
                m_Rerouted[car] = (alt, 0);
                Mod.log.Info($"Ambulance {e.Index}: Straße zu eng, Fahrzeug {car.Index} biegt anders ab");
            }
        }

        /// <summary>Another connection from the same lane end, leading to an exit away from <paramref name="avoidExit"/>.</summary>
        private Entity AlternativeExit(Entity nodeLane, float3 avoidExit)
        {
            var em = EntityManager;
            if (!em.HasComponent<Lane>(nodeLane) || !em.HasComponent<Owner>(nodeLane)) return Entity.Null;
            var node = em.GetComponentData<Owner>(nodeLane).m_Owner;
            if (!em.HasBuffer<SubLane>(node)) return Entity.Null;
            var start = em.GetComponentData<Lane>(nodeLane).m_StartNode;
            Entity best = Entity.Null; float bestDistance = 12f;
            foreach (var sl in em.GetBuffer<SubLane>(node, true))
            {
                var c = sl.m_SubLane;
                if (!em.HasComponent<Lane>(c) || !em.HasComponent<CarLane>(c) || !em.HasComponent<Curve>(c)) continue;
                if (!em.GetComponentData<Lane>(c).m_StartNode.EqualsIgnoreCurvePos(start)) continue;
                var flags = em.GetComponentData<CarLane>(c).m_Flags;
                if ((flags & (Game.Net.CarLaneFlags.UTurnLeft | Game.Net.CarLaneFlags.UTurnRight | Game.Net.CarLaneFlags.Forbidden)) != 0) continue;
                float d = math.distance(em.GetComponentData<Curve>(c).m_Bezier.d, avoidExit);
                if (d > bestDistance) { best = c; bestDistance = d; }
            }
            return best;
        }

        /// <summary>A rerouted car on its new connection: path obsolete, vanilla finds a new route from there.</summary>
        private void UpdateRerouted()
        {
            if (m_Rerouted.Count == 0) return;
            var em = EntityManager;
            foreach (var car in new List<Entity>(m_Rerouted.Keys))
            {
                var (alt, frames) = m_Rerouted[car];
                bool done = frames > 1800 || !em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<CarCurrentLane>(car);
                if (!done && em.GetComponentData<CarCurrentLane>(car).m_Lane == alt && em.HasComponent<Game.Pathfind.PathOwner>(car))
                {
                    var po = em.GetComponentData<Game.Pathfind.PathOwner>(car);
                    po.m_State |= Game.Pathfind.PathFlags.Obsolete;
                    em.SetComponentData(car, po);
                    done = true;
                }
                if (done) m_Rerouted.Remove(car); else m_Rerouted[car] = (alt, frames + 1);
            }
        }

        /// <summary>The oncoming car lane right next to us on our left, on the same road segment; Null if none.</summary>
        private Entity OncomingLane(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasComponent<Owner>(lane.m_Lane)) return Entity.Null;
            var owner = em.GetComponentData<Owner>(lane.m_Lane).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return Entity.Null;
            var tr = em.GetComponentData<Game.Objects.Transform>(e);
            float3 heading = math.forward(tr.m_Rotation);
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), heading));
            foreach (var sl in em.GetBuffer<SubLane>(owner, true))
            {
                var c = sl.m_SubLane;
                if (c == lane.m_Lane || !em.HasComponent<CarLane>(c) || !em.HasComponent<Curve>(c) || !em.HasBuffer<LaneObject>(c)) continue;
                var bez = em.GetComponentData<Curve>(c).m_Bezier;
                MathUtils.Distance(bez, tr.m_Position, out float t);
                if (math.dot(math.normalizesafe(MathUtils.Tangent(bez, t)), heading) > -0.8f) continue; // not oncoming
                float lateral = math.dot(MathUtils.Position(bez, t) - tr.m_Position, right);
                if (lateral < 0f && lateral >= -6f) return c;
            }
            return Entity.Null;
        }

        private const float OncomingAhead = 80f; // oncoming cars are fast: they need to start braking early

        /// <summary>
        /// The emergency vehicle passes over the middle line, so oncoming traffic in the lane next to it stops and
        /// moves to its own right edge (a vehicle with a trailer only stops). Held like passed cars: they drive on
        /// once the emergency vehicle is past them. Only the oncoming lane right next to us on the same road segment.
        /// </summary>
        // ponytail: oncoming cars on the next segment (beyond the junction) are not stopped; add if they clip in.
        private void YieldOncoming(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            var c = OncomingLane(e, lane);
            if (c == Entity.Null) return;
            foreach (var car in OncomingCars(e, c))
            {
                Freeze(car);
                m_Held[car] = (e, 0);
                if (HasTrailer(car)) continue;
                var cl = em.GetComponentData<CarCurrentLane>(car);
                SetLateral(car, ref cl, EdgeShift(car), slide: true); // same edge CorridorFits counted on
                em.SetComponentData(car, cl);
            }
        }

        // Cars that made room stay stopped after the ambulance passed them, until they are this far behind its tail.
        private const float HoldBehind = 15f;
        private const int HoldMaxFrames = 600; // ~10 s, in case the ambulance stops right after passing
        private readonly Dictionary<Entity, (Entity ambulance, int frames)> m_Held = new Dictionary<Entity, (Entity, int)>();

        /// <summary>
        /// Runs after all ambulances. A held car still ahead of its ambulance that was not refreshed by YieldAndPass
        /// this frame (corridor ended) drives on; a passed one waits until the ambulance is HoldBehind metres ahead.
        /// </summary>
        private void HoldPassedCars()
        {
            if (m_Held.Count == 0) return;
            var em = EntityManager;
            foreach (var car in new List<Entity>(m_Held.Keys))
            {
                var (amb, frames) = m_Held[car];
                bool release = frames >= HoldMaxFrames || !em.Exists(car) || !em.Exists(amb) || em.HasComponent<Deleted>(car)
                    || !em.HasComponent<Game.Objects.Transform>(car) || !em.HasComponent<Game.Objects.Transform>(amb);
                if (!release)
                {
                    var t = em.GetComponentData<Game.Objects.Transform>(amb);
                    float along = math.dot(em.GetComponentData<Game.Objects.Transform>(car).m_Position - t.m_Position, math.forward(t.m_Rotation));
                    release = along > 0f ? frames > 0 : -along > VehicleLength(amb) * 0.5f + HoldBehind;
                }
                if (release) m_Held.Remove(car);
                else { Freeze(car); m_Held[car] = (amb, frames + 1); }
            }
        }

        // ---- Two-lane road: overtake a truck queue on the oncoming lane, closed at its start node ----

        private const int OncomingWaitFrames = 600;      // ~10 s for the oncoming lane to empty, else give up
        private const int OncomingMaxFrames = 3600;      // ~60 s on the oncoming lane at most
        private const int OncomingCooldownFrames = 1200; // ~20 s after giving up

        private class OncomingPass
        {
            public Entity Own, Oncoming;
            public List<(Entity approach, Entity feeder)> Entries; // lanes leading into the oncoming lane at its start node
            public readonly HashSet<Entity> Waiting = new HashSet<Entity>(); // cars held in front of that node
            public readonly HashSet<Entity> Queue = new HashSet<Entity>();   // cars being passed (may be missing from the lane list)
            public bool Out, There, Failed; // There: reached the oncoming lane once; stays set so it does not stop-and-go
            public int Frames;
        }
        private const int OncomingRetryFrames = 60; // after a pass, before the next one may start
        private readonly Dictionary<Entity, OncomingPass> m_Oncoming = new Dictionary<Entity, OncomingPass>();
        private readonly Dictionary<Entity, int> m_OncomingCooldown = new Dictionary<Entity, int>(); // ambulance -> frame it may try again

        /// <summary>
        /// Trucks in the queue: worth closing the oncoming lane and waiting for it to empty. Slower traffic still
        /// moving ahead of us: only when the oncoming lane is already free, then the ambulance overtakes straight away.
        /// </summary>
        private bool StartOncoming(Entity e, CarCurrentLane lane, Entity blocker, float speed)
        {
            if (m_OncomingCooldown.TryGetValue(e, out var until) && m_Frame < until) return false;
            m_OncomingCooldown[e] = m_Frame + 60; // the checks below are not free: once a second at most
            if (ParallelLanes(lane, e).Count > 1 || !MayStartCorridor(e, lane, Entity.Null)) return false;
            var oncoming = OncomingLane(e, lane);
            if (oncoming == Entity.Null) return false;
            var pass = new OncomingPass { Own = lane.m_Lane, Oncoming = oncoming, Entries = OncomingEntries(oncoming) };
            // A big vehicle (fire engine) gets no corridor: it may close the oncoming lane for any standing queue.
            bool truck = VehicleLength(e) > BigVehicleLength || TruckAhead(e, lane, blocker);
            if (!truck && !(speed >= 1f && OncomingClear(e, pass))) return false;
            m_OncomingCooldown.Remove(e);
            m_Oncoming[e] = pass;
            Mod.log.Info($"Ambulance {e.Index}: {(truck ? "Lkw in der Kolonne" : "Gegenfahrbahn frei, überholt fahrende Kolonne")}, sperrt Gegenfahrbahn (Spur {oncoming.Index})");
            return true;
        }

        private void EndOncoming(Entity e)
        {
            if (!m_Oncoming.TryGetValue(e, out var p)) return;
            m_OncomingCooldown[e] = m_Frame + (p.Failed ? OncomingCooldownFrames : OncomingRetryFrames);
            if (p.Failed) Mod.log.Info($"Ambulance {e.Index}: Überholen auf der Gegenfahrbahn abgebrochen, Sperre aufgehoben");
            m_Oncoming.Remove(e); // held node cars are no longer refreshed and drive on
        }

        /// <summary>
        /// Phase 1: the oncoming lane is closed (CloseOncoming) and the ambulance waits until it is empty.
        /// Phase 2: it moves over onto the oncoming lane (still logically in its own lane), the queue ahead stops and
        /// leaves the lane list, so the ambulance passes it; with nothing ahead it moves back. Ends at the lane end.
        /// Returns false when done or given up (Failed).
        /// </summary>
        private bool OncomingOvertake(Entity e, ref CarCurrentLane lane, float speed, Entity blocker)
        {
            var em = EntityManager;
            var p = m_Oncoming[e];
            p.Frames++;
            if (lane.m_Lane != p.Own || !em.Exists(p.Oncoming)) return false; // into the junction: done
            CloseOncoming(p);
            if (!p.Out)
            {
                if (OncomingClear(e, p)) { p.Out = true; p.Frames = 0; Mod.log.Info($"Ambulance {e.Index}: überholt auf der Gegenfahrbahn"); }
                else if (p.Frames > OncomingWaitFrames) { p.Failed = true; return false; }
                else return true;
            }
            if (p.Frames > OncomingMaxFrames) { p.Failed = true; return false; }

            var tr = em.GetComponentData<Game.Objects.Transform>(e);
            float3 heading = math.forward(tr.m_Rotation);
            float myHalf = VehicleLength(e) * 0.5f;
            p.Queue.UnionWith(QueueAhead(e, lane));
            if (QueueBlocker(e, blocker)) p.Queue.Add(blocker);
            p.Queue.RemoveWhere(car => !em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<CarCurrentLane>(car)
                || math.dot(em.GetComponentData<Game.Objects.Transform>(car).m_Position - tr.m_Position, heading) < -(myHalf + VehicleLength(car) * 0.5f + 2f));
            if (p.Queue.Count == 0 && !p.There) { p.Failed = true; return false; } // nothing to pass: no retry every frame

            float target = p.Queue.Count > 0 ? -(LaneWidth(p.Own) + LaneWidth(p.Oncoming)) * 0.5f : 0f;
            // Slide sideways only while standing; once halfway out the queue leaves the lane lists, so the
            // ambulance pulls out diagonally while driving instead of crab-walking the whole way.
            float lateral = SetLateral(e, ref lane, target, slide: speed < 0.5f && !p.There);
            if (p.Queue.Count == 0) return math.abs(lateral) > 0.3f; // back in its own lane: done
            p.There |= math.abs(lateral) >= math.abs(target) * 0.5f;
            // Anything on the oncoming lane ahead (late lane changers, driveways) stops and keeps right.
            YieldOncoming(e, lane);
            foreach (var car in p.Queue)
            {
                Freeze(car);
                m_Held[car] = (e, 0); // drives on once the ambulance is past
                if (p.There) RemoveFromLanes(car);
            }
            return true;
        }

        private bool IsTruck(Entity v) =>
            EntityManager.HasComponent<Car>(v) && (EntityManager.GetComponentData<Car>(v).m_Flags & CarFlags.Emergency) == 0
            && (HasTrailer(v) || VehicleLength(v) > BigVehicleLength);

        private bool TruckAhead(Entity e, CarCurrentLane lane, Entity blocker) =>
            (QueueBlocker(e, blocker) && IsTruck(blocker)) || QueueAhead(e, lane).Exists(IsTruck);

        /// <summary>The vehicle we are stuck behind belongs to the queue even when it is not in our lane's list (next lane, bus stop).</summary>
        private bool QueueBlocker(Entity e, Entity blocker)
        {
            var em = EntityManager;
            if (blocker == Entity.Null || blocker == e || !em.HasComponent<Car>(blocker) || !em.HasComponent<CarCurrentLane>(blocker) || m_Oncoming.ContainsKey(blocker)) return false;
            var tr = em.GetComponentData<Game.Objects.Transform>(e);
            float3 offset = em.GetComponentData<Game.Objects.Transform>(blocker).m_Position - tr.m_Position;
            return math.dot(offset, math.forward(tr.m_Rotation)) > 0f && math.length(offset) <= MovingAhead;
        }

        /// <summary>
        /// Cars ahead of us on our lane, up to its end; a trailer counts as the vehicle pulling it. Other vehicles
        /// passing on the oncoming lane are skipped.
        /// </summary>
        private List<Entity> QueueAhead(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            var result = new List<Entity>();
            if (!em.HasBuffer<LaneObject>(lane.m_Lane)) return result;
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            float me = lane.m_CurvePosition.x;
            foreach (var o in em.GetBuffer<LaneObject>(lane.m_Lane, true))
            {
                var car = o.m_LaneObject;
                if (!em.HasComponent<Car>(car) && em.HasComponent<Controller>(car)) car = em.GetComponentData<Controller>(car).m_Controller;
                if (car == e || result.Contains(car) || !em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car) || m_Oncoming.ContainsKey(car)) continue;
                if (forward ? o.m_CurvePosition.x > me : o.m_CurvePosition.y < me) result.Add(car);
            }
            return result;
        }

        /// <summary>Every (approach lane, junction lane) pair at the node where the oncoming lane starts.</summary>
        private List<(Entity, Entity)> OncomingEntries(Entity oncoming)
        {
            var em = EntityManager;
            var result = new List<(Entity, Entity)>();
            var edge = em.GetComponentData<Owner>(oncoming).m_Owner;
            if (!em.HasComponent<Game.Net.Edge>(edge) || !em.HasComponent<Lane>(oncoming)) return result;
            var start = em.GetComponentData<Lane>(oncoming).m_StartNode;
            var ends = em.GetComponentData<Game.Net.Edge>(edge);
            foreach (var node in new[] { ends.m_Start, ends.m_End })
            {
                if (!em.HasBuffer<SubLane>(node) || !em.HasBuffer<ConnectedEdge>(node)) continue;
                foreach (var sl in em.GetBuffer<SubLane>(node, true))
                {
                    var feeder = sl.m_SubLane;
                    if (!em.HasComponent<CarLane>(feeder) || !em.HasComponent<Lane>(feeder) || !em.GetComponentData<Lane>(feeder).m_EndNode.EqualsIgnoreCurvePos(start)) continue;
                    var from = em.GetComponentData<Lane>(feeder).m_StartNode;
                    foreach (var ce in em.GetBuffer<ConnectedEdge>(node, true))
                    {
                        if (!em.HasBuffer<SubLane>(ce.m_Edge)) continue;
                        foreach (var al in em.GetBuffer<SubLane>(ce.m_Edge, true))
                            if (em.HasComponent<CarLane>(al.m_SubLane) && em.HasComponent<Lane>(al.m_SubLane) && em.GetComponentData<Lane>(al.m_SubLane).m_EndNode.EqualsIgnoreCurvePos(from))
                                result.Add((al.m_SubLane, feeder));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Lane closure without rerouting: a car about to turn into the oncoming lane is held from the moment its braking
        /// distance reaches the end of its lane, so it waits right in front of the junction. One that can no longer stop
        /// drives through; OncomingClear waits for it.
        /// </summary>
        // ponytail: driveways (buildings, parking) along the segment are not closed; a car leaving one meets the ambulance.
        private void CloseOncoming(OncomingPass p)
        {
            var em = EntityManager;
            foreach (var (approach, feeder) in p.Entries)
            {
                if (!em.HasBuffer<LaneObject>(approach)) continue;
                foreach (var o in em.GetBuffer<LaneObject>(approach, true))
                {
                    var car = o.m_LaneObject;
                    if (p.Waiting.Contains(car) || !em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car) || !em.HasComponent<Moving>(car)) continue;
                    var cl = em.GetComponentData<CarCurrentLane>(car);
                    if (cl.m_Lane != approach || !NavContains(car, feeder)) continue;
                    float v = math.length(em.GetComponentData<Moving>(car).m_Velocity);
                    float brake = v * v / (2f * Braking(car));
                    float left = RemainingDistance(cl);
                    if (left >= brake && left <= brake + 4f) p.Waiting.Add(car);
                }
            }
            p.Waiting.RemoveWhere(car => !em.Exists(car) || em.HasComponent<Deleted>(car));
            foreach (var car in p.Waiting) Freeze(car);
        }

        /// <summary>No car on the junction lanes into the oncoming lane, none on it ahead of our tail.</summary>
        private bool OncomingClear(Entity e, OncomingPass p)
        {
            var em = EntityManager;
            foreach (var (_, feeder) in p.Entries)
                if (em.HasBuffer<LaneObject>(feeder) && em.GetBuffer<LaneObject>(feeder, true).Length > 0) return false;
            if (!em.HasBuffer<LaneObject>(p.Oncoming)) return false;
            var tr = em.GetComponentData<Game.Objects.Transform>(e);
            float3 heading = math.forward(tr.m_Rotation);
            float myLength = VehicleLength(e);
            foreach (var o in em.GetBuffer<LaneObject>(p.Oncoming, true))
                if (em.HasComponent<Game.Objects.Transform>(o.m_LaneObject)
                    && math.dot(em.GetComponentData<Game.Objects.Transform>(o.m_LaneObject).m_Position - tr.m_Position, heading) > -myLength) return false;
            return true;
        }

        /// <summary>Takes a vehicle and its trailers out of the lane lists, so the ambulance does not brake for them.</summary>
        private void RemoveFromLanes(Entity car)
        {
            var em = EntityManager;
            var cl = em.GetComponentData<CarCurrentLane>(car);
            Remove(cl.m_Lane, car);
            Remove(cl.m_ChangeLane, car);
            if (!em.HasBuffer<LayoutElement>(car)) return;
            using var layout = em.GetBuffer<LayoutElement>(car, true).ToNativeArray(Allocator.Temp);
            foreach (var le in layout)
            {
                if (le.m_Vehicle == car || !em.HasComponent<CarTrailerLane>(le.m_Vehicle)) continue;
                var tl = em.GetComponentData<CarTrailerLane>(le.m_Vehicle);
                Remove(tl.m_Lane, le.m_Vehicle);
                Remove(tl.m_NextLane, le.m_Vehicle);
            }
            void Remove(Entity l, Entity v) { if (em.HasBuffer<LaneObject>(l)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(l), v); }
        }

        private float Braking(Entity car)
        {
            var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(car).m_Prefab;
            return EntityManager.HasComponent<Game.Prefabs.CarData>(prefab) ? math.max(1f, EntityManager.GetComponentData<Game.Prefabs.CarData>(prefab).m_Braking) : 5f;
        }

        // ---- Moving emergency lane: traffic ahead slows down and keeps right while still driving ----

        private const float MovingAhead = 60f;
        private const float BigVehicleLength = 6.5f; // trucks, buses, campers: they keep their line

        /// <summary>
        /// Driving traffic ahead of an emergency vehicle: normal cars slow down and steer to the right edge of their lane,
        /// the emergency vehicle steers over the middle line and passes. Cars that have made room leave the lane list
        /// (as in the standing corridor). A long vehicle (truck, bus, camper) neither slows down nor moves aside, and
        /// nothing behind it takes part: the emergency vehicle follows it as usual.
        /// Returns false when no car ahead takes part.
        /// </summary>
        private bool MovingCorridor(Entity e, ref CarCurrentLane lane, Entity blocker, float limit)
        {
            var em = EntityManager;
            if (!em.HasComponent<Curve>(lane.m_Lane) || !em.HasBuffer<LaneObject>(lane.m_Lane) || LaneWidth(lane.m_Lane) < 2.5f) return false;
            if (!MayStartCorridor(e, lane, blocker)) return false;
            float laneLength = math.max(1f, MathUtils.Length(em.GetComponentData<Curve>(lane.m_Lane).m_Bezier));
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            float me = lane.m_CurvePosition.x;
            var ahead = new List<(Entity car, float d)>();
            var behind = new List<Entity>();
            using (var objects = em.GetBuffer<LaneObject>(lane.m_Lane, true).ToNativeArray(Allocator.Temp))
            {
                foreach (var o in objects)
                {
                    var car = o.m_LaneObject;
                    if (car == e || !em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car)) continue;
                    float d = (forward ? o.m_CurvePosition.x - me : me - o.m_CurvePosition.y) * laneLength;
                    if (d > 0f && d <= MovingAhead) ahead.Add((car, d));
                    else if (d <= 0f && d >= -CorridorBehind && Yields(car)) behind.Add(car);
                }
            }
            ahead.Sort((a, b) => a.d.CompareTo(b.d));
            var yielding = new List<Entity>();
            foreach (var (car, _) in ahead)
            {
                if (!Yields(car) || VehicleLength(car) > BigVehicleLength) break; // nothing gets past this one
                yielding.Add(car);
            }
            if (yielding.Count == 0) return false;
            // The cars pull over and stop, exactly as in the standing corridor.
            if (!YieldAndPass(e, ref lane, yielding, behind)) { RerouteQueue(e, yielding); return false; }
            return true;
        }

        private float VehicleLength(Entity v)
        {
            var em = EntityManager;
            var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(v).m_Prefab;
            float length = em.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab) ? em.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab).m_Size.z : 4.5f;
            // Car with a trailer (camper, caravan): count the trailer too.
            if (HasTrailer(v)) length += 4f;
            return length;
        }

        private bool HasTrailer(Entity v) =>
            EntityManager.HasBuffer<Game.Vehicles.LayoutElement>(v) && EntityManager.GetBuffer<Game.Vehicles.LayoutElement>(v, true).Length > 1;

        // Lane position targets eased per vehicle; vanilla pulls a driving car's value back towards [-0.5, 0.5] on each
        // navigation update, so easing from the stored value would never get there.
        private readonly Dictionary<Entity, (float value, int frame)> m_Eased = new Dictionary<Entity, (float, int)>();
        private int m_Frame;

        private float Ease(Entity v, float current, float wanted)
        {
            float value = m_Eased.TryGetValue(v, out var x) && m_Frame - x.frame <= 2 ? x.value : current;
            value += math.clamp(wanted - value, -0.02f, 0.02f);
            m_Eased[v] = (value, m_Frame);
            return value;
        }

        private void CleanupEased()
        {
            m_Frame++;
            if ((m_Frame & 63) != 0) return;
            foreach (var v in new List<Entity>(m_Eased.Keys)) if (m_Frame - m_Eased[v].frame > 2) m_Eased.Remove(v);
        }
        private const float CorridorBuildingDistance = 100f;

        /// <summary>
        /// Where a corridor may start. Freezing cars where the ambulance cannot get through anyway jams the
        /// surroundings (seen at hospitals and stations, where emergency vehicles queue behind each other):
        ///  - only on a road segment, not in a junction, parking lot or on building grounds;
        ///  - not when the vehicle in front is an emergency vehicle itself (it does not yield);
        ///  - not within 100 m of the own station/hospital or the hospital it is driving to.
        /// </summary>
        private bool MayStartCorridor(Entity e, CarCurrentLane lane, Entity blocker)
        {
            var em = EntityManager;
            if (!em.HasComponent<EdgeLane>(lane.m_Lane)) return false;
            if (blocker != Entity.Null && em.HasComponent<Car>(blocker) && !Yields(blocker)) return false;
            float3 pos = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
            foreach (var building in new[] { em.GetComponentData<Owner>(e).m_Owner, em.HasComponent<Target>(e) ? em.GetComponentData<Target>(e).m_Target : Entity.Null })
            {
                if (building == Entity.Null || !em.HasComponent<Game.Buildings.Building>(building) || !em.HasComponent<Game.Objects.Transform>(building)) continue;
                if (!em.HasComponent<Game.Buildings.Hospital>(building) && !em.HasComponent<Game.Buildings.PoliceStation>(building)) continue;
                if (math.distance(em.GetComponentData<Game.Objects.Transform>(building).m_Position.xz, pos.xz) < CorridorBuildingDistance) return false;
            }
            return true;
        }

        /// <summary>Signal on the way of the front car of the queue shows green.</summary>
        private bool QueueHasGreen(List<Entity> ahead)
        {
            var em = EntityManager;
            foreach (var car in ahead)
            {
                if (!em.HasBuffer<CarNavigationLane>(car)) continue;
                var nav = em.GetBuffer<CarNavigationLane>(car, true);
                for (int k = 0; k < nav.Length && k < 3; k++)
                {
                    if (!em.HasComponent<LaneSignal>(nav[k].m_Lane)) continue;
                    if (em.GetComponentData<LaneSignal>(nav[k].m_Lane).m_Signal == LaneSignalType.Go) return true;
                    break;
                }
            }
            return false;
        }

        private bool Yields(Entity car)
        {
            var em = EntityManager;
            return em.HasComponent<Car>(car) && em.HasComponent<CarCurrentLane>(car) && !em.HasComponent<Ambulance>(car)
                && (em.GetComponentData<Car>(car).m_Flags & CarFlags.Emergency) == 0
                && !HasTrailer(car); // only the towing vehicle could be slid aside, its trailer would stay in the lane
        }

        private void Freeze(Entity car)
        {
            var em = EntityManager;
            if (!em.HasComponent<CarNavigation>(car)) return;
            var nav = em.GetComponentData<CarNavigation>(car);
            nav.m_MaxSpeed = 0f; // CarMoveSystem brakes to a stop
            em.SetComponentData(car, nav);
        }

        /// <summary>
        /// Target offset to the vehicle's right (metres); returns the current lateral offset. <paramref name="slide"/>
        /// moves a standing car there (transform and its interpolation frames, so it glides instead of jumping);
        /// a driving vehicle only gets the target, eased in, and steers there itself.
        /// </summary>
        private float SetLateral(Entity v, ref CarCurrentLane cl, float metres, bool slide)
        {
            var em = EntityManager;
            if (!em.HasComponent<Curve>(cl.m_Lane)) return 0f;
            var bez = em.GetComponentData<Curve>(cl.m_Lane).m_Bezier;
            float t = math.saturate(cl.m_CurvePosition.x);
            float3 centre = MathUtils.Position(bez, t);
            float2 tangent = math.normalizesafe(MathUtils.Tangent(bez, t).xz);
            var tr = em.GetComponentData<Game.Objects.Transform>(v);
            float sign = math.dot(math.forward(tr.m_Rotation).xz, tangent) >= 0f ? 1f : -1f; // lane offsets are relative to the curve
            float2 right = MathUtils.Right(tangent) * sign;
            float room = math.max(0.1f, LaneWidth(cl.m_Lane) - VehicleWidth(v));
            float wanted = sign * metres / room;
            float lateral = math.dot(tr.m_Position.xz - centre.xz, right);
            if (!slide)
            {
                cl.m_LanePosition = Ease(v, cl.m_LanePosition, wanted);
                return lateral;
            }
            float step = math.clamp(metres - lateral, -0.03f, 0.03f); // ~2 m/s sideways at 60 fps
            // Remembered for Ease: when the car starts driving and steers itself, it keeps this target instead of
            // restarting from vanilla's pulled-back value (that made it swerve back towards its lane and jump).
            cl.m_LanePosition = wanted;
            m_Eased[v] = (wanted, m_Frame);
            if (math.abs(step) > 0.001f)
            {
                float3 shift = new float3(right.x, 0f, right.y) * step;
                tr.m_Position += shift;
                em.SetComponentData(v, tr);
                // Moving objects are drawn from their TransformFrame history: shift it too, else the car snaps.
                if (em.HasBuffer<TransformFrame>(v))
                {
                    var frames = em.GetBuffer<TransformFrame>(v);
                    for (int i = 0; i < frames.Length; i++) { var f = frames[i]; f.m_Position += shift; frames[i] = f; }
                }
            }
            return lateral + step;
        }

        private float LaneWidth(Entity lane)
        {
            var em = EntityManager;
            var prefab = em.HasComponent<Game.Prefabs.PrefabRef>(lane) ? em.GetComponentData<Game.Prefabs.PrefabRef>(lane).m_Prefab : Entity.Null;
            return em.HasComponent<Game.Prefabs.NetLaneData>(prefab) ? em.GetComponentData<Game.Prefabs.NetLaneData>(prefab).m_Width : 0f;
        }

        private float VehicleWidth(Entity v)
        {
            var em = EntityManager;
            var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(v).m_Prefab;
            if (!em.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab)) return 2f;
            var b = em.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab).m_Bounds;
            return b.max.x - b.min.x;
        }

        // ---- Clear 1-2 cars waiting at a red light in front of the ambulance ----

        /// <summary>Node lanes forced to green -> frames left. Applied by SignalOverrideSystem after TrafficLightSystem.</summary>
        public static readonly Dictionary<Entity, int> ForcedGreen = new Dictionary<Entity, int>();

        /// <summary>
        /// Cars ahead of the ambulance on its lane, up to the lane end. If there are only 1-2 and they wait at a
        /// signal, the signal of the node lane each one takes next turns green for a few seconds so they clear the
        /// way. The ambulance follows and, if it is on the wrong lane, uses its route/temporary turn lane as before.
        /// </summary>
        private void ClearCarsAtRedLight(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasBuffer<LaneObject>(lane.m_Lane)) return;
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            float me = lane.m_CurvePosition.x;
            var ahead = new List<Entity>();
            foreach (var o in em.GetBuffer<LaneObject>(lane.m_Lane, true))
            {
                if (o.m_LaneObject == e || !em.HasComponent<Car>(o.m_LaneObject)) continue;
                if (forward ? o.m_CurvePosition.x > me : o.m_CurvePosition.y < me) ahead.Add(o.m_LaneObject);
            }
            if (ahead.Count == 0 || ahead.Count > Mod.Setting.ClearRedLightMaxCars) return;

            foreach (var car in ahead)
            {
                if (!em.HasComponent<Blocker>(car) || em.GetComponentData<Blocker>(car).m_Type != BlockerType.Signal) continue;
                if (!em.HasBuffer<CarNavigationLane>(car)) continue;
                var nav = em.GetBuffer<CarNavigationLane>(car, true);
                for (int k = 0; k < nav.Length && k < 3; k++)
                {
                    if (!em.HasComponent<LaneSignal>(nav[k].m_Lane)) continue;
                    if (!ForcedGreen.ContainsKey(nav[k].m_Lane))
                        Mod.log.Info($"Ambulance {e.Index}: Ampel für {ahead.Count} wartende(s) Fahrzeug(e) auf Grün (Spur {nav[k].m_Lane.Index})");
                    ForcedGreen[nav[k].m_Lane] = 300; // ~5 s at 1x
                    break;
                }
            }
        }

        // ---- Temporary turn lane: a private copy of the node's turn connection, starting at the end of our lane ----

        private struct TempTurn { public Entity Ambulance; public int Age; public int UnusedFrames; }
        private readonly Dictionary<Entity, TempTurn> m_TempTurns = new Dictionary<Entity, TempTurn>();

        /// <summary>
        /// The next navigation lane is the node connection that starts at the (full) turn lane. Copy it, move its
        /// start to the end of the lane we are on, and put the copy into this ambulance's navigation and path only.
        /// The copy is not in the node's SubLane buffer and never pathfind-registered, so no other car can use it.
        /// </summary>
        private bool CreateTemporaryTurn(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasBuffer<CarNavigationLane>(e) || !em.HasComponent<Curve>(lane.m_Lane)) return false;
            // nav[0] is not always the junction lane (e.g. route lane still listed after our manual change).
            var node = NextNodeLane(e);
            if (node == Entity.Null || !em.HasComponent<Curve>(node)) return false;

            var mine = em.GetComponentData<Curve>(lane.m_Lane).m_Bezier;
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            float3 start = forward ? mine.d : mine.a;
            var orig = em.GetComponentData<Curve>(node).m_Bezier;
            // Leftmost turn pocket -> right turn crosses the whole carriageway; 12 m failed on 3+ lane approaches.
            if (math.distance(start, orig.a) > 25f) return false; // ponytail: distance guess, check same node if it misfires

            var temp = em.Instantiate(node);
            if (em.HasComponent<LaneSignal>(temp)) em.RemoveComponent<LaneSignal>(temp); // lights: ambulance has priority anyway
            if (em.HasBuffer<LaneObject>(temp)) em.GetBuffer<LaneObject>(temp).Clear();
            var bez = new Colossal.Mathematics.Bezier4x3(start, start + (orig.b - orig.a), orig.c, orig.d);
            em.SetComponentData(temp, new Curve { m_Bezier = bez, m_Length = Colossal.Mathematics.MathUtils.Length(bez) });

            var nav = em.GetBuffer<CarNavigationLane>(e); // after the structural changes above
            for (int k = 0; k < nav.Length; k++)
            {
                if (nav[k].m_Lane != node) continue;
                var n = nav[k]; n.m_Lane = temp; nav[k] = n;
                if (k > 0) nav.RemoveRange(0, k); // drop the route lane we no longer drive on
                break;
            }
            if (em.HasBuffer<Game.Pathfind.PathElement>(e))
            {
                var path = em.GetBuffer<Game.Pathfind.PathElement>(e);
                for (int k = 0; k < path.Length; k++)
                    if (path[k].m_Target == node) { var p = path[k]; p.m_Target = temp; path[k] = p; }
            }
            m_TempTurns[temp] = new TempTurn { Ambulance = e };
            return true;
        }

        /// <summary>Delete each copy ~2 s after its ambulance no longer uses it, or after 30 s once it is not on it.</summary>
        // ponytail: a save made while a copy exists stores it as an orphan lane; lifetimes are kept short for that reason.
        private void CleanupTemporaryTurns()
        {
            if (m_TempTurns.Count == 0) return;
            var em = EntityManager;
            foreach (var temp in new List<Entity>(m_TempTurns.Keys))
            {
                var t = m_TempTurns[temp];
                t.Age++;
                bool inUse = em.Exists(t.Ambulance) && em.HasComponent<CarCurrentLane>(t.Ambulance)
                    && (em.GetComponentData<CarCurrentLane>(t.Ambulance).m_Lane == temp || em.GetComponentData<CarCurrentLane>(t.Ambulance).m_ChangeLane == temp || NavContains(t.Ambulance, temp));
                t.UnusedFrames = inUse ? 0 : t.UnusedFrames + 1;
                // Never while the ambulance is on it: its current lane would vanish under it and vanilla removes the car.
                if (t.UnusedFrames > 120 || (t.Age > 1800 && !inUse))
                {
                    if (em.Exists(temp)) em.DestroyEntity(temp);
                    m_TempTurns.Remove(temp);
                }
                else m_TempTurns[temp] = t;
            }
        }

        private bool NavContains(Entity e, Entity laneEntity)
        {
            if (!EntityManager.HasBuffer<CarNavigationLane>(e)) return false;
            foreach (var n in EntityManager.GetBuffer<CarNavigationLane>(e, true)) if (n.m_Lane == laneEntity) return true;
            return false;
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_TempTurns.Clear(); // a new world: entities from the old one are gone
            m_InCorridor.Clear(); m_MovingActive.Clear(); m_Held.Clear(); m_Rerouted.Clear(); m_CorridorCars.Clear(); m_CorridorGrace.Clear(); m_AmbulanceOffset.Clear(); m_StuckFrames.Clear(); m_CorridorStill.Clear(); m_CorridorCooldown.Clear(); m_GreenIgnore.Clear();
            m_Oncoming.Clear(); m_OncomingCooldown.Clear();
            ForcedGreen.Clear();
        }

        private void StartChange(ref CarCurrentLane lane, Entity target, Entity e, string why)
        {
            lane.m_ChangeLane = target;
            lane.m_ChangeProgress = 0f;
            lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
            m_Changing.Add(e);
            // Vanilla completes the change within a tick or two but keeps the old lane's blocker a while longer:
            // wait ~1 s for a fresh one before the next change, else the ambulance ping-pongs between lanes.
            m_BlockedFrames[e] = -60;
            Mod.log.Info($"Ambulance {e.Index}: Spurwechsel ({why})");
        }

        private float Position(CarCurrentLane lane) => lane.m_CurvePosition.x;

        /// <summary>Metres left on the current lane in driving direction.</summary>
        private float RemainingDistance(CarCurrentLane lane)
        {
            if (!EntityManager.HasComponent<Curve>(lane.m_Lane)) return 0f;
            float len = EntityManager.GetComponentData<Curve>(lane.m_Lane).m_Length;
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            return len * (forward ? 1f - lane.m_CurvePosition.x : lane.m_CurvePosition.x);
        }

        private bool SameGroup(Entity a, Entity b)
        {
            var em = EntityManager;
            return em.HasComponent<SlaveLane>(a) && em.HasComponent<SlaveLane>(b) && em.HasComponent<Owner>(a) && em.HasComponent<Owner>(b)
                && em.GetComponentData<Owner>(a).m_Owner == em.GetComponentData<Owner>(b).m_Owner
                && em.GetComponentData<SlaveLane>(a).m_Group == em.GetComponentData<SlaveLane>(b).m_Group;
        }

        // ---- Overtaking within the lanes of our direction ----

        /// <summary>ambulance -> (lane it is changing to, real node connection from that lane to swap into the route)</summary>
        private readonly Dictionary<Entity, (Entity lane, Entity oldConnection, Entity newConnection)> m_PendingSwap = new Dictionary<Entity, (Entity, Entity, Entity)>();

        /// <summary>
        /// Looks at every lane of our direction group. A lane that has its own connection to the same exit at the
        /// next junction is a real route lane: we may switch there up to the junction and take over its connection
        /// (no merging back). Other lanes are only for overtaking with merge-back as before. If the best lane is
        /// not adjacent, we step into the adjacent lane when it has a short gap at our position.
        /// </summary>
        private void TryOvertake(ref CarCurrentLane lane, Entity e)
        {
            var em = EntityManager;
            float remaining = RemainingDistance(lane);
            var nodeLane = NextNodeLane(e);
            var parallel = ParallelLanes(lane, e); // sorted by lateral offset, ours at offset 0
            var current = lane.m_Lane;
            int me = parallel.FindIndex(p => p.lane == current);
            if (me < 0) return;

            int best = -1; bool bestRoute = false; Entity bestConnection = Entity.Null;
            for (int k = 0; k < parallel.Count; k++)
            {
                if (k == me) continue;
                var (cand, candPos, _) = parallel[k];
                var connection = nodeLane == Entity.Null ? Entity.Null : ConnectionTowards(cand, nodeLane);
                bool route = connection != Entity.Null;
                float candRemaining = LaneRemaining(cand, candPos);
                // Route lanes only need to be free up to the junction; overtaking lanes need room to merge back.
                if (route ? !IsFree(cand, candPos, e, math.min(FreeDistance, candRemaining)) : candRemaining < MergeBackDistance + 5f || !IsFree(cand, candPos, e, math.min(FreeDistance, candRemaining))) continue;
                bool better = best < 0 || (route && !bestRoute) || (route == bestRoute && math.abs(k - me) < math.abs(best - me));
                if (better) { best = k; bestRoute = route; bestConnection = connection; }
            }
            if (best < 0) return;

            int step = me + (best > me ? 1 : -1);
            var stepLane = parallel[step].lane;
            if (step != best && !IsFreeWindow(stepLane, parallel[step].pos, e, -(VehicleLength(e) + 3f), 10f)) return; // middle lane has no gap right here

            if (step == best && bestRoute)
            {
                m_RouteLane.Remove(e);
                m_PendingSwap[e] = (stepLane, nodeLane, bestConnection);
                StartChange(ref lane, stepLane, e, "wechselt auf freie Routenspur");
            }
            else
            {
                if (!m_RouteLane.ContainsKey(e)) m_RouteLane[e] = lane.m_Lane;
                StartChange(ref lane, stepLane, e, step == best ? "überholt auf freier Spur" : "zieht über Mittelspur");
            }
        }

        /// <summary>
        /// All car lanes of the same road segment and carriageway that run in our direction next to us (also turn
        /// lanes, which vanilla keeps in a separate SlaveLane group), with the curve position closest to us,
        /// sorted left to right. Only when we drive the lane in curve direction (the normal case).
        /// </summary>
        private List<(Entity lane, float pos, float offset)> ParallelLanes(CarCurrentLane lane, Entity e)
        {
            var em = EntityManager;
            var result = new List<(Entity, float, float)>();
            if (!em.HasComponent<Owner>(lane.m_Lane) || !em.HasComponent<CarLane>(lane.m_Lane) || !em.HasComponent<Curve>(lane.m_Lane)) return result;
            if (lane.m_CurvePosition.z < lane.m_CurvePosition.x) return result;
            var owner = em.GetComponentData<Owner>(lane.m_Lane).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return result;
            ushort carriageway = em.GetComponentData<CarLane>(lane.m_Lane).m_CarriagewayGroup;
            var myCurve = em.GetComponentData<Curve>(lane.m_Lane).m_Bezier;
            float3 here = MathUtils.Position(myCurve, lane.m_CurvePosition.x);
            float3 dir = math.normalizesafe(MathUtils.Tangent(myCurve, lane.m_CurvePosition.x));
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), dir));

            foreach (var sl in em.GetBuffer<SubLane>(owner, true))
            {
                var c = sl.m_SubLane;
                if (!em.HasComponent<CarLane>(c) || !em.HasComponent<Curve>(c) || em.GetComponentData<CarLane>(c).m_CarriagewayGroup != carriageway) continue;
                if (c == lane.m_Lane) { result.Add((c, lane.m_CurvePosition.x, 0f)); continue; }
                var bez = em.GetComponentData<Curve>(c).m_Bezier;
                float dist = MathUtils.Distance(bez, here, out float t);
                if (dist > 12f || t <= 0f || t >= 1f) continue; // too far away, or the lane does not exist at our position
                if (math.dot(math.normalizesafe(MathUtils.Tangent(bez, t)), dir) < 0.8f) continue; // other direction
                result.Add((c, t, math.dot(MathUtils.Position(bez, t) - here, right)));
            }
            result.Sort((a, b) => a.Item3.CompareTo(b.Item3));
            return result;
        }

        private float LaneRemaining(Entity lane, float pos) =>
            EntityManager.HasComponent<Curve>(lane) ? EntityManager.GetComponentData<Curve>(lane).m_Length * (1f - pos) : 0f;

        /// <summary>
        /// Vanilla keeps the curve position when a lane change completes. Between lanes of different length
        /// (a turn lane starting mid-segment) that would make the car jump, so put it back where it really is.
        /// </summary>
        private void ReprojectAfterChange(Entity e, ref CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasComponent<Curve>(lane.m_Lane) || !em.HasComponent<Game.Objects.Transform>(e)) return;
            var bez = em.GetComponentData<Curve>(lane.m_Lane).m_Bezier;
            MathUtils.Distance(bez, em.GetComponentData<Game.Objects.Transform>(e).m_Position, out float t);
            if (math.abs(t - lane.m_CurvePosition.x) < 0.02f) return;
            lane.m_CurvePosition.x = t;
            lane.m_CurvePosition.y = math.max(t, lane.m_CurvePosition.y);
        }

        /// <summary>First node (junction) lane in the ambulance's navigation list.</summary>
        private Entity NextNodeLane(Entity e)
        {
            var em = EntityManager;
            if (!em.HasBuffer<CarNavigationLane>(e)) return Entity.Null;
            foreach (var n in em.GetBuffer<CarNavigationLane>(e, true))
                if (em.HasComponent<Owner>(n.m_Lane) && em.HasComponent<Node>(em.GetComponentData<Owner>(n.m_Lane).m_Owner)) return n.m_Lane;
            return Entity.Null;
        }

        /// <summary>A real junction lane that starts at the end of <paramref name="from"/> and ends where <paramref name="nodeLane"/> ends (same exit).</summary>
        private Entity ConnectionTowards(Entity from, Entity nodeLane)
        {
            var em = EntityManager;
            if (!em.HasComponent<Lane>(from) || !em.HasComponent<Curve>(nodeLane)) return Entity.Null;
            var node = em.GetComponentData<Owner>(nodeLane).m_Owner;
            if (!em.HasBuffer<SubLane>(node)) return Entity.Null;
            var end = em.GetComponentData<Lane>(from).m_EndNode;
            float3 exit = em.GetComponentData<Curve>(nodeLane).m_Bezier.d;
            foreach (var sl in em.GetBuffer<SubLane>(node, true))
            {
                var c = sl.m_SubLane;
                if (!em.HasComponent<Lane>(c) || !em.HasComponent<CarLane>(c) || !em.HasComponent<Curve>(c)) continue;
                if (!em.GetComponentData<Lane>(c).m_StartNode.EqualsIgnoreCurvePos(end)) continue;
                if (math.distance(em.GetComponentData<Curve>(c).m_Bezier.d, exit) < 8f) return c;
            }
            return Entity.Null;
        }

        /// <summary>Once the change onto a route lane is done, put that lane's own connection into navigation and path.</summary>
        private void FinishRouteLaneSwap(Entity e, CarCurrentLane lane)
        {
            if (!m_PendingSwap.TryGetValue(e, out var swap) || lane.m_ChangeLane != Entity.Null) return;
            m_PendingSwap.Remove(e);
            if (lane.m_Lane != swap.lane) return;
            var em = EntityManager;
            if (em.HasBuffer<CarNavigationLane>(e))
            {
                var nav = em.GetBuffer<CarNavigationLane>(e);
                for (int k = 0; k < nav.Length; k++) if (nav[k].m_Lane == swap.oldConnection) { var n = nav[k]; n.m_Lane = swap.newConnection; nav[k] = n; }
            }
            if (em.HasBuffer<Game.Pathfind.PathElement>(e))
            {
                var path = em.GetBuffer<Game.Pathfind.PathElement>(e);
                for (int k = 0; k < path.Length; k++) if (path[k].m_Target == swap.oldConnection) { var p = path[k]; p.m_Target = swap.newConnection; path[k] = p; }
            }
        }

        // Behind: own length plus a gap, so we never cut in right in front of a truck cab next to our tail.
        private bool IsFree(Entity lane, float pos, Entity self, float ahead) => IsFreeWindow(lane, pos, self, -(VehicleLength(self) + 3f), ahead);

        /// <summary>No other vehicle on the lane between <paramref name="behind"/> and <paramref name="ahead"/> metres from pos (curve position space).</summary>
        private bool IsFreeWindow(Entity lane, float pos, Entity self, float behind, float ahead)
        {
            var em = EntityManager;
            if (!em.HasBuffer<LaneObject>(lane) || !em.HasComponent<Curve>(lane)) return false;
            float len = math.max(1f, em.GetComponentData<Curve>(lane).m_Length);
            float from = pos + behind / len, to = pos + ahead / len;
            foreach (var o in em.GetBuffer<LaneObject>(lane, true))
            {
                if (o.m_LaneObject == self) continue;
                if (o.m_CurvePosition.y >= from && o.m_CurvePosition.x <= to) return false;
            }
            return true;
        }
    }
}
