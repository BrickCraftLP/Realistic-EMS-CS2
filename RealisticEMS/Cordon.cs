using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Fire cordon car: stands sideways across the lanes leading from a junction into the burning building's street,
    /// a few metres in. Only a picture of a road block: it is taken off every lane list, so no vehicle ever waits for
    /// it and emergency vehicles drive past. Through traffic stays away because the street is closed for routing
    /// (FireAlarmSystem, Forbidden lanes). When released it is put back where it stood on its lane, like
    /// SidewalkParkingSystem.Restore.
    /// </summary>
    public static class Cordon
    {
        private static readonly Dictionary<Entity, Transform> s_Cars = new Dictionary<Entity, Transform>(); // car -> transform on its lane

        public static bool IsCordon(Entity car) => s_Cars.ContainsKey(car);
        public static void Clear() => s_Cars.Clear();

        private const float Inset = 7f; // car centre this far into the street: behind the crosswalk, so pedestrians pass in front
        private const float KerbGap = 0.3f; // its outer end this far inside the kerb, so it never stands on the sidewalk

        /// <summary>
        /// The street's through lanes (no parking, no driveway connections, no master lanes, at least half as long as its
        /// longest lane). Edges also carry short side-connection lanes to every building; counting those put the cordon
        /// on the sidewalk and made a 2-lane street look like 15 lanes. Compared with the longest lane, not the street's
        /// centre line: lanes stop at the intersections, so on short segments between big ones they are much shorter.
        /// </summary>
        public static List<(Entity lane, Curve curve)> MainLanes(EntityManager em, Entity edge)
        {
            var result = new List<(Entity, Curve)>();
            if (!em.HasBuffer<SubLane>(edge) || !em.HasComponent<Curve>(edge)) return result;
            var lanes = em.GetBuffer<SubLane>(edge, true);
            float longest = 0f;
            for (int i = 0; i < lanes.Length; i++)
            {
                var l = lanes[i].m_SubLane;
                if (!em.HasComponent<Game.Net.CarLane>(l) || em.HasComponent<Game.Net.ParkingLane>(l) || em.HasComponent<MasterLane>(l) || !em.HasComponent<Curve>(l)) continue;
                if ((em.GetComponentData<Game.Net.CarLane>(l).m_Flags & Game.Net.CarLaneFlags.SideConnection) != 0) continue;
                var curve = em.GetComponentData<Curve>(l);
                result.Add((l, curve));
                longest = math.max(longest, curve.m_Length);
            }
            result.RemoveAll(x => x.Item2.m_Length < longest * 0.5f);
            return result;
        }

        public static bool Place(EntityManager em, Entity car, Entity edge, Entity node)
        {
            if (!em.HasComponent<Node>(node) || !em.HasComponent<CarCurrentLane>(car)) return false;
            var nodePos = em.GetComponentData<Node>(node).m_Position;
            // Through lanes coming in from this junction: they start at its end of the street.
            float3 dir = default, kerbLane = default;
            float kerbWidth = 3f, kerbDist = -1f;
            int n = 0;
            var main = MainLanes(em, edge);
            float3 centre = default; // middle of the carriageway here: average of all through lanes at the same spot
            foreach (var (l, curve) in main)
            {
                bool inbound = math.distance(curve.m_Bezier.a, nodePos) < math.distance(curve.m_Bezier.d, nodePos);
                float t = math.min(0.4f, Inset / math.max(1f, curve.m_Length));
                centre += MathUtils.Position(curve.m_Bezier, inbound ? t : 1f - t);
                if (!inbound) continue;
                dir += math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, t));
                n++;
            }
            if (n == 0) return false;
            centre /= main.Count;
            foreach (var (l, curve) in main) // the inbound lane at the kerb: furthest from the middle
            {
                if (math.distance(curve.m_Bezier.a, nodePos) >= math.distance(curve.m_Bezier.d, nodePos)) continue;
                var p = MathUtils.Position(curve.m_Bezier, math.min(0.4f, Inset / math.max(1f, curve.m_Length)));
                float d = math.distance(p.xz, centre.xz);
                if (d <= kerbDist) continue;
                kerbDist = d; kerbLane = p;
                var lp = em.GetComponentData<Game.Prefabs.PrefabRef>(l).m_Prefab;
                kerbWidth = em.HasComponent<Game.Prefabs.NetLaneData>(lp) ? em.GetComponentData<Game.Prefabs.NetLaneData>(lp).m_Width : 3f;
            }
            // Sideways across the inbound lanes, its outer end just inside the kerb (a car is longer than a lane is wide).
            var outward = math.normalizesafe(math.cross(math.normalizesafe(dir), math.up()));
            if (math.dot(outward.xz, (kerbLane - centre).xz) < 0f) outward = -outward;
            var cp = em.GetComponentData<Game.Prefabs.PrefabRef>(car).m_Prefab;
            float half = (em.HasComponent<Game.Prefabs.ObjectGeometryData>(cp) ? em.GetComponentData<Game.Prefabs.ObjectGeometryData>(cp).m_Size.z : 4.5f) * 0.5f;
            var pos = kerbLane + outward * (kerbWidth * 0.5f - half - KerbGap);
            var rot = quaternion.LookRotationSafe(outward, math.up());

            var onLane = em.GetComponentData<Transform>(car);
            if (!s_Cars.ContainsKey(car)) s_Cars[car] = onLane;
            em.SetComponentData(car, new Transform(pos, rot));
            if (!em.HasComponent<Updated>(car)) em.AddComponent<Updated>(car);
            KeepOffLanes(em, car);
            Log.Info($"cordon car={car.Index} sideways across lanes={n} street={edge.Index} junction={node.Index} jump={math.distance(onLane.m_Position.xz, pos.xz):0}m");
            return true;
        }

        /// <summary>Back onto its lane where it stood, so it drives off from there.</summary>
        public static void Lift(EntityManager em, Entity car)
        {
            if (!s_Cars.TryGetValue(car, out var onLane)) return;
            s_Cars.Remove(car);
            if (!em.Exists(car) || em.HasComponent<Deleted>(car) || em.HasComponent<Game.Objects.Moving>(car)) return;
            em.SetComponentData(car, onLane);
            if (!em.HasComponent<Updated>(car)) em.AddComponent<Updated>(car);
            var lane = em.GetComponentData<CarCurrentLane>(car);
            if (em.HasBuffer<LaneObject>(lane.m_Lane))
            {
                var buffer = em.GetBuffer<LaneObject>(lane.m_Lane);
                NetUtils.RemoveLaneObject(buffer, car);
                NetUtils.AddLaneObject(buffer, car, lane.m_CurvePosition.xy);
            }
        }

        /// <summary>Every frame: forget deleted cars, keep the others off their lane list (a vanilla system may put them back).</summary>
        public static void Purge(EntityManager em)
        {
            if (s_Cars.Count == 0) return;
            List<Entity> gone = null;
            foreach (var car in s_Cars.Keys)
            {
                if (!em.Exists(car) || em.HasComponent<Deleted>(car)) (gone ??= new List<Entity>()).Add(car);
                else KeepOffLanes(em, car);
            }
            if (gone != null) foreach (var car in gone) s_Cars.Remove(car);
        }

        private static void KeepOffLanes(EntityManager em, Entity car)
        {
            var lane = em.GetComponentData<CarCurrentLane>(car);
            if (em.HasBuffer<LaneObject>(lane.m_Lane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(lane.m_Lane), car);
            if (em.HasBuffer<LaneObject>(lane.m_ChangeLane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(lane.m_ChangeLane), car);
        }
    }
}
