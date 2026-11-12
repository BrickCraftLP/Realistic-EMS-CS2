using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Vehicles;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using Edge = Game.Net.Edge;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>Older versions built temporary lanes; saves made with them still contain some and are cleaned on load.</summary>
    public partial class EmergencyLaneSystem
    {
        /// <summary>
        /// Temp lanes from a save made mid-manoeuvre. Marker: MakeTempLane gives every temp lane a middle path node
        /// owned by the lane itself; vanilla always uses the edge or node (LaneSystem), and PathNode saves its owner
        /// as an entity, so the marker survives loading. (SubLane is not saved, so "not listed by its road" is no test.)
        /// </summary>
        private void SweepOrphanLanes()
        {
            var em = EntityManager;
            var query = GetEntityQuery(ComponentType.ReadOnly<CarLane>(), ComponentType.ReadOnly<Lane>(), ComponentType.Exclude<Deleted>());
            using var lanes = query.ToEntityArray(Allocator.Temp);
            var orphans = new List<Entity>();
            foreach (var l in lanes)
                if (em.GetComponentData<Lane>(l).m_MiddleNode.OwnerEquals(new PathNode(l, (ushort)1))) orphans.Add(l);
            if (orphans.Count == 0) return;
            // Vehicles saved on or routed over them must leave first: native vanilla jobs read the lane unchecked and crash.
            var orphanSet = new HashSet<Entity>(orphans);
            using var cars = GetEntityQuery(ComponentType.ReadWrite<CarCurrentLane>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp);
            foreach (var car in cars) RescueFromOrphans(car, orphanSet);
            // Loading lists them as real sub lanes of their road/junction (SubLane is rebuilt from Owner) and registers them
            // everywhere. Removing them from outside left dangling references that crash Burst jobs; instead the owner is
            // marked Updated and vanilla LaneSystem rebuilds its lanes, deleting the ones it does not generate (ours).
            foreach (var l in orphans)
            {
                var owner = em.HasComponent<Owner>(l) ? em.GetComponentData<Owner>(l).m_Owner : Entity.Null;
                if (owner != Entity.Null && em.Exists(owner)) { if (!em.HasComponent<Updated>(owner)) em.AddComponent<Updated>(owner); }
                else em.AddComponent<Deleted>(l);
            }
            Mod.log.Info($"Rettungsgasse: {orphans.Count} temporäre Spur(en) aus dem Spielstand entfernt");
        }

        /// <summary>
        /// A car on an orphan lane goes onto the nearest real car lane of the same road/junction (no real lane: the car
        /// is deleted); one with an orphan in its route drops navigation and repaths.
        /// </summary>
        // ponytail: trailers (CarTrailerLane) are not moved; temp lanes are only used by emergency vehicles, which have none.
        private void RescueFromOrphans(Entity car, HashSet<Entity> orphans)
        {
            var em = EntityManager;
            var cl = em.GetComponentData<CarCurrentLane>(car);
            bool onOrphan = orphans.Contains(cl.m_Lane) || orphans.Contains(cl.m_ChangeLane);
            bool routed = false;
            if (em.HasBuffer<CarNavigationLane>(car)) foreach (var n in em.GetBuffer<CarNavigationLane>(car, true)) routed |= orphans.Contains(n.m_Lane);
            if (em.HasBuffer<PathElement>(car)) foreach (var p in em.GetBuffer<PathElement>(car, true)) routed |= orphans.Contains(p.m_Target);
            if (!onOrphan && !routed) return;

            if (onOrphan)
            {
                var orphan = orphans.Contains(cl.m_Lane) ? cl.m_Lane : cl.m_ChangeLane;
                var owner = em.HasComponent<Owner>(orphan) ? em.GetComponentData<Owner>(orphan).m_Owner : Entity.Null;
                float3 pos = em.GetComponentData<Game.Objects.Transform>(car).m_Position;
                Entity best = Entity.Null; float bestDistance = float.MaxValue, bestT = 0f;
                if (owner != Entity.Null && em.HasBuffer<SubLane>(owner))
                    foreach (var sl in em.GetBuffer<SubLane>(owner, true))
                    {
                        if (orphans.Contains(sl.m_SubLane) || !em.HasComponent<CarLane>(sl.m_SubLane) || !em.HasComponent<Curve>(sl.m_SubLane) || IsPedestrianStreet(em, sl.m_SubLane)) continue;
                        float d = MathUtils.Distance(em.GetComponentData<Curve>(sl.m_SubLane).m_Bezier, pos, out float t);
                        if (d < bestDistance) { best = sl.m_SubLane; bestDistance = d; bestT = t; }
                    }
                if (best == Entity.Null) { em.AddComponent<Deleted>(car); return; }
                cl.m_Lane = best;
                cl.m_ChangeLane = Entity.Null;
                cl.m_CurvePosition = new float3(bestT, bestT, 1f);
                em.SetComponentData(car, cl);
            }
            if (em.HasBuffer<CarNavigationLane>(car)) em.GetBuffer<CarNavigationLane>(car).Clear();
            if (em.HasComponent<PathOwner>(car))
            {
                var po = em.GetComponentData<PathOwner>(car);
                po.m_State |= PathFlags.Obsolete;
                em.SetComponentData(car, po);
            }
            Mod.log.Info($"Fahrzeug {car.Index}: von temporärer Spur aus dem Spielstand auf {(onOrphan ? "echte Spur gesetzt" : "neue Route")}");
        }
    }
}
