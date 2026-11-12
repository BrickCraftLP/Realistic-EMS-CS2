using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;

namespace RealisticEMS
{
    /// <summary>
    /// The Rettungsgasse per carriageway (all lanes of one road segment and direction). Its layout is computed once:
    /// every lane shifts as a block (lane 0 to the centre line, all others to the kerb, one lane: to the kerb), so the
    /// rows stay straight and lanes never overlap. Highway carriageways open on their own when traffic gets slow
    /// (ScanJams, a few per frame); any carriageway also opens ahead of an emergency vehicle (DriveGap).
    /// Cars that vanilla must not see while an emergency vehicle passes are hidden from the lane lists and put back
    /// afterwards (RestoreHidden); vanilla only re-lists a car when it moves.
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        public const float DesignVehicleWidth = 2.6f; // every lane shifts far enough for a truck
        private const float LaneSqueeze = 0.2f;       // space kept between the widest vehicles of two compressed lanes
        private const float Overhang = 0.5f;          // a shifted vehicle may reach this far past the road edge / shoulder
        private const int JamCars = 3;                // slow cars on one highway carriageway that make it a jam
        private const int JamHoldFrames = 600;        // ~10 s: a corridor stays open this long after its last slow car / vehicle
        private const int FormedFrames = 180;         // ~3 s after opening the cars are in place: full speed in the gap
        private const float RampClear = 60f;          // highway: no shoulder use and nobody stopped this close to an exit or entry

        /// <summary>
        /// Rettungsgasse layout in traffic-frame metres (positive = kerb side). <paramref name="offsets"/> are lane centres
        /// from lane 0, <paramref name="roomLeft"/>/<paramref name="roomRight"/> the shoulder past the outer lanes.
        /// Writes each lane's centre shift to <paramref name="shift"/> and returns the emergency vehicle's line (from
        /// lane 0's centre). Two or more lanes: the needed movement is split by the room on each side; kerb-side lanes
        /// close up (never below a truck plus LaneSqueeze) before anyone goes further than Overhang past the edge.
        /// </summary>
        public static float CorridorLayout(float[] offsets, float[] widths, float roomLeft, float roomRight, float[] shift)
        {
            int n = widths.Length;
            const float W = CorridorVehicleWidth + 2f * Gap, V = DesignVehicleWidth;
            float capR = math.max(0f, roomRight + widths[n - 1] * 0.5f - V * 0.5f);
            if (n == 1)
            {
                float leftLimit = -widths[0] * 0.5f - roomLeft;
                shift[0] = math.clamp(leftLimit + W + V * 0.5f, 0f, capR + Overhang);
                return (leftLimit + shift[0] - V * 0.5f) * 0.5f;
            }
            float capL = math.max(0f, roomLeft + widths[0] * 0.5f - V * 0.5f);
            float need = math.max(0f, W + V - offsets[1]); // lane 0 and lane 1 trucks W apart
            float sL = math.min(capL + capR > 0f ? need * capL / (capL + capR) : need * 0.5f, capL + Overhang);
            float sR = need - sL;
            float maxComp = float.MaxValue;
            for (int j = 1; j + 1 < n; j++) maxComp = math.min(maxComp, offsets[j + 1] - offsets[j] - V - LaneSqueeze);
            float comp = n > 2 ? math.clamp((sR - capR) / (n - 2), 0f, math.max(0f, maxComp)) : 0f;
            float last = sR - (n - 2) * comp;
            if (last > capR + Overhang)
            {
                sR -= last - capR - Overhang;              // no more room on the kerb side
                sL = math.min(capL + Overhang, need - sR); // the centre-line side takes what it can
            }
            shift[0] = -sL;
            for (int j = 1; j < n; j++) shift[j] = sR - (j - 1) * comp;
            return (-sL + V * 0.5f + offsets[1] + sR - V * 0.5f) * 0.5f;
        }

