using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;

namespace RealisticEMS
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger(nameof(RealisticEMS)).SetShowsErrorsInUI(false);
        public static Setting Setting;

        /// <summary>Shown in the "what's new" screen; keep in sync with ModVersion in Properties/PublishConfiguration.xml and the newest entry in UI/src/mods/news.tsx.</summary>
        public const string Version = "26.0.1b";

        /// <summary>German texts when the game runs in German, English otherwise.</summary>
        public static bool German => GameManager.instance?.localizationManager?.activeLocaleId?.StartsWith("de") == true;
        public static string T(string de, string en) => German ? de : en;

        public void OnLoad(UpdateSystem updateSystem)
        {
            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new Locale(Setting, false));
            GameManager.instance.localizationManager.AddSource("de-DE", new Locale(Setting, true));
            AssetDatabase.global.LoadSettings(nameof(RealisticEMS), Setting, new Setting(this));

            // Both run after the vanilla systems whose output they correct, every simulation frame.
            updateSystem.UpdateAfter<HospitalSelectionSystem, HospitalAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<AmbulanceReturnSystem, AmbulanceAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<DepotOnlyDispatchSystem, HospitalAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<UnitNamingSystem, AmbulanceAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<AmbulanceStuckGuardSystem, AmbulanceAISystem>(SystemUpdatePhase.GameSimulation); // before it deletes stuck ambulances
            updateSystem.UpdateAfter<EmergencyLaneSystem, CarNavigationSystem.Actions>(SystemUpdatePhase.GameSimulation); // after lane object updates
            updateSystem.UpdateAfter<SignalOverrideSystem, TrafficLightSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<NefPrefabSystem, Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateAfter<NefDispatchSystem, AmbulanceAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<OnSceneCareSystem, NefDispatchSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<ResponderDispatchSystem, PoliceCarAISystem>(SystemUpdatePhase.GameSimulation); // re-sets lights after vanilla ResetPath
            updateSystem.UpdateAfter<CrewHoldSystem, OnSceneCareSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<SidewalkParkingSystem, OnSceneCareSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<CrewHoldLateSystem, ResidentAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<BedCapacitySystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<CallVolumeSystem, Game.Events.AddHealthProblemSystem>(SystemUpdatePhase.GameSimulation); // sees new health events before they are applied
            updateSystem.UpdateAt<DispatchUISystem>(SystemUpdatePhase.UIUpdate);
            Ampds.Load();
            Ampds.SelfCheck();
            // Must add the prefab before PrefabInitializeSystem runs that frame, else it is never LateInitialized (no toolbar group).
            updateSystem.UpdateBefore<DepotPrefabSystem, Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            log.Info("Loaded");
        }

        public void OnDispose()
        {
            Setting?.UnregisterInOptionsUI();
            Setting = null;
        }
    }
}
