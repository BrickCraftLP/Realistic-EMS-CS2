using Colossal.Entities;
using Colossal.UI.Binding;
using Game.UI.InGame;
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Ambulance = Game.Vehicles.Ambulance;
using OwnedVehicle = Game.Vehicles.OwnedVehicle;
using PoliceCar = Game.Vehicles.PoliceCar;

namespace RealisticEMS
{
    /// <summary>
    /// The vanilla building panel's vehicle list knows no "on scene" for ambulances (always Dispatched) and shows our
    /// responder police cars as Dispatched too. This replaces the vanilla VehiclesSection in SelectedInfoUISystem and
    /// writes the state key "OnScene" (locale SelectedInfoPanel.VEHICLE_STATES[OnScene], Setting.cs) for those.
    /// ponytail: vanilla's private fields are read via reflection; breaks loudly (log) if a game update renames them.
    /// </summary>
    public partial class VehicleStateSection : VehiclesSection
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static T Get<T>(object o, string prop) => (T)typeof(VehiclesSection).GetProperty(prop, Private).GetValue(o);

        /// <summary>Swaps the vanilla section for this one. Call once after SelectedInfoUISystem exists.</summary>
        public static void Install(World world)
        {
            try
            {
                var info = world.GetOrCreateSystemManaged<SelectedInfoUISystem>();
                var sections = (List<ISectionSource>)typeof(SelectedInfoUISystem).GetField("m_MiddleSections", Private).GetValue(info);
                int i = sections.FindIndex(s => s?.GetType() == typeof(VehiclesSection));
                if (i >= 0) sections[i] = world.GetOrCreateSystemManaged<VehicleStateSection>();
                else Mod.log.Warn("VehiclesSection not found, building panel shows vanilla vehicle states");
            }
            catch (Exception ex) { Mod.log.Error(ex, "VehicleStateSection.Install failed"); }
        }

        // Saves from before AmbulanceReturnSystem.SetOwner can hold destroyed vehicles in a depot's buffer; vanilla's
        // AddVehicle crashes on them. Drop them before vanilla reads the buffer.
        protected override void OnUpdate()
        {
            if (EntityManager.HasBuffer<OwnedVehicle>(selectedEntity))
            {
                var owned = EntityManager.GetBuffer<OwnedVehicle>(selectedEntity);
                for (int i = owned.Length - 1; i >= 0; i--)
                    if (!EntityManager.Exists(owned[i].m_Vehicle)) owned.RemoveAt(i);
            }
            base.OnUpdate();
        }

        private bool OnScene(Entity e)
        {
            var em = EntityManager;
            if (em.TryGetComponent<Ambulance>(e, out var a)) return DispatchUISystem.Status(em, e, a, false) == "am Einsatzort";
            // Vanilla already says AccidentSite/CrimeScene for its own police calls; only our responders need it.
            return em.TryGetComponent<PoliceCar>(e, out var p) && p.m_PurposeMask == 0 && DispatchUISystem.PoliceStatus(em, e, p, false) == "am Einsatzort";
        }

        // Same output as VehiclesSection.OnWriteProperties/BindVehicle, except the stateKey.
        public override void OnWriteProperties(IJsonWriter writer)
        {
            var list = Get<NativeList<UIVehicle>>(this, "vehicleList");
            list.Sort();
            writer.PropertyName("vehicleKey"); writer.Write(Get<VehicleLocaleKey>(this, "vehicleKey").ToString());
            writer.PropertyName("vehicleCount"); writer.Write(Get<int>(this, "vehicleCount"));
            writer.PropertyName("availableVehicleCount"); writer.Write(Get<int>(this, "availableVehicleCount"));
            writer.PropertyName("vehicleCapacity"); writer.Write(Get<int>(this, "vehicleCapacity"));
            writer.PropertyName("vehicleList"); writer.ArrayBegin(list.Length);
            foreach (var v in list)
            {
                writer.TypeBegin("Game.UI.InGame.VehiclesSection.Vehicle");
                writer.PropertyName("entity"); writer.Write(v.entity);
                writer.PropertyName("name"); m_NameSystem.BindName(writer, v.entity);
                writer.PropertyName("vehicleKey"); writer.Write(v.vehicleKey.ToString());
                writer.PropertyName("stateKey"); writer.Write(OnScene(v.entity) ? "OnScene" : v.stateKey.ToString());
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }
    }
}
