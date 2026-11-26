using Colossal.Entities;
using Colossal.UI.Binding;
using Game.UI.InGame;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;

namespace RealisticEMS
{
    /// <summary>
    /// Vanilla HealthcareSection shows the prefab's bed capacity. This replaces it and shows the bed slider value
    /// (StationCaps.m_Beds) instead when one is set. ponytail: private vanilla properties read via reflection, like VehicleStateSection.
    /// </summary>
    public partial class BedDisplaySection : HealthcareSection
    {
        private static readonly Dictionary<string, PropertyInfo> s_Props = new Dictionary<string, PropertyInfo>();
        private static int Get(object o, string prop)
        {
            if (!s_Props.TryGetValue(prop, out var p)) s_Props[prop] = p = typeof(HealthcareSection).GetProperty(prop, BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)p.GetValue(o);
        }

        /// <summary>Swaps the vanilla section in whichever section list holds it.</summary>
        public static void Install(World world)
        {
            var info = world.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            var mine = world.GetOrCreateSystemManaged<BedDisplaySection>();
            foreach (var f in typeof(SelectedInfoUISystem).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!(f.GetValue(info) is List<ISectionSource> list)) continue;
                int i = list.FindIndex(s => s?.GetType() == typeof(HealthcareSection));
                if (i >= 0) { list[i] = mine; return; }
            }
            Log.Warn("HealthcareSection not found, building panel shows the unmodified bed capacity");
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            int capacity = Get(this, "patientCapacity");
            if (EntityManager.TryGetComponent(selectedEntity, out StationCaps c) && c.m_Beds >= 0) capacity = c.m_Beds;
            writer.PropertyName("patientCount"); writer.Write(Get(this, "patientCount"));
            writer.PropertyName("patientCapacity"); writer.Write(capacity);
        }
    }
}
