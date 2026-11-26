using Game;
using Game.Common;
using Game.Pathfind;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Vanilla HospitalAISystem counts every moving vehicle with an Ambulance component against the building's capacity
    /// and sets Disabled on the ones beyond it, mid-call included. The mod's NEFs and rescue helicopters are such vehicles,
    /// so they push real ambulances over the limit. AmbulanceAISystem then sends a Disabled ambulance that is not
    /// Returning/Transporting home, and the call is lost (EINSATZ VERLOREN). Runs right before AmbulanceAISystem:
    /// a vehicle on its way to a patient or with a patient on board keeps going. Disabled while Returning (set on purpose
    /// by this mod) and on parked vehicles (capacity caps) is left alone.
    /// </summary>
    public partial class AmbulanceKeepOnCallSystem : GameSystemBase
    {
        private EntityQuery m_Query;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadWrite<Ambulance>(), ComponentType.ReadOnly<PathOwner>(),
                // Helicopters: HelicopterSystem owns their state; clearing Disabled there fought vanilla every frame.
                ComponentType.Exclude<ParkedCar>(), ComponentType.Exclude<Helicopter>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            RequireForUpdate(m_Query);
        }

        protected override void OnUpdate()
        {
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                var a = EntityManager.GetComponentData<Ambulance>(e);
                if (!OnCall(a.m_State) || a.m_TargetPatient == Entity.Null) continue;
                a.m_State &= ~AmbulanceFlags.Disabled;
                EntityManager.SetComponentData(e, a);
            }
        }

        /// <summary>Disabled while heading to a patient or carrying one, and not on the way home.</summary>
        public static bool OnCall(AmbulanceFlags s) =>
            (s & AmbulanceFlags.Disabled) != 0 && (s & AmbulanceFlags.Returning) == 0
            && (s & (AmbulanceFlags.Dispatched | AmbulanceFlags.Transporting)) != 0;
    }
}
