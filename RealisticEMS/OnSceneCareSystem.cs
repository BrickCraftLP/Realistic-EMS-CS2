using Game;
using Game.Common;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Ambulance = Game.Vehicles.Ambulance;

namespace RealisticEMS
{
    /// <summary>
    /// On-scene care: an ambulance stays with the patient for OnSceneMinutes (game time) after it arrives, before the
    /// patient may board (CrewHoldSystem keeps AtTarget cleared). With an NEF involved, care starts once both are on
    /// scene; if the NEF is not there within NefMaxWaitMinutes, the crew starts alone. The drive never counts.
    /// </summary>
    public partial class OnSceneCareSystem : GameSystemBase
    {
        public const float FramesPerGameMinute = TimeSystem.kTicksPerDay / 1440f;

        private class Care { public uint Arrive, Start; }
        private readonly Dictionary<Entity, Care> m_Care = new Dictionary<Entity, Care>(); // patient -> on-scene times
        private EntityQuery m_Ambulances;
        private SimulationSystem m_Sim;
        private UnitNamingSystem m_Units;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        /// <summary>Arrived at the call location, the same test AmbulanceAISystem uses (end of path OR curb parking spot).</summary>
        public static bool OnScene(EntityManager em, Entity vehicle)
        {
            if (!em.HasComponent<CarCurrentLane>(vehicle) || !em.HasComponent<PathOwner>(vehicle)) return false;
            var lane = em.GetComponentData<CarCurrentLane>(vehicle);
            var pathOwner = em.GetComponentData<PathOwner>(vehicle);
            // A freshly dispatched vehicle still shows "end reached" from its parking spot at the station until the
            // new path arrives: require a settled path and being near the call location.
            if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated)) != 0) return false;
            if (!VehicleUtils.PathEndReached(lane) && !VehicleUtils.ParkingSpaceReached(lane, pathOwner)) return false;
            var target = em.HasComponent<Game.Common.Target>(vehicle) ? em.GetComponentData<Game.Common.Target>(vehicle).m_Target : Entity.Null;
            if (!em.HasComponent<Game.Objects.Transform>(target)) return false;
            // ponytail: 150 m covers large lots; huge industrial lots could be further.
            return math.distance(em.GetComponentData<Game.Objects.Transform>(target).m_Position.xz, em.GetComponentData<Game.Objects.Transform>(vehicle).m_Position.xz) < 150f;
        }

        private readonly HashSet<Entity> m_Silenced = new HashSet<Entity>();

        /// <summary>
        /// The siren effect needs CarFlags.Emergency, the flashing lights Emergency or Warning (EffectControlData).
        /// Vanilla only sets Emergency again when a new path starts, i.e. on departure.
        /// </summary>
        private void SilenceSirens(HashSet<Entity> quiet)
        {
            var em = EntityManager;
            foreach (var v in quiet)
            {
                if (!em.HasComponent<Car>(v)) continue;
                var car = em.GetComponentData<Car>(v);
                var flags = (car.m_Flags & ~CarFlags.Emergency) | CarFlags.Warning;
                if (flags != car.m_Flags) { car.m_Flags = flags; em.SetComponentData(v, car); }
                if (m_Silenced.Add(v)) Mod.log.Info($"{m_Units.CallsignOf(v)}: Sirene aus, Blaulicht an");
            }
            foreach (var v in new List<Entity>(m_Silenced))
            {
                if (quiet.Contains(v)) continue;
                m_Silenced.Remove(v);
                if (!em.Exists(v) || !em.HasComponent<Car>(v)) continue;
                var car = em.GetComponentData<Car>(v);
                car.m_Flags &= ~CarFlags.Warning; // departure path sets Emergency again if needed
                em.SetComponentData(v, car);
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Units = World.GetOrCreateSystemManaged<UnitNamingSystem>();
            m_Ambulances = GetEntityQuery(ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<PathOwner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Care.Clear();
            m_Silenced.Clear();
            CrewHoldSystem.Held.Clear();
        }

        protected override void OnUpdate()
        {
            var em = EntityManager;
            uint now = m_Sim.frameIndex;
            uint care = (uint)(Mod.Setting.OnSceneMinutes * FramesPerGameMinute);
            uint maxWait = Mod.Setting.EnableNef ? (uint)(Mod.Setting.NefMaxWaitMinutes * FramesPerGameMinute) : 0;
            uint cap = care + maxWait + (uint)(10 * FramesPerGameMinute);
            var held = new HashSet<Entity>();
            var quiet = new HashSet<Entity>(); // on scene during care: lights on, siren off
            var seen = new HashSet<Entity>();
            using var vehicles = m_Ambulances.ToEntityArray(Allocator.Temp);

            foreach (var rtw in vehicles)
            {
                if (NefDispatchSystem.IsNef(em, rtw)) continue;
                var a = em.GetComponentData<Ambulance>(rtw);
                var p = a.m_TargetPatient;
                if (p == Entity.Null || (a.m_State & AmbulanceFlags.Dispatched) == 0
                    || (a.m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) != 0) continue;
                bool nefInvolved = NefDispatchSystem.NefByPatient.TryGetValue(p, out var nef);
                if (care == 0 && !nefInvolved) continue;
                seen.Add(p);
                // Held already on the way: the patient boards in the first frame AtTarget is visible.
                if (!OnScene(em, rtw)) { held.Add(rtw); continue; }

                string unit = m_Units.CallsignOf(rtw);
                if (!m_Care.TryGetValue(p, out var c))
                {
                    m_Care[p] = c = new Care { Arrive = now };
                    if (!nefInvolved) c.Start = now;
                    Mod.log.Info($"{unit} vor Ort, {(nefInvolved ? "wartet auf NEF" : $"versorgt Patient ({Mod.Setting.OnSceneMinutes} min)")}");
                }
                else if (c.Start == 0 && !nefInvolved) c.Start = now; // NEF mission ended: start alone
                if (c.Start == 0 && nefInvolved)
                {
                    bool nefOnScene = nef != Entity.Null && em.Exists(nef) && OnScene(em, nef)
                        && (em.GetComponentData<Ambulance>(nef).m_State & (AmbulanceFlags.Transporting | AmbulanceFlags.Returning)) == 0;
                    if (nefOnScene) { c.Start = now; Mod.log.Info($"{unit} behandelt gemeinsam mit NEF ({Mod.Setting.OnSceneMinutes} min)"); }
                    else if (now - c.Arrive >= maxWait) { c.Start = now; Mod.log.Info($"{unit}: NEF nicht rechtzeitig da, versorgt allein"); }
                }

                bool hold = c.Start == 0 || now - c.Start < care;
                if (hold && now - c.Arrive > cap) { hold = false; Mod.log.Warn($"{unit}: maximale Zeit vor Ort überschritten, lädt Patient ein"); }
                if (hold)
                {
                    held.Add(rtw);
                    quiet.Add(rtw);
                    if (nefInvolved && nef != Entity.Null && em.Exists(nef) && OnScene(em, nef)) quiet.Add(nef);
                }
                else if (CrewHoldSystem.Held.Contains(rtw))
                    Mod.log.Info($"{unit} lädt Patient ein ({(now - c.Arrive) / FramesPerGameMinute:0} min vor Ort)");
            }

            CrewHoldSystem.Held.Clear();
            CrewHoldSystem.Held.UnionWith(held);
            SilenceSirens(quiet);
            foreach (var p in new List<Entity>(m_Care.Keys)) if (!seen.Contains(p)) m_Care.Remove(p);
        }
    }
}
