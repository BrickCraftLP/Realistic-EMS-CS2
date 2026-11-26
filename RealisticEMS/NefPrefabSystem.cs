using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using AmbulancePrefab = Game.Prefabs.Ambulance;
using PoliceCarPrefab = Game.Prefabs.PoliceCar;

namespace RealisticEMS
{
    /// <summary>
    /// Experimental emergency doctor car (NEF). Technically an ambulance (so vanilla AmbulanceAISystem drives it:
    /// routes, lights, waiting on scene, returning) with the body and light effects of a vanilla police car.
    /// After initialization AmbulanceData is removed from the prefab, so HealthcareVehicleSelectData (which needs
    /// AmbulanceData) never picks it as a regular ambulance. Only NefDispatchSystem spawns it.
    /// </summary>
    public partial class NefPrefabSystem : GameSystemBase
    {
        public const string NefName = "RealisticEMS_NEF";
        public static Entity NefPrefab = Entity.Null;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_CarQuery;
        private PrefabBase m_Nef;
        private bool m_Added, m_Stripped;
        private readonly System.Collections.Generic.List<PrefabBase> m_Custom = new System.Collections.Generic.List<PrefabBase>(); // installed custom NEF models, hidden from vanilla

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CarQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>());
        }

        protected override void OnUpdate()
        {
            if (!m_Added) { if (!m_CarQuery.IsEmpty) AddNef(); return; }
            for (int i = m_Custom.Count - 1; i >= 0; i--)
            {
                if (!m_PrefabSystem.TryGetEntity(m_Custom[i], out var c) || !EntityManager.HasComponent<ObjectData>(c)) continue;
                if (EntityManager.HasComponent<AmbulanceData>(c)) EntityManager.RemoveComponent<AmbulanceData>(c);
                m_Custom.RemoveAt(i);
            }
            if (m_Stripped || m_Nef == null || !m_PrefabSystem.TryGetEntity(m_Nef, out var e)) return;
            // Initialized (it has its archetype by now): hide it from vanilla vehicle selection.
            if (!EntityManager.HasComponent<ObjectData>(e)) return;
            if (EntityManager.HasComponent<AmbulanceData>(e)) EntityManager.RemoveComponent<AmbulanceData>(e);
            NefPrefab = e;
            m_Stripped = true;
            Log.Info($"NEF prefab ready (entity {e.Index})");
        }

        private void AddNef()
        {
            m_Added = true;
            CarPrefab ambulance = null, police = null, tintPolice = null, tintCar = null;
            using var cars = m_CarQuery.ToEntityArray(Allocator.Temp);
            foreach (var c in cars)
            {
                if (!m_PrefabSystem.TryGetPrefab(c, out PrefabBase p) || !(p is CarPrefab car)) continue;
                // Installed custom NEF models are never used: only the default (cloned) NEF spawns. They are hidden
                // from vanilla vehicle selection too (see OnUpdate), or they would drive as regular ambulances.
                if (car.name == NefName || car.name == "NEFVan") { m_Custom.Add(car); Log.Info($"NEF: ignoring custom model {car.name}"); continue; }
                if (car.Has<Game.Prefabs.MaintenanceVehicle>()) DumpLights(car); // fire/crash diversion: find the arrow board lights
                if (ambulance == null && car.Has<AmbulancePrefab>()) ambulance = car;
                if (car.Has<PoliceCarPrefab>())
                {
                    police ??= car;
                    if (tintPolice == null && Tintable(car)) tintPolice = car;
                }
                else if (tintCar == null && car.Has<PersonalCar>() && Tintable(car)) tintCar = car;
            }
            if (ambulance == null || police == null) { Log.Error($"NEF: no template (ambulance={ambulance?.name}, police={police?.name})"); return; }

            // NEF colours need a body whose mesh supports tint (ColorProperties -> MeshColor/CustomMeshColor).
            // Police liveries may be baked into the texture; then a tintable passenger car body is used instead.
            var body = tintPolice ?? tintCar ?? police;
            Log.Info($"NEF body: {body.name} ({(body == tintPolice ? "tintable police car" : body == tintCar ? "no tintable police car, using passenger car" : "nothing tintable, colours stay vanilla")})");

            // A custom asset may already own NefName.
            var nef = (CarPrefab)ambulance.Clone(m_Custom.Exists(c => c.name == NefName) ? NefName + "_Default" : NefName);
            nef.Remove<ObsoleteIdentifiers>();
            nef.m_Meshes = body.m_Meshes;
            if (police.TryGet(out EffectSource policeLights)) // light bar positions match the police body
            {
                nef.Remove<EffectSource>();
                nef.AddComponentFrom(policeLights);
                ApplyFlashPattern(nef.GetComponent<EffectSource>());
            }
            nef.GetComponent<AmbulancePrefab>().m_PatientCapacity = 0;
            m_PrefabSystem.AddPrefab(nef);
            m_Nef = nef;
            Log.Info($"NEF prefab added: ambulance logic from {ambulance.name}, body from {body.name}, lights from {police.name}");
        }

        /// <summary>Own copies of the police light settings (the clone may share them), left bars/flashers (x&lt;0) and
        /// right ones (x&gt;=0) get the same double-flash, shifted by half a cycle: alternating left/right.</summary>
        // ponytail: side is decided by x offset only; split bars/flashers by effect type if they need different patterns.
        private static void ApplyFlashPattern(EffectSource src)
        {
            const float Side = 0.05f; // m off the centre line
            int left = 0, right = 0;
            foreach (var e in src.m_Effects)
            {
                Log.Debug($"NEF light effect {e.m_Effect?.name}: x={e.m_PositionOffset.x:0.00} anim={e.m_AnimationIndex}");
                if (e.m_PositionOffset.x < -Side) left++; else if (e.m_PositionOffset.x > Side) right++;
            }
            // Lights on the centre line (one light bar effect) cannot be split by side: one curve for all would make them
            // flash together, so keep the police car's own (alternating) animation.
            if (left == 0 || right == 0)
            {
                Log.Info($"NEF lights: {left} left, {right} right, keeping police flash pattern ({src.m_AnimationCurves?.Count ?? 0} curves)");
                return;
            }
            var effects = new List<EffectSource.EffectSettings>();
            foreach (var e in src.m_Effects)
                effects.Add(new EffectSource.EffectSettings
                {
                    m_Effect = e.m_Effect, m_PositionOffset = e.m_PositionOffset, m_Rotation = e.m_Rotation, m_Scale = e.m_Scale,
                    m_Intensity = e.m_Intensity, m_ParentMesh = e.m_ParentMesh,
                    // 0 = left, 1 = right; centre lights keep their vanilla index, mapped onto the two new curves
                    m_AnimationIndex = e.m_PositionOffset.x < -Side ? 0 : e.m_PositionOffset.x > Side ? 1 : e.m_AnimationIndex < 0 ? -1 : e.m_AnimationIndex % 2
                });
            src.m_Effects = effects;
            src.m_AnimationCurves = new List<EffectSource.AnimationProperties>
            {
                new EffectSource.AnimationProperties { m_Duration = 1f, m_Curve = Curve(0f) },
                new EffectSource.AnimationProperties { m_Duration = 1f, m_Curve = Curve(0.5f) },
            };
        }

        private static AnimationCurve Curve(float phase)
        {
            var keys = new List<Keyframe>();
            for (int i = 0; i < 4; i++) // on, off, on, off within a quarter cycle each
            {
                float t = Mathf.Repeat(phase + i * 0.0625f, 1f);
                keys.Add(new Keyframe(t, i % 2 == 0 ? 1f : 0f, float.PositiveInfinity, float.PositiveInfinity)); // stepped
            }
            keys.Add(new Keyframe(Mathf.Repeat(phase + 0.25f, 1f), 0f, float.PositiveInfinity, float.PositiveInfinity));
            keys.Sort((a, b) => a.time.CompareTo(b.time));
            if (keys[0].time > 0f) keys.Insert(0, new Keyframe(0f, 0f, float.PositiveInfinity, float.PositiveInfinity)); // else the game holds the first key (on) before it
            return new AnimationCurve(keys.ToArray()) { preWrapMode = WrapMode.Loop, postWrapMode = WrapMode.Loop };
        }

        /// <summary>Imported NEF model: its blue light materials have purpose Emergency1 (left) / Emergency2 (right) but
        /// no animation, so they glow steadily. Give them the alternating double-flash and re-initialize the mesh.</summary>
        private void FlashEmissive(CarPrefab car)
        {
            if (car.m_Meshes == null) return;
            foreach (var mi in car.m_Meshes)
            {
                if (mi?.m_Mesh == null || !mi.m_Mesh.TryGet(out EmissiveProperties em) || em.m_SingleLights == null) continue;
                em.m_AnimationCurves = new List<EmissiveProperties.AnimationProperties>
                {
                    new EmissiveProperties.AnimationProperties { m_Duration = 1f, m_Curve = Curve(0f) },
                    new EmissiveProperties.AnimationProperties { m_Duration = 1f, m_Curve = Curve(0.5f) },
                };
                int n = 0;
                foreach (var l in em.m_SingleLights)
                {
                    if (l.purpose == EmissiveProperties.Purpose.Emergency1) { l.animationIndex = 0; n++; }
                    else if (l.purpose == EmissiveProperties.Purpose.Emergency2) { l.animationIndex = 1; n++; }
                }
                if (m_PrefabSystem.TryGetEntity(mi.m_Mesh, out var e)) m_PrefabSystem.UpdatePrefab(mi.m_Mesh, Entity.Null);
                Log.Info($"NEF: flash pattern on {n} emissive lights of {mi.m_Mesh.name}");
            }
        }

        /// <summary>Logs every emissive light and light effect of a vehicle (purpose, position, animation), to find which
        /// of the road maintenance truck's lights form the arrow board and if left/right can be driven separately.</summary>
        private static void DumpLights(CarPrefab car)
        {
            if (car.m_Meshes == null) return;
            foreach (var mi in car.m_Meshes)
            {
                if (mi?.m_Mesh == null || !mi.m_Mesh.TryGet(out EmissiveProperties em)) continue;
                if (em.m_SingleLights != null)
                    for (int i = 0; i < em.m_SingleLights.Count; i++)
                    {
                        var l = em.m_SingleLights[i];
                        Log.Info($"light_dump car={car.name} mesh={mi.m_Mesh.name} single[{i}] purpose={l.purpose} anim={l.animationIndex} intensity={l.intensity}");
                    }
                if (em.m_MultiLights != null)
                    for (int i = 0; i < em.m_MultiLights.Count; i++)
                        Log.Info($"light_dump car={car.name} mesh={mi.m_Mesh.name} multi[{i}] purpose={em.m_MultiLights[i].purpose} layer={em.m_MultiLights[i].layerId}");
                Log.Info($"light_dump car={car.name} mesh={mi.m_Mesh.name} curves={em.m_AnimationCurves?.Count ?? 0}");
            }
            if (car.TryGet(out EffectSource src) && src.m_Effects != null)
                foreach (var e in src.m_Effects)
                    Log.Info($"light_dump car={car.name} effect={e.m_Effect?.name} pos={e.m_PositionOffset} anim={e.m_AnimationIndex}");
        }

        private static bool Tintable(CarPrefab car)
        {
            if (car.m_Meshes == null) return false;
            foreach (var m in car.m_Meshes) if (m?.m_Mesh != null && m.m_Mesh.Has<ColorProperties>()) return true;
            return false;
        }
    }
}
