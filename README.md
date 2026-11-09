# Realistic EMS (Beta)

A Cities: Skylines II code mod that reworks ambulances, modelled on Vienna's emergency medical service (Wiener Rettung).

> **Beta:** back up your saves. Report bugs at https://github.com/BrickCraftLP/Realistic-EMS-CS2/issues and attach `Logs/RealisticEMS.log` (turn on **Debug logging** in the options first).

## Why this mod exists

In vanilla CS2, EMS is simple and a bit silly:

- Every hospital sends ambulances, and an ambulance returning from a call can immediately grab a new one wherever it is.
- Patients are routed by travel cost. A full hospital only gets a small penalty, so ambulances still drive to it.
- Patients board instantly. There is no care on scene, no doctor, and no police or fire brigade support.
- Ambulances get stuck in traffic, and vanilla may delete a "stuck" ambulance together with its patient.

This mod makes EMS behave more like a real system. It has dedicated depots, coded calls, on-scene care and proper emergency driving. Every feature has its own toggle, so you can use only the parts you like.

## What it does

### Dispatch and logistics
- **Ambulance Depot**: a new station with no beds that houses many ambulances (default count is configurable). Once one exists, hospitals stop dispatching ambulances.
- **Depot-only availability**: an ambulance that is returning does not take new calls until it is back at its depot.
- **Bed-aware transport**: ambulances only drive to hospitals that have a free bed and can treat the patient. If no hospital qualifies, vanilla ranking is used as a fallback so patients are never stranded.
- **Bed capacity**: scale hospital beds by a percentage.

### Calls and crews
- **AMPDS call codes**: calls get an Einsatzcode from Vienna's code list (`Data/einsatzcodes.csv`). The code decides what is sent: ambulance (RTW), emergency doctor (NEF), police or fire brigade.
- **Unit callsigns and live call status** in the vehicle UI.
- **Emergency doctor cars (NEF)** *(experimental)*: sent from hospitals for codes that list them, or called in by the crew on scene.
- **Police and fire brigade** are dispatched along with the ambulance for matching codes: psychiatric calls, AED/first-responder calls and locked doors.

### On scene
- **On-scene care time**: the crew stays with the patient for a configurable number of game minutes before the patient boards. With an NEF involved, care starts when both are there. If the NEF does not arrive within the maximum wait, the crew starts alone. Driving time doesn't count.
- **Scene parking**: vehicles park off the road, on the sidewalk, in parking lanes or at intersection corners. A free parking spot is preferred over the sidewalk. Angled parking keeps the rear out of the lane.
- <img width="1920" height="1080" alt="08-November-14-14-15-05" src="https://github.com/user-attachments/assets/7318f415-161f-466a-aa6f-185712d178a8" />
- <img width="1920" height="1080" alt="08-November-14-12-51-04" src="https://github.com/user-attachments/assets/f6b25c4b-b44b-457a-9f09-e9a3c277086c" />


### Emergency driving
Only used by vehicles with lights on:
- Push blocking vehicles immediately instead of waiting 5 seconds.
- Change lanes around a blocked lane.
- Turn from any lane.
- Clear red lights by holding the signal green, up to a configurable number of cars.
- **Emergency corridor**: traffic stops and moves to the edges and the ambulance passes between.
- **Oncoming overtake**: pass slow traffic or trucks on the oncoming lane when it is free (fire engines too).
- **Drive-through**: after N seconds still blocked, drive through the blocker as a last resort.
- **Stuck guard**: prevents vanilla from deleting an ambulance that is only briefly deadlocked. A truly trapped one is still removed after about 2 minutes.
- Pedestrian streets: vehicles stop where they are, with no driving aids.
- <img width="1920" height="1080" alt="08-November-14-11-50-02" src="https://github.com/user-attachments/assets/de449d9e-e435-4218-a493-0a2c6d6406fd" />
- <img width="1920" height="1080" alt="08-November-14-15-15-08" src="https://github.com/user-attachments/assets/24c98ed2-5194-4eb9-9fd4-1efd9630dfb2" />


## How it works

The mod avoids patching vanilla Burst jobs. It adds ECS systems that run right after the vanilla ones and change the flags vanilla already understands:

| Goal | Approach |
|---|---|
| Depot-only dispatch | Clear `HasAvailableAmbulances` on hospitals and mark returning ambulances `Disabled` so vanilla won't dispatch them (`DepotOnlyDispatchSystem`, `AmbulanceReturnSystem`) |
| Skip full hospitals | Clear a hospital's `CanCure*` flags after vanilla recomputes them, so pathfinding never targets it (`HospitalSelectionSystem`) |
| On-scene care | Clear the ambulance's `AtTarget` flag so patients don't board yet (`OnSceneCareSystem`, `CrewHoldSystem`) |
| Emergency driving | Set vanilla's own blocker, lane-change and ignore-blocker flags each frame (`EmergencyLaneSystem`, `SignalOverrideSystem`) |

Settings live in [Setting.cs](RealisticEMS/Setting.cs). German and English are included.

## Installation

- Subscribe on Paradox Mods (mod ID `166759`).
- Requires Cities: Skylines II, game version `1.*`.

S.csproj -c Release

Credits Call-codes: [Einsatzcodes Wien](https://einsatzcodes.at), Stand 06.01.2025.
