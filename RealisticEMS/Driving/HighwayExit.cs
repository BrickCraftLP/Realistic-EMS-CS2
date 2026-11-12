using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using SubLane = Game.Net.SubLane;

namespace RealisticEMS
{
    /// <summary>
    /// A vehicle in the Rettungsgasse never changes lane: it stays on its real lane (any lane of the carriageway) and
    /// drives the corridor line from there, to the end of the corridor. At the junction it takes a connection from its
    /// own lane to its route's exit, or, if its lane has none, crosses diagonally into the right one: any direction.
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        private const float RetargetDistance = 60f; // junction lane chosen this close to the corridor end

        /// <summary>
        /// Every frame on a segment in gap mode. The lane is pinned (vanilla FixedLane: no "optimal lane" changes);
        /// a change vanilla already started is undone while less than half done. A vehicle standing or crawling in the
        /// queue cannot steer, so it slides onto the line (as in the one-lane squeeze); a driving one steers.
        /// </summary>
        private void KeepGapLane(Entity e, EvState st, ref CarCurrentLane lane, Cw cw)
        {
            if (lane.m_ChangeLane != Entity.Null)
            {
                if (st.Changing || lane.m_ChangeProgress >= 0.5f) return; // ours, or nearly done: let it finish
                lane.m_ChangeLane = Entity.Null;
                lane.m_ChangeProgress = 0f;
            }
            lane.m_LaneFlags |= CarLaneFlags.FixedLane;
            st.Pinned.Add(lane.m_Lane);
            int at = Array.IndexOf(cw.Lanes, lane.m_Lane);
            float target = at >= 0 ? cw.EvTarget(at, lane.m_CurvePosition.x, m_Frame) : 0f; // a lane the layout lacks (short turn lane): its centre
            // The corridor line is a lane or more away (fire engine in the kerb lane, a vehicle coming from an outer
            // lane): one real lane change towards it, the cars there out of the way first, instead of crossing the
            // queue sideways. Not close to the junction, where its connection is chosen (RetargetJunction).
            if (at >= 0 && !cw.TwoWay && math.abs(target) > 0.75f * cw.Width[at] && RemainingDistance(lane) > RetargetDistance)
            {
                int next = at + (target < 0f ? -1 : 1);
                if (next >= 0 && next < cw.Lanes.Length) { StepTo(e, st, ref lane, cw.Lanes[next], "fährt zur Rettungsgasse"); return; }
            }
            SetLateral(e, ref lane, target, slide: Speed(e) < 1.5f);
            if (RemainingDistance(lane) < RetargetDistance) RetargetJunction(e, st, lane);
        }

        /// <summary>
        /// The junction lane after the corridor: among the connections that start at the end of the vehicle's own lane
        /// and lead onto its route's next road, the straightest (through node: it stays in the corridor lane) or the one
        /// ending closest to the planned one (junction). None from its lane: the planned connection stays and the
        /// vehicle crosses over to it from the corridor end; junction traffic is held (HoldJunction). Both lanes are
        /// pinned so vanilla does not pick others.
        /// </summary>
        private void RetargetJunction(Entity e, EvState st, CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasBuffer<CarNavigationLane>(e) || !em.HasComponent<Lane>(lane.m_Lane)) return;
            var nav = em.GetBuffer<CarNavigationLane>(e);
            if (nav.Length == 0) return;
            var first = nav[0];
            if (first.m_Lane == st.Retargeted && (first.m_Flags & CarLaneFlags.FixedLane) != 0) return; // done for this junction
            if (!IsNodeLane(first.m_Lane) || !em.HasComponent<Curve>(first.m_Lane)) return;
            var node = em.GetComponentData<Owner>(first.m_Lane).m_Owner;
            var end = em.GetComponentData<Lane>(lane.m_Lane).m_EndNode;
            Entity next = nav.Length > 1 && em.HasComponent<Owner>(nav[1].m_Lane) ? em.GetComponentData<Owner>(nav[1].m_Lane).m_Owner : Entity.Null;
            float3 plannedEnd = em.GetComponentData<Curve>(first.m_Lane).m_Bezier.d;
            bool through = em.HasBuffer<ConnectedEdge>(node) && em.GetBuffer<ConnectedEdge>(node, true).Length <= 2;

