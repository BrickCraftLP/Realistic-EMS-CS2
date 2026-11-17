using Colossal.UI.Binding;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Hospital = Game.Buildings.Hospital;
using Transform = Game.Objects.Transform;

namespace RealisticEMS
{
    /// <summary>
    /// Experimental "3D Beta" map (UI/src/mods/map3d-pro.tsx). Separate from the classic map (DispatchUISystem / map3d.tsx), which stays untouched.
    /// Does nothing unless Setting.EnableMap3dPro is on and the map is open, and only builds data when it cannot cost framerate (FrameBudget).
    /// </summary>
    public partial class Map3dProSystem : UISystemBase
    {
        private const string Group = "realisticEMS";
        private const float MapMin = -7168f, MapSize = 14336f;
        public const int WaterN = 512; // 28 m cells; the UI smooths edges with marching squares

        private EntityQuery m_Buildings, m_Trees, m_Roads, m_Parks;
        private SimulationSystem m_Sim;
        private WaterSystem m_Water;
        private TerrainSystem m_Terrain;
        private RawValueBinding m_Data;
        private ValueBinding<bool> m_Live;
        private ValueBinding<int> m_Status; // 0 closed, 1 waiting for framerate headroom, 2 building, 3 ready
        private bool m_Open, m_Pending, m_HasData;
        private float m_Dt = 1f / 60f; // smoothed unscaled frame time

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Water = World.GetOrCreateSystemManaged<WaterSystem>();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_Buildings = GetEntityQuery(ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Trees = GetEntityQuery(ComponentType.ReadOnly<Game.Objects.Tree>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Roads = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Curve>(), ComponentType.ReadOnly<Game.Net.Road>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_Parks = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.Park>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            AddBinding(m_Data = new RawValueBinding(Group, "m3d", Write));
            AddBinding(m_Status = new ValueBinding<int>(Group, "m3dStatus", 0));
            AddBinding(m_Live = new ValueBinding<bool>(Group, "m3dLive", false));
            AddBinding(new TriggerBinding(Group, "m3dOpen", () => { m_Open = true; m_Pending = true; m_Status.Update(2); }));
            AddBinding(new TriggerBinding(Group, "m3dClose", () => { m_Open = false; m_Pending = false; m_HasData = false; m_Data.Update(); m_Status.Update(0); }));
        }

        /// <summary>
        /// True when heavy work cannot hurt the framerate: simulation paused (building with sim off), or slow sim speed with plenty of frame headroom.
        /// ponytail: one-shot build once the gate opens; slice across frames if a huge city hitches even while paused.
        /// </summary>
        private bool FrameBudget => m_Sim.selectedSpeed == 0f || (m_Sim.selectedSpeed <= 1f && m_Dt < 0.7f / 60f);

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (!m_Open || !Mod.Setting.EnableMap3dPro) return; // off = zero cost
            m_Dt = math.lerp(m_Dt, UnityEngine.Time.unscaledDeltaTime, 0.05f);
            if (m_Live.value != FrameBudget) m_Live.Update(FrameBudget); // UI animates vehicles only while true
            if (!m_Pending) return;
            if (!FrameBudget) { if (m_Status.value != 1) m_Status.Update(1); return; }
            m_Pending = false;
            m_HasData = true;
            m_Data.Update(); // builds everything in Write
            m_Status.Update(3);
        }

        private int TreeCap => Mod.Setting.Map3dQuality switch { 1 => 4000, 3 => 60000, _ => 20000 };

        private void Write(IJsonWriter w)
        {
            w.TypeBegin("M3d");
            if (!m_HasData) { w.TypeEnd(); return; }
            var em = EntityManager;
            var heights = m_Terrain.GetHeightData();

            // Buildings: 7 ints x, z, halfW, halfD, height, yaw*100, kind (same layout as the classic map).
            w.PropertyName("b");
            using (var ents = m_Buildings.ToEntityArray(Allocator.Temp))
            {
                var list = new List<int>(ents.Length * 7);
                foreach (var e in ents)
                {
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!em.HasComponent<ObjectGeometryData>(prefab)) continue;
                    var size = em.GetComponentData<ObjectGeometryData>(prefab).m_Size;
                    var t = em.GetComponentData<Transform>(e);
                    var f = math.mul(t.m_Rotation, new float3(0, 0, 1));
                    int kind = !em.HasComponent<Hospital>(e) ? 0 : em.HasBuffer<Patient>(e) ? 1 : 2;
                    list.Add((int)t.m_Position.x); list.Add((int)t.m_Position.z);
                    list.Add(math.max(1, (int)(size.x / 2))); list.Add(math.max(1, (int)(size.z / 2)));
                    list.Add(math.max(3, (int)size.y));
                    list.Add((int)(math.degrees(math.atan2(f.x, f.z)) * 100)); list.Add(kind);
                }
                WriteInts(w, list);
            }

