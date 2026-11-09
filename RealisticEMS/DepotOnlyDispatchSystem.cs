using Game;
using Game.Buildings;
using Game.Common;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Hospitals (buildings with a Patient buffer) stop sending ambulances while at least one depot (Hospital without
    /// Patient buffer, i.e. 0 beds) exists. Dispatch sources in HealthcarePathfindSetup.SetupAmbulancesJob are
    /// buildings with HasAvailableAmbulances and parked ambulances that are not Disabled, so we clear the one and set
    /// the other after HospitalAISystem recomputes them.
    /// </summary>
    public partial class DepotOnlyDispatchSystem : GameSystemBase
    {
        private EntityQuery m_HospitalQuery;
        private EntityQuery m_DepotQuery;
        private EntityQuery m_ParkedQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_HospitalQuery = GetEntityQuery(ComponentType.ReadWrite<Hospital>(), ComponentType.ReadOnly<Patient>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_DepotQuery = GetEntityQuery(ComponentType.ReadOnly<Hospital>(), ComponentType.ReadOnly<OwnedVehicle>(), ComponentType.Exclude<Patient>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_ParkedQuery = GetEntityQuery(ComponentType.ReadWrite<Ambulance>(), ComponentType.ReadOnly<ParkedCar>(), ComponentType.ReadOnly<Owner>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            RequireForUpdate(m_HospitalQuery);
        }

        protected override void OnUpdate()
        {
            // ponytail: "any depot exists" is city-wide; per-district coverage would need ServiceDistrict checks.
            if (!Mod.Setting.OnlyDepotsDispatch || m_DepotQuery.IsEmpty) return;

            var hospitals = m_HospitalQuery.ToComponentDataArray<Hospital>(Allocator.Temp);
            bool changed = false;
            for (int i = 0; i < hospitals.Length; i++)
            {
                var h = hospitals[i];
                if ((h.m_Flags & HospitalFlags.HasAvailableAmbulances) == 0) continue;
                h.m_Flags &= ~HospitalFlags.HasAvailableAmbulances;
                hospitals[i] = h;
                changed = true;
            }
            if (changed) m_HospitalQuery.CopyFromComponentDataArray(hospitals);

            using var owners = m_ParkedQuery.ToComponentDataArray<Owner>(Allocator.Temp);
            var parked = m_ParkedQuery.ToComponentDataArray<Ambulance>(Allocator.Temp);
            changed = false;
            for (int i = 0; i < parked.Length; i++)
            {
                var a = parked[i];
                if ((a.m_State & AmbulanceFlags.Disabled) != 0 || !EntityManager.HasBuffer<Patient>(owners[i].m_Owner)) continue;
                a.m_State |= AmbulanceFlags.Disabled;
                parked[i] = a;
                changed = true;
            }
            if (changed) m_ParkedQuery.CopyFromComponentDataArray(parked);
        }
    }
}
