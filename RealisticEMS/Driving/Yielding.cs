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
    /// Shared by every road type: corridor constants and self-check, cars stopped for an emergency vehicle (Hold,
    /// convoy-aware), cars easing back after a corridor (SteerBack), the route ahead, and PassThrough, which lets the
    /// vehicle roll through stopped cars on its real lane. All offsets are in the traffic frame (see LaneOps).
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        private const float Gap = 0.4f;            // metres between a yielding car and the passing vehicle
        private const float GapScan = 250f;        // route ahead in which slow traffic forms the Rettungsgasse
        private const float GapSlow = 8f;          // m/s (~30 km/h): below this traffic counts as slow / jammed
        private const float GapNear = 80f;         // cars at least this far ahead of a vehicle in the gap stop (more when fast)
        private const float GapEnter = 60f;        // the vehicle moves into the gap with slow cars this close (more when fast)
        private const int GapGraceFrames = 120;    // ~2 s: keeps gap mode across short holes in the queue
        private const float GapSpeed = 8f;         // m/s (~30 km/h): vehicle speed while the corridor is forming
        private const float GapSpeedFormed = 14f;  // m/s (~50 km/h): vehicle speed in a formed corridor
        private const float Behind = 30f;          // cars behind the vehicle in its lane stop: nobody follows it through
        private const float HoldBehind = 15f;      // a passed car drives on once this far behind the vehicle's tail
        private const int HoldMaxFrames = 600;     // ~10 s, in case the vehicle stops right after passing
        private const float ConvoyLookback = 200f; // a held car waits while another vehicle in the gap is this close behind it
        private const int StillGiveUp = 600;       // ~10 s standing in a corridor: release everyone
        private const int RetryCooldown = 1200;    // ~20 s before the next try
        private const float BigVehicleLength = 6.5f; // trucks, buses, campers: they keep their line in a one-lane squeeze
        private const float TypicalCarWidth = 2f;
        private const float CorridorBuildingDistance = 100f;

        // Every corridor is made wide enough for the widest emergency vehicle (fire engine), so one gap serves all.
        private const float CorridorVehicleWidth = 2.8f;

        private readonly List<Entity> m_Keys = new List<Entity>(); // reused key buffer (never nested)

        /// <summary>Corridor layout for 1-4 lanes, lane widths 3 / 3.75 m, with and without shoulders; checked at load.</summary>
        public static void SelfCheck()
        {
            const float W = CorridorVehicleWidth + 2f * Gap, V = DesignVehicleWidth;
            foreach (var lw in new[] { 3f, 3.75f })
                foreach (var room in new[] { 0f, 2.5f })
                    for (int n = 1; n <= 4; n++)
                    {
                        var widths = new float[n]; var offsets = new float[n]; var shift = new float[n];
                        for (int k = 0; k < n; k++) { widths[k] = lw; offsets[k] = k * lw; }
                        float line = CorridorLayout(offsets, widths, room, room, shift);
                        string at = $"{n} lane(s), width {lw}, shoulder {room}";
                        // Free space: from the road edge (one lane) or the lane 0 trucks to the lane 1 trucks.
                        float left = n == 1 ? -lw * 0.5f - room : shift[0] + V * 0.5f;
                        float right = n == 1 ? shift[0] - V * 0.5f : offsets[1] + shift[1] - V * 0.5f;
                        if (room > 0f && right - left < W - 0.01f) throw new Exception($"Rettungsgasse too narrow: {at}");
                        if (line < left - 0.01f || line > right + 0.01f) throw new Exception($"Rettungsgasse: line outside the gap: {at}");
                        if (n > 1 && shift[0] > 0f) throw new Exception($"Rettungsgasse: lane 0 moves the wrong way: {at}");
                        for (int k = n > 1 ? 1 : 0; k < n; k++) if (shift[k] < 0f) throw new Exception($"Rettungsgasse: lane {k} moves the wrong way: {at}");
                        for (int k = 1; k + 1 < n; k++)
                            if (offsets[k + 1] + shift[k + 1] - offsets[k] - shift[k] < V + LaneSqueeze - 0.01f) throw new Exception($"Rettungsgasse: lanes overlap: {at}");
                        if (shift[n - 1] + V * 0.5f > lw * 0.5f + room + Overhang + 0.01f) throw new Exception($"Rettungsgasse: off the road on the kerb side: {at}");
                        if (n > 1 && shift[0] - V * 0.5f < -lw * 0.5f - room - Overhang - 0.01f) throw new Exception($"Rettungsgasse: off the road on the centre side: {at}");

                        // Fire engine kerb layout: everyone to the kerb, the space along the centre line free.
                        foreach (var shared in new[] { true, false })
                        {
                            float need = shared ? W * 0.5f : W;
                            float kerbLine = KerbLayout(offsets, widths, 0f, room, shared, shift);
                            string kat = $"kerb, {at}, shared {shared}";
                            float free = shift[0] - V * 0.5f + lw * 0.5f; // centre line (or road edge) to the lane 0 trucks
                            bool atKerb = shift[n - 1] + V * 0.5f >= lw * 0.5f + room + Overhang - 0.01f; // no more room: narrower is fine
                            if (!atKerb && free < need - 0.01f) throw new Exception($"Rettungsgasse too narrow: {kat}");
                            for (int k = 0; k < n; k++) if (shift[k] < 0f) throw new Exception($"Rettungsgasse: lane {k} moves the wrong way: {kat}");
                            for (int k = 0; k + 1 < n; k++)
                                if (offsets[k + 1] + shift[k + 1] - offsets[k] - shift[k] < V + LaneSqueeze - 0.01f) throw new Exception($"Rettungsgasse: lanes overlap: {kat}");
                            if (shift[n - 1] + V * 0.5f > lw * 0.5f + room + Overhang + 0.01f) throw new Exception($"Rettungsgasse: off the road on the kerb side: {kat}");
                            if (kerbLine < -lw * 0.5f - 0.01f || kerbLine > shift[0] - V * 0.5f + 0.01f) throw new Exception($"Rettungsgasse: fire engine line outside: {kat}");
                        }
                    }
            Mod.log.Info("Rettungsgasse self-check ok");
        }

        // ---- Cars stopped for an emergency vehicle ----

        private readonly Dictionary<Entity, (Entity ev, int frames)> m_Held = new Dictionary<Entity, (Entity, int)>();
        private readonly List<(float3 pos, float3 fwd)> m_GapEvs = new List<(float3, float3)>(); // vehicles in gap mode this frame

        private void Hold(Entity car, Entity ev) { Freeze(car); m_Held[car] = (ev, 0); }

        /// <summary>
        /// After all vehicles: a held car still ahead of its vehicle that was not refreshed for a few frames (corridor ended)
        /// drives on; a passed one waits until the vehicle is HoldBehind metres ahead. Either way it keeps waiting while
        /// another vehicle in the gap comes up behind it (convoy), up to HoldMaxFrames.
        /// </summary>
        private void ReleaseHeld()
        {
            if (m_Held.Count == 0) return;
            var em = EntityManager;
            m_Keys.Clear();
            m_Keys.AddRange(m_Held.Keys);
            foreach (var car in m_Keys)
            {
                var (ev, frames) = m_Held[car];
                bool gone = !em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<Transform>(car);
                bool release = gone || frames >= HoldMaxFrames || !em.Exists(ev) || !em.HasComponent<Transform>(ev);
                if (!release)
                {
                    float along = Along(ev, car);
                    // Ahead and not held again for a few frames (vehicles refresh every 4): the corridor ended.
                    release = along > 0f ? frames > HiddenFrames : -along > VehicleLength(ev) * 0.5f + HoldBehind;
                }
                if (release && !gone && frames < HoldMaxFrames && ConvoyComing(car)) release = false;
                if (release) m_Held.Remove(car);
                else { Freeze(car); m_Held[car] = (ev, frames + 1); }
            }
        }

        /// <summary>A vehicle in gap mode up to ConvoyLookback metres behind the car, roughly in line with it.</summary>
        private bool ConvoyComing(Entity car)
        {
            if (m_GapEvs.Count == 0) return false;
            float3 p = EntityManager.GetComponentData<Transform>(car).m_Position;
            foreach (var (pos, fwd) in m_GapEvs)
            {
                float3 d = p - pos;
                float along = math.dot(d, fwd);
                if (along > 0f && along < ConvoyLookback && math.lengthsq((d - fwd * along).xz) < 144f) return true;
            }
            return false;
        }

        /// <summary>
        /// No corridor next to the own station, the target hospital or a police/fire station: emergency vehicles queue
        /// there behind each other and frozen cars would only jam the surroundings.
        /// </summary>
        private bool NearOwnBuilding(Entity e)
        {
            var em = EntityManager;
            float3 pos = em.GetComponentData<Transform>(e).m_Position;
            foreach (var building in new[] { em.HasComponent<Owner>(e) ? em.GetComponentData<Owner>(e).m_Owner : Entity.Null, em.HasComponent<Target>(e) ? em.GetComponentData<Target>(e).m_Target : Entity.Null })
            {
                if (building == Entity.Null || !em.HasComponent<Game.Buildings.Building>(building) || !em.HasComponent<Transform>(building)) continue;
                if (!em.HasComponent<Game.Buildings.Hospital>(building) && !em.HasComponent<Game.Buildings.PoliceStation>(building) && !em.HasComponent<Game.Buildings.FireStation>(building)) continue;
                if (math.distance(em.GetComponentData<Transform>(building).m_Position.xz, pos.xz) < CorridorBuildingDistance) return true;
            }
            return false;
        }

        /// <summary>Lane of a road with highway rules (motorway, highway ramps, also its nodes).</summary>
        private bool IsHighway(Entity lane) =>
            EntityManager.HasComponent<Owner>(lane) && IsHighwayRoad(EntityManager.GetComponentData<Owner>(lane).m_Owner);

        /// <summary>Road segment or node of a road prefab with highway rules.</summary>
        private bool IsHighwayRoad(Entity road)
        {
            var em = EntityManager;
            if (!em.HasComponent<Game.Prefabs.PrefabRef>(road)) return false;
            var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(road).m_Prefab;
            return em.HasComponent<Game.Prefabs.RoadData>(prefab)
                && (em.GetComponentData<Game.Prefabs.RoadData>(prefab).m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
        }

        // ---- Cars leaving a corridor ----

        /// <summary>
        /// Cars no open corridor placed for a full vanilla cycle return to their lane gently (vanilla would snap their
        /// lane position back to [-0.5, 0.5]); each is looked at once per 16 frames, like vanilla moves it. A standing
        /// one keeps its spot until it drives off, a moving one eases back; on a junction lane (between two segments)
        /// or while hidden for a passing vehicle the value is kept. Once within vanilla's own range vanilla takes over.
        /// </summary>
        private void SteerBack()
        {
            if (m_ShiftedAt.Count == 0) return;
            var em = EntityManager;
            m_Keys.Clear();
            foreach (var kv in m_ShiftedAt)
                if (m_Frame - kv.Value > 20 && ((kv.Key.Index + m_Frame) & 15) == 0) m_Keys.Add(kv.Key);
            foreach (var car in m_Keys)
            {
                m_Settled.Remove(car);
                m_Sliding.Remove(car);
                (float value, int frame) x = default;
                bool done = !em.Exists(car) || !IsCivilian(car) || !m_Eased.TryGetValue(car, out x) || math.abs(x.value) <= 0.5f;
                var cl = done ? default : em.GetComponentData<CarCurrentLane>(car);
                if (done || cl.m_ChangeLane != Entity.Null) { m_ShiftedAt.Remove(car); continue; }
                m_Visited++;
                if (Speed(car) < 0.5f || !em.HasComponent<EdgeLane>(cl.m_Lane) || m_Hidden.ContainsKey(car)) { cl.m_LanePosition = x.value; m_Eased[car] = (x.value, m_Frame); }
                else cl.m_LanePosition = Ease(car, cl.m_LanePosition, 0f, LateralStep * 16f / math.max(0.1f, LaneWidth(cl.m_Lane) - VehicleWidth(car)));
                em.SetComponentData(car, cl);
            }
        }

        /// <summary>Segment lanes along the route, from the vehicle's tail up to GapScan metres: (lane, from, to) curve positions.</summary>
        private List<(Entity lane, float from, float to)> RouteAhead(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            var result = new List<(Entity, float, float)>();
            float budget = GapScan;
            void Add(Entity l, float2 range)
            {
                if (budget <= 0f || !em.HasComponent<Curve>(l)) return;
                float length = math.max(1f, em.GetComponentData<Curve>(l).m_Length);
                if (range.y < range.x) { budget -= length * (range.x - range.y); return; } // driven against the curve: no gap there
                if (em.HasComponent<EdgeLane>(l)) result.Add((l, range.x, math.min(range.y, range.x + budget / length)));
                budget -= length * (range.y - range.x);
            }
            if (em.HasComponent<Curve>(lane.m_Lane))
            {
                float back = VehicleLength(e) / math.max(1f, em.GetComponentData<Curve>(lane.m_Lane).m_Length);
                bool fwd = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
                Add(lane.m_Lane, fwd ? new float2(math.max(0f, lane.m_CurvePosition.x - back), 1f) : new float2(lane.m_CurvePosition.x, 0f));
            }
            if (em.HasBuffer<CarNavigationLane>(e))
                foreach (var n in em.GetBuffer<CarNavigationLane>(e, true)) if (n.m_Lane != lane.m_Lane) Add(n.m_Lane, n.m_CurvePosition);
            if (em.HasBuffer<PathElement>(e) && em.HasComponent<PathOwner>(e))
            {
                var path = em.GetBuffer<PathElement>(e, true);
                for (int k = em.GetComponentData<PathOwner>(e).m_ElementIndex; k < path.Length && budget > 0f; k++)
                    if (!NavContains(e, path[k].m_Target)) Add(path[k].m_Target, path[k].m_TargetDelta);
            }
            return result;
        }

        // ---- Passing through: the vehicle stays on its real lane, cars on its way stop and are hidden from it ----

        /// <summary>
        /// Civilian cars on the lanes vanilla checks for this vehicle (current, change and navigation lanes), from level
        /// with it to <paramref name="ahead"/> metres in front, stop and leave those lane lists, so it does not brake for
        /// them. Once passed they are left alone; vanilla lists them again and ReleaseHeld lets them drive on.
        /// </summary>
        private void PassThrough(Entity e, CarCurrentLane lane, float ahead)
        {
            var em = EntityManager;
            float from = -(VehicleLength(e) * 0.5f + 4f); // a car beside the vehicle is not passed yet
            var lanes = new List<Entity> { lane.m_Lane, lane.m_ChangeLane };
            if (em.HasBuffer<CarNavigationLane>(e))
            {
                var nav = em.GetBuffer<CarNavigationLane>(e, true);
                for (int k = 0; k < nav.Length && k < 8; k++) lanes.Add(nav[k].m_Lane);
            }
            foreach (var l in lanes)
            {
                ClearLane(e, l, from, ahead);
                // Junction lanes: vanilla also waits for cars on lanes crossing or merging into this one.
                if (l == Entity.Null || !em.HasBuffer<LaneOverlap>(l)) continue;
                using var overlaps = em.GetBuffer<LaneOverlap>(l, true).ToNativeArray(Allocator.Temp);
                foreach (var ov in overlaps) ClearLane(e, ov.m_Other, from, ahead);
            }
        }

        /// <summary>
        /// Cars on <paramref name="l"/> between <paramref name="from"/> and <paramref name="to"/> metres along the
        /// vehicle leave the lane lists. A civilian (any car without lights, also ambulances on a normal transport)
        /// stops too; an emergency vehicle is passed only while it stands (on scene, or stuck itself).
        /// </summary>
        private void ClearLane(Entity e, Entity l, float from, float to)
        {
            var em = EntityManager;
            if (l == Entity.Null || !em.HasBuffer<LaneObject>(l)) return;
            using var objects = em.GetBuffer<LaneObject>(l, true).ToNativeArray(Allocator.Temp); // RemoveFromLanes edits the buffer
            foreach (var o in objects)
            {
                var car = Tractor(o.m_LaneObject);
                if (car == e || !em.HasComponent<Car>(car) || !em.HasComponent<CarCurrentLane>(car) || em.HasComponent<Deleted>(car)) continue;
                bool civilian = IsCivilian(car);
                if (!civilian && Speed(car) > 0.5f) continue; // a moving emergency vehicle: follow it
                float along = Along(e, car);
                if (along < from || along > to) continue;
                if (civilian) Hold(car, e);
                RemoveFromLanes(car);
                // Also where vanilla listed it: an entry on another lane than the car's own still blocks.
                NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(l), o.m_LaneObject);
            }
        }

        /// <summary>Lane change onto <paramref name="target"/>, the stretch next to the vehicle there cleared first.</summary>
        private void StepTo(Entity e, EvState st, ref CarCurrentLane lane, Entity target, string why)
        {
            ClearLane(e, target, -(VehicleLength(e) + 3f), 15f);
            StartChange(e, st, ref lane, target, why);
        }
    }
}
