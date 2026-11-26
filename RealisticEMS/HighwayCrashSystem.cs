using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Events;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using CarLaneFlags = Game.Net.CarLaneFlags;
using SubLane = Game.Net.SubLane;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Realistic highway crashes on top of vanilla traffic accidents. A vanilla AccidentSite on a highway segment
    /// gets a crash class (rear-end ... pileup): more cars are pulled into the same accident event (vanilla Impact, so
    /// vanilla spins them, stops them and injures their passengers by severity), lanes are closed with CrashCordon,
    /// and police plus road maintenance trucks with arrow boards come (ResponderDispatchSystem). The scene ends when
    /// vanilla clears the accident site and the class's minimum time is over; then lanes reopen and units go home.
    /// Not yet: ambulances waiting for the engines to free trapped people; units stop on whatever lane they are on.
    /// </summary>
    public partial class HighwayCrashSystem : GameSystemBase
    {
        public enum Kind { RearEnd, MultiCar, Truck, Pileup, HeadOn }

        // Per class: extra cars (min, max), lanes closed (99 = all, -1 = all but one), police, fire engines (rescue),
        // road maint trucks, impact severity (vanilla 5), minimum scene time in game minutes.
        private static readonly (int min, int max, int lanes, int police, int engines, int maint, float severity, int minutes)[] Table =
        {
            (1, 1, 1, 1, 0, 0, 4f, 30),    // RearEnd
            (2, 3, 2, 2, 1, 1, 6f, 45),    // MultiCar
            (1, 2, -1, 2, 2, 1, 8f, 75),   // Truck
            (5, 10, 99, 3, 3, 2, 8f, 120), // Pileup
            (1, 1, 99, 3, 1, 1, 10f, 90),  // HeadOn
        };
        private const int ReopenMinutes = 5; // after clearing, one lane reopens every ReopenMinutes
        private static readonly int[] Weights = { 45, 30, 15, 8, 2 };
        /// <summary>Feature switched off: no new crash scenes; the system only ends running ones and reopens their
        /// lanes (also after loading a save that has crash closures). Set true to turn highway crashes back on.</summary>
        public const bool On = false;

        public class Scene
        {
            public Kind Kind; public Entity Edge, Event, Location; public uint Start, SiteGoneAt, LastReopen; public int Wanted, Involved;
            public float3 At, Dir; // crash point and driving direction
            public readonly List<Entity> Closed = new List<Entity>();
            public bool Dispatched, Restored;
            public uint MinFrames => (uint)(Table[(int)Kind].minutes * OnSceneCareSystem.FramesPerGameMinute);
        }

        /// <summary>Active crashes by accident event.</summary>
        public static readonly Dictionary<Entity, Scene> Scenes = new Dictionary<Entity, Scene>();
        /// <summary>Debug: class for the next highway crash (null = random by weight).</summary>
        public static Kind? NextKind;
        /// <summary>Debug: end every crash now (lanes reopen, units released).</summary>
        public static bool ClearAll;

        /// <summary>Units sent to a crash wait at its first car; true while that crash is on.</summary>
        public static bool IsCrashLocation(Entity car)
        {
            foreach (var s in Scenes.Values) if (s.Location == car) return true;
            return false;
        }

        private SimulationSystem m_Sim;
        private ResponderDispatchSystem m_Responders;
        private EntityQuery m_Sites, m_Cars;
        private EntityArchetype m_ImpactArchetype;
        private bool m_Restore;
        private Random m_Random = new Random(0x5eed1234u);

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Responders = World.GetOrCreateSystemManaged<ResponderDispatchSystem>();
            m_Sites = GetEntityQuery(ComponentType.ReadOnly<AccidentSite>(), ComponentType.ReadOnly<Edge>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Cars = GetEntityQuery(ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Moving>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<CarCurrentLane>(),
                ComponentType.Exclude<InvolvedInAccident>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_ImpactArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Impact>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Scenes.Clear(); CrashCordon.Clear(); m_Restore = true;
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            uint now = m_Sim.frameIndex;
            using var sites = m_Sites.ToEntityArray(Allocator.Temp);
            if (m_Restore) { m_Restore = false; Restore(sites, now); }

            var live = new HashSet<Entity>();
            foreach (var edge in sites)
            {
                var ev = em.GetComponentData<AccidentSite>(edge).m_Event;
                live.Add(ev);
                if (Scenes.ContainsKey(ev) || !On || !IsHighway(edge)) continue;
                var kind = NextKind ?? Roll();
                NextKind = null;
                var t = Table[(int)kind];
                Scenes[ev] = new Scene { Kind = kind, Edge = edge, Event = ev, Start = now, Wanted = m_Random.NextInt(t.min, t.max + 1) };
                Log.Info($"crash start kind={kind} edge={edge.Index} event={ev.Index} extra_cars={Scenes[ev].Wanted}");
            }

            foreach (var ev in new List<Entity>(Scenes.Keys))
            {
                var s = Scenes[ev];
                if (ClearAll || !On) { End(s); continue; }
                if (!live.Contains(ev) && s.SiteGoneAt == 0) s.SiteGoneAt = now;
                if (s.SiteGoneAt != 0 && now - s.Start >= s.MinFrames) { Reopen(s, now); continue; }
                if (s.Restored) continue;
                if (s.Location == Entity.Null && !FindFirstCar(s)) { if (now - s.Start > 600) End(s); continue; }
                if (s.Involved < s.Wanted) PullIn(s); // one per update: the pileup builds up over a few seconds
                if (!s.Dispatched && (s.Involved >= s.Wanted || now - s.Start > 300)) Close(s, now);
            }
            ClearAll = false;
        }

        private Kind Roll()
        {
            int r = m_Random.NextInt(100);
            for (int i = 0; i < Weights.Length; i++) { if (r < Weights[i]) return (Kind)i; r -= Weights[i]; }
            return Kind.RearEnd;
        }

        private bool IsHighway(Entity edge)
        {
            var em = EntityManager;
            return em.TryGetComponent<PrefabRef>(edge, out var p) && em.TryGetComponent<RoadData>(p.m_Prefab, out var rd)
                && (rd.m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
        }

        /// <summary>The car vanilla crashed for this event: the crash point and driving direction come from it.</summary>
        private bool FindFirstCar(Scene s)
        {
            var em = EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<InvolvedInAccident>(), ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Transform>());
            using var cars = q.ToEntityArray(Allocator.Temp);
            foreach (var c in cars)
            {
                if (em.GetComponentData<InvolvedInAccident>(c).m_Event != s.Event) continue;
                var t = em.GetComponentData<Transform>(c);
                s.Location = c; s.At = t.m_Position;
                // Direction from the lane it was on (the car itself is spinning).
                s.Dir = em.TryGetComponent<CarCurrentLane>(c, out var cl) && em.TryGetComponent<Curve>(cl.m_Lane, out var curve)
                    ? math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, cl.m_CurvePosition.x))
                    : math.mul(t.m_Rotation, new float3(0, 0, 1));
                s.Dir.y = 0; s.Dir = math.normalizesafe(s.Dir);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Next car into the crash: the nearest moving car behind the crash point, same direction, within 150 m.
        /// Truck class prefers a truck first. Vanilla Impact: same push as vanilla's own accidents, scaled by class.
        /// </summary>
        private void PullIn(Scene s)
        {
            var em = EntityManager;
            using var cars = m_Cars.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null; float bestD = float.MaxValue;
            for (int pass = 0; pass < 2 && best == Entity.Null; pass++)
                foreach (var c in cars)
                {
                    if (pass == 0 && s.Kind == Kind.Truck && s.Involved == 0 && !em.HasComponent<Game.Vehicles.DeliveryTruck>(c)) continue;
                    var p = em.GetComponentData<Transform>(c).m_Position;
                    var d = p - s.At; d.y = 0;
                    float along = math.dot(d, s.Dir);
                    if (along > 5f || along < -150f || math.lengthsq(d) > 150f * 150f) continue;
                    var v = em.GetComponentData<Moving>(c).m_Velocity; v.y = 0;
                    if (math.lengthsq(v) < 1f || math.dot(math.normalizesafe(v), s.Dir) < 0.7f) continue;
                    if (-along < bestD) { bestD = -along; best = c; }
                }
            if (best == Entity.Null) { s.Wanted = s.Involved; return; } // no traffic left behind it: the crash stays smaller
            var vel = em.GetComponentData<Moving>(best).m_Velocity;
            float sev = Table[(int)s.Kind].severity;
            bool left = m_Random.NextBool();
            var impact = new Impact
            {
                m_Event = s.Event, m_Target = best, m_Severity = sev,
                m_AngularVelocityDelta = new float3(0, left ? -2f : 2f, 0) * m_Random.NextFloat(0.5f, 1.2f),
                // sideways like vanilla, plus braking hard (rear-end into the queue)
                m_VelocityDelta = new float3(0, 0, 0),
            };
            var dir2 = math.normalizesafe(vel.xz);
            impact.m_VelocityDelta.xz = (left ? MathUtils.Left(dir2) : MathUtils.Right(dir2)) * math.min(sev, 5f) * m_Random.NextFloat(0.4f, 1f) - vel.xz * 0.5f;
            using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
            em.CreateEntity(m_ImpactArchetype, created);
            em.SetComponentData(created[0], impact);
            s.Involved++;
            Log.Debug($"crash event={s.Event.Index} car={best.Index} pulled in {s.Involved}/{s.Wanted} behind={bestD:F0}m");
        }

        /// <summary>Lane closure and units, once the cars are in.</summary>
        private void Close(Scene s, uint now)
        {
            s.Dispatched = true;
            var t = Table[(int)s.Kind];
            var lanes = CrashCordon.Lanes(EntityManager, s.Edge, s.At, s.Dir); // same direction, sorted right to left
            int n = t.lanes == 99 ? lanes.Count : t.lanes == -1 ? math.max(1, lanes.Count - 1) : math.min(t.lanes, lanes.Count);
            // Start at the lane the first car was on, grow outward (right first: the shoulder side).
            int at = math.max(0, lanes.FindIndex(l => l.lane == CurrentLane(s.Location)));
            int lo = at, hi = at;
            var pick = new List<Entity>();
            if (lanes.Count > 0) pick.Add(lanes[at].lane);
            while (pick.Count < n && (lo > 0 || hi < lanes.Count - 1))
            {
                if (lo > 0) pick.Add(lanes[--lo].lane);
                if (pick.Count < n && hi < lanes.Count - 1) pick.Add(lanes[++hi].lane);
            }
            CrashCordon.Close(EntityManager, pick, s.Closed);
            // Arrow: towards the open lanes (left of the closed block if lanes right of it are closed, else right).
            // Full closure: right, towards the shoulder / previous exit.
            float3 right = new float3(s.Dir.z, 0, -s.Dir.x);
            bool openLeft = lanes.Count - 1 - hi > lo; // more open lanes left of the block than right of it
            float3 detour = s.At + (openLeft ? -right : right) * 20f;
            var boards = new List<float3>();
            for (int i = 0; i < t.maint; i++) boards.Add(detour);
            uint hold = s.MinFrames * 2;
            m_Responders.DispatchToCrash(s.Location, s.At, s.Dir, t.police, t.engines, boards, hold);
            Log.Info($"crash dispatch kind={s.Kind} event={s.Event.Index} cars={s.Involved + 1} lanes_closed={s.Closed.Count}/{lanes.Count} police={t.police} engines={t.engines} maint={t.maint} arrow={(openLeft ? "left" : "right")}");
        }

        private Entity CurrentLane(Entity car) => EntityManager.TryGetComponent<CarCurrentLane>(car, out var cl) ? cl.m_Lane : Entity.Null;

        /// <summary>Cleared: lanes reopen one by one, the last one closed first (the shoulder-side lane of the block
        /// stays shut longest); units leave once all are open.</summary>
        private void Reopen(Scene s, uint now)
        {
            uint step = (uint)(ReopenMinutes * OnSceneCareSystem.FramesPerGameMinute);
            if (s.LastReopen != 0 && now - s.LastReopen < step) return;
            if (s.Closed.Count == 0) { End(s); return; }
            s.LastReopen = now;
            CrashCordon.OpenLast(EntityManager, s.Closed);
            Log.Info($"crash event={s.Event.Index} lane reopened, still closed={s.Closed.Count}");
        }

        private void End(Scene s)
        {
            CrashCordon.Open(EntityManager, s.Closed);
            Scenes.Remove(s.Event);
            Log.Info($"crash end kind={s.Kind} event={s.Event.Index} minutes={(m_Sim.frameIndex - s.Start) / OnSceneCareSystem.FramesPerGameMinute}");
        }

        /// <summary>After a load: crashes still on keep their closed lanes (no new units, they were sent before the save);
        /// closed highway lanes without an accident site are left over from an earlier session and reopen.</summary>
        private void Restore(NativeArray<Entity> sites, uint now)
        {
            var em = EntityManager;
            var keep = new HashSet<Entity>();
            foreach (var edge in sites)
            {
                if (!IsHighway(edge)) continue;
                keep.Add(edge);
                var ev = em.GetComponentData<AccidentSite>(edge).m_Event;
                var s = new Scene { Kind = Kind.RearEnd, Edge = edge, Event = ev, Start = now, Restored = true, Dispatched = true };
                s.Closed.AddRange(CrashCordon.ClosedLanes(em, edge));
                Scenes[ev] = s;
            }
            int opened = CrashCordon.LiftLeftovers(em, keep, IsHighway);
            Log.Info($"crash: restored scenes={Scenes.Count} lifted_leftover_lanes={opened}");
        }
    }

    /// <summary>
    /// Crash lane closure, separate from the fire Cordon: single lanes of a highway segment get Forbidden (high path
    /// cost, FireAlarmSystem.ClosedCost), so traffic merges into the open lanes and a full closure detours at the
    /// previous exit. Emergency vehicles still get through (cost, not a ban). FireAlarmSystem leaves lanes on
    /// segments with an accident site alone.
    /// </summary>
    public static class CrashCordon
    {
        private static readonly HashSet<Entity> s_Lanes = new HashSet<Entity>();
        public static void Clear() => s_Lanes.Clear();

        /// <summary>The segment's through lanes in the crash direction, sorted right to left.</summary>
        public static List<(Entity lane, float side)> Lanes(EntityManager em, Entity edge, float3 at, float3 dir)
        {
            var result = new List<(Entity, float)>();
            float3 right = new float3(dir.z, 0, -dir.x);
            foreach (var (lane, curve) in Cordon.MainLanes(em, edge))
            {
                var tan = MathUtils.Tangent(curve.m_Bezier, 0.5f); tan.y = 0;
                if (math.dot(math.normalizesafe(tan), dir) < 0.5f) continue;
                var p = MathUtils.Position(curve.m_Bezier, 0.5f) - at;
                result.Add((lane, math.dot(p, right)));
            }
            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return result;
        }

        public static void Close(EntityManager em, List<Entity> lanes, List<Entity> closed)
        {
            foreach (var l in lanes) if (Set(em, l, true)) { closed.Add(l); s_Lanes.Add(l); }
        }

        public static void OpenLast(EntityManager em, List<Entity> lanes)
        {
            var l = lanes[lanes.Count - 1];
            Set(em, l, false); s_Lanes.Remove(l);
            lanes.RemoveAt(lanes.Count - 1);
        }

        public static void Open(EntityManager em, List<Entity> lanes)
        {
            foreach (var l in lanes) { Set(em, l, false); s_Lanes.Remove(l); }
            lanes.Clear();
        }

        public static List<Entity> ClosedLanes(EntityManager em, Entity edge)
        {
            var r = new List<Entity>();
            foreach (var (lane, _) in Cordon.MainLanes(em, edge))
                if ((em.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Forbidden) != 0)
                {
                    r.Add(lane); s_Lanes.Add(lane);
                    if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane); // rebuild with the raised cost
                }
            return r;
        }

        /// <summary>Reopens Forbidden lanes on highway segments not in keep. Returns lanes opened.</summary>
        public static int LiftLeftovers(EntityManager em, HashSet<Entity> keep, System.Func<Entity, bool> highway)
        {
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<CarLane>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            using var lanes = q.ToEntityArray(Allocator.Temp);
            int n = 0;
            foreach (var l in lanes)
            {
                if ((em.GetComponentData<CarLane>(l).m_Flags & CarLaneFlags.Forbidden) == 0) continue;
                var owner = em.GetComponentData<Owner>(l).m_Owner;
                if (!em.HasComponent<Edge>(owner) || keep.Contains(owner) || !highway(owner)) continue;
                if (Set(em, l, false)) n++;
            }
            return n;
        }

        private static bool Set(EntityManager em, Entity lane, bool closed)
        {
            if (!em.Exists(lane) || !em.HasComponent<CarLane>(lane)) return false;
            var c = em.GetComponentData<CarLane>(lane);
            if (((c.m_Flags & CarLaneFlags.Forbidden) != 0) == closed) return false;
            c.m_Flags = closed ? c.m_Flags | CarLaneFlags.Forbidden : c.m_Flags & ~CarLaneFlags.Forbidden;
            em.SetComponentData(lane, c);
            if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
            return true;
        }
    }
}
