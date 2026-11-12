using Game;
using Game.Common;
using Game.Net;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// SirenOnlyWhenNeeded: the siren sounds only in a jam / corridor, on the oncoming lane and in front of a red light,
    /// plus SirenHoldFrames afterwards. The vanilla siren effect needs CarFlags.Emergency, which also drives lane
    /// reservations, signal priority and the police/fire AI, so the flag is never cleared in the simulation. Instead
    /// SirenMuteSystem hides it from EffectControlSystem only (PreCulling phase) and SirenRestoreSystem puts it back.
    /// </summary>
    public partial class EmergencyLaneSystem
    {
        /// <summary>Emergency vehicles whose siren is muted right now (lights stay on).</summary>
        public static readonly HashSet<Entity> Quiet = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> m_SirenHold = new Dictionary<Entity, int>();
        private const int SirenHoldFrames = 180;     // ~3 s at 1x
        private const int SirenJamFrames = 30;       // ~0.5 s queued behind a car counts as a jam
        private const float SirenRedDistance = 70f;  // metres ahead to look for a red signal

        /// <summary>Per emergency vehicle, after Drive(). Returns true when its quiet state changed.</summary>
        private bool UpdateSiren(Entity e, EvState st, CarCurrentLane lane)
        {
            bool need = st.InGap || st.InSqueeze || st.Oncoming != null || st.Blocked > SirenJamFrames || RedAhead(e, lane);
            m_SirenHold.TryGetValue(e, out int hold);
            hold = need ? SirenHoldFrames : System.Math.Max(0, hold - 1);
            m_SirenHold[e] = hold;
            bool quiet = hold == 0;
            if (quiet == Quiet.Contains(e)) return false;
            if (quiet) Quiet.Add(e); else Quiet.Remove(e);
            Mod.log.Info($"Einsatzfahrzeug {e.Index}: Sirene {(quiet ? "aus" : "an")}");
            return true;
        }

        /// <summary>A signal showing red on the route within SirenRedDistance.</summary>
        private bool RedAhead(Entity e, CarCurrentLane lane)
        {
            var em = EntityManager;
            if (!em.HasBuffer<CarNavigationLane>(e)) return false;
            float d = RemainingDistance(lane);
            foreach (var n in em.GetBuffer<CarNavigationLane>(e, true))
            {
                if (d > SirenRedDistance) break;
                if (em.HasComponent<LaneSignal>(n.m_Lane))
                {
                    var sig = em.GetComponentData<LaneSignal>(n.m_Lane).m_Signal;
                    if (sig == LaneSignalType.Stop || sig == LaneSignalType.SafeStop) return true;
                }
                if (em.HasComponent<Curve>(n.m_Lane)) d += em.GetComponentData<Curve>(n.m_Lane).m_Length;
            }
            return false;
        }

        /// <summary>Unmutes every vehicle not in <paramref name="keep"/> (setting off, lights off, gone). Adds them to <paramref name="changed"/>.</summary>
        private void ReleaseSirens(HashSet<Entity> keep, List<Entity> changed)
        {
            foreach (var e in new List<Entity>(Quiet))
                if (keep == null || !keep.Contains(e)) { Quiet.Remove(e); m_SirenHold.Remove(e); changed.Add(e); }
            if (keep == null) m_SirenHold.Clear();
            else foreach (var e in new List<Entity>(m_SirenHold.Keys)) if (!keep.Contains(e)) m_SirenHold.Remove(e);
        }

        /// <summary>EffectsUpdated makes vanilla re-check the vehicle's effects (siren on/off). After all chunk copies: structural change.</summary>
        private void RefreshEffects(List<Entity> changed)
        {
            var em = EntityManager;
            foreach (var e in changed)
                if (em.Exists(e) && !em.HasComponent<Deleted>(e) && !em.HasComponent<EffectsUpdated>(e)) em.AddComponent<EffectsUpdated>(e);
        }
    }

    /// <summary>PreCulling, before EffectControlSystem: muted vehicles show Warning instead of Emergency (lights without siren).</summary>
    public partial class SirenMuteSystem : GameSystemBase
    {
        /// <summary>Vehicle -> had Warning set before we masked it.</summary>
        public static readonly Dictionary<Entity, bool> Masked = new Dictionary<Entity, bool>();

        protected override void OnUpdate()
        {
            var quiet = EmergencyLaneSystem.Quiet;
            if (quiet.Count == 0) return;
            var em = EntityManager;
            foreach (var e in quiet)
            {
                if (!em.Exists(e) || !em.HasComponent<Car>(e)) continue;
                var car = em.GetComponentData<Car>(e);
                if ((car.m_Flags & CarFlags.Emergency) == 0) continue;
                Masked[e] = (car.m_Flags & CarFlags.Warning) != 0;
                car.m_Flags = (car.m_Flags & ~CarFlags.Emergency) | CarFlags.Warning;
                em.SetComponentData(e, car);
            }
        }
    }

    /// <summary>PreCulling, after EffectControlSystem: gives the masked vehicles their Emergency flag back.</summary>
    public partial class SirenRestoreSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            var masked = SirenMuteSystem.Masked;
            if (masked.Count == 0) return;
            var em = EntityManager;
            foreach (var kv in masked)
            {
                if (!em.Exists(kv.Key) || !em.HasComponent<Car>(kv.Key)) continue;
                var car = em.GetComponentData<Car>(kv.Key); // completes the effect job reading Car
                car.m_Flags |= CarFlags.Emergency;
                if (!kv.Value) car.m_Flags &= ~CarFlags.Warning;
                em.SetComponentData(kv.Key, car);
            }
            masked.Clear();
        }
    }
}
