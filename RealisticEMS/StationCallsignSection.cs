using Colossal.Entities;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game.UI.InGame;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

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
    /// Rendered by UI/src/mods/station-section.tsx.
    /// </summary>
    public partial class StationCallsignSection : InfoSectionBase
    {
        protected override string group => nameof(StationCallsignSection);
        private string m_Callsign = "";

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

        protected override void Reset() => m_Callsign = "";

        protected override void OnUpdate()
        {
            visible = IsStation(EntityManager, selectedEntity);
            if (visible) m_Callsign = Of(EntityManager, selectedEntity) ?? "";
        }

        protected override void OnProcess() { }

        public override void OnWriteProperties(IJsonWriter w)
        {
            w.PropertyName("callsign"); w.Write(m_Callsign);
        }
    }
}
