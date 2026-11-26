using Colossal.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Which buildings can reach each other by road: the road network split into connected areas (union-find over road
    /// segments). An island without a bridge is its own area. Used wherever we pick a station by straight-line distance,
    /// so a unit across the water is never sent. Built on demand, rebuilt when the number of roads changes or after
    /// RebuildFrames. Not in the update loop: other systems call it.
    /// ponytail: one-way streets and access restrictions are ignored; connected means "some road links them".
    /// </summary>
    public partial class RoadReachSystem : GameSystemBase
    {
        private EntityQuery m_Roads;
        private SimulationSystem m_Sim;
        private readonly Dictionary<Entity, Entity> m_Parent = new Dictionary<Entity, Entity>(); // node -> parent node
        private readonly Dictionary<Entity, Entity> m_EdgeNode = new Dictionary<Entity, Entity>(); // road segment -> one of its nodes
        private int m_Count = -1;
        private uint m_BuiltAt;
        private const uint RebuildFrames = 4096;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Roads = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate() { }

        /// <summary>True if <paramref name="a"/> and <paramref name="b"/> (buildings, or anything standing at one) are linked by road.
        /// Unknown (no road access found): true, so nothing is blocked by a guess.</summary>
        public bool Connected(Entity a, Entity b)
        {
            var x = AreaOf(a);
            var y = AreaOf(b);
            return x == Entity.Null || y == Entity.Null || x == y;
        }

        /// <summary>Road area of a building (its access road), Entity.Null if unknown.</summary>
        public Entity AreaOf(Entity building)
        {
            var em = EntityManager;
            for (int i = 0; i < 4 && building != Entity.Null; i++) // sub-buildings: their owner has the road access
            {
                if (em.TryGetComponent<Building>(building, out var bd) && bd.m_RoadEdge != Entity.Null)
                {
                    Ensure();
                    return m_EdgeNode.TryGetValue(bd.m_RoadEdge, out var node) ? Find(node) : Entity.Null;
                }
                building = em.TryGetComponent<Owner>(building, out var o) ? o.m_Owner : Entity.Null;
            }
            return Entity.Null;
        }

        private void Ensure()
        {
            int n = m_Roads.CalculateEntityCount();
            uint now = m_Sim.frameIndex;
            if (n == m_Count && now - m_BuiltAt < RebuildFrames) return;
            m_Count = n; m_BuiltAt = now;
            m_Parent.Clear(); m_EdgeNode.Clear();
            using var edges = m_Roads.ToEntityArray(Allocator.Temp);
            using var data = m_Roads.ToComponentDataArray<Edge>(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                m_EdgeNode[edges[i]] = data[i].m_Start;
                var ra = Find(data[i].m_Start);
                var rb = Find(data[i].m_End);
                if (ra != rb) m_Parent[ra] = rb;
            }
        }

        private Entity Find(Entity node)
        {
            var root = node;
            while (m_Parent.TryGetValue(root, out var p) && p != root) root = p;
            while (node != root && m_Parent.TryGetValue(node, out var p)) { m_Parent[node] = root; node = p; } // path compression
            if (!m_Parent.ContainsKey(root)) m_Parent[root] = root;
            return root;
        }
    }
}