        /// <summary>
        /// Kerb layout (fire engines on normal roads): every lane moves to the kerb, so the space along the centre-line
        /// side of lane 0 is free. <paramref name="shared"/>: the oncoming carriageway beside it moves away as well, so
        /// the lane 0 trucks keep half the corridor from the centre line; otherwise all of it from the road edge.
        /// Lanes close up (never below a truck plus LaneSqueeze) before anyone goes further than Overhang past the kerb.
        /// Returns the vehicle's line from lane 0's centre: on the centre line (shared) or the middle of the free space.
        /// </summary>
        public static float KerbLayout(float[] offsets, float[] widths, float roomLeft, float roomRight, bool shared, float[] shift)
        {
            int n = widths.Length;
            const float W = CorridorVehicleWidth + 2f * Gap, V = DesignVehicleWidth;
            float leftEdge = -widths[0] * 0.5f - roomLeft; // centre line, or the road edge on a one-way road
            shift[0] = math.max(0f, leftEdge + (shared ? W * 0.5f : W) + V * 0.5f);
            for (int j = 1; j < n; j++) shift[j] = math.max(0f, shift[j - 1] - math.max(0f, offsets[j] - offsets[j - 1] - V - LaneSqueeze));
            float excess = shift[n - 1] - (math.max(0f, roomRight + widths[n - 1] * 0.5f - V * 0.5f) + Overhang);
            if (excess > 0f) for (int j = 0; j < n; j++) shift[j] = math.max(0f, shift[j] - excess); // no more room at the kerb
            return shared ? leftEdge : (leftEdge + shift[0] - V * 0.5f) * 0.5f;
        }

        /// <summary>
        /// One carriageway with its layout and its corridor state. Shifts and lines are per lane in that lane's own
        /// traffic frame (positive = its kerb), so a two-way road (one lane each way) is one corridor for both directions.
        /// </summary>
        private sealed class Cw
        {
            public Entity[] Lanes;                  // one-way: centre-line side first; two-way: the lane it was built from, then the oncoming one
            public float[] Width, Shift, RampShift;
            public float[] Ev, RampEv;              // emergency vehicle line from each lane's centre
            public float[] KerbShift, KerbEv;       // kerb layout for fire engines (normal roads only, else null)
            public Entity Opposite;                 // a lane of the oncoming carriageway beside lane 0 (moves away too)
            public float Length;
            public bool Highway, TwoWay, RampStart, RampEnd, WasKerb;
            public int ActiveUntil = int.MinValue, ActiveSince, KerbUntil = int.MinValue, KerbSince;
            public bool Active(int frame) => frame < ActiveUntil;
            public bool Kerb(int frame) => KerbShift != null && frame < KerbUntil;
            public bool RampZone(float pos) => (RampStart && Length * pos < RampClear) || (RampEnd && Length * (1f - pos) < RampClear);
            /// <summary>Lateral target for the emergency vehicle on lane <paramref name="k"/>, from that lane's centre.</summary>
            public float EvTarget(int k, float pos, int frame) => Kerb(frame) ? KerbEv[k] : (RampZone(pos) ? RampEv : Ev)[k];
            /// <summary>Where the cars of lane <paramref name="k"/> go, from its centre.</summary>
            public float CarShift(int k, float pos, int frame) => Kerb(frame) ? KerbShift[k] : (RampZone(pos) ? RampShift : Shift)[k];
            public int Since(int frame) => Kerb(frame) ? KerbSince : ActiveSince;
        }

        private const float MinTownLane = 2.7f; // narrower town lanes (alleys): old squeeze / oncoming overtake (NormalRoad.cs)

        private readonly Dictionary<Entity, Cw> m_Cw = new Dictionary<Entity, Cw>(); // lane -> carriageway (null: none)
        private readonly List<Entity> m_HighwayEdges = new List<Entity>();
        private readonly List<Entity> m_NewHighwayEdges = new List<Entity>();
        private Entity[] m_EdgeSnapshot;          // road edges being sorted into m_NewHighwayEdges, a few hundred per frame
        private int m_SnapshotPos;
        private readonly List<Cw> m_ActiveCws = new List<Cw>();
        private int m_HighwayScan, m_HighwayBuilt = int.MinValue / 2;
        private bool m_HighwayDirty = true;
        private EntityQuery m_ChangedEdges, m_RoadEdges;

        private Cw GetCw(Entity lane)
        {
            var em = EntityManager;
            if (em.HasComponent<MasterLane>(lane)) lane = FirstSlave(lane); // paths hold the group's master lane
            if (lane == Entity.Null) return null;
            if (m_Cw.TryGetValue(lane, out var cw) && (cw == null || em.Exists(cw.Lanes[0]))) return cw;
            cw = BuildCw(lane);
            m_Cw[lane] = cw;
            if (cw != null) foreach (var l in cw.Lanes) m_Cw[l] = cw;
            return cw;
        }

