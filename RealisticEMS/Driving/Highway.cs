using Game.Common;
using Game.Net;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;

namespace RealisticEMS
{
    /// <summary>
    /// The emergency vehicle in the Rettungsgasse, on highways and normal roads (any lane count, also one lane each
    /// way: both directions to their kerb, the vehicle on the centre line). The corridor
    /// layout and opening live in Corridor.cs; here the vehicle opens it along its route, drives its line, stops the
    /// cars around it and passes through them.
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        private const float JunctionHoldDistance = 40f; // junction traffic stops once a vehicle in the gap is this close

        private readonly List<(Entity car, float along, bool ramp)> m_GapCars = new List<(Entity, float, bool)>();
        private readonly HashSet<Entity> m_GapDone = new HashSet<Entity>();

        /// <summary>
        /// Slow traffic on the route ahead opens its carriageways (whole carriageway, not single cars). With slow cars
        /// within braking distance the vehicle goes into gap mode: it drives the corridor line on its real lane
        /// (KeepGapLane, HighwayExit.cs), cars around it stop (except at highway exits and entries), cars on its way
        /// leave its lane lists (PassThrough), junction traffic waits. ~50 km/h once the corridor has formed, ~30 before.
        /// Returns true while in gap mode (then nothing else is tried).
        /// </summary>
        // ponytail: the route scan runs every 4th frame per vehicle (vanilla re-plans each car only every 16th); hidden
        // cars are kept out in between by CorridorPreNavSystem.
        private bool DriveGap(Entity e, EvState st, ref CarCurrentLane lane)
        {
            bool scan = ((e.Index + m_Frame) & 3) == 0;
            if (!scan && !st.InGap) return false;
            var em = EntityManager;
            float tail = VehicleLength(e), speed = Speed(e);
            float brake = speed * speed / (2f * Braking(e));
            float reach = math.max(GapNear, brake + 30f), enter = math.max(GapEnter, brake + 30f);
            bool kerb = em.HasComponent<Game.Vehicles.FireEngine>(e); // fire engine: everyone to the kerb, it drives the centre line
            if (scan) ScanRoute(e, st, lane, tail, reach, enter, kerb);
            if (st.GapGrace <= 0) return false;
            bool onEdge = em.HasComponent<EdgeLane>(lane.m_Lane);
            if (!st.InGap && (!onEdge || NearOwnBuilding(e))) return false; // a gap starts on a segment, never at the own station

            bool formed = false;
            Entity junction = Entity.Null;
            if (onEdge)
            {
                var cw = GetCw(lane.m_Lane);
                if (cw == null) return false;
                // Stays open behind the vehicle for the rest of a convoy.
                if (kerb && !cw.Highway) OpenKerb(cw); else Activate(cw, JamHoldFrames);
                formed = m_Frame - cw.Since(m_Frame) >= FormedFrames;
                KeepGapLane(e, st, ref lane, cw);
                var node = NextNodeLane(e);
                if (node != Entity.Null && RemainingDistance(lane) < JunctionHoldDistance) junction = node;
                // One lane in town without its own oncoming partner (one-way, or 1 + 2 lanes): oncoming traffic still stops.
                if (scan && !cw.Highway && !cw.TwoWay && cw.Lanes.Length == 1 && cw.Opposite == Entity.Null) YieldOncoming(e, lane);
            }
            else junction = lane.m_Lane; // in the junction between two jammed segments

            if (scan)
            {
                if (junction != Entity.Null) HoldJunction(e, junction);
                foreach (var c in m_GapCars)
                    if (!c.ramp && c.along >= -(tail + Behind) && c.along <= reach) Hold(c.car, e); // a stopped car at a ramp blocks it
                PassThrough(e, lane, reach);
            }
            ForceThrough(e, st, ref lane);
            CapSpeed(e, formed ? GapSpeedFormed : GapSpeed);
            return true;
        }

