using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
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

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CarQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>());
        }

        protected override void OnUpdate()
        {
            if (!m_Added) { if (!m_CarQuery.IsEmpty) AddNef(); return; }
            if (m_Stripped || m_Nef == null || !m_PrefabSystem.TryGetEntity(m_Nef, out var e)) return;
            // Initialized (it has its archetype by now): hide it from vanilla vehicle selection.
            if (!EntityManager.HasComponent<ObjectData>(e)) return;
            if (EntityManager.HasComponent<AmbulanceData>(e)) EntityManager.RemoveComponent<AmbulanceData>(e);
            NefPrefab = e;
            m_Stripped = true;
            Mod.log.Info($"NEF prefab ready (entity {e.Index})");
        }

        private void AddNef()
        {
            m_Added = true;
            CarPrefab ambulance = null, police = null, tintPolice = null, tintCar = null;
            using var cars = m_CarQuery.ToEntityArray(Allocator.Temp);
            foreach (var c in cars)
            {
                if (!m_PrefabSystem.TryGetPrefab(c, out PrefabBase p) || !(p is CarPrefab car)) continue;
                if (car.name == NefName) { m_Nef = car; return; }
                if (ambulance == null && car.Has<AmbulancePrefab>()) ambulance = car;
                if (car.Has<PoliceCarPrefab>())
                {
                    police ??= car;
                    if (tintPolice == null && Tintable(car)) tintPolice = car;
                }
                else if (tintCar == null && car.Has<PersonalCar>() && Tintable(car)) tintCar = car;
            }
            if (ambulance == null || police == null) { Mod.log.Error($"NEF: no template (ambulance={ambulance?.name}, police={police?.name})"); return; }

            // NEF colours need a body whose mesh supports tint (ColorProperties -> MeshColor/CustomMeshColor).
            // Police liveries may be baked into the texture; then a tintable passenger car body is used instead.
            var body = tintPolice ?? tintCar ?? police;
            Mod.log.Info($"NEF body: {body.name} ({(body == tintPolice ? "tintable police car" : body == tintCar ? "no tintable police car, using passenger car" : "nothing tintable, colours stay vanilla")})");

            var nef = (CarPrefab)ambulance.Clone(NefName);
            nef.Remove<ObsoleteIdentifiers>();
            nef.m_Meshes = body.m_Meshes;
            if (police.TryGet(out EffectSource policeLights)) // light bar positions match the police body
            {
                nef.Remove<EffectSource>();
                nef.AddComponentFrom(policeLights);
            }
            nef.GetComponent<AmbulancePrefab>().m_PatientCapacity = 0;
            m_PrefabSystem.AddPrefab(nef);
            m_Nef = nef;
            Mod.log.Info($"NEF prefab added: ambulance logic from {ambulance.name}, body from {body.name}, lights from {police.name}");
        }

        private static bool Tintable(CarPrefab car)
        {
            if (car.m_Meshes == null) return false;
            foreach (var m in car.m_Meshes) if (m?.m_Mesh != null && m.m_Mesh.Has<ColorProperties>()) return true;
            return false;
        }
    }
}
