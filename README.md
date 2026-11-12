<img width="1920" height="1080" alt="overview" src="https://github.com/user-attachments/assets/f7e6cca4-920e-4196-bbc1-04334ede05b6" />

# Realistic EMS (Beta)

A Cities: Skylines II code mod that reworks ambulances, modelled on Vienna's emergency medical service (Wiener Rettung).

> **Beta:** back up your saves. Report bugs at https://github.com/BrickCraftLP/Realistic-EMS-CS2/issues and attach the log: turn on **Debug logging** in the options, press **Export log** and attach the new folder from `Logs/`.

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
- **Scene parking**: vehicles park off the road, on the sidewalk, in parking lanes or at intersection corners. A free parking spot is p
-
- referred over the sidewalk. Angled parking keeps the rear out of the lane.

### Emergency driving
Used by every vehicle with lights on: ambulance, emergency doctor (NEF), police and fire brigade. Each aid has one toggle that covers all of them.
- **Rettungsgasse on highways and multi-lane roads**: on highways, every traffic jam forms the gap, emergency vehicle or not. On other multi-lane roads it forms as soon as traffic slows and an emergency vehicle is coming along the route. The gap runs between the left lane and all other lanes (Austrian rule). The left lane moves left, every other lane moves right, trucks included. The emergency vehicle moves into the gap from any lane; if it is still blocked there for 3 s, it drives past the obstacle. Cars next to it stop and drive on once it has passed.
- **One lane per direction**: cars ahead pull to the kerb, oncoming traffic too, and the vehicle passes over the centre line. If the road is too narrow, the queue turns off at the next junction instead.
- **Oncoming overtake**: trucks in the queue, or slow traffic with a free oncoming lane: the oncoming lane is closed and the vehicle overtakes on it.
- Push blocking vehicles immediately instead of waiting 5 seconds.
- Change to a free lane, merge back, or turn from any lane.
- Clear red lights by holding the signal green, up to a configurable number of cars.
- Left-hand traffic cities are mirrored automatically.
- **Stuck guard**: prevents vanilla from deleting an ambulance that is only briefly deadlocked. A truly trapped one is still removed after about 2 minutes.
- Pedestrian streets: vehicles stop where they are, with no driving aids.

## How it works

The mod avoids patching vanilla Burst jobs. It adds ECS systems that run right after the vanilla ones and change the flags vanilla already understands:

| Goal | Approach |
|---|---|
| Depot-only dispatch | Clear `HasAvailableAmbulances` on hospitals and mark returning ambulances `Disabled` so vanilla won't dispatch them (`DepotOnlyDispatchSystem`, `AmbulanceReturnSystem`) |
| Skip full hospitals | Clear a hospital's `CanCure*` flags after vanilla recomputes them, so pathfinding never targets it (`HospitalSelectionSystem`) |
| On-scene care | Clear the ambulance's `AtTarget` flag so patients don't board yet (`OnSceneCareSystem`, `CrewHoldSystem`) |
| Emergency driving | Set vanilla's own blocker, lane-change, lane-position and max-speed fields each frame (`Driving/`: `EmergencyLaneSystem`, `Corridor`, `Manoeuvres`, `LaneOps`; `SignalOverrideSystem`) |

Settings live in [Setting.cs](RealisticEMS/Setting.cs). German and English are included.

## Installation

- Subscribe on Paradox Mods (mod ID `166759`).
- Requires Cities: Skylines II, game version `1.*`.

## Building

Requires the CS2 modding toolchain (.NET Framework 4.8 target) and Node for the UI in `RealisticEMS/UI`.

```
dotnet build RealisticEMS/RealisticEMS.csproj -c Release
```

## Credits

- Icons: [Google Material Icons](https://github.com/google/material-design-icons), Apache License 2.0 (inlined as SVG, nothing loaded from Google). License text in [THIRD_PARTY_LICENSES.txt](THIRD_PARTY_LICENSES.txt).

Call codes: Einsatzcodes Wien (einsatzcodes.at), Stand 06.01.2025.
