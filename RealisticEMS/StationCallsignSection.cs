using Colossal.Entities;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game.UI.InGame;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticEMS
{
    /// <summary>Player-set station callsign (1-4 of A-Z/0-9), packed as 4 ASCII bytes so it saves with the city.</summary>
    public struct StationCallsign : IComponentData, ISerializable
    {
        public uint m_Packed;

        public static bool TryPack(string s, out uint packed)
        {
            packed = 0;
            s = (s ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0 || s.Length > 4 || !s.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            for (int i = 0; i < s.Length; i++) packed |= (uint)s[i] << (8 * i);
            return true;
        }

        public override string ToString()
        {
            var chars = new List<char>();
            for (int i = 0; i < 4; i++) { var c = (char)((m_Packed >> (8 * i)) & 0xFF); if (c != 0) chars.Add(c); }
            return new string(chars.ToArray());
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(m_Packed);
        public void Deserialize<TReader>(TReader reader) where TReader : IReader => reader.Read(out m_Packed);
    }

    /// <summary>
    /// Building-panel section for hospitals, ambulance depots, police and fire stations: edit the station callsign.
    /// Vehicles of the station are then named "RTW {callsign}-01" etc. Empty input clears it (back to the number).
    /// Fire stations (with Setting.FireStationAmbulances) and depots also get the ambulance slider (StationFleetSystem);
    /// hospitals get bed and doctor car sliders, police stations a patrol car slider (StationCaps).
    /// Rendered by UI/src/mods/station-section.tsx.
    /// </summary>
    public partial class StationCallsignSection : InfoSectionBase
    {
        protected override string group => nameof(StationCallsignSection);
        private string m_Callsign = "";
        private int m_Slots, m_Ambulances; // m_Slots 0 = no slider
        private bool m_Fire, m_Livery;

        public static void Install(World world, List<ISectionSource> sections) =>
            sections.Insert(0, world.GetOrCreateSystemManaged<StationCallsignSection>());

        public static bool IsStation(EntityManager em, Entity e) =>
            em.HasComponent<Game.Buildings.Hospital>(e) || em.HasComponent<Game.Buildings.PoliceStation>(e) || em.HasComponent<Game.Buildings.FireStation>(e);

        /// <summary>The station's callsign, or null if none set.</summary>
        public static string Of(EntityManager em, Entity station) =>
            em.TryGetComponent<StationCallsign>(station, out var c) ? c.ToString() : null;

        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(new TriggerBinding<string>(group, "set", Set));
            AddBinding(new TriggerBinding<int>(group, "setAmbulances", SetAmbulances));
            AddBinding(new TriggerBinding<bool>(group, "setLivery", SetLivery));
            AddBinding(new TriggerBinding<int>(group, "setBeds", v => SetCap(v, BedMax, 0)));
            AddBinding(new TriggerBinding<int>(group, "setNef", v => SetCap(v, NefMax, 1)));
            AddBinding(new TriggerBinding<int>(group, "setCars", v => SetCap(v, CarMax, 2)));
            AddBinding(new TriggerBinding(group, "heliCritical", HeliCritical));
        }

        // Debug: one patient of this hospital critical -> rescue helicopter transfer (HelicopterSystem.DebugCriticalTransfer).
        private bool m_HeliDebug;
        private string m_HeliStatus = "";
        private Entity m_HeliStatusFor;

        private void HeliCritical()
        {
            var e = selectedEntity;
            if (!DispatchUISystem.DebugUnlocked || !EntityManager.Exists(e) || !IsHospital(e)) return;
            m_HeliStatus = World.GetOrCreateSystemManaged<HelicopterSystem>().DebugCriticalTransfer(e);
            m_HeliStatusFor = e;
            Mod.log.Info($"Debug RTH Spital {e.Index}: {m_HeliStatus}");
            RequestUpdate();
        }

        private void SetAmbulances(int value)
        {
            var e = selectedEntity;
            int slots = EntityManager.Exists(e) ? Slots(e) : 0;
            if (slots == 0) return;
            EntityManager.AddComponentData(e, new StationFleet { m_Ambulances = math.clamp(value, 0, slots) });
            RequestUpdate();
        }

        // Hospital (has patients): bed + doctor car sliders; police station: patrol car slider. Max 0 = no slider.
        private bool IsHospital(Entity e) => EntityManager.HasComponent<Game.Buildings.Hospital>(e) && EntityManager.HasBuffer<Game.Buildings.Patient>(e);
        private int BedMax(Entity e) => IsHospital(e) ? StationFleetSystem.BedMax(EntityManager, e) : 0;
        private int NefMax(Entity e) => IsHospital(e) && Mod.Setting.EnableNef && BedMax(e) > 0 ? StationFleetSystem.NefMax : 0;
        private int CarMax(Entity e) => EntityManager.HasComponent<Game.Buildings.PoliceStation>(e) ? StationFleetSystem.CarSlots(EntityManager, e) : 0;

        private void SetCap(int value, System.Func<Entity, int> max, int field)
        {
            var e = selectedEntity;
            int m = EntityManager.Exists(e) ? max(e) : 0;
            if (m <= 0) return;
            var c = EntityManager.TryGetComponent(e, out StationCaps old) ? old : StationCaps.Unset;
            value = math.clamp(value, 0, m);
            if (field == 0) c.m_Beds = value; else if (field == 1) c.m_Nef = value; else c.m_Cars = value;
            EntityManager.AddComponentData(e, c);
            RequestUpdate();
        }

        private int Cap(Entity e, int max, int field)
        {
            if (max <= 0) return 0;
            int v = EntityManager.TryGetComponent(e, out StationCaps c) ? (field == 0 ? c.m_Beds : field == 1 ? c.m_Nef : c.m_Cars) : -1;
            return v >= 0 ? math.min(v, max) : field == 0 ? math.min(StationFleetSystem.BedSlots(EntityManager, e), max) : field == 1 ? math.min(Mod.Setting.NefPerHospital, max) : max;
        }

        private void SetLivery(bool on)
        {
            var e = selectedEntity;
            if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Game.Buildings.FireStation>(e)) return;
            if (on) EntityManager.AddComponentData(e, new FireLivery { m_On = 1 });
            else EntityManager.RemoveComponent<FireLivery>(e);
            RequestUpdate();
        }

        /// <summary>Slider size: fire engine slots of a fire station (when it has ambulances), ambulance slots of a depot, else 0.</summary>
        private int Slots(Entity e)
        {
            var em = EntityManager;
            bool fire = em.HasComponent<Game.Buildings.FireStation>(e);
            if (fire ? !Mod.Setting.FireStationAmbulances : !em.HasComponent<Game.Buildings.Hospital>(e) || em.HasBuffer<Game.Buildings.Patient>(e)) return 0;
            return StationFleetSystem.Slots(em, e);
        }

        private void Set(string value)
        {
            var e = selectedEntity;
            if (!EntityManager.Exists(e) || !IsStation(EntityManager, e)) return;
            if (string.IsNullOrWhiteSpace(value)) { if (EntityManager.HasComponent<StationCallsign>(e)) EntityManager.RemoveComponent<StationCallsign>(e); }
            else if (StationCallsign.TryPack(value, out var packed)) EntityManager.AddComponentData(e, new StationCallsign { m_Packed = packed });
            else return; // UI already restricts input; ignore anything else
            UnitNamingSystem.RenameStation(e);
            RequestUpdate();
        }

        private int m_BedMax, m_Beds, m_NefMax, m_Nef, m_CarMax, m_Cars;

        protected override void Reset() { m_Callsign = ""; m_Slots = m_Ambulances = m_BedMax = m_Beds = m_NefMax = m_Nef = m_CarMax = m_Cars = 0; m_Fire = m_Livery = false; }

        protected override void OnUpdate()
        {
            visible = IsStation(EntityManager, selectedEntity);
            if (!visible) return;
            m_Callsign = Of(EntityManager, selectedEntity) ?? "";
            m_Slots = Slots(selectedEntity);
            m_Fire = EntityManager.HasComponent<Game.Buildings.FireStation>(selectedEntity);
            m_Livery = EntityManager.HasComponent<FireLivery>(selectedEntity);
            m_Ambulances = m_Slots > 0 ? StationFleetSystem.Ambulances(EntityManager, selectedEntity, m_Slots) : 0;
            var e = selectedEntity;
            m_BedMax = BedMax(e); m_Beds = Cap(e, m_BedMax, 0);
            m_NefMax = NefMax(e); m_Nef = Cap(e, m_NefMax, 1);
            m_CarMax = CarMax(e); m_Cars = Cap(e, m_CarMax, 2);
            m_HeliDebug = DispatchUISystem.DebugUnlocked && Mod.Setting.EnableHelicopter && IsHospital(e) && BedMax(e) > 0;
            if (m_HeliStatusFor != e) m_HeliStatus = ""; // status belongs to the hospital it was pressed on
        }

        protected override void OnProcess() { }

        public override void OnWriteProperties(IJsonWriter w)
        {
            w.PropertyName("callsign"); w.Write(m_Callsign);
            w.PropertyName("slots"); w.Write(m_Slots);
            w.PropertyName("ambulances"); w.Write(m_Ambulances);
            w.PropertyName("fire"); w.Write(m_Fire);
            w.PropertyName("livery"); w.Write(m_Livery);
            w.PropertyName("bedMax"); w.Write(m_BedMax);
            w.PropertyName("beds"); w.Write(m_Beds);
            w.PropertyName("nefMax"); w.Write(m_NefMax);
            w.PropertyName("nef"); w.Write(m_Nef);
            w.PropertyName("carMax"); w.Write(m_CarMax);
            w.PropertyName("cars"); w.Write(m_Cars);
            w.PropertyName("heliDebug"); w.Write(m_HeliDebug);
            w.PropertyName("heliStatus"); w.Write(m_HeliStatus);
        }
    }
}