            Entity best = Entity.Null, bestTarget = Entity.Null;
            float bestScore = float.MaxValue;
            if (em.HasBuffer<SubLane>(node))
            {
                using var subLanes = em.GetBuffer<SubLane>(node, true).ToNativeArray(Allocator.Temp);
                foreach (var sl in subLanes)
                {
                    var c = sl.m_SubLane;
                    if (!em.HasComponent<CarLane>(c) || !em.HasComponent<Lane>(c) || !em.HasComponent<Curve>(c)) continue;
                    var cLane = em.GetComponentData<Lane>(c);
                    if (!cLane.m_StartNode.EqualsIgnoreCurvePos(end)) continue;
                    var flags = em.GetComponentData<CarLane>(c).m_Flags;
                    if ((flags & (Game.Net.CarLaneFlags.UTurnLeft | Game.Net.CarLaneFlags.UTurnRight | Game.Net.CarLaneFlags.Forbidden)) != 0) continue;
                    var bez = em.GetComponentData<Curve>(c).m_Bezier;
                    Entity target = Entity.Null;
                    if (next != Entity.Null) { target = LaneStartingAt(next, cLane.m_EndNode); if (target == Entity.Null) continue; }
                    else if (math.distance(bez.d, plannedEnd) > 8f) continue;
                    float score = through ? Bend(bez) : math.distance(bez.d, plannedEnd);
                    if (score < bestScore) { best = c; bestTarget = target; bestScore = score; }
                }
            }

            if (best != Entity.Null) first.m_Lane = best;
            else Mod.log.Info($"Einsatzfahrzeug {e.Index}: fährt bis zum Ende der Gasse, biegt von Spur {lane.m_Lane.Index} quer ab");
            first.m_Flags |= CarLaneFlags.FixedLane;
            nav[0] = first;
            if (bestTarget != Entity.Null)
            {
                var n1 = nav[1];
                n1.m_Lane = bestTarget;
                n1.m_Flags |= CarLaneFlags.FixedLane;
                nav[1] = n1;
                st.Pinned.Add(bestTarget);
            }
            st.Retargeted = first.m_Lane;
        }

        /// <summary>The car lane of <paramref name="owner"/> (road or junction) that starts at <paramref name="at"/>.</summary>
        private Entity LaneStartingAt(Entity owner, PathNode at)
        {
            var em = EntityManager;
            if (!em.HasBuffer<SubLane>(owner)) return Entity.Null;
            foreach (var sl in em.GetBuffer<SubLane>(owner, true))
                if (em.HasComponent<CarLane>(sl.m_SubLane) && em.HasComponent<Lane>(sl.m_SubLane)
                    && em.GetComponentData<Lane>(sl.m_SubLane).m_StartNode.EqualsIgnoreCurvePos(at)) return sl.m_SubLane;
            return Entity.Null;
        }

        /// <summary>Sideways distance between a junction lane's end and the straight line out of its start.</summary>
        private static float Bend(Bezier4x3 bez)
        {
            float2 dir = math.normalizesafe(MathUtils.Tangent(bez, 0f).xz), d = bez.d.xz - bez.a.xz;
            return math.abs(dir.x * d.y - dir.y * d.x);
        }

        /// <summary>
        /// Outside the gap (or lights off): the lanes this system pinned get their lane choice back, the current one and
        /// any still in the navigation list. Vanilla's own FixedLane uses (parking, target lanes) are never touched.
        /// </summary>
        private void ReleasePins(Entity e, EvState st, ref CarCurrentLane lane)
        {
            if (st.Pinned.Count == 0 && st.Retargeted == Entity.Null) return;
            if (st.Pinned.Contains(lane.m_Lane))
            {
                lane.m_LaneFlags &= ~CarLaneFlags.FixedLane;
                lane.m_LaneFlags |= CarLaneFlags.UpdateOptimalLane;
            }
            if (EntityManager.HasBuffer<CarNavigationLane>(e))
            {
                var nav = EntityManager.GetBuffer<CarNavigationLane>(e);
                for (int k = 0; k < nav.Length; k++)
                {
                    var n = nav[k];
                    if (!st.Pinned.Contains(n.m_Lane) && n.m_Lane != st.Retargeted) continue;
                    n.m_Flags &= ~CarLaneFlags.FixedLane;
                    nav[k] = n;
                }
            }
            st.Pinned.Clear();
            st.Retargeted = Entity.Null;
        }
    }
}