        private Cw BuildCw(Entity lane)
        {
            var em = EntityManager;
            if (!em.HasComponent<EdgeLane>(lane) || !em.HasComponent<Curve>(lane) || IsPedestrianStreet(em, lane)) return null;
            bool highway = IsHighway(lane);
            var list = Carriageway(lane, 0.5f);
            int n = list.Count;
            if (n == 0) return null;
            if (!highway)
            {
                foreach (var c in list) if (LaneWidth(c.lane) < MinTownLane) return null;
                if (n == 1)
                {
                    var (other, d) = OncomingPartner(lane, single: true);
                    if (other != Entity.Null) return BuildTwoWay(lane, other, d);
                }
            }
            var cw = new Cw { Lanes = new Entity[n], Width = new float[n], Shift = new float[n], RampShift = new float[n], Ev = new float[n], RampEv = new float[n], Highway = highway };
            var offset = new float[n];
            for (int k = 0; k < n; k++)
            {
                cw.Lanes[k] = list[k].lane;
                offset[k] = list[k].offset - list[0].offset;
                cw.Width[k] = math.max(2.5f, LaneWidth(list[k].lane));
            }
            float line = CorridorLayout(offset, cw.Width, ShoulderRoom(cw.Lanes[0], centre: true), ShoulderRoom(cw.Lanes[n - 1], centre: false), cw.Shift);
            float rampLine = CorridorLayout(offset, cw.Width, 0f, 0f, cw.RampShift);
            for (int k = 0; k < n; k++) { cw.Ev[k] = line - offset[k]; cw.RampEv[k] = rampLine - offset[k]; }
            if (!highway)
            {
                // Fire engines: everyone to the kerb, the oncoming carriageway beside it too (it shares the space).
                cw.Opposite = OncomingPartner(cw.Lanes[0], single: false).lane;
                cw.KerbShift = new float[n];
                cw.KerbEv = new float[n];
                float kerbLine = KerbLayout(offset, cw.Width, ShoulderRoom(cw.Lanes[0], centre: true), ShoulderRoom(cw.Lanes[n - 1], centre: false),
                    shared: cw.Opposite != Entity.Null, cw.KerbShift);
                for (int k = 0; k < n; k++) cw.KerbEv[k] = kerbLine - offset[k];
            }
            var owner = em.GetComponentData<Owner>(lane).m_Owner;
            if (cw.Highway && em.HasComponent<Edge>(owner) && em.HasComponent<Curve>(cw.Lanes[0]))
            {
                var edge = em.GetComponentData<Edge>(owner);
                var delta = em.GetComponentData<EdgeLane>(cw.Lanes[0]).m_EdgeDelta;
                bool fwd = delta.y >= delta.x; // lanes run from the edge's start node to its end node
                cw.RampStart = IsRampNode(fwd ? edge.m_Start : edge.m_End);
                cw.RampEnd = IsRampNode(fwd ? edge.m_End : edge.m_Start);
                cw.Length = em.GetComponentData<Curve>(cw.Lanes[0]).m_Length;
            }
            return cw;
        }

        /// <summary>
        /// Two-way road with one lane each way: both directions pull to their kerb, the vehicle drives the centre line.
        /// Same layout as two lanes in one direction with the oncoming lane on the centre-line side, then turned into
        /// each lane's own frame.
        /// </summary>
        private Cw BuildTwoWay(Entity a, Entity b, float d)
        {
            float wa = math.max(2.5f, LaneWidth(a)), wb = math.max(2.5f, LaneWidth(b));
            var shift = new float[2];
            float line = CorridorLayout(new[] { 0f, d }, new[] { wb, wa }, 0f, 0f, shift); // in a's frame, from b's centre
            var lanes = new[] { a, b };
            var kerb = new[] { shift[1], -shift[0] };
            var ev = new[] { line - d, -line };
            // Already everyone at the kerb and the vehicle on the centre line: the fire engine layout is the same.
            return new Cw { Lanes = lanes, Width = new[] { wa, wb }, Shift = kerb, RampShift = kerb, Ev = ev, RampEv = ev, KerbShift = kerb, KerbEv = ev, TwoWay = true };
        }

