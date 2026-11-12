using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace RealisticEMS
{
    /// <summary>
    /// Lane geometry and the low-level mechanics every manoeuvre uses. All lateral values are in metres in the
    /// "traffic frame": positive = towards the kerb side (right in right-hand traffic, left in left-hand traffic).
    /// m_Side converts to world right/left, so every caller is written once for right-hand traffic.
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        private float m_Side = 1f; // 1 = right-hand traffic, -1 = left-hand traffic
        private int m_Frame;

        /// <summary>Pedestrian street lane: vanilla marks its car lanes Forbidden, only service vehicles may enter.</summary>
        // ponytail: Forbidden is the only marker; if another road type ever uses it, it is treated as pedestrian too.
        public static bool IsPedestrianStreet(EntityManager em, Entity lane) =>
            em.HasComponent<CarLane>(lane) && (em.GetComponentData<CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0;

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

        private float VehicleLength(Entity v)
        {
            var em = EntityManager;
            var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(v).m_Prefab;
            float length = em.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab) ? em.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab).m_Size.z : 4.5f;
            return HasTrailer(v) ? length + 4f : length; // camper, caravan, semi: count the trailer too
        }

        private bool HasTrailer(Entity v) =>
            EntityManager.HasBuffer<LayoutElement>(v) && EntityManager.GetBuffer<LayoutElement>(v, true).Length > 1;

        private float Speed(Entity v) =>
            EntityManager.HasComponent<Moving>(v) ? math.length(EntityManager.GetComponentData<Moving>(v).m_Velocity) : 0f;

        private float Braking(Entity car)
        {
            var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(car).m_Prefab;
            return EntityManager.HasComponent<Game.Prefabs.CarData>(prefab) ? math.max(1f, EntityManager.GetComponentData<Game.Prefabs.CarData>(prefab).m_Braking) : 5f;
        }

        /// <summary>Metres from the vehicle to <paramref name="other"/> along the vehicle's heading.</summary>
        private float Along(Entity v, Entity other)
        {
            var tr = EntityManager.GetComponentData<Transform>(v);
            return math.dot(EntityManager.GetComponentData<Transform>(other).m_Position - tr.m_Position, math.forward(tr.m_Rotation));
        }

        /// <summary>
        /// A normal road user that makes room: a car on a lane without emergency lights. Ambulances on a normal
        /// transport or returning count too; they used to be excluded and then blocked the gap for everyone behind.
        /// </summary>
        private bool IsCivilian(Entity car)
        {
            var em = EntityManager;
            return em.HasComponent<Car>(car) && em.HasComponent<CarCurrentLane>(car) && !em.HasComponent<Deleted>(car)
                && (em.GetComponentData<Car>(car).m_Flags & CarFlags.Emergency) == 0;
        }

        /// <summary>A trailer is listed on lanes by itself; the vehicle pulling it is the one that steers.</summary>
        private Entity Tractor(Entity v) =>
            !EntityManager.HasComponent<Car>(v) && EntityManager.HasComponent<Controller>(v) ? EntityManager.GetComponentData<Controller>(v).m_Controller : v;

        /// <summary>Metres a car can move from its lane centre to the lane edge without leaving it.</summary>
        private float EdgeShift(Entity car)
        {
            var cl = EntityManager.GetComponentData<CarCurrentLane>(car);
            return math.max(0f, LaneWidth(cl.m_Lane) * 0.5f - VehicleWidth(car) * 0.5f - 0.05f);
        }

        /// <summary>Metres left on the current lane in driving direction.</summary>
        private float RemainingDistance(CarCurrentLane lane)
        {
            if (!EntityManager.HasComponent<Curve>(lane.m_Lane)) return 0f;
            float len = EntityManager.GetComponentData<Curve>(lane.m_Lane).m_Length;
            bool forward = lane.m_CurvePosition.z >= lane.m_CurvePosition.x;
            return len * (forward ? 1f - lane.m_CurvePosition.x : lane.m_CurvePosition.x);
        }

        private float LaneRemaining(Entity lane, float pos) =>
            EntityManager.HasComponent<Curve>(lane) ? EntityManager.GetComponentData<Curve>(lane).m_Length * (1f - pos) : 0f;

        private void Freeze(Entity car)
        {
            if (!EntityManager.HasComponent<CarNavigation>(car)) return;
            var nav = EntityManager.GetComponentData<CarNavigation>(car);
            nav.m_MaxSpeed = 0f; // CarMoveSystem brakes to a stop
            EntityManager.SetComponentData(car, nav);
        }

        private void CapSpeed(Entity car, float max)
        {
            if (!EntityManager.HasComponent<CarNavigation>(car)) return;
            var nav = EntityManager.GetComponentData<CarNavigation>(car);
            if (nav.m_MaxSpeed <= max) return;
            nav.m_MaxSpeed = max;
            EntityManager.SetComponentData(car, nav);
        }

        /// <summary>
        /// Takes a vehicle and its trailers out of the lane lists, so an emergency vehicle no longer brakes for it.
        /// Vanilla re-lists it only when it moves, so <paramref name="track"/> puts it back once nobody hides it any
        /// more (RestoreHidden, Corridor.cs); the obstacle pass restores its vehicle itself.
        /// </summary>
        private void RemoveFromLanes(Entity car, bool track = true)
        {
            var em = EntityManager;
            if (track) m_Hidden[car] = m_Frame;
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

        // Lane position targets eased per vehicle; vanilla pulls a driving car's value back towards [-0.5, 0.5] on each
        // navigation update, so easing from the stored value would never get there.
        private readonly Dictionary<Entity, (float value, int frame)> m_Eased = new Dictionary<Entity, (float, int)>();

        private const float LateralStep = 0.035f; // metres per frame (~2 m/s) a driving vehicle's lateral target moves

        // Vanilla moves each car only every 16th frame (UpdateFrame groups), so a value up to EaseFresh frames old still counts.
        private const int EaseFresh = 32;

        /// <summary>Moves the stored target <paramref name="step"/> towards <paramref name="wanted"/> (0.02 per frame, ~0.3 per vanilla update).</summary>
        private float Ease(Entity v, float current, float wanted, float step = 0.02f)
        {
            float value = m_Eased.TryGetValue(v, out var x) && m_Frame - x.frame <= EaseFresh ? x.value : current;
            value += math.clamp(wanted - value, -step, step);
            m_Eased[v] = (value, m_Frame);
            return value;
        }

        private readonly Dictionary<Entity, (float step, int frame)> m_Slide = new Dictionary<Entity, (float, int)>(); // last slide step per vehicle

        private void CleanupEased()
        {
            if ((m_Frame & 63) != 0) return;
            m_Keys.Clear();
            foreach (var kv in m_Eased) if (m_Frame - kv.Value.frame > EaseFresh) m_Keys.Add(kv.Key);
            foreach (var v in m_Keys) m_Eased.Remove(v);
            m_Keys.Clear();
            foreach (var kv in m_Slide) if (m_Frame - kv.Value.frame > 2) m_Keys.Add(kv.Key);
            foreach (var v in m_Keys) m_Slide.Remove(v);
        }

        /// <summary>
        /// Target offset from the lane centre in traffic-frame metres; returns the current offset (same frame).
        /// <paramref name="slide"/> moves a standing car there (transform and its interpolation frames, so it glides
        /// instead of jumping): a standing car cannot steer. A driving vehicle only gets the eased target and steers.
        /// </summary>
        private float SetLateral(Entity v, ref CarCurrentLane cl, float metres, bool slide)
        {
            var em = EntityManager;
            if (!em.HasComponent<Curve>(cl.m_Lane)) return 0f;
            metres *= m_Side;
            var bez = em.GetComponentData<Curve>(cl.m_Lane).m_Bezier;
            var tr = em.GetComponentData<Transform>(v);
            // Measure from the lane point beside the vehicle, not at its curve position: on a curve that one can lie
            // ahead, and the along-road error would read as sideways offset and push the vehicle off the road.
            MathUtils.Distance(bez, tr.m_Position, out float t);
            float3 centre = MathUtils.Position(bez, t);
            float2 tangent = math.normalizesafe(MathUtils.Tangent(bez, t).xz);
            float sign = math.dot(math.forward(tr.m_Rotation).xz, tangent) >= 0f ? 1f : -1f; // lane offsets are relative to the curve
            float2 right = MathUtils.Right(tangent) * sign;
            float room = math.max(0.1f, LaneWidth(cl.m_Lane) - VehicleWidth(v));
            float wanted = sign * metres / room;
            float lateral = math.dot(tr.m_Position.xz - centre.xz, right);
            if (!slide)
            {
                cl.m_LanePosition = Ease(v, cl.m_LanePosition, wanted, LateralStep / room); // same sideways speed for every width
                return lateral * m_Side;
            }
            // Glide: speeds up over ~0.5 s to at most ~2 m/s sideways (60 fps) and slows down as it gets there.
            float prev = m_Slide.TryGetValue(v, out var p) && m_Frame - p.frame <= 2 ? p.step : 0f;
            float cap = math.min(0.03f, math.abs(prev) + 0.001f);
            float step = math.clamp((metres - lateral) * 0.05f, -cap, cap);
            m_Slide[v] = (step, m_Frame);
            // Remembered for Ease: when the car drives off it keeps this target instead of swerving back.
            cl.m_LanePosition = wanted;
            m_Eased[v] = (wanted, m_Frame);
            if (math.abs(step) > 0.001f)
            {
                float3 shift = new float3(right.x, 0f, right.y) * step;
                Shift(v, shift);
            }
            return (lateral + step) * m_Side;
        }

        /// <summary>Moves a vehicle and its trailers sideways together (a trailer left behind would twist).</summary>
        private void Shift(Entity v, float3 shift)
        {
            ShiftOne(v, shift);
            if (!HasTrailer(v)) return;
            using var layout = EntityManager.GetBuffer<LayoutElement>(v, true).ToNativeArray(Allocator.Temp);
            foreach (var le in layout) if (le.m_Vehicle != v) ShiftOne(le.m_Vehicle, shift);
        }

        /// <summary>Moves one vehicle sideways, with its TransformFrame history (moving objects are drawn from it, else it snaps).</summary>
        private void ShiftOne(Entity v, float3 shift)
        {
            var em = EntityManager;
            if (!em.HasComponent<Transform>(v)) return;
            var tr = em.GetComponentData<Transform>(v);
            tr.m_Position += shift;
            em.SetComponentData(v, tr);
            if (!em.HasBuffer<TransformFrame>(v)) return;
            var frames = em.GetBuffer<TransformFrame>(v);
            for (int i = 0; i < frames.Length; i++) { var f = frames[i]; f.m_Position += shift; frames[i] = f; }
        }

        /// <summary>Same as SetLateral, writing the lane back to the car.</summary>
        private float Steer(Entity car, float metres, bool slide)
        {
            var cl = EntityManager.GetComponentData<CarCurrentLane>(car);
            float lateral = SetLateral(car, ref cl, metres, slide);
            EntityManager.SetComponentData(car, cl);
            return lateral;
        }

        /// <summary>
        /// All car lanes of the same road segment and carriageway that run in the direction of <paramref name="lane"/>
        /// at curve position <paramref name="t"/> (also turn lanes, which vanilla keeps in a separate SlaveLane group),
        /// with the closest curve position on each and the traffic-frame offset from <paramref name="lane"/>.
        /// Sorted from the centre-line side (index 0 = leftmost in right-hand traffic) to the kerb side.
        /// </summary>
        private List<(Entity lane, float pos, float offset)> Carriageway(Entity lane, float t)
        {
            var em = EntityManager;
            var result = new List<(Entity, float, float)>();
            if (!em.HasComponent<Owner>(lane) || !em.HasComponent<CarLane>(lane) || !em.HasComponent<Curve>(lane)) return result;
            var owner = em.GetComponentData<Owner>(lane).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return result;
            ushort carriageway = em.GetComponentData<CarLane>(lane).m_CarriagewayGroup;
            var myCurve = em.GetComponentData<Curve>(lane).m_Bezier;
            float3 here = MathUtils.Position(myCurve, t);
            float3 dir = math.normalizesafe(MathUtils.Tangent(myCurve, t));
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), dir)) * m_Side;

            foreach (var sl in em.GetBuffer<SubLane>(owner, true))
            {
                var c = sl.m_SubLane;
                if (!em.HasComponent<CarLane>(c) || !em.HasComponent<Curve>(c) || em.GetComponentData<CarLane>(c).m_CarriagewayGroup != carriageway) continue;
                if (c == lane) { result.Add((c, t, 0f)); continue; }
                // A master lane is the invisible lane of a lane group that paths use, lying between its real lanes:
                // counted as a lane it put an extra lane into every multi-lane carriageway.
                if (IsPedestrianStreet(em, c) || em.HasComponent<MasterLane>(c)) continue;
                var bez = em.GetComponentData<Curve>(c).m_Bezier;
                float dist = MathUtils.Distance(bez, here, out float ct);
                if (dist > 12f || ct <= 0f || ct >= 1f) continue; // too far away, or the lane does not exist here
                if (math.dot(math.normalizesafe(MathUtils.Tangent(bez, ct)), dir) < 0.8f) continue; // other direction
                result.Add((c, ct, math.dot(MathUtils.Position(bez, ct) - here, right)));
            }
            result.Sort((a, b) => a.Item3.CompareTo(b.Item3));
            return result;
        }

        private const float ShoulderMargin = 0.3f; // a yielding car keeps this far from the road edge / median
        private const float ShoulderMax = 3f;      // sanity cap for odd compositions

        /// <summary>
        /// Highway shoulder next to <paramref name="lane"/> in metres: on the kerb side up to the road edge; on the
        /// centre side up to the road edge (one-way road) or half the median to the oncoming lanes (two-way road).
        /// 0 for other roads: there the kerb side is parking, bike lane or sidewalk.
        /// </summary>
        // ponytail: assumes the composition is centred on the edge curve (ignores m_MiddleOffset).
        private float ShoulderRoom(Entity lane, bool centre)
        {
            var em = EntityManager;
            if (!IsHighway(lane) || !em.HasComponent<Curve>(lane)) return 0f;
            var edge = em.GetComponentData<Owner>(lane).m_Owner;
            if (!em.HasComponent<Composition>(edge) || !em.HasComponent<Curve>(edge) || !em.HasBuffer<SubLane>(edge)) return 0f;
            var composition = em.GetComponentData<Composition>(edge).m_Edge;
            if (!em.HasComponent<Game.Prefabs.NetCompositionData>(composition)) return 0f;
            float half = em.GetComponentData<Game.Prefabs.NetCompositionData>(composition).m_Width * 0.5f;
            var bez = em.GetComponentData<Curve>(lane).m_Bezier;
            float3 p = MathUtils.Position(bez, 0.5f);
            float3 dir = math.normalizesafe(MathUtils.Tangent(bez, 0.5f));
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), dir)) * m_Side;
            var edgeBez = em.GetComponentData<Curve>(edge).m_Bezier;
            MathUtils.Distance(edgeBez, p, out float et);
            float3 mid = MathUtils.Position(edgeBez, et);
            float own = math.dot(p - mid, right), w = LaneWidth(lane) * 0.5f;
            float room;
            if (!centre) room = half - (own + w);
            else
            {
                float limit = -half; // one-way road: the road edge
                foreach (var sl in em.GetBuffer<SubLane>(edge, true))
                {
                    var c = sl.m_SubLane;
                    if (c == lane || !em.HasComponent<CarLane>(c) || !em.HasComponent<Curve>(c)) continue;
                    var cb = em.GetComponentData<Curve>(c).m_Bezier;
                    MathUtils.Distance(cb, p, out float ct);
                    if (math.dot(math.normalizesafe(MathUtils.Tangent(cb, ct)), dir) > -0.8f) continue; // oncoming lanes only
                    float edgeOfOncoming = math.dot(MathUtils.Position(cb, ct) - mid, right) + LaneWidth(c) * 0.5f;
                    if (edgeOfOncoming < own) limit = math.max(limit, (edgeOfOncoming + own - w) * 0.5f); // share the median
                }
                room = own - w - limit;
            }
            return math.clamp(room - ShoulderMargin, 0f, ShoulderMax);
        }

        /// <summary>Carriageway at the vehicle's own position; empty when it drives its lane against the curve direction.</summary>
        private List<(Entity lane, float pos, float offset)> ParallelLanes(CarCurrentLane lane) =>
            lane.m_CurvePosition.z < lane.m_CurvePosition.x ? new List<(Entity, float, float)>() : Carriageway(lane.m_Lane, lane.m_CurvePosition.x);

        /// <summary>The oncoming car lane right next to us on the centre-line side, on the same road segment; Null if none.</summary>
        private Entity OncomingLane(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasComponent<Owner>(lane.m_Lane)) return Entity.Null;
            var owner = em.GetComponentData<Owner>(lane.m_Lane).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return Entity.Null;
            var tr = em.GetComponentData<Transform>(e);
            float3 heading = math.forward(tr.m_Rotation);
            float3 right = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), heading)) * m_Side;
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

        /// <summary>No other vehicle on the lane between <paramref name="behind"/> and <paramref name="ahead"/> metres from pos.</summary>
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

        // Behind: own length plus a gap, so we never cut in right in front of a truck cab next to our tail.
        /// <summary>
        /// Civilian cars waiting behind an emergency vehicle that stands in a car lane on scene change to a free parallel
        /// lane and repath from there, instead of waiting until vanilla turns them around.
        /// </summary>
        private void PassOnRoadObstacles()
        {
            var em = EntityManager;
            foreach (var v in new List<Entity>(m_ObstaclePass.Keys))
                if (!SidewalkParkingSystem.OnRoad.Contains(v)) EndObstaclePass(v, "Einsatzfahrzeug fährt weiter");
            foreach (var v in SidewalkParkingSystem.OnRoad)
            {
                if (!em.Exists(v) || !em.HasComponent<CarCurrentLane>(v)) continue;
                if (m_ObstaclePass.ContainsKey(v)) { ContinueObstaclePass(v); continue; }
                var road = em.GetComponentData<CarCurrentLane>(v).m_Lane;
                if (!em.HasBuffer<LaneObject>(road) || !em.HasComponent<Curve>(road)) continue;
                float laneLength = math.max(1f, em.GetComponentData<Curve>(road).m_Length);
                float vt = -1f;
                foreach (var o in em.GetBuffer<LaneObject>(road, true)) if (o.m_LaneObject == v) vt = o.m_CurvePosition.x;
                if (vt < 0f) continue;
                Entity first = Entity.Null; float firstBehind = float.MaxValue; // nearest standing vehicle behind v
                using var objects = em.GetBuffer<LaneObject>(road, true).ToNativeArray(Allocator.Temp);
                foreach (var o in objects)
                {
                    var car = Tractor(o.m_LaneObject);
                    if (car == v || !em.HasComponent<Car>(car) || Speed(car) > 2f || !em.HasComponent<CarCurrentLane>(car)) continue;
                    var cl = em.GetComponentData<CarCurrentLane>(car);
                    if (cl.m_Lane != road || cl.m_ChangeLane != Entity.Null) continue;
                    // ponytail: "behind" = 0-30 m before v in the lane's driving direction.
                    float behind = (vt - o.m_CurvePosition.y) * laneLength * (cl.m_CurvePosition.z >= cl.m_CurvePosition.x ? 1f : -1f);
                    if (behind < 0f || behind > 30f) continue;
                    if (behind < firstBehind) { firstBehind = behind; first = car; }
                    if (!IsCivilian(car)) continue; // lane changes below are for normal traffic only
                    var parallel = ParallelLanes(cl);
                    int me = parallel.FindIndex(p => p.lane == road);
                    if (me < 0) continue;
                    foreach (int k in new[] { me - 1, me + 1 })
                    {
                        if (k < 0 || k >= parallel.Count || !IsFree(parallel[k].lane, parallel[k].pos, car, 20f)) continue;
                        cl.m_ChangeLane = parallel[k].lane;
                        cl.m_ChangeProgress = 0f;
                        cl.m_LaneFlags |= Game.Vehicles.CarLaneFlags.IgnoreBlocker;
                        em.SetComponentData(car, cl);
                        if (em.HasComponent<Game.Pathfind.PathOwner>(car))
                        {
                            var po = em.GetComponentData<Game.Pathfind.PathOwner>(car);
                            po.m_State |= Game.Pathfind.PathFlags.Obsolete; // repath from the new lane
                            em.SetComponentData(car, po);
                        }
                        Mod.log.Info($"Auto {car.Index} wechselt die Spur, um an Einsatzfahrzeug {v.Index} vorbeizufahren");
                        break;
                    }
                }
                if (first != Entity.Null && Speed(first) < 1f) TryStartObstaclePass(v, first);
            }
        }

        // ---- One lane per direction: pass a standing emergency vehicle on the oncoming lane ----

        private const int ObstaclePassMaxFrames = 900; // ~15 s, then the pass is given up
        private const float EmergencyLookout = 150f;   // emergency vehicles this close on either lane have priority

        private class ObstaclePass { public Entity Car; public OncomingPass Lanes; public bool Removed; public int Frames; }
        private readonly Dictionary<Entity, ObstaclePass> m_ObstaclePass = new Dictionary<Entity, ObstaclePass>(); // standing vehicle -> car passing it

        /// <summary>
        /// One lane per direction, a vehicle stands in it on scene: the first car behind it (any car, also an emergency
        /// vehicle) passes on the oncoming lane - only if that lane is completely free and no emergency vehicle with
        /// lights is on either lane. Otherwise it waits behind. One car at a time.
        /// </summary>
        private void TryStartObstaclePass(Entity v, Entity car)
        {
            var em = EntityManager;
            var cl = em.GetComponentData<CarCurrentLane>(car);
            if (ParallelLanes(cl).Count > 1 || !em.HasComponent<EdgeLane>(cl.m_Lane)) return; // multi-lane: lane change above
            var oncoming = OncomingLane(car, cl);
            if (oncoming == Entity.Null || LaneWidth(oncoming) < 2f) return;
            // Room to get back in after v before the lane ends.
            var vl = em.GetComponentData<CarCurrentLane>(v);
            if (RemainingDistance(vl) < VehicleLength(v) * 0.5f + VehicleLength(car) + 3f) return;
            if (EmergencyApproaching(car, v, cl.m_Lane, oncoming)) return;
            var lanes = new OncomingPass { Own = cl.m_Lane, Oncoming = oncoming, Entries = OncomingEntries(oncoming) };
            if (!OncomingClear(car, lanes)) return; // oncoming traffic: wait behind
            m_ObstaclePass[v] = new ObstaclePass { Car = car, Lanes = lanes };
            Mod.log.Info($"Fahrzeug {car.Index} überholt stehendes Einsatzfahrzeug {v.Index} auf der Gegenfahrbahn");
        }

        /// <summary>An emergency vehicle with lights on our lane (behind us) or on the oncoming lane, within EmergencyLookout.</summary>
        private bool EmergencyApproaching(Entity car, Entity standing, Entity own, Entity oncoming)
        {
            var em = EntityManager;
            foreach (var lane in new[] { own, oncoming })
            {
                if (!em.HasBuffer<LaneObject>(lane)) continue;
                foreach (var o in em.GetBuffer<LaneObject>(lane, true))
                {
                    var other = Tractor(o.m_LaneObject);
                    if (other == car || other == standing || !em.HasComponent<Car>(other) || !em.HasComponent<Transform>(other)) continue;
                    if ((em.GetComponentData<Car>(other).m_Flags & CarFlags.Emergency) == 0) continue;
                    float along = Along(car, other);
                    if (lane == own ? along < 0f && along > -EmergencyLookout : math.abs(along) < EmergencyLookout) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Swing out next to the standing vehicle, then take it out of the lane list (the passing car no longer brakes
        /// for it) while the cars behind wait; once passed, put it back. Same mechanics as OncomingOvertake.
        /// </summary>
        private void ContinueObstaclePass(Entity v)
        {
            var em = EntityManager;
            var p = m_ObstaclePass[v];
            var car = p.Car;
            if (++p.Frames > ObstaclePassMaxFrames || !em.Exists(car) || em.HasComponent<Deleted>(car) || !em.HasComponent<CarCurrentLane>(car))
            {
                EndObstaclePass(v, "abgebrochen");
                return;
            }
            var cl = em.GetComponentData<CarCurrentLane>(car);
            float clear = VehicleLength(v) * 0.5f + VehicleLength(car) * 0.5f + 2f;
            if (Along(car, v) < -clear || cl.m_Lane != p.Lanes.Own)
            {
                EndObstaclePass(v, "vorbei"); // passed (or already on the next lane): vanilla steers it back into its lane
                return;
            }
            CloseOncoming(p.Lanes);   // cars about to turn into the oncoming lane wait
            YieldOncoming(car, cl);   // and late ones on it stop at their kerb
            float target = -(LaneWidth(p.Lanes.Own) + LaneWidth(p.Lanes.Oncoming)) * 0.5f;
            float lateral = SetLateral(car, ref cl, target, slide: Speed(car) < 0.5f && !p.Removed);
            em.SetComponentData(car, cl);
            if (!p.Removed && math.abs(lateral) >= math.abs(target) * 0.5f)
            {
                RemoveFromLanes(v, track: false); // halfway out: drive past it (EndObstaclePass puts it back)
                p.Removed = true;
            }
            if (!p.Removed || !em.HasBuffer<LaneObject>(p.Lanes.Own)) return;
            // Everyone else behind waits: with v out of the list they would drive through it.
            using var objects = em.GetBuffer<LaneObject>(p.Lanes.Own, true).ToNativeArray(Allocator.Temp);
            foreach (var o in objects)
            {
                var other = Tractor(o.m_LaneObject);
                if (other != car && other != v && em.HasComponent<Car>(other) && em.HasComponent<Transform>(other) && Along(v, other) < 0f) Freeze(other);
            }
        }

        private void EndObstaclePass(Entity v, string why)
        {
            var em = EntityManager;
            var p = m_ObstaclePass[v];
            m_ObstaclePass.Remove(v);
            if (p.Removed && em.Exists(v) && !em.HasComponent<Deleted>(v) && !em.HasComponent<Moving>(v) && em.HasComponent<CarCurrentLane>(v))
            {
                var lane = em.GetComponentData<CarCurrentLane>(v);
                if (em.HasBuffer<LaneObject>(lane.m_Lane))
                {
                    var buffer = em.GetBuffer<LaneObject>(lane.m_Lane);
                    NetUtils.RemoveLaneObject(buffer, v); // no duplicates
                    NetUtils.AddLaneObject(buffer, v, lane.m_CurvePosition.xy); // obstacle again for the next car
                }
            }
            Mod.log.Info($"Überholen an Einsatzfahrzeug {v.Index}: {why}");
        }

        private bool IsFree(Entity lane, float pos, Entity self, float ahead) => IsFreeWindow(lane, pos, self, -(VehicleLength(self) + 3f), ahead);

        private bool IsNodeLane(Entity lane) =>
            EntityManager.HasComponent<Owner>(lane) && EntityManager.HasComponent<Node>(EntityManager.GetComponentData<Owner>(lane).m_Owner);

        /// <summary>First node (junction) lane in the vehicle's navigation list.</summary>
        private Entity NextNodeLane(Entity e)
        {
            if (!EntityManager.HasBuffer<CarNavigationLane>(e)) return Entity.Null;
            foreach (var n in EntityManager.GetBuffer<CarNavigationLane>(e, true)) if (IsNodeLane(n.m_Lane)) return n.m_Lane;
            return Entity.Null;
        }

        private bool NavContains(Entity e, Entity laneEntity)
        {
            if (!EntityManager.HasBuffer<CarNavigationLane>(e)) return false;
            foreach (var n in EntityManager.GetBuffer<CarNavigationLane>(e, true)) if (n.m_Lane == laneEntity) return true;
            return false;
        }

        /// <summary>A real junction lane that starts at the end of <paramref name="from"/> and ends where <paramref name="nodeLane"/> ends.</summary>
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
    }
}
