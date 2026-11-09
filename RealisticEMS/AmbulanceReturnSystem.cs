using Game;
using Game.Common;
using Game.Pathfind;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Vanilla lets a Returning ambulance take new calls (it is a dispatch source in HealthcarePathfindSetup and
    /// requests work itself in AmbulanceAISystem.RequestTargetIfNeeded). Marking it Disabled blocks both; ParkCar
    /// resets m_State when it arrives at its home depot, so it becomes available again only there.
    /// Also logs ambulance state changes for in-game testing.
    /// </summary>
    public partial class AmbulanceReturnSystem : GameSystemBase
    {
        private const AmbulanceFlags Mask = AmbulanceFlags.Returning | AmbulanceFlags.Dispatched | AmbulanceFlags.Transporting | AmbulanceFlags.Disabled;
        private EntityQuery m_Query;
        private Dictionary<Entity, AmbulanceFlags> m_LastState = new Dictionary<Entity, AmbulanceFlags>();

        protected override void OnCreate()
        {
            base.OnCreate();
            // PathOwner = moving vehicle; parked ambulances lose it in ParkCar.
            m_Query = GetEntityQuery(ComponentType.ReadWrite<Ambulance>(), ComponentType.ReadOnly<PathOwner>(), ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Target>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            RequireForUpdate(m_Query);
        }

        private readonly Dictionary<Entity, int> m_Attempts = new Dictionary<Entity, int>();      // ambulance -> failed hospital searches this trip
        private readonly Dictionary<Entity, Entity> m_Borrowed = new Dictionary<Entity, Entity>(); // ambulance -> real owner while a hospital is borrowed

        private void Retarget(Entity e, Entity target)
        {
            var pathOwner = EntityManager.GetComponentData<PathOwner>(e);
            var t = EntityManager.GetComponentData<Target>(e);
            VehicleUtils.SetTarget(ref pathOwner, ref t, target);
            EntityManager.SetComponentData(e, pathOwner);
            EntityManager.SetComponentData(e, t);
        }

        /// <summary>
        /// Moves the vehicle to another owner's OwnedVehicle buffer too. Vanilla only removes a deleted vehicle from its
        /// current owner's buffer, so a stale entry left in the old one crashes that building's info panel.
        /// </summary>
        private void SetOwner(Entity e, Entity owner)
        {
            var em = EntityManager;
            var old = em.GetComponentData<Owner>(e).m_Owner;
            if (em.HasBuffer<OwnedVehicle>(old))
            {
                var b = em.GetBuffer<OwnedVehicle>(old);
                for (int i = b.Length - 1; i >= 0; i--) if (b[i].m_Vehicle == e) b.RemoveAt(i);
            }
            if (em.HasBuffer<OwnedVehicle>(owner)) em.GetBuffer<OwnedVehicle>(owner).Add(new OwnedVehicle(e));
            em.SetComponentData(e, new Owner(owner));
        }

        /// <summary>After delivering at a borrowed hospital: give the ambulance back to its depot.</summary>
        private void ReturnBorrowed()
        {
            var em = EntityManager;
            foreach (var e in new List<Entity>(m_Borrowed.Keys))
            {
                var home = m_Borrowed[e];
                if (!em.Exists(e) || em.HasComponent<Deleted>(e)) { m_Borrowed.Remove(e); continue; }
                if ((em.GetComponentData<Ambulance>(e).m_State & AmbulanceFlags.Transporting) != 0) continue; // still delivering
                m_Borrowed.Remove(e);
                if (!em.Exists(home)) { em.AddComponent<Deleted>(e); continue; }
                SetOwner(e, home);
                if (em.HasComponent<ParkedCar>(e)) { em.AddComponent<Deleted>(e); continue; } // parked at the hospital: depot spawns a fresh one
                Retarget(e, home);
                Mod.log.Info($"Ambulance {e.Index}: patient delivered, back to depot {home.Index}");
            }
        }

        protected override void OnUpdate()
        {
            ReturnBorrowed();
            using var entities = m_Query.ToEntityArray(Allocator.Temp);
            var ambulances = m_Query.ToComponentDataArray<Ambulance>(Allocator.Temp);
            bool changed = false;
            bool log = Mod.Setting.LogAmbulanceStates;
            var seen = log ? new Dictionary<Entity, AmbulanceFlags>(entities.Length) : null;

            for (int i = 0; i < ambulances.Length; i++)
            {
                var a = ambulances[i];
                // ponytail: HospitalAISystem may clear Disabled on its 256-frame tick; we re-set it the same frame,
                // leaving a one-frame window. Patch HospitalAction.SetDisabled if that ever shows up in testing.
                if (Mod.Setting.AvailableOnlyAtDepot && (a.m_State & Mask) == AmbulanceFlags.Returning)
                {
                    a.m_State |= AmbulanceFlags.Disabled;
                    ambulances[i] = a;
                    changed = true;
                }
                // Heading home without a critical patient: no blue lights (vanilla only re-evaluates them on a new path).
                const AmbulanceFlags urgent = AmbulanceFlags.Transporting | AmbulanceFlags.Critical;
                if ((a.m_State & AmbulanceFlags.Returning) != 0 && (a.m_State & urgent) != urgent && EntityManager.HasComponent<Car>(entities[i]))
                {
                    var car = EntityManager.GetComponentData<Car>(entities[i]);
                    if ((car.m_Flags & CarFlags.Emergency) != 0)
                    {
                        car.m_Flags &= ~CarFlags.Emergency;
                        EntityManager.SetComponentData(entities[i], car);
                        if (!EntityManager.HasComponent<EffectsUpdated>(entities[i])) EntityManager.AddComponent<EffectsUpdated>(entities[i]);
                    }
                }
                // Hospital pathfind failed: vanilla brings the patient to the ambulance's own building (the depot,
                // which loops). Attempt 1: search again with the full-hospital filter suspended. Attempt 2: borrow the
                // nearest suitable hospital as owner, so vanilla's return-to-owner trip (with SpecialParking into the
                // ambulance bay, which a plain target does not get) delivers the patient there. Then give up.
                const AmbulanceFlags failed = AmbulanceFlags.Returning | AmbulanceFlags.Transporting;
                var ent = entities[i];
                if ((a.m_State & AmbulanceFlags.Transporting) == 0) m_Attempts.Remove(ent);
                else if ((a.m_State & failed) == failed && !m_Borrowed.ContainsKey(ent)
                         && !EntityManager.HasBuffer<Game.Buildings.Patient>(EntityManager.GetComponentData<Owner>(ent).m_Owner))
                {
                    int attempt = m_Attempts.TryGetValue(ent, out var n) ? n + 1 : 1;
                    m_Attempts[ent] = attempt;
                    if (attempt == 1)
                    {
                        World.GetExistingSystemManaged<HospitalSelectionSystem>().Suspend(256);
                        a.m_State = (a.m_State & ~AmbulanceFlags.Returning) | AmbulanceFlags.FindHospital;
                        ambulances[i] = a;
                        changed = true;
                        // Keep the (valid) current target: AmbulanceAISystem aborts on a null target before the new
                        // hospital path arrives. FindHospital makes the next path search a hospital search anyway.
                        Retarget(ent, EntityManager.GetComponentData<Target>(ent).m_Target);
                        Mod.log.Info($"Ambulance {ent.Index}: hospital search failed, retrying without full-hospital filter");
                    }
                    else if (attempt == 2)
                    {
                        var from = EntityManager.GetComponentData<Game.Objects.Transform>(ent).m_Position;
                        var hospital = HospitalSelectionSystem.FindNearestHospital(EntityManager, a.m_TargetPatient, from);
                        if (hospital != Entity.Null)
                        {
                            m_Borrowed[ent] = EntityManager.GetComponentData<Owner>(ent).m_Owner;
                            SetOwner(ent, hospital);
                            Retarget(ent, hospital); // stays Returning|Transporting: vanilla "return home" = deliver here
                            Mod.log.Info($"Ambulance {ent.Index}: no hospital found by pathfind, delivers patient to hospital {hospital.Index} as temporary home");
                        }
                        else Mod.log.Warn($"Ambulance {ent.Index}: no hospital in the city can treat patient {a.m_TargetPatient.Index}");
                    }
                    // attempt >= 3: give up, vanilla takes over (logged once above)
                }
                if (log)
                {
                    var e = entities[i];
                    // Held ambulances flicker AtTarget every tick (CrewHoldSystem): leave it out of the log.
                    var state = CrewHoldSystem.Held.Contains(e) ? a.m_State & ~AmbulanceFlags.AtTarget : a.m_State;
                    seen[e] = state;
                    if (!m_LastState.TryGetValue(e, out var prev) || prev != state)
                        Mod.log.Info($"Ambulance {e.Index}: {prev} -> {state} | depot {EntityManager.GetComponentData<Owner>(e).m_Owner.Index}" +
                                     $" target {EntityManager.GetComponentData<Target>(e).m_Target.Index} patient {a.m_TargetPatient.Index}");
                }
            }
            if (changed) m_Query.CopyFromComponentDataArray(ambulances);
            if (log) m_LastState = seen;
        }
    }
}
