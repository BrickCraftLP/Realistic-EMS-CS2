using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Events;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Game.Tools;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using CarLane = Game.Net.CarLane;
using CarLaneFlags = Game.Net.CarLaneFlags;

namespace RealisticEMS
{
    /// <summary>
    /// Alarm levels for burning buildings (vanilla OnFire). Vanilla sends one engine; this adds engines (as vanilla Fire
    /// requests, so they really fight the fire) and police (held on scene while it burns), scaled by building type and
    /// the street it stands on. Still burning after EscalateMinutes: next alarm. Hold ceiling grows with severity.
    /// Cordon: the building's street is closed (car lanes Forbidden: a high path cost, so through traffic and buses detour
    /// while anything with a destination on it, emergency vehicles included, still gets there). Police stand at the
    /// addresses nearest both ends of the street, the rest at the scene. Vanilla only sets Forbidden on node lanes, so
    /// Forbidden on an edge's car lane is always ours; it is saved with the lane, so after a load scenes still burning
    /// are adopted (no new units) and closures without a fire are lifted.
    /// Not yet: diversion boards (arrow board lights: see light_dump in NefPrefabSystem), trams.
    /// </summary>
    public partial class FireAlarmSystem : GameSystemBase
    {
        public enum Level { Small, Medium, Large, Industrial, Public }
        //                                     Small Medium Large Industrial Public
        internal static readonly int[] Engines = { 2, 3, 5, 4, 5 };
        /// <summary>Minimum fire brigade time on scene, from dispatch, in fire-clock hours (what the panel shows):
        /// after the fire is out the engines stay for overhaul and fire watch, the street stays closed.</summary>
        private static readonly int[] MinHours = { 4, 4, 6, 6, 6 };
        // ponytail: fixed escalation timer, setting if players want it.
        private const int EscalateMinutes = 20, MaxAlarm = 3;
        /// <summary>The fire's clock (dispatch time shown, escalation) runs this many times slower than game time.</summary>
        public const float FireClock = 2f;
        /// <summary>Share of the minimum scene time the fire burns once engines work; the rest is overhaul / fire watch.</summary>
        public const float BurnShare = 1f / 3f;
        public static uint FireFrames(uint gameFrames) => (uint)(gameFrames / FireClock);

        public class Scene { public Level Level; public int Alarm; public uint Since, Start; public bool Closed; public Entity Road; public AmpdsCode Code;
            /// <summary>Road blocks: crossing, the closed segment entering it, the address the police car drives to first.</summary>
            public readonly List<(Entity node, Entity edge, Entity post)> Cordons = new List<(Entity, Entity, Entity)>();
            public readonly List<Entity> Roads = new List<Entity>(); // closed segments: from the nearest junction on one side to the other
            public Entity EngineLeader; // first engine on scene: the others line up behind it
            public uint ControlFrom; public float ControlIntensity; // first engine at work: from then the fire dies down evenly until MinFrames
            public bool EnginesReleased; // controlled period over: held engines back to vanilla to put out the rest
            public uint OutAt; // frame the fire went out (0: still burning); the scene stays until MinHours are over
            public readonly HashSet<Entity> Engines = new HashSet<Entity>(); // engines that worked this fire: held after it is out
            public uint MinFrames => (uint)(MinHours[(int)Level] * 60 * OnSceneCareSystem.FramesPerGameMinute * FireClock); // game frames
            public uint BurnEnd => Start + (uint)(MinFrames * BurnShare); // the fire is out by then once engines work
            public bool Over(uint now) => OutAt != 0 && now - Start >= MinFrames;
            public int Pending; // engines still to hand to a station
            public Entity Staging; public uint RescueTry; // where the rescue service standby lines up; last top-up
        }

        /// <summary>
        /// The rescue service codes a building fire like any call (protocol 07 Burns/Explosion): small 07-A-02 fire alarm,
        /// medium/industrial 07-C-01 persons trapped, large/public and every 2nd+ alarm 07-D-01 multiple patients.
        /// </summary>
        public static AmpdsCode CodeFor(Level level, int alarm)
        {
            string code = alarm >= 2 || level == Level.Large || level == Level.Public ? "07-D-01"
                : level == Level.Small ? "07-A-02" : "07-C-01";
            return Ampds.Codes.Find(c => c.ToString() == code);
        }
        /// <summary>Burning building -> its scene. Read by the dispatch UI later.</summary>
        public static readonly Dictionary<Entity, Scene> Scenes = new Dictionary<Entity, Scene>();
        /// <summary>Debug: buildings whose scene escalates on the next update.</summary>
        public static readonly HashSet<Entity> ForceEscalate = new HashSet<Entity>();