            // Trees: 4 ints x, z, canopy radius, height. Evenly thinned to the quality cap.
            w.PropertyName("t");
            using (var ents = m_Trees.ToEntityArray(Allocator.Temp))
            {
                int step = math.max(1, (ents.Length + TreeCap - 1) / TreeCap);
                var list = new List<int>(ents.Length / step * 4 + 4);
                for (int i = 0; i < ents.Length; i += step)
                {
                    var p = em.GetComponentData<Transform>(ents[i]).m_Position;
                    var prefab = em.GetComponentData<PrefabRef>(ents[i]).m_Prefab;
                    var size = em.HasComponent<ObjectGeometryData>(prefab) ? em.GetComponentData<ObjectGeometryData>(prefab).m_Size : new float3(6, 10, 6);
                    list.Add((int)p.x); list.Add((int)p.z);
                    list.Add(math.max(1, (int)(math.cmax(size.xz) / 2))); list.Add(math.max(2, (int)size.y));
                }
                WriteInts(w, list);
            }

            // Roads: 10 ints bezier a,b,c,d as x,z + width + elevation above terrain (bridges/overpasses).
            w.PropertyName("r");
            using (var ents = m_Roads.ToEntityArray(Allocator.Temp))
            {
                var list = new List<int>(ents.Length * 10);
                foreach (var e in ents)
                {
                    var b = em.GetComponentData<Game.Net.Curve>(e).m_Bezier;
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    float width = em.HasComponent<NetGeometryData>(prefab) ? em.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth : 8f;
                    var mid = Colossal.Mathematics.MathUtils.Position(b, 0.5f);
                    float elev = mid.y - TerrainUtils.SampleHeight(ref heights, mid);
                    list.Add((int)b.a.x); list.Add((int)b.a.z); list.Add((int)b.b.x); list.Add((int)b.b.z);
                    list.Add((int)b.c.x); list.Add((int)b.c.z); list.Add((int)b.d.x); list.Add((int)b.d.z);
                    list.Add((int)width); list.Add(math.max(0, (int)elev));
                }
                WriteInts(w, list);
            }

            // Ground fields, WaterN² chars row-major from (MapMin, MapMin): water depth '0'..'9' (metres, clamped) and green '0'/'1'.
            float cell = MapSize / WaterN;
            var water = m_Water.GetSurfaceData(out var deps);
            deps.Complete();
            var wc = new char[WaterN * WaterN];
            for (int z = 0; z < WaterN; z++)
                for (int x = 0; x < WaterN; x++)
                {
                    var pos = new float3(MapMin + (x + 0.5f) * cell, 0, MapMin + (z + 0.5f) * cell);
                    wc[z * WaterN + x] = (char)('0' + math.clamp((int)WaterUtils.SampleDepth(ref water, pos), 0, 9));
                }
            w.PropertyName("w"); w.Write(new string(wc));

            var gc = new char[WaterN * WaterN];
            for (int i = 0; i < gc.Length; i++) gc[i] = '0';
            int Cell(float x, float z) { int cx = (int)((x - MapMin) / cell), cz = (int)((z - MapMin) / cell); return cx < 0 || cz < 0 || cx >= WaterN || cz >= WaterN ? -1 : cz * WaterN + cx; }
            using (var parks = m_Parks.ToEntityArray(Allocator.Temp))
                foreach (var e in parks)
                {
                    var p = em.GetComponentData<Transform>(e).m_Position;
                    var pf = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    float r = em.HasComponent<ObjectGeometryData>(pf) ? math.cmax(em.GetComponentData<ObjectGeometryData>(pf).m_Size.xz) / 2 : cell;
                    for (float x = p.x - r; x <= p.x + r; x += cell / 2)
                        for (float z = p.z - r; z <= p.z + r; z += cell / 2) { int c = Cell(x, z); if (c >= 0) gc[c] = '1'; }
                }
            // Dense tree areas read as lawn/forest floor under the individual trees.
            var count = new byte[gc.Length];
            using (var tt = m_Trees.ToComponentDataArray<Transform>(Allocator.Temp))
                foreach (var t in tt) { int c = Cell(t.m_Position.x, t.m_Position.z); if (c >= 0 && count[c] < 255 && ++count[c] == 3) gc[c] = '1'; }
            w.PropertyName("g"); w.Write(new string(gc));
            w.TypeEnd();
        }

        private static void WriteInts(IJsonWriter w, List<int> list)
        {
            w.ArrayBegin((uint)list.Count);
            foreach (var v in list) w.Write(v);
            w.ArrayEnd();
        }
    }
}
