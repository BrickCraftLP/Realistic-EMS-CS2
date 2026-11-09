using Game;
using Game.Net;
using System.Collections.Generic;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Runs after TrafficLightSystem (which rewrites every LaneSignal on its tick) and keeps the lanes in
    /// EmergencyLaneSystem.ForcedGreen at Go until their timer runs out. Cross traffic keeps its own signal;
    /// vanilla crossing blockers make cars yield inside the junction.
    /// </summary>
    public partial class SignalOverrideSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            var forced = EmergencyLaneSystem.ForcedGreen;
            if (forced.Count == 0) return;
            foreach (var lane in new List<Entity>(forced.Keys))
            {
                if (--forced[lane] <= 0 || !EntityManager.Exists(lane) || !EntityManager.HasComponent<LaneSignal>(lane)) { forced.Remove(lane); continue; }
                var sig = EntityManager.GetComponentData<LaneSignal>(lane);
                if (sig.m_Signal == LaneSignalType.Go) continue;
                sig.m_Signal = LaneSignalType.Go;
                EntityManager.SetComponentData(lane, sig);
            }
        }
    }
}
