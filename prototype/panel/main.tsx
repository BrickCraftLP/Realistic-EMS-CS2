import { createRoot } from "react-dom/client";
import { store, set } from "./mocks/api";
import { EmsPanel } from "../../RealisticEMS/UI/src/mods/ems-panel";

// ---- fake city: jittered road grid, buildings, units ----
let seed = 7; const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
const roads: number[] = [], N = 9, STEP = 320, O = -(N - 1) * STEP / 2;
const seg = (ax: number, az: number, bx: number, bz: number) => roads.push(ax, az, ax + (bx - ax) / 3 + rnd() * 20, az + (bz - az) / 3 + rnd() * 20, ax + 2 * (bx - ax) / 3, az + 2 * (bz - az) / 3, bx, bz);
for (let i = 0; i < N; i++) for (let j = 0; j < N; j++) {
    const x = O + i * STEP + rnd() * 30, z = O + j * STEP + rnd() * 30;
    if (i < N - 1) seg(x, z, O + (i + 1) * STEP, z); if (j < N - 1) seg(x, z, x, O + (j + 1) * STEP);
}
const hospitals = [{ name: "St. Mary", used: 38, beds: 40, accepts: false, ambulances: 2, x: -500, z: 420 }, { name: "Central Hospital", used: 61, beds: 90, accepts: true, ambulances: 3, x: 640, z: -300 }, { name: "East Clinic", used: 12, beds: 30, accepts: true, ambulances: 1, x: 300, z: 700 }];
const stations = [{ name: "Depot North", total: 5, free: 2, onCall: 3, x: -200, z: 800 }, { name: "Depot South", total: 4, free: 3, onCall: 1, x: 100, z: -800 }];
const ST = ["Anfahrt", "am Einsatzort", "Transport", "Rückfahrt", "frei", "frei"];
const REASONS = [["09E01", "E", "Not breathing"], ["10C01", "C", "Chest pain"], ["17B01", "B", "Falls"], ["26A01", "A", "Sick person"], ["06D01", "D", "Breathing problems"], ["29B01", "B", "Traffic accident"]];
const vehicles = Array.from({ length: 9 }, (_, i) => {
    const status = ST[i % ST.length], act = ["Anfahrt", "am Einsatzort", "Transport"].includes(status), r = REASONS[i % REASONS.length];
    return { callsign: `RTW ${i + 1}`, status, station: stations[i % 2].name, code: act ? r[0] : "", level: act ? r[1] : "", reason: act ? r[2] : "", units: act ? "RTW" + (i % 3 ? "" : " + NEF") : "",
        destination: status === "Transport" ? hospitals[1].name : act ? "Main St " + (10 + i) : "", call: act ? 100 + i : 0, callNo: act ? "R26" + String(100 + i).padStart(4, "0") : "", address: act ? "Main St " + (10 + i) : "", nef: false, x: (rnd() - .5) * 2000, z: (rnd() - .5) * 2000 };
});
const police = [{ callsign: "P1", status: "Streife", station: "Police HQ", x: 100, z: 100 }, { callsign: "P2", status: "Anfahrt", station: "Police HQ", x: -400, z: -200 }];
const hist: number[] = []; for (let i = 0; i < 256; i++) { const on = 1 + ((3 + 2 * Math.sin(i / 20)) | 0), bu = 40 + ((30 + 20 * Math.sin(i / 35)) | 0); const call = rnd() < .3 ? 1 : 0, lv = [0, 0, 0, 0, 0, 0]; if (call) lv[[0, 1, 2, 2, 3, 3, 3, 4, 4, 5][(rnd() * 10) | 0]]++; hist.push(on, 1, 9, bu, 160, call, ...lv); }
store.open = true; store.debug = true; store.roads = roads; store.intro = false;
store.state = { overview: { total: 9, free: 3, onCall: 4, returning: 2, bedsUsed: 111, bedsTotal: 160, hospitals: 3, depots: 2 }, vehicles, hospitals, stations, police,
    stats: { calls: 63, sampleMinutes: 6, history: hist, levels: [3, 8, 14, 20, 12, 1], response: [4, 6, 7, 9, 11, 12, 14, 16, 18, 22, 8, 5], reasons: REASONS.map((r, i) => ({ reason: r[2], n: 12 - i * 2 })) } };
const sw = (name: string, bool: boolean, value: number, min = 0, max = 1, step = 1) => ({ name, label: name, desc: "", bool, value, min, max, step });
store.settings = ["RejectFullHospitals", "AvailableOnlyAtDepot", "OnlyDepotsDispatch", "EnableNef", "ParkOnSidewalk", "ParkAtCrossing", "PushBlockers", "EmergencyLaneChange", "ClearRedLight", "EmergencyCorridor", "OncomingOvertake", "SirenOnlyWhenNeeded", "ShowUnitNames", "LogAmbulanceStates"].map(n => sw(n, true, 1))
    .concat([sw("DepotAmbulances", false, 4, 1, 12), sw("BedCapacityPercent", false, 25, 5, 50, 5), sw("NefPerHospital", false, 1, 1, 4), sw("NefMaxWaitMinutes", false, 10, 1, 60), sw("OnSceneMinutes", false, 60, 0, 120, 10), sw("ClearRedLightMaxCars", false, 3, 1, 8)]);
(window as any).__loadBuildings = () => { // 7 ints each: x, z, halfW, halfD, height, yaw*100, kind
    const b: number[] = []; for (let i = 0; i < 1500; i++) b.push((rnd() - .5) * 2600, (rnd() - .5) * 2600, 15 + rnd() * 25, 15 + rnd() * 25, 15 + rnd() * 70, (rnd() * 90 * 100) | 0, 0);
    hospitals.forEach(h => b.push(h.x, h.z, 50, 40, 60, 0, 1)); stations.forEach(s => b.push(s.x, s.z, 40, 30, 20, 0, 2));
    set("buildings", b);
};
createRoot(document.getElementById("root")!).render(<EmsPanel />);
// ?click=Statistics,Vehicles ... clicks buttons by label (handy for screenshots)
new URLSearchParams(location.search).get("click")?.split(",").forEach((label, i) => setTimeout(() => (Array.from(document.querySelectorAll("button")).find(b => b.textContent?.trim() === label) as HTMLElement | undefined)?.click(), 300 + i * 300));
// ?scroll=<px> scrolls the page content (for screenshots)
const sc = new URLSearchParams(location.search).get("scroll");
if (sc) setTimeout(() => document.querySelectorAll<HTMLElement>("div").forEach(d => { if (getComputedStyle(d).overflowY === "auto") d.scrollTop = +sc; }), 1200);
