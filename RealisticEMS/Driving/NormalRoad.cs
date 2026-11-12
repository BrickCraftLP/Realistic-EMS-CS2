using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Vehicles;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using Edge = Game.Net.Edge;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Non-highway roads. Multi-lane carriageways use the Rettungsgasse (DriveGap, Highway.cs); here: overtaking on a
    /// free lane, clearing a red light, junction traffic waiting for a vehicle in the gap, and one lane per direction
    /// (oncoming overtake or squeeze past over the centre line).
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        private const float FreeDistance = 40f;
        private const float MergeBackDistance = 40f;

        // ---- One lane per direction: oncoming overtake, else squeeze ----

        private void SingleLane(Entity e, EvState st, ref CarCurrentLane lane, Entity blocker, float speed, bool blockedByCar)
        {
            var s = Mod.Setting;
            if (s.OncomingOvertake && lane.m_ChangeLane == Entity.Null
                && (st.Oncoming != null || (st.Blocked >= 60 && StartOncoming(e, st, lane, blocker, speed))))
            {
                if (OncomingOvertake(e, st, ref lane, speed, blocker)) { EndSqueeze(st); return; }
                EndOncoming(e, st);
            }
            bool start = blockedByCar && (st.Stuck >= 30 || (speed >= 0.5f && st.Blocked >= 30));
            if (!s.EmergencyCorridor || lane.m_ChangeLane != Entity.Null || ParallelLanes(lane).Count >= 2 || st.Cooldown > 0 || !(st.InSqueeze || start))
            {
                EndSqueeze(st);
                return;
            }
            st.Still = st.InSqueeze && speed < 0.5f ? st.Still + 1 : 0;
            if (st.Still > StillGiveUp)
            {
                EndSqueeze(st);
                st.Cooldown = RetryCooldown;
                Mod.log.Info($"Einsatzfahrzeug {e.Index}: kommt nicht durch, Rettungsgasse aufgelöst");
                return;
            }
            bool was = st.InSqueeze;
            st.InSqueeze = Squeeze(e, st, ref lane, blocker);
            if (st.InSqueeze != was) Mod.log.Info($"Einsatzfahrzeug {e.Index}: {(st.InSqueeze ? $"Gasse auf Spur {lane.m_Lane.Index}" : "Gasse beendet")}");
            if (!st.InSqueeze) EndSqueeze(st);
        }

        // ---- Lane change within our direction ----

        private void LaneChange(Entity e, EvState st, ref CarCurrentLane lane, bool blockedByCar)
        {
            var em = EntityManager;
            if (st.RouteLane != Entity.Null && st.RouteLane != lane.m_Lane && RemainingDistance(lane) < MergeBackDistance)
            {
                // Back onto the route lane before the node, if it is still in our carriageway.
                var route = st.RouteLane;
                var back = ParallelLanes(lane).Find(p => p.lane == route);
                if (back.lane == route && IsFree(route, back.pos, e, math.min(FreeDistance, LaneRemaining(route, back.pos))))
                    StartChange(e, st, ref lane, route, "zurück auf Routenspur");
                else if (em.HasComponent<PathOwner>(e))
                {
                    var po = em.GetComponentData<PathOwner>(e);
                    po.m_State |= PathFlags.Obsolete; // vanilla repaths from the current lane
                    em.SetComponentData(e, po);
                    Mod.log.Info($"Einsatzfahrzeug {e.Index}: Routenspur besetzt, Route wird neu berechnet");
                }
                st.RouteLane = Entity.Null;
            }
            else if (blockedByCar && st.Blocked >= 6 && !st.InSqueeze) TryOvertake(e, st, ref lane);
        }

        private void StartChange(Entity e, EvState st, ref CarCurrentLane lane, Entity target, string why)
        {
            lane.m_ChangeLane = target;
            lane.m_ChangeProgress = 0f;
            lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
            st.Changing = true;
            // Vanilla keeps the old lane's blocker a while after the change: wait ~1 s before the next one (no ping-pong).
            st.Blocked = -60;
            Mod.log.Info($"Einsatzfahrzeug {e.Index}: Spurwechsel ({why})");
        }

        /// <summary>
        /// Every lane of our carriageway. One with its own connection to the same exit at the next junction is a real
        /// route lane: switch there and take over its connection. Others only for overtaking with merge back. If the
        /// best lane is not adjacent, step into the adjacent one when it has a short gap right here.
        /// </summary>
        private void TryOvertake(Entity e, EvState st, ref CarCurrentLane lane)
        {
            var nodeLane = NextNodeLane(e);
            var parallel = ParallelLanes(lane);
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
                if (!route && candRemaining < MergeBackDistance + 5f) continue; // no room to merge back
                if (!IsFree(cand, candPos, e, math.min(FreeDistance, candRemaining))) continue;
                bool better = best < 0 || (route && !bestRoute) || (route == bestRoute && math.abs(k - me) < math.abs(best - me));
                if (better) { best = k; bestRoute = route; bestConnection = connection; }
            }
            if (best < 0) return;

            int step = me + (best > me ? 1 : -1);
            var stepLane = parallel[step].lane;
            if (step != best && !IsFreeWindow(stepLane, parallel[step].pos, e, -(VehicleLength(e) + 3f), 10f)) return;

            if (step == best && bestRoute)
            {
                st.RouteLane = Entity.Null;
                st.Swap = (stepLane, nodeLane, bestConnection);
                StartChange(e, st, ref lane, stepLane, "wechselt auf freie Routenspur");
            }
            else
            {
                if (st.RouteLane == Entity.Null) st.RouteLane = lane.m_Lane;
                StartChange(e, st, ref lane, stepLane, step == best ? "überholt auf freier Spur" : "zieht über Mittelspur");
            }
        }

        /// <summary>Once the change onto a route lane is done, put that lane's own connection into navigation and path.</summary>
        private void FinishRouteLaneSwap(Entity e, EvState st, CarCurrentLane lane)
        {
            if (st.Swap == null || lane.m_ChangeLane != Entity.Null) return;
            var swap = st.Swap.Value;
            st.Swap = null;
            if (lane.m_Lane != swap.lane) return;
            ReplaceInRoute(e, swap.oldConnection, swap.newConnection);
        }

        private void ReplaceInRoute(Entity e, Entity old, Entity replacement)
        {
            var em = EntityManager;
            if (em.HasBuffer<CarNavigationLane>(e))
            {
                var nav = em.GetBuffer<CarNavigationLane>(e);
                for (int k = 0; k < nav.Length; k++) if (nav[k].m_Lane == old) { var n = nav[k]; n.m_Lane = replacement; nav[k] = n; }
            }
            if (em.HasBuffer<PathElement>(e))
            {
                var path = em.GetBuffer<PathElement>(e);
                for (int k = 0; k < path.Length; k++) if (path[k].m_Target == old) { var p = path[k]; p.m_Target = replacement; path[k] = p; }
            }
        }

        /// <summary>
        /// Vanilla keeps the curve position when a lane change completes. Between lanes of different length (a turn
        /// lane starting mid-segment) that would make the car jump, so put it back where it really is.
        /// </summary>
        private void ReprojectAfterChange(Entity e, ref CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasComponent<Curve>(lane.m_Lane)) return;
            var bez = em.GetComponentData<Curve>(lane.m_Lane).m_Bezier;
            Colossal.Mathematics.MathUtils.Distance(bez, em.GetComponentData<Game.Objects.Transform>(e).m_Position, out float t);
            if (math.abs(t - lane.m_CurvePosition.x) < 0.02f) return;
            lane.m_CurvePosition.x = t;
            lane.m_CurvePosition.y = math.max(t, lane.m_CurvePosition.y);
        }

        // ---- Clear a few cars waiting at a red light ahead ----

        /// <summary>Node lanes forced to green -> frames left. Applied by SignalOverrideSystem after TrafficLightSystem.</summary>
        public static readonly Dictionary<Entity, int> ForcedGreen = new Dictionary<Entity, int>();

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
                if (!em.HasComponent<Blocker>(car) || em.GetComponentData<Blocker>(car).m_Type != BlockerType.Signal || !em.HasBuffer<CarNavigationLane>(car)) continue;
                var nav = em.GetBuffer<CarNavigationLane>(car, true);
                for (int k = 0; k < nav.Length && k < 3; k++)
                {
                    if (!em.HasComponent<LaneSignal>(nav[k].m_Lane)) continue;
                    if (!ForcedGreen.ContainsKey(nav[k].m_Lane))
                        Mod.log.Info($"Einsatzfahrzeug {e.Index}: Ampel für {ahead.Count} wartende(s) Fahrzeug(e) auf Grün (Spur {nav[k].m_Lane.Index})");
                    ForcedGreen[nav[k].m_Lane] = 300; // ~5 s at 1x
                    break;
                }
            }
        }

        // ---- One lane per direction: overtake the queue on the oncoming lane, closed at its start node ----

        private const int OncomingWaitFrames = 600;  // ~10 s for the oncoming lane to empty, else give up
        private const int OncomingMaxFrames = 3600;  // ~60 s on the oncoming lane at most
        private const int OncomingRetryFrames = 60;  // after a pass, before the next one may start

        private class OncomingPass
        {
            public Entity Own, Oncoming;
            public List<(Entity approach, Entity feeder)> Entries;          // lanes leading into the oncoming lane at its start node
            public readonly HashSet<Entity> Waiting = new HashSet<Entity>(); // cars held in front of that node
            public readonly HashSet<Entity> Queue = new HashSet<Entity>();   // cars being passed (may be missing from the lane list)
            public bool Out, There, Failed; // There: reached the oncoming lane once; stays set so it does not stop-and-go
            public int Frames;
        }

        private bool Overtaking(Entity v) => m_State.TryGetValue(v, out var s) && s.Oncoming != null;

        /// <summary>
        /// Trucks in the queue (or a big vehicle ourselves): close the oncoming lane and wait for it to empty. Slower
        /// traffic still moving: only when the oncoming lane is already free, then overtake straight away.
        /// </summary>
        private bool StartOncoming(Entity e, EvState st, CarCurrentLane lane, Entity blocker, float speed)
        {
            if (m_Frame < st.OncomingRetryFrame) return false;
            st.OncomingRetryFrame = m_Frame + 60; // the checks below are not free: once a second at most
            if (ParallelLanes(lane).Count > 1 || !EntityManager.HasComponent<EdgeLane>(lane.m_Lane) || NearOwnBuilding(e)) return false;
            if (blocker != Entity.Null && EntityManager.HasComponent<Car>(blocker) && !IsCivilian(blocker)) return false;
            var oncoming = OncomingLane(e, lane);
            if (oncoming == Entity.Null) return false;
            var pass = new OncomingPass { Own = lane.m_Lane, Oncoming = oncoming, Entries = OncomingEntries(oncoming) };
            bool truck = VehicleLength(e) > BigVehicleLength || TruckAhead(e, lane, blocker);
            if (!truck && !(speed >= 1f && OncomingClear(e, pass))) return false;
            st.Oncoming = pass;
            Mod.log.Info($"Einsatzfahrzeug {e.Index}: {(truck ? "Lkw in der Kolonne" : "Gegenfahrbahn frei, überholt fahrende Kolonne")}, sperrt Gegenfahrbahn (Spur {oncoming.Index})");
            return true;
        }

        private void EndOncoming(Entity e, EvState st)
        {
            if (st.Oncoming == null) return;
            st.OncomingRetryFrame = m_Frame + (st.Oncoming.Failed ? RetryCooldown : OncomingRetryFrames);
            if (st.Oncoming.Failed) Mod.log.Info($"Einsatzfahrzeug {e.Index}: Überholen auf der Gegenfahrbahn abgebrochen, Sperre aufgehoben");
            st.Oncoming = null; // held node cars are no longer refreshed and drive on
        }

        /// <summary>
        /// Phase 1: the oncoming lane is closed and the vehicle waits until it is empty. Phase 2: it moves over onto
        /// the oncoming lane (still logically in its own lane), the queue ahead stops and leaves the lane list, so it
        /// passes; with nothing ahead it moves back. Ends at the lane end. Returns false when done or given up.
        /// </summary>
        private bool OncomingOvertake(Entity e, EvState st, ref CarCurrentLane lane, float speed, Entity blocker)
        {
            var em = EntityManager;
            var p = st.Oncoming;
            p.Frames++;
            if (lane.m_Lane != p.Own || !em.Exists(p.Oncoming)) return false; // into the junction: done
            CloseOncoming(p);
            if (!p.Out)
            {
                if (OncomingClear(e, p)) { p.Out = true; p.Frames = 0; Mod.log.Info($"Einsatzfahrzeug {e.Index}: überholt auf der Gegenfahrbahn"); }
                else if (p.Frames > OncomingWaitFrames) { p.Failed = true; return false; }
                else return true;
            }
            if (p.Frames > OncomingMaxFrames) { p.Failed = true; return false; }

            float myHalf = VehicleLength(e) * 0.5f;
            p.Queue.UnionWith(QueueAhead(e, lane));
            if (QueueBlocker(e, blocker)) p.Queue.Add(blocker);
            p.Queue.RemoveWhere(car => !em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<CarCurrentLane>(car)
                || Along(e, car) < -(myHalf + VehicleLength(car) * 0.5f + 2f));
            if (p.Queue.Count == 0 && !p.There) { p.Failed = true; return false; } // nothing to pass: no retry every frame

            float target = p.Queue.Count > 0 ? -(LaneWidth(p.Own) + LaneWidth(p.Oncoming)) * 0.5f : 0f;
            // Slide sideways only while standing; once halfway out the queue leaves the lane lists and the vehicle
            // pulls out diagonally while driving instead of crab-walking.
            float lateral = SetLateral(e, ref lane, target, slide: speed < 0.5f && !p.There);
            if (p.Queue.Count == 0) return math.abs(lateral) > 0.3f; // back in its own lane: done
            p.There |= math.abs(lateral) >= math.abs(target) * 0.5f;
            YieldOncoming(e, lane); // late lane changers and driveways on the oncoming lane stop and keep to the kerb
            foreach (var car in p.Queue)
            {
                Hold(car, e);
                if (p.There) RemoveFromLanes(car);
            }
            return true;
        }

        private bool IsTruck(Entity v) => IsCivilian(v) && (HasTrailer(v) || VehicleLength(v) > BigVehicleLength);

        private bool TruckAhead(Entity e, CarCurrentLane lane, Entity blocker) =>
            (QueueBlocker(e, blocker) && IsTruck(blocker)) || QueueAhead(e, lane).Exists(IsTruck);

        /// <summary>The vehicle we are stuck behind belongs to the queue even when it is not in our lane's list.</summary>
        private bool QueueBlocker(Entity e, Entity blocker)
        {
            var em = EntityManager;
            if (blocker == Entity.Null || blocker == e || !em.HasComponent<Car>(blocker) || !em.HasComponent<CarCurrentLane>(blocker) || Overtaking(blocker)) return false;
            float along = Along(e, blocker);
            return along > 0f && along <= 60f;
        }

        /// <summary>Vehicles ahead of us on our lane up to its end (a trailer counts as its truck), not other overtakers.</summary>
        private List<Entity> QueueAhead(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            var result = new List<Entity>();
            if (!em.HasBuffer<LaneObject>(lane.m_Lane)) return result;
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            float me = lane.m_CurvePosition.x;
            foreach (var o in em.GetBuffer<LaneObject>(lane.m_Lane, true))
            {
                var car = Tractor(o.m_LaneObject);
                if (car == e || result.Contains(car) || !em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car) || Overtaking(car)) continue;
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
            if (!em.HasComponent<Edge>(edge) || !em.HasComponent<Lane>(oncoming)) return result;
            var start = em.GetComponentData<Lane>(oncoming).m_StartNode;
            var ends = em.GetComponentData<Edge>(edge);
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
        /// Lane closure without rerouting: a car about to turn into the oncoming lane is held from the moment its
        /// braking distance reaches the end of its lane. One that can no longer stop drives through; OncomingClear waits.
        /// </summary>
        // ponytail: driveways along the segment are not closed; a car leaving one meets the vehicle.
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
                    float v = Speed(car);
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
            float myLength = VehicleLength(e);
            foreach (var o in em.GetBuffer<LaneObject>(p.Oncoming, true))
                if (em.HasComponent<Game.Objects.Transform>(o.m_LaneObject) && Along(e, o.m_LaneObject) > -myLength) return false;
            return true;
        }

        // ---- One lane per direction: squeeze past over the centre line ----

        private void EndSqueeze(EvState st) { st.InSqueeze = false; st.Kept.Clear(); st.Grace = 0; st.Offset = 0f; st.Still = 0; }

        /// <summary>
        /// Cars ahead (standing or driving) up to the first one that cannot move aside (trailer, truck, bus) pull to
        /// the kerb and stop; the vehicle passes them. Returns false when there is nothing (left) to pass.
        /// </summary>
        private bool Squeeze(Entity e, EvState st, ref CarCurrentLane lane, Entity blocker)
        {
            var em = EntityManager;
            if (!em.HasComponent<EdgeLane>(lane.m_Lane) || !em.HasBuffer<LaneObject>(lane.m_Lane) || LaneWidth(lane.m_Lane) < 2.5f) return false;
            if (!st.InSqueeze && (NearOwnBuilding(e) || (blocker != Entity.Null && em.HasComponent<Car>(blocker) && !IsCivilian(blocker)))) { st.Cooldown = 120; return false; }

            float tail = VehicleLength(e);
            var candidates = new List<(Entity car, float along)>();
            using (var objects = em.GetBuffer<LaneObject>(lane.m_Lane, true).ToNativeArray(Allocator.Temp))
                foreach (var o in objects)
                {
                    var car = Tractor(o.m_LaneObject);
                    if (car != e && IsCivilian(car) && !candidates.Exists(c => c.car == car)) candidates.Add((car, Along(e, car)));
                }
            // The car in front may already stand on the next (junction) lane; cars already yielding stay in until passed.
            if (blocker != Entity.Null && IsCivilian(blocker) && !candidates.Exists(c => c.car == blocker)) candidates.Add((blocker, Along(e, blocker)));
            foreach (var car in st.Kept)
                if (em.Exists(car) && IsCivilian(car) && !candidates.Exists(c => c.car == car)) candidates.Add((car, Along(e, car)));
            candidates.Sort((a, b) => a.along.CompareTo(b.along));

            var ahead = new List<Entity>();
            var behind = new List<Entity>();
            foreach (var (car, along) in candidates)
            {
                if (along < 0f) { if (along >= -(tail + Behind)) behind.Add(car); continue; }
                if (along > 50f || ahead.Count >= 6) break;
                if (HasTrailer(car) || VehicleLength(car) > BigVehicleLength) break; // nothing gets past this one
                ahead.Add(car);
            }
            st.Kept.Clear();
            st.Kept.UnionWith(ahead); // passed cars drop out here and drive on

            // Light ahead turned green: the queue drives off normally and the vehicle follows. Once per stuck episode.
            if (st.GreenIgnore <= 0 && QueueHasGreen(ahead))
            {
                st.GreenIgnore = 600;
                st.Cooldown = 120;
                return false;
            }
            if (ahead.Count == 0)
            {
                if (!st.InSqueeze || ++st.Grace > 90) { st.Cooldown = 60; return false; }
                SetLateral(e, ref lane, st.Offset, slide: false); // between two gaps in the queue: no left-right swerving
                return true;
            }
            st.Grace = 0;

            // Cars to the kerb, the vehicle left of the widest of them.
            var shifts = new float[ahead.Count];
            float leftmost = 0f;
            for (int i = 0; i < ahead.Count; i++)
            {
                shifts[i] = EdgeShift(ahead[i]);
                leftmost = math.min(leftmost, shifts[i] - VehicleWidth(ahead[i]) * 0.5f);
            }
            float passOffset = leftmost - Gap - VehicleWidth(e) * 0.5f;
            if (!CorridorFits(e, lane, passOffset)) { RerouteQueue(e, ahead); st.Cooldown = 120; return false; }

            st.Offset = passOffset;
            float own = SetLateral(e, ref lane, passOffset, slide: Speed(e) < 1.5f);
            bool ready = math.abs(own - passOffset) <= 0.3f;
            for (int i = 0; i < ahead.Count; i++)
            {
                var car = ahead[i];
                float lateral = Steer(car, shifts[i], slide: true);
                Hold(car, e);
                if (ready && math.abs(lateral - shifts[i]) <= 0.15f) RemoveFromLanes(car); // both out of the way: pass it
            }
            foreach (var car in behind) Hold(car, e);
            YieldOncoming(e, lane);
            return true;
        }

        /// <summary>
        /// The vehicle at <paramref name="passOffset"/> reaches this far over the centre line. Each oncoming vehicle in
        /// range leaves room there: a car pulled to its kerb, a vehicle with a trailer stays in the middle.
        /// TypicalCarWidth stands for one still to come. No oncoming lane (one-way): always fits.
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

        private const float OncomingAhead = 80f; // oncoming cars are fast: they need to start braking early

        /// <summary>Vehicles on the oncoming lane from level with our tail to OncomingAhead in front.</summary>
        private List<Entity> OncomingCars(Entity e, Entity oncoming)
        {
            var em = EntityManager;
            var cars = new List<Entity>();
            if (!em.HasBuffer<LaneObject>(oncoming)) return cars;
            float myLength = VehicleLength(e);
            using var objects = em.GetBuffer<LaneObject>(oncoming, true).ToNativeArray(Allocator.Temp);
            foreach (var o in objects)
            {
                var car = Tractor(o.m_LaneObject);
                if (!IsCivilian(car) || cars.Contains(car)) continue;
                float along = Along(e, car);
                if (along >= -myLength && along <= OncomingAhead) cars.Add(car);
            }
            return cars;
        }

        /// <summary>Oncoming traffic next to us stops and pulls to its own kerb (a vehicle with a trailer only stops).</summary>
        // ponytail: oncoming cars beyond the next junction are not stopped; add if they clip in.
        private void YieldOncoming(Entity e, CarCurrentLane lane)
        {
            var c = OncomingLane(e, lane);
            if (c == Entity.Null) return;
            foreach (var car in OncomingCars(e, c))
            {
                Hold(car, e);
                if (!HasTrailer(car)) Steer(car, EdgeShift(car), slide: true); // same edge CorridorFits counted on
            }
        }

        /// <summary>Signal on the way of a car of the queue shows green.</summary>
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

        // ---- Too narrow: the queue turns off at the next junction instead (never against traffic) ----

        private readonly Dictionary<Entity, (Entity alt, int frames)> m_Rerouted = new Dictionary<Entity, (Entity, int)>();

        /// <summary>
        /// Cars ahead that take the same exit as the emergency vehicle at the next junction get another real connection
        /// from their own lane (the exit farthest from ours, no U-turn). Navigation and path are cut after it; the car
        /// repaths once it is on it (UpdateRerouted).
        /// </summary>
        // ponytail: if pathfinding takes longer than driving the connection, the car briefly reaches the end of its path.
        private void RerouteQueue(Entity e, List<Entity> cars)
        {
            var em = EntityManager;
            var evNode = NextNodeLane(e);
            if (evNode == Entity.Null) return;
            float3 evExit = em.GetComponentData<Curve>(evNode).m_Bezier.d;
            foreach (var car in cars)
            {
                if (m_Rerouted.ContainsKey(car) || !em.HasBuffer<CarNavigationLane>(car) || !em.HasBuffer<PathElement>(car) || !em.HasComponent<PathOwner>(car)) continue;
                var nav = em.GetBuffer<CarNavigationLane>(car);
                int k = -1;
                for (int j = 0; j < nav.Length; j++) if (IsNodeLane(nav[j].m_Lane)) { k = j; break; }
                if (k < 0 || !em.HasComponent<Curve>(nav[k].m_Lane)) continue;
                if (math.distance(em.GetComponentData<Curve>(nav[k].m_Lane).m_Bezier.d, evExit) > 8f) continue; // goes elsewhere anyway
                var alt = AlternativeExit(nav[k].m_Lane, evExit);
                if (alt == Entity.Null) continue;
                var n = nav[k]; n.m_Lane = alt; nav[k] = n;
                nav.RemoveRange(k + 1, nav.Length - k - 1);
                var path = em.GetBuffer<PathElement>(car);
                int from = math.min(em.GetComponentData<PathOwner>(car).m_ElementIndex, path.Length);
                path.RemoveRange(from, path.Length - from);
                m_Rerouted[car] = (alt, 0);
                Mod.log.Info($"Einsatzfahrzeug {e.Index}: Straße zu eng, Fahrzeug {car.Index} biegt anders ab");
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
                if (!done && em.GetComponentData<CarCurrentLane>(car).m_Lane == alt && em.HasComponent<PathOwner>(car))
                {
                    var po = em.GetComponentData<PathOwner>(car);
                    po.m_State |= PathFlags.Obsolete;
                    em.SetComponentData(car, po);
                    done = true;
                }
                if (done) m_Rerouted.Remove(car); else m_Rerouted[car] = (alt, frames + 1);
            }
        }

        // ---- Junction: everyone else stops while an emergency vehicle in the gap crosses ----

        /// <summary>
        /// Cars about to enter the junction of <paramref name="nodeLane"/> (that can still stop) wait while the vehicle
        /// crosses. Cars already inside drive on and clear it; holding them deadlocked the vehicle, which waits for
        /// crossing traffic on its real junction lane. The ones right in its way are passed (PassThrough, overlaps).
        /// </summary>
        private void HoldJunction(Entity e, Entity nodeLane)
        {
            var em = EntityManager;
            if (!em.HasComponent<Owner>(nodeLane)) return;
            var node = em.GetComponentData<Owner>(nodeLane).m_Owner;
            if (!em.HasBuffer<ConnectedEdge>(node)) return;
            using var edges = em.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp);
            foreach (var ce in edges)
            {
                if (!em.HasBuffer<SubLane>(ce.m_Edge)) continue;
                using var lanes = em.GetBuffer<SubLane>(ce.m_Edge, true).ToNativeArray(Allocator.Temp);
                foreach (var sl in lanes)
                {
                    if (!em.HasComponent<CarLane>(sl.m_SubLane) || !em.HasBuffer<LaneObject>(sl.m_SubLane)) continue;
                    using var objects = em.GetBuffer<LaneObject>(sl.m_SubLane, true).ToNativeArray(Allocator.Temp);
                    foreach (var o in objects)
                    {
                        var car = Tractor(o.m_LaneObject);
                        if (!IsCivilian(car) || !em.HasBuffer<CarNavigationLane>(car)) continue;
                        if (m_Held.ContainsKey(car)) { Hold(car, e); continue; }
                        var nav = em.GetBuffer<CarNavigationLane>(car, true);
                        if (nav.Length == 0 || !em.HasComponent<Owner>(nav[0].m_Lane) || em.GetComponentData<Owner>(nav[0].m_Lane).m_Owner != node) continue;
                        var cl = em.GetComponentData<CarCurrentLane>(car);
                        float v = Speed(car);
                        if (RemainingDistance(cl) >= v * v / (2f * Braking(car))) Hold(car, e); // one that can no longer stop drives through
                    }
                }
            }
        }
    }
}