        /// <summary>
        /// Slow cars on the route ahead: their carriageways open, cars in the window go to m_GapCars, and a slow one
        /// within <paramref name="enter"/> metres keeps the vehicle in gap mode (GapGrace).
        /// </summary>
        private void ScanRoute(Entity e, EvState st, CarCurrentLane lane, float tail, float reach, float enter, bool kerb)
        {
            m_GapCars.Clear();
            m_GapDone.Clear();
            bool near = false;
            foreach (var (segment, from, to) in RouteAhead(e, lane))
            {
                var cw = GetCw(segment);
                if (cw == null) continue; // pedestrian street, alley: old squeeze (NormalRoad.cs)
                bool fire = kerb && !cw.Highway;
                bool slow = ScanCw(e, cw, segment, false, from, to, tail, reach, enter, ref near);
                // Fire engine over the centre line: the oncoming carriageway beside it stops while it passes.
                if (fire && cw.Opposite != Entity.Null && GetCw(cw.Opposite) is Cw opposite && opposite != cw)
                    ScanCw(e, opposite, segment, true, from, to, tail, reach, enter, ref near);
                if (slow) { if (fire) OpenKerb(cw); else Activate(cw, JamHoldFrames); }
            }
            st.GapGrace = near ? GapGraceFrames : st.GapGrace - 4;
        }

        /// <summary>
        /// Cars of one carriageway in the vehicle's window go to m_GapCars. Returns true when a slow one in its own
        /// direction is ahead; oncoming ones (the other lane of a two-way road, or the whole <paramref name="oncomingCw"/>)
        /// are only stopped while the vehicle passes, never open anything.
        /// </summary>
        private bool ScanCw(Entity e, Cw cw, Entity segment, bool oncomingCw, float from, float to, float tail, float reach, float enter, ref bool near)
        {
            var em = EntityManager;
            bool slow = false;
            foreach (var l in cw.Lanes)
            {
                if (!em.HasBuffer<LaneObject>(l)) continue;
                bool oncoming = oncomingCw || (cw.TwoWay && l != segment);
                var objects = em.GetBuffer<LaneObject>(l, true);
                for (int i = 0; i < objects.Length; i++)
                {
                    var o = objects[i];
                    // ponytail: parallel lanes share the curve parameter only roughly; the from..to window is approximate.
                    if (!oncoming && (o.m_CurvePosition.y < from || o.m_CurvePosition.x > to)) continue;
                    var car = Tractor(o.m_LaneObject);
                    if (car == e || m_GapDone.Contains(car) || !IsCivilian(car)) continue;
                    var cl = em.GetComponentData<CarCurrentLane>(car);
                    if (cl.m_Lane != l && cl.m_ChangeLane != l) continue;
                    float v = Speed(car), along = Along(e, car);
                    bool window = along >= -(tail + Behind) && along <= reach;
                    if (!window && (oncoming || v > GapSlow || along < -tail)) continue; // free-flowing traffic further ahead keeps driving
                    m_GapDone.Add(car);
                    if (!oncoming && v <= GapSlow && along >= -tail) { slow = true; near |= along <= enter; }
                    m_GapCars.Add((car, along, cw.RampZone(o.m_CurvePosition.x)));
                }
            }
            return slow;
        }

        /// <summary>
        /// Standing ~1 s in the gap: the vehicle it brakes for leaves its lane lists for the rest of the pass (a
        /// moving emergency vehicle ahead is followed, that is a convoy). Logs once what it was.
        /// </summary>
        private void Unblock(Entity e, EvState st, ref CarCurrentLane lane, Blocker blocker)
        {
            var em = EntityManager;
            var b = blocker.m_Blocker == Entity.Null ? Entity.Null : Tractor(blocker.m_Blocker);
            if (b == Entity.Null || b == e || !em.HasComponent<Car>(b) || !em.HasComponent<CarCurrentLane>(b)) return;
            if (!IsCivilian(b) && Speed(b) > 0.5f) return;
            if (st.Forced.Add(b)) LogStanding(e, lane, blocker);
            ForceThrough(e, st, ref lane);
        }

        /// <summary>Keeps the vehicles in st.Forced out of the lane lists until the emergency vehicle is past them.</summary>
        private void ForceThrough(Entity e, EvState st, ref CarCurrentLane lane)
        {
            if (st.Forced.Count == 0) return;
            var em = EntityManager;
            float past = VehicleLength(e) * 0.5f + 4f;
            st.Forced.RemoveWhere(b => !em.Exists(b) || em.HasComponent<Deleted>(b) || !em.HasComponent<CarCurrentLane>(b) || Along(e, b) < -past);
            foreach (var b in st.Forced)
            {
                if (IsCivilian(b)) Hold(b, e);
                RemoveFromLanes(b);
                if (em.HasBuffer<LaneObject>(lane.m_Lane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(lane.m_Lane), b);
                lane.m_LaneFlags |= CarLaneFlags.IgnoreBlocker;
            }
        }
    }
}
