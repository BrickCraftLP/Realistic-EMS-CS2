using Colossal.UI.Binding;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using System;
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
    /// Does nothing unless Setting.EnableMap3dPro is on and the map is open. Builds one layer per frame, only while it cannot cost framerate (FrameBudget).
    /// Layers stay cached across close/reopen (the UI keeps its decoded copy too) and refresh lazily every RefreshSeconds.
    /// </summary>
    public partial class Map3dProSystem : UISystemBase
    {
        private const string Group = "realisticEMS";
        private const float MapMin = -7168f, MapSize = 14336f;
        public const int WaterN = 512; // 28 m cells; the UI smooths edges with marching squares
        private const float CellSize = MapSize / WaterN;
        private const int Layers = 5;
        private const float RefreshSeconds = 60f;

        private EntityQuery m_Buildings, m_Trees, m_Roads, m_Parks;
        private SimulationSystem m_Sim;
        private WaterSystem m_Water;
        private TerrainSystem m_Terrain;
        private ComponentLookup<ObjectGeometryData> m_ObjGeo;
        private ComponentLookup<NetGeometryData> m_NetGeo;
        private ComponentLookup<Hospital> m_Hospital;
        private BufferLookup<Patient> m_Patient;
        // One string per layer (int16 base64 / char fields). A giant JSON number array crashed cohtml in TypeEnd.
        // ValueBinding skips re-sending a layer that did not change.
        private ValueBinding<string> m_B, m_T, m_R, m_W, m_G;
        private ValueBinding<bool> m_Live;
        private ValueBinding<int> m_Status; // 0 closed, 1 waiting for framerate headroom, 2 building, 3 ready
        private bool m_Open;
        private int m_Next = Layers; // next layer to rebuild; Layers = idle
        private float m_Idle;
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
            m_ObjGeo = GetComponentLookup<ObjectGeometryData>(true);
            m_NetGeo = GetComponentLookup<NetGeometryData>(true);
            m_Hospital = GetComponentLookup<Hospital>(true);
            m_Patient = GetBufferLookup<Patient>(true);
            AddBinding(m_B = new ValueBinding<string>(Group, "m3dB", ""));
            AddBinding(m_T = new ValueBinding<string>(Group, "m3dT", ""));
            AddBinding(m_R = new ValueBinding<string>(Group, "m3dR", ""));
            AddBinding(m_W = new ValueBinding<string>(Group, "m3dW", ""));
            AddBinding(m_G = new ValueBinding<string>(Group, "m3dG", ""));
            AddBinding(m_Status = new ValueBinding<int>(Group, "m3dStatus", 0));
            AddBinding(m_Live = new ValueBinding<bool>(Group, "m3dLive", false));
            AddBinding(new TriggerBinding(Group, "m3dOpen", () => { m_Open = true; m_Next = 0; m_Idle = 0; }));
            AddBinding(new TriggerBinding(Group, "m3dClose", () => { m_Open = false; m_Next = Layers; m_Status.Update(0); })); // keeps the cached layers
        }

        /// <summary>True when heavy work cannot hurt the framerate: simulation paused, or slow sim speed with plenty of frame headroom.</summary>
        private bool FrameBudget => m_Sim.selectedSpeed == 0f || (m_Sim.selectedSpeed <= 1f && m_Dt < 0.7f / 60f);

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (!m_Open || !Mod.Setting.EnableMap3dPro) return; // off = zero cost
            m_Dt = math.lerp(m_Dt, UnityEngine.Time.unscaledDeltaTime, 0.05f);
            if (m_Live.value != FrameBudget) m_Live.Update(FrameBudget); // UI animates vehicles only while true
            if (m_Next >= Layers)
            {
                if ((m_Idle += UnityEngine.Time.unscaledDeltaTime) < RefreshSeconds) return;
                m_Idle = 0; m_Next = 0; // lazy background refresh
            }
            if (!FrameBudget) { if (m_Status.value != 1) m_Status.Update(1); return; }
            if (m_Status.value != 2) m_Status.Update(2);
            switch (m_Next++) // one layer per frame so a huge city never does everything in one frame
            {
                case 0: m_B.Update(BuildBuildings()); break;
                case 1: m_T.Update(BuildTrees()); break;
                case 2: m_R.Update(BuildRoads()); break;
                case 3: m_W.Update(BuildWater()); break;
                case 4: m_G.Update(BuildGreen()); break;
            }
            if (m_Next >= Layers) m_Status.Update(3);
        }

        private int TreeCap => Mod.Setting.Map3dQuality switch { 1 => 4000, 3 => 60000, _ => 20000 };
        private static short S(float v) => (short)math.clamp((int)v, short.MinValue, short.MaxValue);

        /// <summary>int16 little-endian, base64. Decoded by unpack() in map3d-pro.tsx.</summary>
        private static string Pack(List<short> list)
        {
            var bytes = new byte[list.Count * 2];
            Buffer.BlockCopy(list.ToArray(), 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        // Buildings: 7 shorts x, z, halfW, halfD, height, yaw*100, kind (0 generic, 1 hospital, 2 depot).
        private string BuildBuildings()
        {
            m_ObjGeo.Update(this); m_Hospital.Update(this); m_Patient.Update(this);
            using var ents = m_Buildings.ToEntityArray(Allocator.Temp);
            using var tr = m_Buildings.ToComponentDataArray<Transform>(Allocator.Temp);
            using var pr = m_Buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var list = new List<short>(ents.Length * 7);
            for (int i = 0; i < ents.Length; i++)
            {
                if (!m_ObjGeo.TryGetComponent(pr[i].m_Prefab, out var geo)) continue;
                var size = geo.m_Size; var t = tr[i]; var e = ents[i];
                var f = math.mul(t.m_Rotation, new float3(0, 0, 1));
                int kind = !m_Hospital.HasComponent(e) ? 0 : m_Patient.HasBuffer(e) ? 1 : 2;
                list.Add(S(t.m_Position.x)); list.Add(S(t.m_Position.z));
                list.Add(S(math.max(1, size.x / 2))); list.Add(S(math.max(1, size.z / 2)));
                list.Add(S(math.max(3, size.y)));
                list.Add(S(math.degrees(math.atan2(f.x, f.z)) * 100)); list.Add((short)kind);
            }
            return Pack(list);
        }

        // Trees: 4 shorts x, z, canopy radius, height. Evenly thinned to the quality cap.
        private string BuildTrees()
        {
            m_ObjGeo.Update(this);
            using var tr = m_Trees.ToComponentDataArray<Transform>(Allocator.Temp);
            using var pr = m_Trees.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            int step = math.max(1, (tr.Length + TreeCap - 1) / TreeCap);
            var list = new List<short>(tr.Length / step * 4 + 4);
            for (int i = 0; i < tr.Length; i += step)
            {
                var p = tr[i].m_Position;
                var size = m_ObjGeo.TryGetComponent(pr[i].m_Prefab, out var geo) ? geo.m_Size : new float3(6, 10, 6);
                list.Add(S(p.x)); list.Add(S(p.z));
                list.Add(S(math.max(1, math.cmax(size.xz) / 2))); list.Add(S(math.max(2, size.y)));
            }
            return Pack(list);
        }

        // Roads: 10 shorts bezier a,b,c,d as x,z + width + elevation above terrain (bridges/overpasses).
        private string BuildRoads()
        {
            m_NetGeo.Update(this);
            var heights = m_Terrain.GetHeightData();
            using var cv = m_Roads.ToComponentDataArray<Game.Net.Curve>(Allocator.Temp);
            using var pr = m_Roads.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var list = new List<short>(cv.Length * 10);
            for (int i = 0; i < cv.Length; i++)
            {
                var b = cv[i].m_Bezier;
                float width = m_NetGeo.TryGetComponent(pr[i].m_Prefab, out var ng) ? ng.m_DefaultWidth : 8f;
                var mid = Colossal.Mathematics.MathUtils.Position(b, 0.5f);
                float elev = mid.y - TerrainUtils.SampleHeight(ref heights, mid);
                list.Add(S(b.a.x)); list.Add(S(b.a.z)); list.Add(S(b.b.x)); list.Add(S(b.b.z));
                list.Add(S(b.c.x)); list.Add(S(b.c.z)); list.Add(S(b.d.x)); list.Add(S(b.d.z));
                list.Add(S(width)); list.Add(S(math.max(0, elev)));
            }
            return Pack(list);
        }

        // Ground fields, WaterN² chars row-major from (MapMin, MapMin): water depth '0'..'9' (metres, clamped) and green '0'/'1'.
        private string BuildWater()
        {
            var water = m_Water.GetSurfaceData(out var deps);
            deps.Complete();
            var wc = new char[WaterN * WaterN];
            for (int z = 0; z < WaterN; z++)
                for (int x = 0; x < WaterN; x++)
                {
                    var pos = new float3(MapMin + (x + 0.5f) * CellSize, 0, MapMin + (z + 0.5f) * CellSize);
                    wc[z * WaterN + x] = (char)('0' + math.clamp((int)WaterUtils.SampleDepth(ref water, pos), 0, 9));
                }
            return new string(wc);
        }

        private string BuildGreen()
        {
            m_ObjGeo.Update(this);
            var gc = new char[WaterN * WaterN];
            for (int i = 0; i < gc.Length; i++) gc[i] = '0';
            int Cell(float x, float z) { int cx = (int)((x - MapMin) / CellSize), cz = (int)((z - MapMin) / CellSize); return cx < 0 || cz < 0 || cx >= WaterN || cz >= WaterN ? -1 : cz * WaterN + cx; }
            using (var tr = m_Parks.ToComponentDataArray<Transform>(Allocator.Temp))
            using (var pr = m_Parks.ToComponentDataArray<PrefabRef>(Allocator.Temp))
                for (int i = 0; i < tr.Length; i++)
                {
                    var p = tr[i].m_Position;
                    float r = m_ObjGeo.TryGetComponent(pr[i].m_Prefab, out var geo) ? math.cmax(geo.m_Size.xz) / 2 : CellSize;
                    for (float x = p.x - r; x <= p.x + r; x += CellSize / 2)
                        for (float z = p.z - r; z <= p.z + r; z += CellSize / 2) { int c = Cell(x, z); if (c >= 0) gc[c] = '1'; }
                }
            // Dense tree areas read as lawn/forest floor under the individual trees.
            var count = new byte[gc.Length];
            using (var tt = m_Trees.ToComponentDataArray<Transform>(Allocator.Temp))
                foreach (var t in tt) { int c = Cell(t.m_Position.x, t.m_Position.z); if (c >= 0 && count[c] < 255 && ++count[c] == 3) gc[c] = '1'; }
            return new string(gc);
        }
    }
}