        /// <summary>
        /// The oncoming lane right beside a town lane, on its centre-line side, with its lateral distance; Null if none
        /// (or, with <paramref name="single"/>, unless that one is a single lane too: one lane each way).
        /// </summary>
        private (Entity lane, float distance) OncomingPartner(Entity lane, bool single)
        {
            var em = EntityManager;
            var owner = em.GetComponentData<Owner>(lane).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return (Entity.Null, 0f);
            var bez = em.GetComponentData<Curve>(lane).m_Bezier;
            float3 here = MathUtils.Position(bez, 0.5f), dir = math.normalizesafe(MathUtils.Tangent(bez, 0.5f));
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), dir)) * m_Side;
            Entity best = Entity.Null;
            float bestDistance = 6f;
            foreach (var sl in em.GetBuffer<SubLane>(owner, true))
            {
                var c = sl.m_SubLane;
                if (c == lane || !em.HasComponent<CarLane>(c) || !em.HasComponent<EdgeLane>(c) || !em.HasComponent<Curve>(c)
                    || em.HasComponent<MasterLane>(c) || IsPedestrianStreet(em, c)) continue;
                var cb = em.GetComponentData<Curve>(c).m_Bezier;
                MathUtils.Distance(cb, here, out float t);
                if (t <= 0f || t >= 1f || math.dot(math.normalizesafe(MathUtils.Tangent(cb, t)), dir) > -0.8f) continue; // not oncoming
                float side = -math.dot(MathUtils.Position(cb, t) - here, right); // centre-line side is positive here
                if (side > 0f && side < bestDistance) { best = c; bestDistance = side; }
            }
            if (best == Entity.Null || LaneWidth(best) < MinTownLane || (single && Carriageway(best, 0.5f).Count != 1)) return (Entity.Null, 0f);
            return (best, bestDistance);
        }

        /// <summary>The first real lane of a master lane's group; Null if there is none.</summary>
        private Entity FirstSlave(Entity master)
        {
            var em = EntityManager;
            var owner = em.GetComponentData<Owner>(master).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return Entity.Null;
            var subLanes = em.GetBuffer<SubLane>(owner, true);
            int i = em.GetComponentData<MasterLane>(master).m_MinIndex;
            return i < subLanes.Length && em.HasComponent<SlaveLane>(subLanes[i].m_SubLane) ? subLanes[i].m_SubLane : Entity.Null;
        }

        private bool IsRampNode(Entity node) => EntityManager.HasBuffer<ConnectedEdge>(node) && EntityManager.GetBuffer<ConnectedEdge>(node, true).Length > 2;

        /// <summary>Roads were built or changed: forget their carriageways; the highway list is rebuilt soon.</summary>
        private void InvalidateCw()
        {
            if (m_ChangedEdges.IsEmptyIgnoreFilter) return;
            var em = EntityManager;
            using var edges = m_ChangedEdges.ToEntityArray(Allocator.Temp);
            foreach (var edge in edges)
                if (em.HasBuffer<SubLane>(edge))
                    foreach (var sl in em.GetBuffer<SubLane>(edge, true)) m_Cw.Remove(sl.m_SubLane);
            m_HighwayDirty = true;
        }

        /// <summary>
        /// The highway road list, rebuilt after road changes without a hitch: a snapshot of all road edges is sorted a
        /// few hundred per frame (UseHighwayRules), then swapped in; dead cache entries are dropped at the same time.
        /// </summary>
        private void UpdateHighwayEdges()
        {
            var em = EntityManager;
            if (m_EdgeSnapshot == null)
            {
                if (!m_HighwayDirty || m_Frame - m_HighwayBuilt < 256) return;
                using var edges = m_RoadEdges.ToEntityArray(Allocator.Temp);
                m_EdgeSnapshot = edges.ToArray();
                m_SnapshotPos = 0;
                m_NewHighwayEdges.Clear();
                m_HighwayDirty = false;
                return;
            }
            int end = math.min(m_EdgeSnapshot.Length, m_SnapshotPos + 512);
            for (; m_SnapshotPos < end; m_SnapshotPos++)
            {
                var edge = m_EdgeSnapshot[m_SnapshotPos];
                if (em.Exists(edge) && IsHighwayRoad(edge)) m_NewHighwayEdges.Add(edge);
            }
            if (m_SnapshotPos < m_EdgeSnapshot.Length) return;
            m_HighwayEdges.Clear();
            m_HighwayEdges.AddRange(m_NewHighwayEdges);
            m_EdgeSnapshot = null;
            m_HighwayBuilt = m_Frame;
            m_HighwayScan = 0;
            m_Keys.Clear();
            foreach (var k in m_Cw.Keys) if (!em.Exists(k)) m_Keys.Add(k);
            foreach (var k in m_Keys) m_Cw.Remove(k);
        }

        private void Activate(Cw cw, int frames)
        {
            if (!cw.Active(m_Frame)) { cw.ActiveSince = m_Frame; m_ActiveCws.Add(cw); } // counted in the minute log (Stats)
            cw.ActiveUntil = math.max(cw.ActiveUntil, m_Frame + frames);
        }

        /// <summary>
        /// A fire engine on a normal road: the carriageway goes to the kerb layout, and so does the oncoming one beside it,
        /// so the fire engine has the space along the centre line. Highways keep the normal corridor (median).
        /// </summary>
        private void OpenKerb(Cw cw)
        {
            Activate(cw, JamHoldFrames);
            KerbOn(cw);
            if (cw.Opposite == Entity.Null) return;
            var opposite = GetCw(cw.Opposite);
            if (opposite == null || opposite == cw) return;
            Activate(opposite, JamHoldFrames);
            KerbOn(opposite);
        }

        private void KerbOn(Cw cw)
        {
            if (cw.KerbShift == null) return;
            if (!cw.Kerb(m_Frame)) cw.KerbSince = m_Frame;
            cw.KerbUntil = math.max(cw.KerbUntil, m_Frame + JamHoldFrames);
        }

        /// <summary>
        /// A share of the highway roads per frame (each at least every ~0.5 s): JamCars slow cars on a carriageway open
        /// its corridor, any slow car keeps an open one open. Carriageways are built on first visit, not all at once.
        /// </summary>
        private void ScanJams()
        {
            InvalidateCw();
            UpdateHighwayEdges();
            int n = m_HighwayEdges.Count;
            if (n == 0) return;
            var em = EntityManager;
            int budget = math.min(n, math.max(16, n / 32));
            for (int i = 0; i < budget; i++)
            {
                if (m_HighwayScan >= n) m_HighwayScan = 0;
                var edge = m_HighwayEdges[m_HighwayScan++];
                if (!em.HasBuffer<SubLane>(edge)) continue;
                using var subLanes = em.GetBuffer<SubLane>(edge, true).ToNativeArray(Allocator.Temp);
                foreach (var sl in subLanes)
                {
                    var l = sl.m_SubLane;
                    if (!em.HasComponent<CarLane>(l) || !em.HasComponent<EdgeLane>(l) || em.HasComponent<MasterLane>(l) || !em.HasBuffer<LaneObject>(l)) continue;
                    var cw = GetCw(l);
                    if (cw == null || !cw.Highway || cw.Lanes[0] != l) continue; // each carriageway once, from its first lane
                    int slow = 0;
                    foreach (var cl in cw.Lanes)
                    {
                        if (!em.HasBuffer<LaneObject>(cl)) continue;
                        var objects = em.GetBuffer<LaneObject>(cl, true);
                        for (int j = 0; j < objects.Length && slow < JamCars; j++)
                        {
                            var car = objects[j].m_LaneObject; // trailers have no Car: counted once per vehicle
                            if (em.HasComponent<Car>(car) && Speed(car) < GapSlow) slow++;
                        }
                    }
                    if (slow >= JamCars || (slow > 0 && cw.Active(m_Frame))) Activate(cw, JamHoldFrames);
                }
            }
        }

        private void ClearCorridors() { m_ActiveCws.Clear(); m_HighwayEdges.Clear(); m_EdgeSnapshot = null; m_HighwayDirty = true; }

        // Car -> frame its corridor last placed it; standing cars at their target; standing cars gliding there now.
        private readonly Dictionary<Entity, int> m_ShiftedAt = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, float> m_Settled = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, (Cw cw, int k)> m_Sliding = new Dictionary<Entity, (Cw, int)>();
        private readonly List<Entity> m_SlideKeys = new List<Entity>();
        private int m_Visited; // cars looked at this frame (minute log)

        private float LanePositionFor(Entity car, CarCurrentLane cl, float metres, float laneWidth)
        {
            float room = math.max(0.1f, laneWidth - VehicleWidth(car));
            return metres * m_Side * (cl.m_CurvePosition.z >= cl.m_CurvePosition.x ? 1f : -1f) / room;
        }

        /// <summary>
        /// Every civilian car on an open carriageway moves to its lane's shift. Vanilla updates each car only every 16th
        /// frame, so each carriageway is visited once per 16 frames (spread over them): a driving car gets its eased
        /// target, a standing one joins the gliding set, which moves every frame until it is in place (trailers along).
        /// Near a highway exit or entry the shoulder stays free. Cars that left the corridor ease back (SteerBack).
        /// </summary>
        private void ApplyCorridors()
        {
            var em = EntityManager;
            m_Visited = 0;
            for (int i = m_ActiveCws.Count - 1; i >= 0; i--)
            {
                var cw = m_ActiveCws[i];
                if (!cw.Active(m_Frame)) { m_ActiveCws.RemoveAt(i); continue; }
                bool kerb = cw.Kerb(m_Frame), switched = kerb != cw.WasKerb; // a fire engine came or went: everyone moves again
                if (!switched && ((cw.Lanes[0].Index + m_Frame) & 15) != 0 && m_Frame - cw.ActiveSince > 1) continue; // its turn, or just opened
                cw.WasKerb = kerb;
                for (int k = 0; k < cw.Lanes.Length; k++)
                {
                    var lane = cw.Lanes[k];
                    if (!em.HasBuffer<LaneObject>(lane)) continue;
                    using var objects = em.GetBuffer<LaneObject>(lane, true).ToNativeArray(Allocator.Temp);
                    foreach (var o in objects)
                    {
                        var car = o.m_LaneObject;
                        if (!IsCivilian(car) || (m_ShiftedAt.TryGetValue(car, out var at) && at == m_Frame)) continue;
                        var cl = em.GetComponentData<CarCurrentLane>(car);
                        if (cl.m_Lane != lane || cl.m_ChangeLane != Entity.Null) continue;
                        m_Visited++;
                        m_ShiftedAt[car] = m_Frame;
                        if (switched) m_Settled.Remove(car);
                        if (Speed(car) < 0.1f)
                        {
                            if (m_Settled.TryGetValue(car, out var value)) m_Eased[car] = (value, m_Frame); // in place
                            else m_Sliding[car] = (cw, k);
                            continue;
                        }
                        m_Settled.Remove(car);
                        m_Sliding.Remove(car);
                        float metres = cw.CarShift(k, cl.m_CurvePosition.x, m_Frame);
                        float room = math.max(0.1f, cw.Width[k] - VehicleWidth(car));
                        cl.m_LanePosition = Ease(car, cl.m_LanePosition, LanePositionFor(car, cl, metres, cw.Width[k]), LateralStep * 16f / room);
                        em.SetComponentData(car, cl);
                    }
                }
            }
            Glide();
            SteerBack();
        }

        /// <summary>Standing cars on their way to the corridor shift move a little every frame, so they glide.</summary>
        private void Glide()
        {
            if (m_Sliding.Count == 0) return;
            var em = EntityManager;
            m_SlideKeys.Clear();
            m_SlideKeys.AddRange(m_Sliding.Keys);
            foreach (var car in m_SlideKeys)
            {
                var (cw, k) = m_Sliding[car];
                bool valid = em.Exists(car) && IsCivilian(car) && cw.Active(m_Frame);
                var cl = valid ? em.GetComponentData<CarCurrentLane>(car) : default;
                if (!valid || cl.m_Lane != cw.Lanes[k] || cl.m_ChangeLane != Entity.Null || Speed(car) >= 0.1f) { m_Sliding.Remove(car); continue; }
                m_Visited++;
                float metres = cw.CarShift(k, cl.m_CurvePosition.x, m_Frame);
                float lateral = SetLateral(car, ref cl, metres, slide: true);
                em.SetComponentData(car, cl);
                m_ShiftedAt[car] = m_Frame;
                if (math.abs(lateral - metres) < 0.05f) { m_Settled[car] = cl.m_LanePosition; m_Sliding.Remove(car); }
            }
        }

        // ---- Hidden cars: out of the lane lists while an emergency vehicle passes, back in afterwards ----

        private const int HiddenFrames = 6;  // emergency vehicles refresh what they hide every 4 frames
        private readonly Dictionary<Entity, int> m_Hidden = new Dictionary<Entity, int>(); // car -> frame last hidden
        private long m_ReListed;             // hidden cars found back in a lane list before vanilla navigation (minute log)

        /// <summary>
        /// Right before vanilla navigation (CorridorPreNavSystem): every hidden car leaves the lane lists again. Vanilla
        /// lists a car again whenever it moves, and an emergency vehicle that brakes for it then stands for 16 frames.
        /// </summary>
        public void ReHide()
        {
            if (m_Hidden.Count == 0) return;
            var em = EntityManager;
            foreach (var car in m_Hidden.Keys)
            {
                if (!em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<CarCurrentLane>(car)) continue;
                var lane = em.GetComponentData<CarCurrentLane>(car).m_Lane;
                if (em.HasBuffer<LaneObject>(lane))
                    foreach (var o in em.GetBuffer<LaneObject>(lane, true)) if (o.m_LaneObject == car) { m_ReListed++; break; }
                RemoveFromLanes(car, track: false);
            }
        }

        /// <summary>
        /// End of the frame: a car no vehicle has hidden for HiddenFrames goes back into the lane lists at its current
        /// position (vanilla would re-list it only once it moves; until then others drove through it).
        /// </summary>
        private void RestoreHidden()
        {
            if (m_Hidden.Count == 0) return;
            var em = EntityManager;
            m_Keys.Clear();
            foreach (var kv in m_Hidden) if (m_Frame - kv.Value > HiddenFrames) m_Keys.Add(kv.Key);
            foreach (var car in m_Keys)
            {
                m_Hidden.Remove(car);
                if (!em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<CarCurrentLane>(car)) continue;
                var cl = em.GetComponentData<CarCurrentLane>(car);
                Relist(cl.m_Lane, car, cl.m_CurvePosition.xy);
                Relist(cl.m_ChangeLane, car, cl.m_CurvePosition.xy);
                if (!em.HasBuffer<LayoutElement>(car)) continue;
                using var layout = em.GetBuffer<LayoutElement>(car, true).ToNativeArray(Allocator.Temp);
                foreach (var le in layout)
                {
                    if (le.m_Vehicle == car || !em.HasComponent<CarTrailerLane>(le.m_Vehicle)) continue;
                    var tl = em.GetComponentData<CarTrailerLane>(le.m_Vehicle);
                    Relist(tl.m_Lane, le.m_Vehicle, tl.m_CurvePosition);
                    Relist(tl.m_NextLane, le.m_Vehicle, tl.m_NextPosition);
                }
            }
        }

        private void Relist(Entity lane, Entity v, float2 pos)
        {
            if (!EntityManager.HasBuffer<LaneObject>(lane)) return;
            var buffer = EntityManager.GetBuffer<LaneObject>(lane);
            NetUtils.RemoveLaneObject(buffer, v); // vanilla may have listed it again already: no duplicates
            NetUtils.AddLaneObject(buffer, v, pos);
        }

        // ---- Cost, logged once a minute ----

        private readonly System.Diagnostics.Stopwatch m_Watch = new System.Diagnostics.Stopwatch();
        private double m_MsSum, m_MsMax;
        private long m_VisitedSum;
        private int m_StatFrames;

        private void Stats()
        {
            double ms = m_Watch.Elapsed.TotalMilliseconds;
            m_MsSum += ms;
            m_MsMax = System.Math.Max(m_MsMax, ms);
            m_VisitedSum += m_Visited;
            if (++m_StatFrames < 3600) return;
            if (m_ActiveCws.Count > 0 || m_MsMax > 1.0)
                Mod.log.Info($"Rettungsgasse Leistung: {m_MsSum / m_StatFrames:F3} ms/Frame (max {m_MsMax:F2}), {m_ActiveCws.Count} offene Fahrbahn(en), " +
                             $"Ø {m_VisitedSum / m_StatFrames} Autos/Frame bearbeitet, {m_ShiftedAt.Count} in Gassen, {m_Hidden.Count} ausgeblendet, " +
                             $"{m_ReListed} wieder gelistet, {m_HighwayEdges.Count} Autobahn-Abschnitte");
            m_MsSum = m_MsMax = 0; m_VisitedSum = 0; m_StatFrames = 0; m_ReListed = 0;
        }

    }

    /// <summary>
    /// Runs right before vanilla CarNavigationSystem: cars hidden for a passing emergency vehicle leave the lane lists
    /// again, whatever listed them since (EmergencyLaneSystem.ReHide).
    /// </summary>
    public partial class CorridorPreNavSystem : Game.GameSystemBase
    {
        private EmergencyLaneSystem m_Lanes;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Lanes = World.GetOrCreateSystemManaged<EmergencyLaneSystem>();
        }

        protected override void OnUpdate() => m_Lanes.ReHide();
    }
}
