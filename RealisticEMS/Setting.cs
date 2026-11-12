using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace RealisticEMS
{
    /// <summary>Options: tabs and groups below; within a group, options appear in declaration order.</summary>
    [FileLocation(nameof(RealisticEMS))]
    [SettingsUITabOrder(Dispatch, Scene, Driving, General)]
    [SettingsUIGroupOrder(Hospitals, Depots, Calls, Care, Doctor, Parking, WayAhead, Lanes, Corridor, Siren, About)]
    [SettingsUIShowGroupName(Hospitals, Depots, Calls, Care, Doctor, Parking, WayAhead, Lanes, Corridor, Siren, About)]
    public class Setting : ModSetting
    {
        public const string Dispatch = "Dispatch", Scene = "Scene", Driving = "Driving", General = "General";
        public const string Hospitals = "Hospitals", Depots = "Depots", Calls = "Calls";
        public const string Care = "Care", Doctor = "Doctor", Parking = "Parking";
        public const string WayAhead = "WayAhead", Lanes = "Lanes", Corridor = "Corridor", Siren = "Siren";
        public const string About = "About";

        public Setting(IMod mod) : base(mod) { SetDefaults(); } // ModSetting does not call it

        // ---- Dispatch ----

        /// <summary>Full hospitals are not offered as transport targets.</summary>
        [SettingsUISection(Dispatch, Hospitals)]
        public bool RejectFullHospitals { get; set; }

        [SettingsUISection(Dispatch, Hospitals)]
        [SettingsUISlider(min = 5, max = 100, step = 5, unit = "percentage")]
        public int BedCapacityPercent { get; set; }

        /// <summary>Hospitals stop sending ambulances while at least one depot exists.</summary>
        [SettingsUISection(Dispatch, Depots)]
        public bool OnlyDepotsDispatch { get; set; }

        /// <summary>Returning ambulances are not available until parked at their home depot.</summary>
        [SettingsUISection(Dispatch, Depots)]
        public bool AvailableOnlyAtDepot { get; set; }

        [SettingsUISection(Dispatch, Depots)]
        [SettingsUISlider(min = 1, max = 40, step = 1)]
        public int DepotAmbulances { get; set; }

        /// <summary>Callsign + AMPDS code as ambulance name.</summary>
        [SettingsUISection(Dispatch, Calls)]
        public bool ShowUnitNames { get; set; }

        /// <summary>How severe calls are coded (share of Delta/Echo).</summary>
        [SettingsUISection(Dispatch, Calls)]
        public CallMix CallMix { get; set; }

        /// <summary>How many sick/injured calls happen compared to vanilla (CallVolumeSystem).</summary>
        [SettingsUISection(Dispatch, Calls)]
        public CallVolume CallVolume { get; set; }

        // ---- On scene ----

        /// <summary>Game minutes an ambulance cares for the patient on scene before loading. 0 = vanilla.</summary>
        [SettingsUISection(Scene, Care)]
        [SettingsUISlider(min = 0, max = 180, step = 5)]
        public int OnSceneMinutes { get; set; }

        /// <summary>Experimental emergency doctor cars from hospitals.</summary>
        [SettingsUISection(Scene, Doctor)]
        public bool EnableNef { get; set; }

        [SettingsUISection(Scene, Doctor)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(EnableNef), true)]
        [SettingsUISlider(min = 1, max = 5, step = 1)]
        public int NefPerHospital { get; set; }

        /// <summary>Game minutes.</summary>
        [SettingsUISection(Scene, Doctor)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(EnableNef), true)]
        [SettingsUISlider(min = 0, max = 120, step = 5)]
        public int NefMaxWaitMinutes { get; set; }

        [SettingsUISection(Scene, Parking)]
        public bool ParkOnSidewalk { get; set; }

        [SettingsUISection(Scene, Parking)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(ParkOnSidewalk), true)]
        public bool ParkAtCrossing { get; set; }

        // ---- Emergency driving ----

        [SettingsUISection(Driving, WayAhead)]
        public bool PushBlockers { get; set; }

        [SettingsUISection(Driving, WayAhead)]
        public bool ClearRedLight { get; set; }

        [SettingsUISection(Driving, WayAhead)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(ClearRedLight), true)]
        [SettingsUISlider(min = 1, max = 5, step = 1)]
        public int ClearRedLightMaxCars { get; set; }

        [SettingsUISection(Driving, Lanes)]
        public bool EmergencyLaneChange { get; set; }

        [SettingsUISection(Driving, Corridor)]
        public bool EmergencyCorridor { get; set; }

        [SettingsUISection(Driving, Corridor)]
        public bool OncomingOvertake { get; set; }

        /// <summary>Siren only in a jam/corridor, on the oncoming lane and at a red light; lights stay on (Driving/Siren.cs).</summary>
        [SettingsUISection(Driving, Siren)]
        public bool SirenOnlyWhenNeeded { get; set; }

        /// <summary>Set when the setup screen (UI/src/mods/intro.tsx) was closed; until then it opens on every game load.</summary>
        [SettingsUIHidden]
        public bool IntroSeen { get; set; }

        /// <summary>Version whose "what's new" screen (UI/src/mods/news.tsx) was closed; empty = never.</summary>
        [SettingsUIHidden]
        public string LastSeenVersion { get; set; }

        // ---- General ----

        /// <summary>Read-only; shown as text in Options.</summary>
        [SettingsUISection(General, About)]
        public string Version => typeof(Setting).Assembly.GetName().Version.ToString();

        /// <summary>Debug logging: off = only warnings/errors reach Logs/RealisticEMS.log.</summary>
        [SettingsUISection(General, About)]
        public bool LogAmbulanceStates
        {
            get => m_Log;
            set { m_Log = value; Mod.log.effectivenessLevel = value ? Colossal.Logging.Level.Info : Colossal.Logging.Level.Warn; }
        }
        private bool m_Log;

        /// <summary>Button: copies the log into an export folder (LogExport).</summary>
        [SettingsUISection(General, About)]
        [SettingsUIButton]
        public bool ExportLog { set => LogExport.Run(); }

        /// <summary>Unlocks the Debug page in the dispatch window (DispatchUISystem.DebugUnlocked).</summary>
        [SettingsUISection(General, About)]
        [SettingsUITextInput]
        public string DebugPassword { get; set; }
        public const string DebugPasswordValue = "ems-debug";

        public override void SetDefaults()
        {
            RejectFullHospitals = true;
            AvailableOnlyAtDepot = true;
            OnlyDepotsDispatch = true;
            ShowUnitNames = true;
            CallMix = CallMix.Realistic;
            CallVolume = CallVolume.Balanced;
            EnableNef = false;
            NefPerHospital = 2;
            NefMaxWaitMinutes = 30;
            OnSceneMinutes = 60;
            ParkOnSidewalk = true;
            ParkAtCrossing = true;
            PushBlockers = true;
            EmergencyLaneChange = true;
            EmergencyCorridor = true;
            OncomingOvertake = true;
            ClearRedLight = true;
            ClearRedLightMaxCars = 2;
            SirenOnlyWhenNeeded = false;
            LogAmbulanceStates = false;
            DebugPassword = "";
            DepotAmbulances = 10;
            BedCapacityPercent = 25;
        }
    }

    /// <summary>Options and asset texts. Registered for en-US and de-DE; other game languages fall back to en-US.</summary>
    public class Locale : IDictionarySource
    {
        private readonly Setting m_Setting;
        private readonly bool m_De;
        public Locale(Setting setting, bool german) { m_Setting = setting; m_De = german; }

        private string T(string de, string en) => m_De ? de : en;

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var d = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), T("Realistischer Rettungsdienst", "Realistic Emergency Medical Services") },
                { "RealisticEMS.Lang", T("de", "en") }, // read by ems-panel.tsx to pick its language
                { "SelectedInfoPanel.VEHICLE_STATES[OnScene]", T("Am Einsatzort", "On scene") }, // VehicleStateSection
                { "Assets.NAME[" + DepotPrefabSystem.DepotName + "]", T("Rettungswache", "Ambulance Depot") },
                { "Assets.DESCRIPTION[" + DepotPrefabSystem.DepotName + "]", T(
                    "Rettungswache ohne Betten. Beherbergt viele RTW, die Patienten ins nächste Spital mit freien Betten bringen.",
                    "Ambulance station without beds. Houses many ambulances that bring patients to the nearest hospital with free capacity.") },
            };
            void Opt(string name, string labelDe, string labelEn, string descDe, string descEn)
            {
                d[m_Setting.GetOptionLabelLocaleID(name)] = T(labelDe, labelEn);
                d[m_Setting.GetOptionDescLocaleID(name)] = T(descDe, descEn);
            }

            void Tab(string tab, string de, string en) => d[m_Setting.GetOptionTabLocaleID(tab)] = T(de, en);
            void Group(string group, string de, string en) => d[m_Setting.GetOptionGroupLocaleID(group)] = T(de, en);
            Tab(Setting.Dispatch, "Alarmierung", "Dispatch");
            Tab(Setting.Scene, "Am Einsatzort", "On scene");
            Tab(Setting.Driving, "Einsatzfahrt", "Emergency driving");
            Tab(Setting.General, "Allgemein", "General");
            Group(Setting.Hospitals, "Spitäler", "Hospitals");
            Group(Setting.Depots, "Rettungswachen", "Ambulance depots");
            Group(Setting.Calls, "Einsätze", "Calls");
            Group(Setting.Care, "Versorgung", "Care");
            Group(Setting.Doctor, "Notarzt (NEF)", "Emergency doctor");
            Group(Setting.Parking, "Parken", "Parking");
            Group(Setting.WayAhead, "Weg freimachen", "Clearing the way");
            Group(Setting.Lanes, "Spuren", "Lanes");
            Group(Setting.Corridor, "Rettungsgasse und Überholen", "Emergency corridor and overtaking");
            Group(Setting.Siren, "Sirene", "Siren");
            Group(Setting.About, "Info", "About");

            Opt(nameof(Setting.Version), "Version", "Version", "Installierte Mod-Version.", "Installed mod version.");
            Opt(nameof(Setting.RejectFullHospitals), "Volle Spitäler ablehnen", "Reject full hospitals",
                "RTW bringen Patienten nur in Spitäler mit freiem Bett.",
                "Ambulances only transport to hospitals with a free bed.");
            Opt(nameof(Setting.AvailableOnlyAtDepot), "Erst nach Rückkehr verfügbar", "Available only after returning",
                "Zurückfahrende RTW übernehmen keine neuen Einsätze, bis sie wieder an ihrer Wache sind.",
                "Returning ambulances cannot take new calls until they are back at their home station.");
            Opt(nameof(Setting.OnlyDepotsDispatch), "Nur Wachen schicken RTW", "Only depots send ambulances",
                "Spitäler nehmen nur Patienten auf, solange mindestens eine Rettungswache existiert. Ohne Wache schicken die Spitäler wie in Vanilla.",
                "Hospitals only receive patients while at least one Ambulance Depot exists. Without a depot, hospitals dispatch as in vanilla.");
            Opt(nameof(Setting.ShowUnitNames), "Funkrufnamen und Einsatzcodes", "Unit callsigns and call codes",
                "Benennt RTW 'RTW <Wache>-<Nr>' und zeigt AMPDS-Code und Status des laufenden Einsatzes.",
                "Names ambulances 'AMB <station>-<nr>' and shows the AMPDS code and status of their current call.");
            Opt(nameof(Setting.CallMix), "Schweregrad der Einsätze", "Call severity mix",
                "Wie oft Einsätze als Delta/Echo (mit NEF, Polizei, Feuerwehr) codiert werden. Realistisch: meist Alpha-Charlie wie im echten Leitstellenalltag. Ausgewogen: mehr Charlie/Delta. Dramatisch: überwiegend Delta. Gilt für neue Einsätze.",
                "How often calls are coded Delta/Echo (with doctor, police, fire). Realistic: mostly Alpha-Charlie like a real dispatch centre. Balanced: more Charlie/Delta. Dramatic: mostly Delta. Applies to new calls.");
            d[m_Setting.GetEnumValueLocaleID(CallMix.Realistic)] = T("Realistisch", "Realistic");
            d[m_Setting.GetEnumValueLocaleID(CallMix.Balanced)] = T("Ausgewogen", "Balanced");
            d[m_Setting.GetEnumValueLocaleID(CallMix.Dramatic)] = T("Dramatisch", "Dramatic");
            Opt(nameof(Setting.CallVolume), "Anzahl der Einsätze", "Number of calls",
                "Wie viele Kranken- und Verletztenmeldungen es gibt. Wenige: etwa halb so viele wie in Vanilla. Ausgewogen: wie Vanilla. Viele: etwa doppelt so viele. Todesfälle sind nicht betroffen.",
                "How many sick and injured calls happen. Few: about half of vanilla. Balanced: as vanilla. Many: about twice as many. Deaths are not affected.");
            d[m_Setting.GetEnumValueLocaleID(CallVolume.Few)] = T("Wenige", "Few");
            d[m_Setting.GetEnumValueLocaleID(CallVolume.Balanced)] = T("Ausgewogen", "Balanced");
            d[m_Setting.GetEnumValueLocaleID(CallVolume.Many)] = T("Viele", "Many");
            Opt(nameof(Setting.BedCapacityPercent), "Bettenkapazität der Spitäler", "Hospital bed capacity",
                "Anteil der Vanilla-Betten (Spital 500, Klinik 100). Bei 25% hat ein Spital 125 Betten und ist bei einer Welle schnell voll. Wirkt sofort.",
                "Share of the vanilla beds (hospital 500, clinic 100). At 25% a hospital has 125 beds and fills up quickly during a wave. Applies immediately.");
            Opt(nameof(Setting.EnableNef), "Notarzt (NEF) aus Spitälern (experimentell)", "Emergency doctor cars from hospitals (experimental)",
                "Spitäler schicken Notarzt-Einsatzfahrzeuge bei Codes mit NEF, oder wenn der RTW vor Ort nachfordert. Der NEF begleitet je nach Lage den RTW ins Spital oder ist nach dessen Abfahrt frei.",
                "Hospitals send emergency doctor cars for codes that need a doctor, or when the ambulance on scene requests one. Depending on the case, the doctor escorts the ambulance to hospital or is free once it leaves.");
            Opt(nameof(Setting.NefPerHospital), "NEF pro Spital", "Doctor cars per hospital",
                "Wie viele Notärzte ein Spital gleichzeitig im Einsatz haben kann.",
                "How many emergency doctors a hospital can have out at the same time.");
            Opt(nameof(Setting.NefMaxWaitMinutes), "RTW wartet max. auf NEF (Spielminuten)", "Max. ambulance wait for doctor (game minutes)",
                "Ist der RTW zuerst vor Ort, wartet er so lange auf den Notarzt, bevor er die Versorgung allein beginnt. Zählt ab Ankunft, nicht ab Alarmierung.",
                "If the ambulance arrives first, it waits this long for the doctor before starting care alone. Counted from arrival, not from dispatch.");
            Opt(nameof(Setting.OnSceneMinutes), "Versorgung vor Ort (Spielminuten)", "On-scene care (game minutes)",
                "So lange versorgt der RTW den Patienten vor Ort, bevor er ihn einlädt. Startet bei Ankunft des RTW, mit Notarzt erst wenn beide vor Ort sind. Die Anfahrt zählt nicht. 0 = sofort einladen (Vanilla).",
                "How long the ambulance treats the patient on scene before loading. Starts when the ambulance arrives, with a doctor only once both are on scene. Travel time does not count. 0 = load immediately (vanilla).");
            Opt(nameof(Setting.ParkOnSidewalk), "RTW/NEF parken vor Ort neben der Fahrbahn", "Park off the road on scene",
                "Während der Versorgung fahren RTW und NEF von der Fahrbahn, damit der Verkehr vorbeikommt: in eine freie Parkspur, sonst auf einen breiten Gehsteig, und hat die Straße keine Parkmöglichkeit, auf den normalen Gehsteig. Alle Fahrzeuge eines Einsatzorts bilden eine Gruppe, auch Polizei und Feuerwehr, die bis zur Abfahrt des RTW vor Ort bleiben: das erste vor Ort parkt, die anderen stellen sich dahinter.",
                "During care, ambulances and doctor cars leave the roadway so traffic can pass: into a free parking lane, otherwise onto a wide sidewalk, and if the road has no parking, onto the normal sidewalk. All vehicles at one scene form a group, including police and fire brigade, who stay until the ambulance leaves: the first one parks, the others line up behind it.");
            Opt(nameof(Setting.ParkAtCrossing), "An Kreuzungen an den Ecken parken", "Park at corners at intersections",
                "Liegt der Einsatzort an einer Kreuzung, parkt das erste Fahrzeug an einer Ecke, die anderen dahinter: in einer freien Parkspur (ist die Lücke zu kurz, schräg mit der Nase auf dem Gehsteig), auf breitem Gehsteig oder auf dem Grünstreifen, kurz nach dem Zebrastreifen. Passt die ganze Gruppe dort nicht hin, parkt sie weiter weg von der Kreuzung. Benötigt 'RTW/NEF parken vor Ort neben der Fahrbahn'.",
                "If the scene is at an intersection, the first vehicle parks at a corner and the others behind it: in a free parking lane (angled with the nose on the sidewalk if the gap is too short), on a wide sidewalk or on the green strip, just past the crosswalk. If the whole group does not fit there, it parks further from the intersection. Requires 'Park off the road on scene'.");
            Opt(nameof(Setting.PushBlockers), "Einsatzfahrzeug drängt Vordermann", "Emergency vehicles push vehicles ahead",
                "Fahrzeuge vor einem Einsatzfahrzeug mit Blaulicht werden sofort weitergeschoben (Vanilla macht das erst nach 5 s). Gilt für alle Einsatzfahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "Vehicles in front of an emergency vehicle with lights are pushed on immediately (vanilla waits 5 s). Applies to every emergency vehicle with lights: ambulance, emergency doctor, police and fire brigade.");
            Opt(nameof(Setting.EmergencyLaneChange), "Einsatzfahrzeug wechselt auf freie Spur", "Emergency vehicles switch to a free lane",
                "Steht ein Einsatzfahrzeug im Stau und ist eine Spur in gleicher Richtung ~40 m frei, überholt es dort und wechselt vor der Kreuzung zurück. Gilt für alle Einsatzfahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "If an emergency vehicle is stuck in traffic and a lane in the same direction is clear for ~40 m, it overtakes there and changes back before the intersection. Applies to every emergency vehicle with lights: ambulance, emergency doctor, police and fire brigade.");
            Opt(nameof(Setting.ClearRedLight), "Wartende vor Einsatzfahrzeugen dürfen bei Rot fahren", "Cars ahead of emergency vehicles may run red lights",
                "Stehen vor einem Einsatzfahrzeug mit Blaulicht nur wenige Autos an einer roten Ampel, bekommen sie kurz Grün und machen den Weg frei. Gilt für alle Einsatzfahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "If only a few cars wait at a red light in front of an emergency vehicle with lights, they briefly get green and clear the way. Applies to every emergency vehicle with lights: ambulance, emergency doctor, police and fire brigade.");
            Opt(nameof(Setting.ClearRedLightMaxCars), "Maximal wartende Autos", "Max. waiting cars",
                "Nur wenn höchstens so viele Autos vor dem Einsatzfahrzeug stehen, wird die Ampel umgeschaltet.",
                "The light is only switched if at most this many cars are in front of the emergency vehicle.");
            Opt(nameof(Setting.EmergencyCorridor), "Rettungsgasse", "Emergency corridor",
                "Autobahn (1 bis 4 Spuren): Sobald der Verkehr stockt, bilden die Autos die Rettungsgasse, auch ohne Einsatzfahrzeug. Alle anderen Straßen: sobald der Verkehr stockt und ein Einsatzfahrzeug auf der Route kommt, nach denselben Regeln. Bei einer Spur je Richtung fahren beide Richtungen an ihren rechten Rand, das Einsatzfahrzeug fährt auf der Mittellinie, und der Gegenverkehr hält, während es vorbeifährt. Für die Feuerwehr fahren auf diesen Straßen alle Spuren beider Richtungen an den rechten Rand, und sie fährt über die Mittellinie. Jede Spur rückt als Ganzes zur Seite, Lkw und Anhänger inklusive, damit die Reihen gerade bleiben: die linke Spur nach links, alle anderen nach rechts, auf der Autobahn bis auf den Pannenstreifen. Bei nur einer Spur fahren alle nach rechts. Die Autos lenken noch im Rollen hinüber, stehende gleiten sanft. Die Gasse ist breit genug für die Feuerwehr. Vor Ausfahrten und an Auffahrten bleibt der Pannenstreifen frei und niemand hält an, damit die Rampen frei bleiben. Das Einsatzfahrzeug fährt mit etwa 50 km/h durch die Gasse (etwa 30 km/h, solange sie sich noch bildet) ohne die Spur zu wechseln, bis zum Ende der Gasse, und biegt von dort in jede Richtung ab, während der übrige Verkehr der Kreuzung wartet. Mehrere Einsatzfahrzeuge hintereinander fahren gemeinsam durch: Die Autos warten, bis das letzte vorbei ist. Kreisverkehre bleiben Vanilla. Danach lenken die Autos sanft zurück und fahren weiter. Sehr schmale Straßen (Spuren unter 2,7 m): Die Autos davor fahren an den rechten Rand und halten, der Gegenverkehr ebenso, und es überholt über die Mittellinie. Ist die Straße dafür zu eng, biegt die Kolonne an der nächsten Kreuzung anders ab. Im Linksverkehr gespiegelt. Gilt für alle Einsatzfahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "Highways (1 to 4 lanes): as soon as traffic slows down, cars form the corridor, even with no emergency vehicle around. All other roads: as soon as traffic slows and an emergency vehicle is coming along the route, by the same rules. With one lane each way both directions pull to their right edge, the emergency vehicle drives on the centre line, and oncoming traffic stops while it passes. For fire engines on these roads all lanes of both directions pull to the right edge and the fire engine drives over the centre line. Every lane moves aside as a whole, trucks and trailers included, so the rows stay straight: the left lane moves left, all others move right, on highways onto the shoulders. With a single lane everyone moves right. Cars steer over while still rolling, standing ones glide smoothly. The corridor is wide enough for fire engines. Near exits and entries the shoulder stays clear and nobody stops, so the ramps stay free. The emergency vehicle drives through at about 50 km/h (about 30 km/h while the corridor is still forming) without changing lanes, to the end of the corridor, and turns from there in any direction while the other junction traffic waits. Several emergency vehicles in a row pass together: cars wait until the last one has passed. Roundabouts stay vanilla. Afterwards cars steer back smoothly and drive on. Very narrow roads (lanes under 2.7 m): cars ahead pull to the right edge and stop, oncoming traffic too, and it passes over the centre line. If the road is too narrow, the queue turns off at the next junction instead. Mirrored in left-hand traffic. Applies to every emergency vehicle with lights: ambulance, emergency doctor, police and fire brigade.");
            Opt(nameof(Setting.OncomingOvertake), "Auf der Gegenfahrbahn überholen", "Overtake on the oncoming lane",
                "Auf Straßen mit einer Spur pro Richtung weichen Lkw, Busse und Wohnmobile nicht aus. Steht so ein Fahrzeug in der Kolonne, wird die Gegenfahrbahn gesperrt: Autos, die in sie einfahren wollen, warten an der Kreuzung (ohne Umleitung). Ist sie leer, überholt das Einsatzfahrzeug die ganze Kolonne darauf. Danach fahren alle weiter. Fährt langsamer Verkehr davor und ist die Gegenfahrbahn frei, überholt es ebenso. Gilt für alle Einsatzfahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "On roads with one lane per direction, trucks, buses and campers do not move aside. If such a vehicle is in the queue, the oncoming lane is closed: cars about to enter it wait at the junction (no rerouting). Once it is empty, the emergency vehicle overtakes the whole queue on it. Afterwards everyone drives on. Slower traffic moving ahead is overtaken the same way when the oncoming lane is free. Applies to every emergency vehicle with lights: ambulance, emergency doctor, police and fire brigade.");
            Opt(nameof(Setting.SirenOnlyWhenNeeded), "Sirene nur bei Bedarf", "Siren only when needed",
                "Einsatzfahrzeuge fahren mit Blaulicht, aber ohne Sirene. Die Sirene ertönt nur, wenn sie gebraucht wird: im Stau oder in der Rettungsgasse, beim Überholen auf der Gegenfahrbahn und vor einer roten Ampel. Danach läuft sie noch ~3 s weiter. Das Fahrverhalten ändert sich nicht. Gilt für alle Einsatzfahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "Emergency vehicles drive with lights but without the siren. The siren only sounds when it is needed: in a traffic jam or emergency corridor, while overtaking on the oncoming lane, and in front of a red light. It keeps sounding for ~3 s afterwards. Driving behaviour does not change. Applies to every emergency vehicle with lights: ambulance, emergency doctor, police and fire brigade.");
            Opt(nameof(Setting.LogAmbulanceStates), "Debug-Protokoll", "Debug logging",
                "Schreibt jede Zustandsänderung und Entscheidung nach Logs/RealisticEMS.log. Nur zur Fehlersuche, Warnungen und Fehler werden immer protokolliert.",
                "Writes every state change and decision to Logs/RealisticEMS.log. For bug reports only; warnings and errors are always logged.");
            Opt(nameof(Setting.ExportLog), "Protokoll exportieren", "Export log",
                "Kopiert das Protokoll mit SHA-256-Hash in einen Ordner unter Logs/, damit es sich leicht an einen Fehlerbericht anhängen lässt.",
                "Copies the log with a SHA-256 hash into a folder under Logs/, ready to attach to a bug report.");
            Opt(nameof(Setting.DebugPassword), "Debug-Passwort", "Debug password",
                "Schaltet die Debug-Seite in der Leitstelle frei.", "Unlocks the Debug page in the dispatch window.");
            Opt(nameof(Setting.DepotAmbulances), "RTW pro Wache", "Ambulances per depot",
                "Fahrzeuganzahl der Rettungswache. Erfordert Neustart.",
                "Vehicle count of the Ambulance Depot. Requires restart.");
            return d;
        }

        public void Unload() { }
    }
}