        private EntityQuery m_Burning, m_FirePrefabs, m_Addresses, m_CarLanes, m_CarPathfind, m_Traffic;
        private bool m_CostRaised;
        /// <summary>
        /// Vanilla charges a Forbidden lane only 10 s extra (CarPathfind.m_ForbiddenCost 10 s / 100 behaviour), so buses
        /// and cars drove through a closed street whenever the detour was longer. Raised so the detour always wins;
        /// a destination on the closed street is still reachable (cost, not a ban). Vanilla's own Forbidden lanes are
        /// illegal turns at junctions: they only get rarer.
        /// </summary>
        private static readonly float4 ClosedCost = new float4(600f, 2000f, 20f, 50f);
        private bool m_Restore;
        private EntityQuery m_EnginePrefabs, m_Patients, m_Engines, m_EnginesDriving;
        private readonly Dictionary<Entity, int> m_Waiting = new Dictionary<Entity, int>(); // engine standing near its fire without arriving -> frames
        private SidewalkParkingSystem m_Parking;
        private readonly HashSet<Entity> m_Taken = new HashSet<Entity>(); // patients a standby ambulance was reserved for
        private ResponderDispatchSystem m_Responders;
        private SimulationSystem m_Sim;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Responders = World.GetOrCreateSystemManaged<ResponderDispatchSystem>();
            m_Burning = GetEntityQuery(ComponentType.ReadOnly<OnFire>(), ComponentType.ReadOnly<Building>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Addresses = GetEntityQuery(ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_CarLanes = GetEntityQuery(ComponentType.ReadOnly<CarLane>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Patients = GetEntityQuery(ComponentType.ReadOnly<Game.Citizens.HealthProblem>(), ComponentType.ReadOnly<Game.Citizens.Citizen>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_EnginePrefabs = GetEntityQuery(ComponentType.ReadWrite<FireEngineData>(), ComponentType.ReadOnly<PrefabData>());
            m_Engines = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.FireEngine>(), ComponentType.ReadOnly<Target>(), ComponentType.ReadOnly<Game.Objects.Stopped>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Parking = World.GetOrCreateSystemManaged<SidewalkParkingSystem>();
            m_EnginesDriving = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.FireEngine>(), ComponentType.ReadOnly<Target>(), ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<Game.Pathfind.PathOwner>(),
                ComponentType.Exclude<Game.Objects.Stopped>(), ComponentType.Exclude<ParkedCar>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_CarPathfind = GetEntityQuery(ComponentType.ReadWrite<PathfindCarData>());
            m_Traffic = GetEntityQuery(ComponentType.ReadOnly<Car>(), ComponentType.ReadWrite<Game.Pathfind.PathOwner>(), ComponentType.ReadOnly<Game.Pathfind.PathElement>(),
                ComponentType.Exclude<ParkedCar>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_FirePrefabs = GetEntityQuery(ComponentType.ReadOnly<FireData>(), ComponentType.ReadOnly<EventData>(), ComponentType.ReadOnly<PrefabData>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Scenes.Clear(); ForceEscalate.Clear(); Cordon.Clear(); m_Taken.Clear(); m_Restore = true;
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            uint now = m_Sim.frameIndex;
            using var burning = m_Burning.ToEntityArray(Allocator.Temp);
            if (!m_CostRaised && !m_CarPathfind.IsEmptyIgnoreFilter) RaiseClosedCost();
            if (m_Restore) { m_Restore = false; Restore(burning, now); }
            if (!Mod.Setting.EnableFireAlarms) { foreach (var b in new List<Entity>(Scenes.Keys)) End(b); return; }
            var alive = new HashSet<Entity>();
            foreach (var b in burning)
            {
                alive.Add(b);
                if (!Scenes.TryGetValue(b, out var s))
                {
                    var level = Classify(b);
                    s = Scenes[b] = new Scene { Level = level, Alarm = 1, Since = now, Start = now, Road = em.GetComponentData<Building>(b).m_RoadEdge, Code = CodeFor(level, 1) };
                    FindEnds(b, s);
                    s.Staging = FindStaging(b, s.Road);
                    int closed = 0;
                    if (Mod.Setting.CloseStreetAtFire) { foreach (var r in s.Roads) closed += SetClosed(r, true); RerouteThrough(s.Roads); }
                    s.Closed = Mod.Setting.CloseStreetAtFire;
                    Dispatch(b, s, Engines[(int)level] - 1); // vanilla already sends the first engine
                    SendEngines(b, s);
                    Log.Info($"fire_alarm building={b.Index} code={s.Code} level={level} alarm=1 engines={Engines[(int)level]} " +
                        $"closed_segments={s.Roads.Count} closed_lanes={closed} road_blocks={(s.Closed ? s.Cordons.Count : 0)} at_crossings={string.Join(",", s.Cordons.ConvertAll(c => c.node.Index))}");
                    continue;
                }
                // Next alarm only while no engine is at work yet (fire not under control); the debug button always.
                if (s.Alarm < MaxAlarm && (ForceEscalate.Remove(b) || s.ControlFrom == 0 && FireFrames(now - s.Since) > EscalateMinutes * OnSceneCareSystem.FramesPerGameMinute))
                {
                    s.Alarm++; s.Since = now; s.Code = CodeFor(s.Level, s.Alarm);
                    Dispatch(b, s, EscalateEngines);
                    Log.Info($"fire_alarm building={b.Index} code={s.Code} level={s.Level} alarm={s.Alarm} +{EscalateEngines} engines");
                }
            }
            foreach (var b in burning) if (Scenes.TryGetValue(b, out var sc) && sc.Pending > 0) SendEngines(b, sc);
            TakePatients();
            AssistArrival();
            LineUpEngines();
            foreach (var kv in Scenes) // controlled period over: held engines put out the rest themselves
            {
                var s = kv.Value;
                if (s.EnginesReleased || now - s.Start < s.MinFrames) continue;
                s.EnginesReleased = true;
                foreach (var e in s.Engines)
                    if (em.Exists(e) && !em.HasComponent<Deleted>(e) && em.HasComponent<Game.Objects.TripSource>(e)) em.RemoveComponent<Game.Objects.TripSource>(e); // path end kept: it keeps extinguishing
            }
            foreach (var kv in Scenes) if (alive.Contains(kv.Key)) SendRescue(kv.Key, kv.Value);
            foreach (var b in new List<Entity>(Scenes.Keys))
            {
                var s = Scenes[b];
                if (alive.Contains(b)) continue;
                if (!em.Exists(b)) { End(b); continue; }
                if (s.OutAt == 0) { s.OutAt = now; StartFireWatch(b, s); }
                // No overhaul without the fire brigade: burned down before any engine worked it, or nothing left to watch.
                bool noOverhaul = s.ControlFrom == 0 || em.HasComponent<Game.Common.Destroyed>(b) || !AnyEngineLeft(s);
                if (s.Over(now) || noOverhaul) End(b);
            }
            ForceEscalate.IntersectWith(alive);
        }

        private void Dispatch(Entity b, Scene s, int engines)
        {
            s.Pending += engines; // handed to a station by SendEngines
            SendRescue(b, s);
            // Police only for the road blocks: one car per closed street entry, sent with the first alarm.
            if (s.Alarm != 1 || !s.Closed || s.Cordons.Count == 0) return;
            var posts = s.Cordons.ConvertAll(c => (c.post != Entity.Null ? c.post : b, c.edge, c.node));
            m_Responders.DispatchPoliceToFire(b, posts, HoldFrames(s));
        }


        /// <summary>Ambulances and NEFs the scene's AMPDS code asks for and that are not on their way yet.</summary>
        /// <summary>
        /// Keeps the standby at what the AMPDS code asks for: a unit that left with a patient is replaced. Retried at
        /// most every RescueRetry frames when no depot/hospital had a free vehicle.
        /// </summary>
        private void SendRescue(Entity b, Scene s, bool now = false)
        {
            if (s.Code == null) return;
            uint frame = m_Sim.frameIndex;
            if (!now && frame - s.RescueTry < RescueRetry) return;
            int rtw = math.max(0, s.Code.Rtw - m_Responders.StandbyCount(b, false));
            int nef = Mod.Setting.EnableNef ? math.max(0, s.Code.Nef - m_Responders.StandbyCount(b, true)) : 0;
            if (rtw + nef == 0) return;
            s.RescueTry = frame;
            var (r, n) = m_Responders.DispatchStandby(b, s.Staging != Entity.Null ? s.Staging : b, rtw, nef, HoldFrames(s));
            var line = $"fire_rescue building={b.Index} code={s.Code} staging={s.Staging.Index} rtw={r}/{rtw} nef={n}/{nef}";
            if (r + n > 0) Log.Info(line); else Log.Debug(line); // nothing free: retried, keep the log readable
        }

        private const uint RescueRetry = 512;
        // Safety ceiling for held police/standby: the minimum scene time plus two hours and 30 min per further alarm.
        private static uint HoldFrames(Scene s) => s.MinFrames + (uint)((120 + 30 * (s.Alarm - 1)) * OnSceneCareSystem.FramesPerGameMinute * FireClock);

        /// <summary>
        /// Staging address for ambulances/NEFs: about 100 m from the fire (70-160 m), on the same street if possible, so
        /// they line up out of the engines' way. Entity.Null: none, they go to the building itself.
        /// </summary>
        private Entity FindStaging(Entity b, Entity road)
        {
            var em = EntityManager;
            var at = em.GetComponentData<Game.Objects.Transform>(b).m_Position;
            using var all = m_Addresses.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null; float bestScore = float.MaxValue;
            foreach (var o in all)
            {
                if (o == b) continue;
                float d = math.distance(at.xz, em.GetComponentData<Game.Objects.Transform>(o).m_Position.xz);
                if (d < 70f || d > 160f) continue;
                float score = math.abs(d - 100f) + (em.GetComponentData<Building>(o).m_RoadEdge == road ? 0f : 40f);
                if (score < bestScore) { bestScore = score; best = o; }
            }
            return best;
        }

        /// <summary>
        /// Patients inside a burning building whose request no vehicle has yet get a standby ambulance from the scene
        /// instead of a separate vanilla call. Without a free standby one vanilla dispatches as usual.
        /// </summary>
        private void TakePatients()
        {
            var em = EntityManager;
            m_Taken.RemoveWhere(p => !em.Exists(p) || !em.HasComponent<Game.Citizens.HealthProblem>(p));
            if (Scenes.Count == 0) return;
            // A fire patient: inside a burning building, or hurt by its fire event and already outside.
            var byEvent = new Dictionary<Entity, Entity>(); // fire event -> burning building
            foreach (var b in Scenes.Keys)
                if (em.TryGetComponent<OnFire>(b, out var f) && f.m_Event != Entity.Null) byEvent[f.m_Event] = b;
            using var patients = m_Patients.ToEntityArray(Allocator.Temp);
            foreach (var p in patients)
            {
                if (m_Taken.Contains(p)) continue;
                var inside = em.TryGetComponent<Game.Citizens.CurrentBuilding>(p, out var cb) ? cb.m_CurrentBuilding : Entity.Null;
                var scene = Scenes.ContainsKey(inside) ? inside
                    : byEvent.TryGetValue(em.GetComponentData<Game.Citizens.HealthProblem>(p).m_Event, out var fb) ? fb : Entity.Null;
                if (scene == Entity.Null || !m_Responders.CanTakeRequest(p)) continue;
                // Where vanilla would pick the patient up: the building it is in, else its creature on the street.
                var at = inside != Entity.Null ? inside
                    : em.TryGetComponent<Game.Citizens.CurrentTransport>(p, out var ct) ? ct.m_CurrentTransport : scene;
                if (!m_Responders.ReserveStandby(scene, p, false, at))
                {
                    // No standby ambulance free: one more from a depot, straight to this patient.
                    var s = Scenes[scene];
                    m_Responders.DispatchStandby(scene, s.Staging != Entity.Null ? s.Staging : scene, 1, 0, HoldFrames(s));
                    if (!m_Responders.ReserveStandby(scene, p, false, at)) continue; // no free ambulance anywhere: vanilla keeps it
                    Log.Info($"fire_rescue building={scene.Index} extra ambulance for patient={p.Index}");
                }
                m_Taken.Add(p);
            }
        }

        /// <summary>
        /// Vanilla gives an address two path ends; further engines queue behind the first ones and never "arrive", so
        /// they never start extinguishing. An engine standing still near its fire for a moment counts as arrived there
        /// (SidewalkParkingSystem.EndPathHere); vanilla then starts extinguishing and LineUpEngines puts it in the row.
        /// </summary>
        private void AssistArrival()
        {
            var em = EntityManager;
            if (Scenes.Count == 0) { m_Waiting.Clear(); return; }
            using var engines = m_EnginesDriving.ToEntityArray(Allocator.Temp);
            var seen = new HashSet<Entity>();
            foreach (var e in engines)
            {
                var fe = em.GetComponentData<Game.Vehicles.FireEngine>(e);
                if ((fe.m_State & (FireEngineFlags.Extinguishing | FireEngineFlags.Returning)) != 0) continue;
                var b = em.GetComponentData<Target>(e).m_Target;
                if (!Scenes.ContainsKey(b) || em.HasComponent<Game.Objects.TripSource>(e)) continue;
                float speed = em.TryGetComponent<Game.Objects.Moving>(e, out var mv) ? math.length(mv.m_Velocity) : 0f;
                var at = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
                if (speed > 0.5f || math.distance(at.xz, em.GetComponentData<Game.Objects.Transform>(b).m_Position.xz) > ArriveRadius) continue;
                seen.Add(e);
                int waited = (m_Waiting.TryGetValue(e, out var w) ? w : 0) + 64;
                m_Waiting[e] = waited;
                if (waited < ArriveWait) continue;
                m_Parking.EndPathHere(e);
                m_Waiting.Remove(e);
                Log.Info($"fire_engine={e.Index} stuck behind others at fire={b.Index}, counts as arrived");
            }
            foreach (var e in new List<Entity>(m_Waiting.Keys)) if (!seen.Contains(e)) m_Waiting.Remove(e);
        }

        private const float ArriveRadius = 90f;
        private const int ArriveWait = 192; // frames standing still near the fire

        private const int EscalateEngines = 2;
        /// <summary>Sends pending engines (ResponderDispatchSystem.SpawnEngines: one column per station); retried while none is free.</summary>
        private void SendEngines(Entity b, Scene s)
        {
            if (s.Pending <= 0) return;
            s.Pending -= m_Responders.SpawnEngines(b, s.Pending);
        }

        /// <summary>
        /// Fire out: one engine stays for the fire watch (the first of the row), every other engine goes home, the
        /// road-block police leave (ResponderDispatchSystem: scene no longer burning) and the street reopens.
        /// </summary>
        private void StartFireWatch(Entity b, Scene s)
        {
            var em = EntityManager;
            Entity keep = s.Engines.Contains(s.EngineLeader) && em.Exists(s.EngineLeader) ? s.EngineLeader : Entity.Null;
            foreach (var e in s.Engines) if (keep == Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e) && !em.HasComponent<ParkedCar>(e)) keep = e;
            int sent = 0;
            foreach (var e in new List<Entity>(s.Engines))
            {
                if (e == keep) continue;
                s.Engines.Remove(e);
                if (em.Exists(e) && !em.HasComponent<Deleted>(e) && em.HasComponent<Game.Objects.TripSource>(e)) { ResponderDispatchSystem.ReleaseVehicle(em, e); sent++; }
            }
            var stillClosed = new HashSet<Entity>();
            foreach (var kv in Scenes) if (kv.Key != b) stillClosed.UnionWith(kv.Value.Roads);
            int opened = 0;
            foreach (var r in s.Roads) if (!stillClosed.Contains(r)) opened += SetClosed(r, false);
            s.Closed = false;
            Log.Info($"fire_under_control building={b.Index} fire watch engine={keep.Index} until {MinHours[(int)s.Level]} h after dispatch, engines_home={sent} reopened_lanes={opened}");
        }

        private bool AnyEngineLeft(Scene s)
        {
            var em = EntityManager;
            foreach (var e in s.Engines) if (em.Exists(e) && !em.HasComponent<Deleted>(e) && !em.HasComponent<ParkedCar>(e)) return true;
            return false;
        }

        private void End(Entity b)
        {
            var s = Scenes[b];
            Scenes.Remove(b);
            var em = EntityManager;
            foreach (var e in s.Engines) // fire watch over: home (vanilla already set them Returning)
                if (em.Exists(e) && !em.HasComponent<Deleted>(e) && em.HasComponent<Game.Objects.TripSource>(e)) ResponderDispatchSystem.ReleaseVehicle(em, e);
            var stillClosed = new HashSet<Entity>();
            foreach (var o in Scenes.Values) stillClosed.UnionWith(o.Roads);
            int opened = 0;
            foreach (var r in s.Roads) if (!stillClosed.Contains(r)) opened += SetClosed(r, false);
            Log.Info($"fire_out building={b.Index} reopened_lanes={opened}");
        }

        /// <summary>
        /// The closed street runs from the nearest crossing on one side of the fire to the nearest on the other: the
        /// building's segment plus every segment through plain 2-way nodes. A road block at each crossing (dead ends need
        /// none). A building on fire at a crossing (its road access within AtCrossing m of it): that crossing is inside
        /// the closure, every street leading into it is closed too, each blocked at its own next crossing.
        /// Each road block's police car drives first to the address on the blocked segment nearest the crossing.
        /// </summary>
        private void FindEnds(Entity b, Scene s)
        {
            var em = EntityManager;
            s.Roads.Clear(); s.Cordons.Clear();
            if (!em.TryGetComponent<Edge>(s.Road, out var edge)) return;
            s.Roads.Add(s.Road);
            var access = MathUtils.Position(em.GetComponentData<Curve>(s.Road).m_Bezier, em.GetComponentData<Building>(b).m_CurvePosition);
            var blocks = new List<(Entity node, Entity edge)>();
            foreach (var (node, e) in new[] { WalkToCrossing(s.Road, edge.m_Start, s.Roads), WalkToCrossing(s.Road, edge.m_End, s.Roads) })
            {
                if (node == Entity.Null) continue;
                if (math.distance(access.xz, em.GetComponentData<Node>(node).m_Position.xz) > AtCrossing) { blocks.Add((node, e)); continue; }
                // Fire at this crossing: close every street leading into it up to its next crossing.
                var into = new List<Entity>();
                foreach (var ce in em.GetBuffer<ConnectedEdge>(node, true)) into.Add(ce.m_Edge);
                foreach (var street in into)
                {
                    if (s.Roads.Contains(street) || !em.TryGetComponent<Edge>(street, out var se)) continue;
                    s.Roads.Add(street);
                    var (far, last) = WalkToCrossing(street, se.m_Start == node ? se.m_End : se.m_Start, s.Roads);
                    if (far != Entity.Null) blocks.Add((far, last));
                }
            }
            foreach (var (node, e) in blocks)
                if (!s.Cordons.Exists(c => c.node == node && c.edge == e)) s.Cordons.Add((node, e, PostNear(b, e, node)));
        }

        private const float AtCrossing = 30f; // m from the building's road access to a crossing: fire "at the crossing"

        /// <summary>From <paramref name="node"/> away from <paramref name="edge"/> through 2-way nodes to a crossing (3+ roads).
        /// Returns the crossing (Entity.Null: dead end) and the segment touching it; passed segments go to <paramref name="roads"/>.</summary>
        private (Entity crossing, Entity edge) WalkToCrossing(Entity edge, Entity node, List<Entity> roads)
        {
            var em = EntityManager;
            for (int i = 0; i < 12; i++) // ponytail: a long curvy street ends the walk here; the cordon then stands at that node
            {
                if (!em.HasBuffer<ConnectedEdge>(node)) return (Entity.Null, edge);
                var ce = em.GetBuffer<ConnectedEdge>(node, true);
                if (ce.Length > 2) return (node, edge);
                if (ce.Length < 2) return (Entity.Null, edge);
                var next = ce[0].m_Edge == edge ? ce[1].m_Edge : ce[0].m_Edge;
                if (roads.Contains(next) || !em.TryGetComponent<Edge>(next, out var ne)) return (node, edge);
                roads.Add(next);
                edge = next;
                node = ne.m_Start == node ? ne.m_End : ne.m_Start;
            }
            return (node, edge);
        }

        /// <summary>Address on <paramref name="edge"/> nearest the crossing, not the burning one (the cordon car drives there first).</summary>
        private Entity PostNear(Entity b, Entity edge, Entity crossing)
        {
            var em = EntityManager;
            if (crossing == Entity.Null) return Entity.Null;
            var at = em.GetComponentData<Node>(crossing).m_Position;
            using var all = m_Addresses.ToEntityArray(Allocator.Temp);
            Entity best = Entity.Null; float bestDist = float.MaxValue;
            foreach (var o in all)
            {
                if (o == b || em.GetComponentData<Building>(o).m_RoadEdge != edge) continue;
                float d = math.distance(at.xz, em.GetComponentData<Game.Objects.Transform>(o).m_Position.xz);
                if (d < bestDist) { bestDist = d; best = o; }
            }
            return best;
        }

        /// <summary>
        /// Engines working the fire stand one behind the other instead of on top of each other (vanilla stops them all
        /// at the address's path end): the first on scene stays, the others line up behind it
        /// (SidewalkParkingSystem.LineUpBehind). Each engine is held first (TripSource, like every vehicle we move), so
        /// vanilla's AI never drives one we are moving; the fire's course is ours anyway (FireDamageGuardSystem.Control).
        /// At the end of the controlled period they are let go and put out the rest.
        /// </summary>
        private void LineUpEngines()
        {
            var em = EntityManager;
            if (Scenes.Count == 0) return;
            using var engines = m_Engines.ToEntityArray(Allocator.Temp);
            var byScene = new Dictionary<Entity, List<Entity>>();
            foreach (var e in engines)
            {
                if ((em.GetComponentData<Game.Vehicles.FireEngine>(e).m_State & FireEngineFlags.Extinguishing) == 0 || !em.HasComponent<Car>(e)) continue;
                var t = em.GetComponentData<Target>(e).m_Target;
                if (!Scenes.ContainsKey(t)) continue;
                Scenes[t].Engines.Add(e);
                if (!byScene.TryGetValue(t, out var list)) byScene[t] = list = new List<Entity>();
                list.Add(e);
            }
            uint now = m_Sim.frameIndex;
            foreach (var kv in byScene)
            {
                var s = Scenes[kv.Key];
                if (s.EnginesReleased) continue;
                uint left = s.MinFrames > now - s.Start ? s.MinFrames - (now - s.Start) : 0;
                foreach (var e in kv.Value)
                    if (!em.HasComponent<Game.Objects.TripSource>(e))
                    {
                        ResponderDispatchSystem.HoldVehicle(em, e, left + 3600);
                        Log.Info($"fire_engine={e.Index} at work at fire={kv.Key.Index}, held (leader={(s.EngineLeader == Entity.Null ? e : s.EngineLeader).Index})");
                    }
                if (!kv.Value.Contains(s.EngineLeader)) s.EngineLeader = kv.Value[0];
                float queued = 0f;
                foreach (var f in kv.Value)
                {
                    if (f == s.EngineLeader) continue;
                    m_Parking.LineUpBehind(f, s.EngineLeader, (m_Parking.VehicleLength(s.EngineLeader) + m_Parking.VehicleLength(f)) * 0.5f + queued);
                    queued += m_Parking.VehicleLength(f);
                }
            }
        }

        private void RaiseClosedCost()
        {
            m_CostRaised = true;
            var em = EntityManager;
            using var prefabs = m_CarPathfind.ToEntityArray(Allocator.Temp);
            foreach (var p in prefabs)
            {
                var d = em.GetComponentData<PathfindCarData>(p);
                var before = d.m_ForbiddenCost.m_Value;
                d.m_ForbiddenCost.m_Value = math.max(before, ClosedCost);
                em.SetComponentData(p, d);
                Log.Info($"fire: closed-street cost {before} -> {d.m_ForbiddenCost.m_Value} (car pathfind prefab={p.Index})");
            }
        }

        /// <summary>
        /// Vehicles already routed through the street when it closes (paths are only planned once) get a new route:
        /// cars and buses alike, except emergency vehicles (lights on, or police/fire/ambulance).
        /// </summary>
        private void RerouteThrough(List<Entity> roads)
        {
            var em = EntityManager;
            var closed = new HashSet<Entity>();
            foreach (var r in roads)
                foreach (var (lane, _) in Cordon.MainLanes(em, r)) closed.Add(lane);
            if (closed.Count == 0) return;
            using var cars = m_Traffic.ToEntityArray(Allocator.Temp);
            int n = 0;
            foreach (var car in cars)
            {
                if ((em.GetComponentData<Car>(car).m_Flags & CarFlags.Emergency) != 0) continue;
                if (em.HasComponent<Game.Vehicles.PoliceCar>(car) || em.HasComponent<Game.Vehicles.FireEngine>(car) || em.HasComponent<Game.Vehicles.Ambulance>(car)) continue;
                var po = em.GetComponentData<Game.Pathfind.PathOwner>(car);
                if ((po.m_State & (Game.Pathfind.PathFlags.Pending | Game.Pathfind.PathFlags.Obsolete)) != 0) continue;
                var path = em.GetBuffer<Game.Pathfind.PathElement>(car, true);
                bool through = false;
                for (int i = po.m_ElementIndex; i < path.Length && !through; i++) through = closed.Contains(path[i].m_Target);
                if (!through && em.HasBuffer<CarNavigationLane>(car))
                    foreach (var nl in em.GetBuffer<CarNavigationLane>(car, true)) if (closed.Contains(nl.m_Lane)) { through = true; break; }
                if (!through) continue;
                po.m_State |= Game.Pathfind.PathFlags.Obsolete;
                em.SetComponentData(car, po);
                n++;
            }
            Log.Info($"fire: street closed, rerouted vehicles={n}");
        }

        /// <summary>Sets or clears Forbidden on the street's car lanes and tells the pathfinder. Returns lanes changed.</summary>
        private int SetClosed(Entity road, bool closed)
        {
            var em = EntityManager;
            if (road == Entity.Null || !em.Exists(road) || !em.HasBuffer<SubLane>(road)) return 0;
            var lanes = em.GetBuffer<SubLane>(road, true);
            var list = new List<Entity>();
            for (int i = 0; i < lanes.Length; i++) list.Add(lanes[i].m_SubLane); // buffer is invalidated by structural changes below
            int n = 0;
            foreach (var lane in list)
            {
                if (!em.HasComponent<CarLane>(lane)) continue;
                var c = em.GetComponentData<CarLane>(lane);
                if (((c.m_Flags & CarLaneFlags.Forbidden) != 0) == closed) continue;
                c.m_Flags = closed ? c.m_Flags | CarLaneFlags.Forbidden : c.m_Flags & ~CarLaneFlags.Forbidden;
                em.SetComponentData(lane, c);
                if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
                n++;
            }
            return n;
        }

        /// <summary>
        /// After a load: fires still burning become scenes again without new units (they were sent before the save);
        /// a Forbidden edge lane whose street has no fire is a closure left over from an earlier session: lifted.
        /// </summary>
        private void Restore(NativeArray<Entity> burning, uint now)
        {
            var em = EntityManager;
            var roads = new HashSet<Entity>();
            foreach (var b in burning)
            {
                var level = Classify(b);
                var s = new Scene { Level = level, Alarm = 1, Since = now, Start = now, Road = em.GetComponentData<Building>(b).m_RoadEdge, Code = CodeFor(level, 1) };
                FindEnds(b, s);
                Scenes[b] = s;
                s.Closed = Mod.Setting.EnableFireAlarms && Mod.Setting.CloseStreetAtFire && s.Road != Entity.Null;
                if (s.Closed) roads.UnionWith(s.Roads);
            }
            using var lanes = m_CarLanes.ToEntityArray(Allocator.Temp);
            var stale = new HashSet<Entity>();
            foreach (var lane in lanes)
            {
                if ((em.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Forbidden) == 0) continue;
                var owner = em.GetComponentData<Owner>(lane).m_Owner;
                // Segments with an accident site belong to HighwayCrashSystem (CrashCordon).
                if (em.HasComponent<Edge>(owner) && !roads.Contains(owner) && !em.HasComponent<Game.Events.AccidentSite>(owner)) stale.Add(owner);
            }
            int opened = 0;
            foreach (var road in stale) opened += SetClosed(road, false);
            foreach (var road in roads)
            {
                SetClosed(road, true);
                foreach (var (lane, _) in Cordon.MainLanes(em, road)) // already closed in the save: rebuild with the raised cost
                    if (!em.HasComponent<PathfindUpdated>(lane)) em.AddComponent<PathfindUpdated>(lane);
            }
            Log.Info($"fire: restored scenes={burning.Length} closed_streets={roads.Count} lifted_leftover_lanes={opened}");
        }

        public Level Classify(Entity b)
        {
            var em = EntityManager;
            if (em.HasComponent<Game.Buildings.Hospital>(b) || em.HasComponent<Game.Buildings.School>(b)) return Level.Public;
            if (em.HasComponent<IndustrialProperty>(b) && !em.HasComponent<OfficeProperty>(b)) return Level.Industrial;
            var prefab = em.GetComponentData<PrefabRef>(b).m_Prefab;
            if (em.HasComponent<ResidentialProperty>(b) && em.TryGetComponent<BuildingPropertyData>(prefab, out var props))
                return props.m_ResidentialProperties <= 2 ? Level.Small : props.m_ResidentialProperties <= 20 ? Level.Medium : Level.Large;
            // ponytail: lot area as size for commercial/office/other; height would be better for towers.
            int area = em.TryGetComponent<BuildingData>(prefab, out var bd) ? bd.m_LotSize.x * bd.m_LotSize.y : 0;
            if (em.HasComponent<OfficeProperty>(b)) return area <= 16 ? Level.Medium : Level.Large;
            return area <= 9 ? Level.Small : area <= 36 ? Level.Medium : Level.Large;
        }

        /// <summary>Debug: starts a vanilla fire event on a building, like FireHazardSystem.CreateFireEvent.</summary>
        public bool Ignite(Entity building)
        {
            var em = EntityManager;
            if (!em.Exists(building) || !em.HasComponent<Building>(building) || em.HasComponent<OnFire>(building)) return false;
            using var prefabs = m_FirePrefabs.ToEntityArray(Allocator.Temp);
            foreach (var p in prefabs)
            {
                if (em.GetComponentData<FireData>(p).m_RandomTargetType != EventTargetType.Building) continue;
                using var created = new NativeArray<Entity>(1, Allocator.Temp); // array overload, see NefDispatchSystem.Spawn
                em.CreateEntity(em.GetComponentData<EventData>(p).m_Archetype, created);
                var e = created[0];
                em.SetComponentData(e, new PrefabRef(p));
                em.GetBuffer<TargetElement>(e).Add(new TargetElement(building));
                Log.Info($"fire_debug ignite building={building.Index} level={Classify(building)}");
                return true;
            }
            Log.Warn("fire_debug: no building fire event prefab");
            return false;
        }
    }

    /// <summary>
    /// A building cannot burn down while the fire brigade is putting it out. Runs every frame right before vanilla
    /// FireSimulationSystem, which adds up to 0.5 fire damage per tick and destroys the building once total damage
    /// (ObjectUtils.GetTotalDamage) reaches 1: while an engine of a scene is extinguishing, fire damage is capped so
    /// one more tick cannot get there.
    /// </summary>
    public partial class FireDamageGuardSystem : GameSystemBase
    {
        private EntityQuery m_Engines;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engines = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.FireEngine>(), ComponentType.ReadOnly<Target>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            if (FireAlarmSystem.Scenes.Count == 0 || !Mod.Setting.EnableFireAlarms) return;
            var em = EntityManager;
            KeepEnginesOnScene(em);
            uint now = World.GetExistingSystemManaged<Game.Simulation.SimulationSystem>().frameIndex;
            var controlled = new HashSet<Entity>();
            using var engines = m_Engines.ToEntityArray(Allocator.Temp);
            foreach (var e in engines)
            {
                var fe = em.GetComponentData<Game.Vehicles.FireEngine>(e);
                if ((fe.m_State & FireEngineFlags.Extinguishing) == 0) continue;
                var b = em.GetComponentData<Target>(e).m_Target;
                if (!FireAlarmSystem.Scenes.TryGetValue(b, out var scene)) continue;
                // Water never runs out on scene: the engine stays until the fire is out instead of leaving to refill.
                var capacity = em.GetComponentData<FireEngineData>(em.GetComponentData<PrefabRef>(e).m_Prefab).m_ExtinguishingCapacity;
                if (capacity > 0f && (fe.m_ExtinguishingAmount < capacity || (fe.m_State & (FireEngineFlags.Empty | FireEngineFlags.EstimatedEmpty)) != 0))
                {
                    fe.m_ExtinguishingAmount = capacity;
                    fe.m_State &= ~(FireEngineFlags.Empty | FireEngineFlags.EstimatedEmpty);
                    em.SetComponentData(e, fe);
                }
                if (controlled.Add(b)) Control(em, b, scene, now);
                if (!em.TryGetComponent<Game.Objects.Damaged>(b, out var d)) continue;
                // Total = x + y + max(0, z - min(0.5, x + y)); the next tick adds at most 0.5 to y.
                float cap = math.max(0f, 0.45f - d.m_Damage.x - math.max(0f, d.m_Damage.z - 0.5f));
                if (d.m_Damage.y <= cap) continue;
                d.m_Damage.y = cap;
                em.SetComponentData(b, d);
            }
        }

        /// <summary>
        /// From the first engine at work the fire's intensity falls evenly from what it was then to 0 at BurnEnd (a third
        /// of the minimum scene time after dispatch); vanilla growth and extinguishing are both overridden. At 0 vanilla
        /// FireSimulationSystem (right after us) removes the fire. The engines stay held for overhaul until MinFrames.
        /// </summary>
        private static void Control(EntityManager em, Entity b, FireAlarmSystem.Scene s, uint now)
        {
            if (!em.TryGetComponent<OnFire>(b, out var f)) return;
            if (s.ControlFrom == 0) { s.ControlFrom = now; s.ControlIntensity = math.max(f.m_Intensity, 1f); }
            uint end = s.BurnEnd;
            float left = now >= end ? 0f : (float)(end - now) / math.max(1u, end - s.ControlFrom);
            f.m_Intensity = left > 0f ? math.max(0.5f, s.ControlIntensity * left) : 0f; // 0: out
            em.SetComponentData(b, f);
        }

        /// <summary>
        /// Fire out but the scene's minimum time not over: an engine that worked it is stopped where it stands the frame
        /// vanilla sends it home (held like ResponderDispatchSystem.HoldVehicle; FireAlarmSystem.End releases it).
        /// Every frame, so it does not drive off first. Empty engines leaving during the fire (refill) are not stopped.
        /// </summary>
        private void KeepEnginesOnScene(EntityManager em)
        {
            uint now = World.GetExistingSystemManaged<Game.Simulation.SimulationSystem>().frameIndex;
            foreach (var kv in FireAlarmSystem.Scenes)
            {
                var b = kv.Key;
                if (em.TryGetComponent<OnFire>(b, out var f) && f.m_Intensity > 0f) continue; // still burning
                foreach (var e in kv.Value.Engines)
                {
                    if (!em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Game.Objects.TripSource>(e) || em.HasComponent<ParkedCar>(e)) continue;
                    if ((em.GetComponentData<Game.Vehicles.FireEngine>(e).m_State & FireEngineFlags.Returning) == 0) continue;
                    uint left = kv.Value.MinFrames > now - kv.Value.Start ? kv.Value.MinFrames - (now - kv.Value.Start) : 0;
                    if (left == 0) continue;
                    ResponderDispatchSystem.HoldVehicle(em, e, left + 3600); // timer: vanilla takes over by itself if we never release
                    ResponderDispatchSystem.FireEngines.Remove(e);
                }
            }
        }
    }

    /// <summary>
    /// Once the fire brigade works a fire it does not spread any more: vanilla FireSimulationSystem spreads by creating
    /// Ignite events carrying the burning building's fire event; right before IgniteSystem applies them, the ones of a
    /// fire under control (an engine at work) aimed within SpreadBlock m of it are dropped.
    /// </summary>
    public partial class FireSpreadBlockSystem : GameSystemBase
    {
        private EntityQuery m_Ignites;
        private const float SpreadBlock = 150f;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Ignites = GetEntityQuery(ComponentType.ReadOnly<Ignite>(), ComponentType.ReadOnly<Game.Common.Event>());
            RequireForUpdate(m_Ignites);
        }

        protected override void OnUpdate()
        {
            if (FireAlarmSystem.Scenes.Count == 0 || !Mod.Setting.EnableFireAlarms) return;
            var em = EntityManager;
            var controlled = new List<(Entity fireEvent, Unity.Mathematics.float3 at)>();
            foreach (var kv in FireAlarmSystem.Scenes)
                if (kv.Value.ControlFrom != 0 && em.TryGetComponent<OnFire>(kv.Key, out var f) && f.m_Event != Entity.Null)
                    controlled.Add((f.m_Event, em.GetComponentData<Game.Objects.Transform>(kv.Key).m_Position));
            if (controlled.Count == 0) return;
            using var events = m_Ignites.ToEntityArray(Allocator.Temp);
            foreach (var e in events)
            {
                var ig = em.GetComponentData<Ignite>(e);
                if (!em.TryGetComponent<Game.Objects.Transform>(ig.m_Target, out var t)) continue;
                foreach (var c in controlled)
                {
                    if (ig.m_Event != c.fireEvent || math.distance(t.m_Position.xz, c.at.xz) > SpreadBlock) continue;
                    em.DestroyEntity(e);
                    Log.Debug($"fire: spread to={ig.m_Target.Index} blocked, fire brigade at work");
                    break;
                }
            }
        }
    }
}
