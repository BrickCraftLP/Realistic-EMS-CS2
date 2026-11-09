using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using PathElement = Game.Pathfind.PathElement;
using PathFlags = Game.Pathfind.PathFlags;
using PathOwner = Game.Pathfind.PathOwner;
using SubLane = Game.Net.SubLane;
using PedestrianLane = Game.Net.PedestrianLane;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// During on-scene care, ambulances and NEFs leave the lane free. All vehicles of one call location form a group:
    /// the first on scene parks at a corner of a nearby intersection (parking lane, wide sidewalk or grass strip) if the
    /// whole group fits on that road, else next to itself (parking lane, wide sidewalk, or any sidewalk when the road has
    /// no free parking). A free parking spot always wins over the pavement. The others line up behind it. Other cars only see an obstacle through the lane's LaneObject
    /// buffer, so the vehicle is taken out of it; when care ends it is put back on the lane (and into the buffer) before
    /// the patient boards.
    /// ponytail: not saved; a game saved while parked loads the vehicle offset and it steers back into the lane on departure.
    /// </summary>
    public partial class SidewalkParkingSystem : GameSystemBase
    {
        private readonly Dictionary<Entity, Transform> m_Parked = new Dictionary<Entity, Transform>(); // vehicle -> lane transform
        private readonly Dictionary<Entity, (float3 spot, float length)> m_Spots = new Dictionary<Entity, (float3, float)>(); // vehicle -> sidewalk spot
        private UnitNamingSystem m_Units;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Parked.Clear(); m_Spots.Clear(); m_Anchors.Clear(); m_Leader.Clear(); m_ParkLane.Clear(); m_Joined.Clear();
        }

        private readonly Dictionary<Entity, Entity> m_Leader = new Dictionary<Entity, Entity>(); // call location -> first vehicle on scene

        protected override void OnUpdate()
        {
            var em = EntityManager;
            bool parking = Mod.Setting.ParkOnSidewalk;
            // Everyone standing by at a call, grouped by call location: held ambulances, the NEFs, held police and fire engines.
            var groups = new Dictionary<Entity, List<Entity>>();
            var members = new HashSet<Entity>();
            void Add(Entity v, Entity location)
            {
                if (location == Entity.Null || !em.Exists(v) || !members.Add(v)) return;
                if (!groups.TryGetValue(location, out var g)) groups[location] = g = new List<Entity>();
                g.Add(v);
            }
            Entity CallOf(Entity v) => em.Exists(v) && em.HasComponent<Ambulance>(v) ? em.GetComponentData<Ambulance>(v).m_TargetLocation : Entity.Null;
            foreach (var rtw in CrewHoldSystem.Held) Add(rtw, CallOf(rtw));
            foreach (var nef in NefDispatchSystem.NefByPatient.Values) if (nef != Entity.Null) Add(nef, CallOf(nef));
            foreach (var kv in ResponderDispatchSystem.HeldAt) Add(kv.Key, kv.Value);
            m_Groups = groups;
            foreach (var kv in groups) foreach (var v in kv.Value) ArriveIfQueued(v, kv.Key);

            foreach (var e in new List<Entity>(m_Parked.Keys))
                if (!parking || !members.Contains(e) || !em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Moving>(e)) Restore(e);
            foreach (var location in new List<Entity>(m_Leader.Keys))
                if (!groups.TryGetValue(location, out var g) || !g.Contains(m_Leader[location])) m_Leader.Remove(location);

            var followers = new HashSet<Entity>();
            foreach (var kv in groups)
            {
                var group = kv.Value;
                if (!m_Leader.TryGetValue(kv.Key, out var leader))
                {
                    leader = group.Find(Ready);
                    if (leader == Entity.Null) continue;
                    m_Leader[kv.Key] = leader;
                }
                // A corner is only taken if the whole group fits behind the leader on that road.
                if (parking && !m_Parked.ContainsKey(leader) && Ready(leader) && !TryParkAtCorner(leader, group.Sum(Length), kv.Key)) Park(leader);

                float queued = 0f; // metres of followers already lined up behind the leader
                foreach (var f in group)
                {
                    if (f == leader) continue;
                    followers.Add(f);
                    bool settled = parking ? m_Parked.ContainsKey(f) : m_Joined.Contains(f);
                    if (!settled && Ready(f) && (parking ? m_Parked.ContainsKey(leader) : Ready(leader)))
                    {
                        // Vanilla routes each vehicle to its own end of the address: line up behind the leader on the road.
                        JoinGroup(f, leader, (Length(leader) + Length(f)) * 0.5f + queued);
                        if (parking && !(m_Anchors.TryGetValue(leader, out var anchor) && ParkBehind(f, leader, anchor))) Park(f);
                        settled = true;
                    }
                    if (settled) queued += Length(f);
                }
            }
            m_Joined.IntersectWith(followers);
        }

        private readonly HashSet<Entity> m_Joined = new HashSet<Entity>(); // followers already lined up behind their leader
        private Dictionary<Entity, List<Entity>> m_Groups = new Dictionary<Entity, List<Entity>>(); // last update's groups

        /// <summary>
        /// Vanilla gives an address only two path ends, so a third vehicle drives up behind whoever stands at its end and
        /// never "arrives" (no EndReached: no care, no hold, no queueing). If it has stopped right behind a vehicle of the
        /// same call, its path ends where it stands. Vanilla then stops it as arrived and the group lines it up next update.
        /// Also called by ResponderDispatchSystem for its cars on the way.
        /// </summary>
        public void ArriveIfQueued(Entity v, Entity location)
        {
            var em = EntityManager;
            if (location == Entity.Null || !m_Groups.TryGetValue(location, out var group) || em.HasComponent<Stopped>(v)) return;
            if (!em.HasComponent<CarCurrentLane>(v) || !em.HasComponent<PathOwner>(v) || !em.HasComponent<Transform>(location)) return;
            var lane = em.GetComponentData<CarCurrentLane>(v);
            var po = em.GetComponentData<PathOwner>(v);
            if (VehicleUtils.PathEndReached(lane) || (po.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated)) != 0) return;
            if (em.HasComponent<Moving>(v) && math.length(em.GetComponentData<Moving>(v).m_Velocity) > 0.5f) return;
            var pos = em.GetComponentData<Transform>(v).m_Position;
            if (math.distance(pos.xz, em.GetComponentData<Transform>(location).m_Position.xz) > 150f) return; // like OnSceneCareSystem.OnScene
            // ponytail: "behind" = within one vehicle length + 6 m of a stopped group vehicle's road position, direction not checked.
            bool queued = false;
            foreach (var o in group)
            {
                if (o == v || !em.Exists(o) || !em.HasComponent<Stopped>(o)) continue;
                var road = m_Parked.TryGetValue(o, out var t) ? t.m_Position : em.GetComponentData<Transform>(o).m_Position;
                if (math.distance(road.xz, pos.xz) < Length(v) + 6f) { queued = true; break; }
            }
            if (!queued) return;

            lane.m_LaneFlags = (lane.m_LaneFlags | CarLaneFlags.EndOfPath | CarLaneFlags.EndReached) & ~(CarLaneFlags.ParkingSpace | CarLaneFlags.Waypoint);
            lane.m_CurvePosition = new float3(lane.m_CurvePosition.x); // stop here
            em.SetComponentData(v, lane);
            if (em.HasBuffer<CarNavigationLane>(v)) em.GetBuffer<CarNavigationLane>(v).Clear();
            if (em.HasBuffer<PathElement>(v)) { po.m_ElementIndex = em.GetBuffer<PathElement>(v).Length; em.SetComponentData(v, po); }
            Mod.log.Info($"{m_Units.CallsignOf(v)} steht hinter der Gruppe, gilt als angekommen");
        }

        /// <summary>
        /// Moves a stopped follower onto the leader's lane, <paramref name="gap"/> metres behind the leader's road position
        /// (also when the leader itself is parked aside). The lane is taken over too, so the follower departs from there.
        /// </summary>
        private void JoinGroup(Entity follower, Entity leader, float gap)
        {
            var em = EntityManager;
            if (!m_Joined.Add(follower)) return;
            var road = m_Parked.TryGetValue(leader, out var parkedFrom) ? parkedFrom : em.GetComponentData<Transform>(leader);
            var queuePos = road.m_Position - math.forward(road.m_Rotation) * gap;
            if (math.distance(em.GetComponentData<Transform>(follower).m_Position.xz, queuePos.xz) < 4f) return; // already in place
            var rl = em.GetComponentData<CarCurrentLane>(leader);
            if (!em.HasComponent<Curve>(rl.m_Lane) || !em.HasBuffer<LaneObject>(rl.m_Lane)) return;

            float laneLength = math.max(1f, em.GetComponentData<Curve>(rl.m_Lane).m_Length);
            bool forward = rl.m_CurvePosition.z >= rl.m_CurvePosition.x;
            float t = math.saturate(rl.m_CurvePosition.x + (forward ? -gap : gap) / laneLength);

            var fl = em.GetComponentData<CarCurrentLane>(follower);
            if (em.HasBuffer<LaneObject>(fl.m_Lane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(fl.m_Lane), follower);
            if (em.HasBuffer<LaneObject>(fl.m_ChangeLane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(fl.m_ChangeLane), follower);
            fl.m_Lane = rl.m_Lane;
            fl.m_ChangeLane = Entity.Null;
            fl.m_CurvePosition = new float3(t); // end-of-path flags stay: still "arrived"
            em.SetComponentData(follower, fl);
            if (em.HasBuffer<CarNavigationLane>(follower)) em.GetBuffer<CarNavigationLane>(follower).Clear(); // lanes of the old approach
            NetUtils.AddLaneObject(em.GetBuffer<LaneObject>(rl.m_Lane), follower, new float2(t));

            em.SetComponentData(follower, new Transform(queuePos, road.m_Rotation));
            if (!em.HasComponent<Updated>(follower)) em.AddComponent<Updated>(follower);
            Mod.log.Info($"{m_Units.CallsignOf(follower)} stellt sich hinter {m_Units.CallsignOf(leader)}");
        }

        private readonly Dictionary<Entity, CornerSpot> m_Anchors = new Dictionary<Entity, CornerSpot>(); // parked vehicle -> where it stands

        /// <summary>For ResponderDispatchSystem: it releases a held car only once the car is back on its lane.</summary>
        public bool IsParked(Entity e) => m_Parked.ContainsKey(e);

        private bool TryParkAtCorner(Entity e, float groupLength, Entity location)
        {
            if (!Mod.Setting.ParkAtCrossing || OnPedestrianStreet(e)) return false;
            var node = NearbyIntersection(e);
            if (node == Entity.Null) return false;
            var em = EntityManager;
            float3 from = em.HasComponent<Transform>(location) ? em.GetComponentData<Transform>(location).m_Position : em.GetComponentData<Transform>(e).m_Position;
            if (!TryCorner(node, e, Length(e), groupLength, from, out var spot)) return false;
            if (spot.ParkingLane == Entity.Null && TryParkingLaneNear(e, out _)) return false; // a parking spot beats the pavement: Park() takes it
            ParkAtCorner(e, node, spot);
            return true;
        }

        private bool OnPedestrianStreet(Entity e) => EmergencyLaneSystem.IsPedestrianStreet(EntityManager, EntityManager.GetComponentData<CarCurrentLane>(e).m_Lane);

        private bool Ready(Entity e) => EntityManager.HasComponent<Stopped>(e) && (ResponderDispatchSystem.HeldAt.ContainsKey(e) || OnSceneCareSystem.OnScene(EntityManager, e));

        /// <summary>
        /// Moves the vehicle off the lane next to itself: into a parking lane, else onto a wide sidewalk, else - the road
        /// has no free parking - onto the normal sidewalk. Stays on the road if none of these is free.
        /// </summary>
        private void Park(Entity e)
        {
            var em = EntityManager;
            var lane = em.GetComponentData<CarCurrentLane>(e);
            var t = em.GetComponentData<Transform>(e);
            if (EmergencyLaneSystem.IsPedestrianStreet(em, lane.m_Lane))
            {
                Mod.log.Info($"{m_Units.CallsignOf(e)}: Fußgängerzone, bleibt stehen");
                m_Parked[e] = t; m_Spots[e] = (float3.zero, 0f); // whole street is walkway: stop where it is
                return;
            }
            if (TryParkingLaneNear(e, out var slot)) { ParkAtCorner(e, Entity.Null, slot); return; }
            if (TryFindSidewalk(lane.m_Lane, t.m_Position, true, out var sidewalk, out var st, out var toRoad) && ParkOnSidewalk(e, sidewalk, st, toRoad, t.m_Rotation, "auf breitem Gehsteig")) return;
            if (TryFindSidewalk(lane.m_Lane, t.m_Position, false, out sidewalk, out st, out toRoad) && ParkOnSidewalk(e, sidewalk, st, toRoad, t.m_Rotation, "auf dem Gehsteig (keine Parkmöglichkeit)")) return;
            Mod.log.Info($"{m_Units.CallsignOf(e)}: kein freier Platz neben der Fahrbahn, bleibt auf der Fahrbahn");
            m_Parked[e] = t; m_Spots[e] = (float3.zero, 0f); // decided: stays on the road
        }

        private bool ParkOnSidewalk(Entity e, Bezier4x3 sidewalk, float st, float3 toRoad, quaternion rotation, string where)
        {
            float length = Length(e);
            if (!TryFreeSpot(sidewalk, st, toRoad, length, out float3 spot, out float spotT)) return false;
            Place(e, spot, rotation, length);
            m_Anchors[e] = new CornerSpot { Pos = spot, Rot = rotation, Curve = sidewalk, LaneT = spotT, ToRoad = toRoad, Forward = math.forward(rotation) };
            Mod.log.Info($"{m_Units.CallsignOf(e)} parkt {where}");
            return true;
        }

        /// <summary>Parks a follower in the same kind of spot as its leader (parking lane or sidewalk), queued behind it.</summary>
        private bool ParkBehind(Entity e, Entity leader, CornerSpot anchor)
        {
            float length = Length(e), laneLength = math.max(1f, MathUtils.Length(anchor.Curve));
            float back = math.dot(anchor.Forward, MathUtils.Tangent(anchor.Curve, anchor.LaneT)) > 0f ? -1f : 1f; // against the leader's heading
            float behind = (Length(leader) + length) * 0.5f;
            for (int k = 0; k < 6; k++) // the first free place in the queue
            {
                float ct = anchor.LaneT + back * (behind + k * length) / laneLength;
                if (ct < 0.02f || ct > 0.98f) break;
                if (anchor.ParkingLane != Entity.Null)
                {
                    if (!TryParkingSlot(anchor.ParkingLane, anchor.Curve, ct, -anchor.ToRoad, anchor.Forward, e, out var slot)) continue;
                    ParkAtCorner(e, Entity.Null, slot, $" hinter {m_Units.CallsignOf(leader)}");
                    return true;
                }
                float3 pos = MathUtils.Position(anchor.Curve, ct) + anchor.ToRoad * 0.5f;
                if (!SpotFree(pos, length)) continue;
                Place(e, pos, anchor.Rot, length);
                m_Anchors[e] = new CornerSpot { Pos = pos, Rot = anchor.Rot, Curve = anchor.Curve, LaneT = ct, ToRoad = anchor.ToRoad, Forward = anchor.Forward };
                Mod.log.Info($"{m_Units.CallsignOf(e)} parkt auf Gehsteig hinter {m_Units.CallsignOf(leader)}");
                return true;
            }
            return false;
        }

        private void Restore(Entity e)
        {
            var em = EntityManager;
            var t = m_Parked[e];
            bool moved = m_Spots.TryGetValue(e, out var s) && s.length > 0f;
            m_Parked.Remove(e); m_Spots.Remove(e); m_Anchors.Remove(e);
            if (m_ParkLane.TryGetValue(e, out var parkLane))
            {
                m_ParkLane.Remove(e);
                if (em.HasBuffer<LaneObject>(parkLane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(parkLane), e);
            }
            if (!moved) return; // stayed on the road
            if (!em.Exists(e) || em.HasComponent<Deleted>(e)) return;
            // Already driving: movement systems own the transform and re-add the lane object themselves.
            if (em.HasComponent<Moving>(e)) return;
            em.SetComponentData(e, t);
            if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
            var lane = em.GetComponentData<CarCurrentLane>(e);
            if (em.HasBuffer<LaneObject>(lane.m_Lane))
            {
                var buffer = em.GetBuffer<LaneObject>(lane.m_Lane);
                NetUtils.RemoveLaneObject(buffer, e); // no duplicates
                NetUtils.AddLaneObject(buffer, e, lane.m_CurvePosition.xy);
            }
            Mod.log.Info($"{m_Units.CallsignOf(e)} fährt vom Gehsteig zurück auf die Fahrbahn");
        }

        /// <summary>Sidewalk next to the vehicle; with <paramref name="wideOnly"/> only on a side with WideSidewalk and without parking spaces.</summary>
        private bool TryFindSidewalk(Entity carLane, float3 pos, bool wideOnly, out Bezier4x3 sidewalk, out float sidewalkT, out float3 toRoad)
        {
            var em = EntityManager;
            sidewalk = default; sidewalkT = 0f; toRoad = float3.zero;
            if (!em.HasComponent<Owner>(carLane)) return false;
            var edge = em.GetComponentData<Owner>(carLane).m_Owner;
            if (!em.HasComponent<Composition>(edge) || !em.HasComponent<Curve>(edge) || !em.HasBuffer<SubLane>(edge)) return false;

            float best = float.MaxValue; float3 nearest = default;
            foreach (var sub in em.GetBuffer<SubLane>(edge))
            {
                if (!em.HasComponent<PedestrianLane>(sub.m_SubLane) || !em.HasComponent<Curve>(sub.m_SubLane)) continue;
                var bez = em.GetComponentData<Curve>(sub.m_SubLane).m_Bezier;
                float d = MathUtils.Distance(bez, pos, out float t);
                if (d < best) { best = d; nearest = MathUtils.Position(bez, t); sidewalk = bez; sidewalkT = t; }
            }
            if (best > 12f) return false; // ponytail: no sidewalk near the vehicle (median lane etc.)

            var edgeBez = em.GetComponentData<Curve>(edge).m_Bezier;
            MathUtils.Distance(edgeBez, pos, out float et);
            float3 dir = MathUtils.Tangent(edgeBez, et);
            float3 off = nearest - MathUtils.Position(edgeBez, et);
            bool right = dir.x * off.z - dir.z * off.x < 0f; // Unity is left-handed: looking along +z, +x is right
            var comp = em.GetComponentData<Composition>(edge).m_Edge;
            if (!em.HasComponent<NetCompositionData>(comp)) return false;
            var side = right ? em.GetComponentData<NetCompositionData>(comp).m_Flags.m_Right : em.GetComponentData<NetCompositionData>(comp).m_Flags.m_Left;
            if (wideOnly && ((side & CompositionFlags.Side.WideSidewalk) == 0 || (side & CompositionFlags.Side.ParkingSpaces) != 0)) return false;

            toRoad = pos - nearest; toRoad.y = 0;
            toRoad = math.lengthsq(toRoad) > 0.01f ? math.normalize(toRoad) : float3.zero;
            return true;
        }

        /// <summary>
        /// Spot on the sidewalk next to the vehicle (its height, slightly towards the road) that no other parked
        /// vehicle occupies. Tries right next to it first, then one vehicle length ahead/behind, then two.
        /// </summary>
        private bool TryFreeSpot(Bezier4x3 sidewalk, float t, float3 toRoad, float length, out float3 spot, out float spotT)
        {
            float laneLength = math.max(MathUtils.Length(sidewalk), 1f);
            spotT = t;
            foreach (int step in new[] { 0, 1, -1, 2, -2 })
            {
                float ct = t + step * length / laneLength;
                spotT = ct;
                if (ct < 0.02f || ct > 0.98f) continue; // stay on this sidewalk, off the crossing
                spot = MathUtils.Position(sidewalk, ct) + toRoad * 0.5f;
                if (SpotFree(spot, length)) return true;
            }
            spot = default;
            return false;
        }

        // ---- Crossing corners: a group's leader parks at a corner of the nearest intersection ----

        private readonly Dictionary<Entity, Entity> m_ParkLane = new Dictionary<Entity, Entity>(); // vehicle -> parking lane it occupies

        /// <summary>
        /// Where a vehicle parks. Curve/LaneT is the lane it stands on (parking lane or sidewalk), Forward the direction
        /// along it the vehicle faces and ToRoad points from there to the carriageway; followers queue along Curve.
        /// </summary>
        private struct CornerSpot { public float3 Pos, ToRoad, Forward; public quaternion Rot; public Entity ParkingLane; public Bezier4x3 Curve; public float LaneT; public bool Angled; }

        /// <summary>Intersection (3+ roads) within 60 m whose road the vehicle stands on.</summary>
        private Entity NearbyIntersection(Entity e)
        {
            var em = EntityManager;
            var lane = em.GetComponentData<CarCurrentLane>(e).m_Lane;
            if (!em.HasComponent<Owner>(lane)) return Entity.Null;
            var edge = em.GetComponentData<Owner>(lane).m_Owner;
            if (!em.HasComponent<Edge>(edge)) return Entity.Null;
            var pos = em.GetComponentData<Transform>(e).m_Position;
            var ed = em.GetComponentData<Edge>(edge);
            Entity best = Entity.Null; float bestDist = 60f;
            foreach (var node in new[] { ed.m_Start, ed.m_End })
            {
                if (!em.HasComponent<Node>(node) || !em.HasBuffer<ConnectedEdge>(node) || em.GetBuffer<ConnectedEdge>(node).Length < 3) continue;
                float d = math.distance(em.GetComponentData<Node>(node).m_Position.xz, pos.xz);
                if (d < bestDist) { bestDist = d; best = node; }
            }
            return best;
        }

        /// <summary>
        /// Free corner of the intersection closest to <paramref name="from"/>: a parking lane slot, a wide sidewalk or a
        /// grass strip on one side of a connected road, just past the crosswalk. Only roads long enough for the whole
        /// group (<paramref name="groupLength"/> metres queued behind the corner) count.
        /// </summary>
        private bool TryCorner(Entity node, Entity vehicle, float length, float groupLength, float3 from, out CornerSpot best)
        {
            var em = EntityManager;
            best = default;
            float bestScore = float.MaxValue;
            foreach (var ce in em.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Unity.Collections.Allocator.Temp))
            {
                var edge = ce.m_Edge;
                if (!em.HasComponent<Edge>(edge) || !em.HasComponent<Curve>(edge) || !em.HasComponent<Composition>(edge) || !em.HasBuffer<SubLane>(edge)) continue;
                var comp = em.GetComponentData<Composition>(edge).m_Edge;
                if (!em.HasComponent<NetCompositionData>(comp)) continue;
                var flags = em.GetComponentData<NetCompositionData>(comp).m_Flags;
                var edgeCurve = em.GetComponentData<Curve>(edge);
                bool atStart = em.GetComponentData<Edge>(edge).m_Start == node;
                // ponytail: 8 m clears a normal crosswalk; very wide junctions may need more.
                float fromNode = 8f + length * 0.5f;
                // Too short a road, or the group would reach the crosswalk at the far end.
                if (fromNode > edgeCurve.m_Length * 0.45f || 16f + groupLength > edgeCurve.m_Length) continue;
                float et = atStart ? fromNode / edgeCurve.m_Length : 1f - fromNode / edgeCurve.m_Length;
                float3 edgePos = MathUtils.Position(edgeCurve.m_Bezier, et);
                float3 tangent = math.normalizesafe(MathUtils.Tangent(edgeCurve.m_Bezier, et) * new float3(1, 0, 1));
                quaternion rot = quaternion.LookRotationSafe(atStart ? -tangent : tangent, math.up()); // facing the junction

                foreach (bool right in new[] { true, false })
                {
                    var side = right ? flags.m_Right : flags.m_Left;
                    bool parking = (side & CompositionFlags.Side.ParkingSpaces) != 0;
                    bool open = (side & (CompositionFlags.Side.WideSidewalk | CompositionFlags.Side.PrimaryBeautification)) != 0;
                    if (!parking && !open) continue;
                    // Lane of that kind on that side, nearest to the corner point.
                    Entity laneEntity = Entity.Null; float laneT = 0f, laneDist = 15f; Bezier4x3 laneBez = default;
                    foreach (var sub in em.GetBuffer<SubLane>(edge, true))
                    {
                        var l = sub.m_SubLane;
                        if (!em.HasComponent<Curve>(l) || !(parking ? em.HasComponent<Game.Net.ParkingLane>(l) : em.HasComponent<PedestrianLane>(l))) continue;
                        var bez = em.GetComponentData<Curve>(l).m_Bezier;
                        float d = MathUtils.Distance(bez, edgePos, out float t);
                        float3 off = MathUtils.Position(bez, t) - edgePos;
                        if ((tangent.x * off.z - tangent.z * off.x < 0f) != right) continue; // other side of the road
                        if (d < laneDist) { laneDist = d; laneEntity = l; laneT = t; laneBez = bez; }
                    }
                    if (laneEntity == Entity.Null) continue;
                    float3 pos = MathUtils.Position(laneBez, laneT);
                    CornerSpot candidate;
                    if (parking)
                    {
                        float3 outward = pos - edgePos; outward.y = 0;
                        if (!TryParkingSlot(laneEntity, laneBez, laneT, math.normalizesafe(outward), math.forward(rot), vehicle, out candidate)) continue;
                    }
                    else
                    {
                        float3 toRoad = edgePos - pos; toRoad.y = 0;
                        toRoad = math.normalizesafe(toRoad);
                        pos += toRoad * 0.5f;
                        if (!SpotFree(pos, length)) continue;
                        candidate = new CornerSpot { Pos = pos, Rot = rot, Curve = laneBez, LaneT = laneT, ToRoad = toRoad, Forward = math.forward(rot) };
                    }
                    float score = math.distance(candidate.Pos, from) + (parking ? 0f : 1000f); // parking spots before pavement
                    if (score < bestScore) { bestScore = score; best = candidate; }
                }
            }
            return bestScore < float.MaxValue;
        }

        /// <summary>Free metres of the parking lane before/after t; parked cars are counted ~5 m long.</summary>
        private (float before, float after) ParkingGap(Entity parkingLane, float t, float laneLength)
        {
            var em = EntityManager;
            float before = t * laneLength, after = (1f - t) * laneLength;
            if (!em.HasBuffer<LaneObject>(parkingLane)) return (0f, 0f);
            foreach (var o in em.GetBuffer<LaneObject>(parkingLane, true))
            {
                float d = ((o.m_CurvePosition.x + o.m_CurvePosition.y) * 0.5f - t) * laneLength;
                if (d >= 0f) after = math.min(after, d - 2.5f);
                else before = math.min(before, -d - 2.5f);
            }
            return (math.max(0f, before), math.max(0f, after));
        }

        /// <summary>
        /// Spot in a parking lane near t: parallel if the gap is long enough, else nose first at an angle, front on the
        /// pavement and the rear inside the parking lane, so nothing sticks out into traffic.
        /// <paramref name="forward"/> is the parking direction along the lane, <paramref name="outward"/> points to the pavement.
        /// </summary>
        private bool TryParkingSlot(Entity parkingLane, Bezier4x3 bez, float t, float3 outward, float3 forward, Entity e, out CornerSpot spot)
        {
            spot = default;
            float laneLength = math.max(1f, MathUtils.Length(bez));
            float length = Length(e) - 1.5f, width = VehicleWidth(e);
            var em = EntityManager;
            var lanePrefab = em.GetComponentData<PrefabRef>(parkingLane).m_Prefab;
            float half = (em.HasComponent<NetLaneData>(lanePrefab) ? em.GetComponentData<NetLaneData>(lanePrefab).m_Width : 2.5f) * 0.5f;
            // Outer edge of the pavement: half the road's full width (sidewalks included) from its centre line.
            Entity edge = em.HasComponent<Owner>(parkingLane) ? em.GetComponentData<Owner>(parkingLane).m_Owner : Entity.Null;
            bool hasEdge = em.HasComponent<Curve>(edge) && em.HasComponent<Composition>(edge)
                && em.HasComponent<NetCompositionData>(em.GetComponentData<Composition>(edge).m_Edge);
            float roadHalf = hasEdge ? em.GetComponentData<NetCompositionData>(em.GetComponentData<Composition>(edge).m_Edge).m_Width * 0.5f : 0f;
            foreach (float shift in new[] { 0f, 3f, -3f, 6f, -6f })
            {
                float ct = t + shift / laneLength;
                if (ct < 0.02f || ct > 0.98f) continue;
                var (before, after) = ParkingGap(parkingLane, ct, laneLength);
                float gap = before + after;
                float centreT = math.clamp(ct + (after - before) * 0.5f / laneLength, 0.02f, 0.98f); // middle of the gap
                float3 p = MathUtils.Position(bez, centreT);
                if (gap >= length + 1f && SpotFree(p, length + 1.5f))
                {
                    spot = new CornerSpot { Pos = p, Rot = quaternion.LookRotationSafe(forward, math.up()), ParkingLane = parkingLane, Curve = bez, LaneT = centreT, ToRoad = -outward, Forward = forward };
                    return true;
                }
                // Pavement depth from the parking lane centre outward (0.3 m off the lot line).
                float room = hasEdge ? roadHalf - MathUtils.Distance(em.GetComponentData<Curve>(edge).m_Bezier, p, out _) - 0.3f : float.MaxValue;
                foreach (float degrees in new[] { 40f, 55f, 70f, 90f })
                {
                    float a = math.radians(degrees);
                    if (length * math.cos(a) + width * math.sin(a) + 0.5f > gap) continue; // footprint along the lane
                    float3 dir = math.normalize(forward * math.cos(a) + outward * math.sin(a));
                    // Push the nose onto the pavement until the rear corner on the road side stays 0.2 m inside the
                    // parking lane: sticking into the driving lane, it blocks passing traffic.
                    float reach = length * 0.5f + (width * 0.5f * math.cos(a) - half + 0.2f) / math.sin(a);
                    // Front corner on the pavement side must stay on the pavement, not in the lot behind it.
                    if ((reach + length * 0.5f) * math.sin(a) + width * 0.5f * math.cos(a) > room) continue;
                    float3 pos = p - forward * (reach * math.cos(a)) + dir * reach;
                    if (!SpotFree(pos, length + 1.5f)) continue;
                    spot = new CornerSpot { Pos = pos, Rot = quaternion.LookRotationSafe(dir, math.up()), ParkingLane = parkingLane, Curve = bez, LaneT = centreT, ToRoad = -outward, Forward = forward, Angled = true };
                    return true;
                }
            }
            return false;
        }

        /// <summary>Parking lane next to the vehicle's own road (mid-block), within 8 m.</summary>
        private bool TryParkingLaneNear(Entity e, out CornerSpot spot)
        {
            var em = EntityManager;
            spot = default;
            var carLane = em.GetComponentData<CarCurrentLane>(e).m_Lane;
            if (em.HasComponent<Game.Net.ParkingLane>(carLane) || !em.HasComponent<Owner>(carLane)) return false; // vanilla already parked it
            var edge = em.GetComponentData<Owner>(carLane).m_Owner;
            if (!em.HasComponent<Curve>(edge) || !em.HasBuffer<SubLane>(edge)) return false;
            var tr = em.GetComponentData<Transform>(e);
            Entity best = Entity.Null; float bestDist = 8f, bestT = 0f; Bezier4x3 bestBez = default;
            foreach (var sub in em.GetBuffer<SubLane>(edge, true))
            {
                if (!em.HasComponent<Game.Net.ParkingLane>(sub.m_SubLane) || !em.HasComponent<Curve>(sub.m_SubLane)) continue;
                var bez = em.GetComponentData<Curve>(sub.m_SubLane).m_Bezier;
                float d = MathUtils.Distance(bez, tr.m_Position, out float t);
                if (d < bestDist) { bestDist = d; best = sub.m_SubLane; bestT = t; bestBez = bez; }
            }
            if (best == Entity.Null) return false;
            var edgeBez = em.GetComponentData<Curve>(edge).m_Bezier;
            float3 p = MathUtils.Position(bestBez, bestT);
            MathUtils.Distance(edgeBez, p, out float et);
            float3 outward = p - MathUtils.Position(edgeBez, et); outward.y = 0f;
            float3 forward = math.forward(tr.m_Rotation); forward.y = 0f;
            float3 laneDir = MathUtils.Tangent(bestBez, bestT); laneDir.y = 0f;
            laneDir = math.normalizesafe(laneDir) * math.sign(math.dot(laneDir, forward) + 1e-4f); // keep the own heading
            return TryParkingSlot(best, bestBez, bestT, math.normalizesafe(outward), laneDir, e, out spot);
        }

        private float VehicleWidth(Entity e)
        {
            var em = EntityManager;
            var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
            if (!em.HasComponent<ObjectGeometryData>(prefab)) return 2f;
            var b = em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
            return b.max.x - b.min.x;
        }

        private bool SpotFree(float3 pos, float length)
        {
            foreach (var other in m_Spots.Values)
                if (other.length > 0f && math.distance(other.spot.xz, pos.xz) < (length + other.length) * 0.5f) return false;
            return true;
        }

        private void ParkAtCorner(Entity e, Entity node, CornerSpot spot, string behind = "")
        {
            var em = EntityManager;
            Place(e, spot.Pos, spot.Rot, Length(e));
            m_Anchors[e] = spot;
            if (spot.ParkingLane != Entity.Null)
            {
                NetUtils.AddLaneObject(em.GetBuffer<LaneObject>(spot.ParkingLane), e, new float2(spot.LaneT)); // nobody parks on top of us
                m_ParkLane[e] = spot.ParkingLane;
            }
            string where = spot.ParkingLane == Entity.Null ? "auf Gehsteig/Grünstreifen" : spot.Angled ? "schräg mit der Nase auf dem Gehsteig" : "in der Parkspur";
            Mod.log.Info($"{m_Units.CallsignOf(e)} parkt {(node != Entity.Null ? "an der Kreuzung " : "")}{where}{behind}");
        }

        /// <summary>Moves a stopped vehicle off its lane to <paramref name="pos"/>; other cars no longer see it as obstacle.</summary>
        private void Place(Entity e, float3 pos, quaternion rot, float length)
        {
            var em = EntityManager;
            var lane = em.GetComponentData<CarCurrentLane>(e);
            m_Parked[e] = em.GetComponentData<Transform>(e);
            m_Spots[e] = (pos, length);
            em.SetComponentData(e, new Transform(pos, rot));
            if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
            if (em.HasBuffer<LaneObject>(lane.m_Lane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(lane.m_Lane), e);
            if (em.HasBuffer<LaneObject>(lane.m_ChangeLane)) NetUtils.RemoveLaneObject(em.GetBuffer<LaneObject>(lane.m_ChangeLane), e);
        }

        /// <summary>Vehicle length plus a small gap.</summary>
        private float Length(Entity e)
        {
            var em = EntityManager;
            var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
            return (em.HasComponent<ObjectGeometryData>(prefab) ? em.GetComponentData<ObjectGeometryData>(prefab).m_Size.z : 6f) + 1.5f;
        }
    }
}
