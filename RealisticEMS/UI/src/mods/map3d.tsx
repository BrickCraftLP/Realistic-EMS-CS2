import { MouseEvent, useEffect, useMemo, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import styles from "./map3d.module.scss";

// Written by DispatchUISystem.cs. Only recomputed on "refreshMap3d" (Refresh button / opening the map).
const buildings$ = bindValue<number[]>("realisticEMS", "buildings", []); // 7 ints: x, z, halfW, halfD, height, yaw*100, kind
const roads$ = bindValue<number[]>("realisticEMS", "roads", []);         // 8 ints: bezier a,b,c,d as x,z
const ground$ = bindValue<string>("realisticEMS", "ground", "");           // GROUND_N² chars: 0 land, 1 water, 2 green
const GROUND_N = 256, GROUND_CELL = 14336 / GROUND_N, GROUND_MIN = -7168;
const state$ = bindValue<any>("realisticEMS", "state", null);

const W = 1600, H = 900, F = 1100; // canvas units, focal length
const RAD = Math.PI / 180;

// Material 3 light tonal palette, map flavoured (neutral surfaces, primary/error accents).
const C = {
    ground: "#ECEEE6", road: "#2B2D31", roadEdge: "#1A1B1E", marking: "#F2F2F2",
    water: "#9CC8F0", green: "#B9DDA0",
    roof: ["#F4F2EE", "#F6D9D5", "#D8E2FF"],  // kind 0 generic, 1 hospital, 2 EMS depot
    wall: [[226, 223, 216], [222, 170, 163], [160, 182, 232]],
    hospital: "#BA1A1A", depot: "#415F91", amb: "#B3261E", outline: "#FFFFFF",
};

interface Cam { tx: number; tz: number; yaw: number; pitch: number; dist: number; }

const bounds = (roads: number[], b: number[]) => {
    let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
    for (let i = 0; i < roads.length; i += 2) { x0 = Math.min(x0, roads[i]); x1 = Math.max(x1, roads[i]); z0 = Math.min(z0, roads[i + 1]); z1 = Math.max(z1, roads[i + 1]); }
    for (let i = 0; i < b.length; i += 7) { x0 = Math.min(x0, b[i]); x1 = Math.max(x1, b[i]); z0 = Math.min(z0, b[i + 1]); z1 = Math.max(z1, b[i + 1]); }
    if (!isFinite(x0)) return { tx: 0, tz: 0, yaw: 0, pitch: 55, dist: 2000 };
    return { tx: (x0 + x1) / 2, tz: (z0 + z1) / 2, yaw: 0, pitch: 55, dist: Math.max(600, Math.max(x1 - x0, z1 - z0) * 0.9) };
};

/** World (x, y up, z north) -> [screenX, screenY, depth]; depth <= 0 means behind the camera. */
const camera = (c: Cam) => {
    const cy = Math.cos(c.yaw * RAD), sy = Math.sin(c.yaw * RAD), cp = Math.cos(c.pitch * RAD), sp = Math.sin(c.pitch * RAD);
    return (x: number, y: number, z: number) => {
        const dx = x - c.tx, dz = z - c.tz;
        const rx = dx * cy - dz * sy, rz = dx * sy + dz * cy;
        // camera sits at (0, dist*sp, -dist*cp) looking at the target
        const vy = y - c.dist * sp, vz = rz + c.dist * cp;
        const depth = -vy * sp + vz * cp, up = vy * cp + vz * sp;
        return [W / 2 + (rx / depth) * F, H / 2 - (up / depth) * F, depth] as const;
    };
};

const shade = (rgb: number[], k: number) => `rgb(${rgb.map(v => Math.round(v * k)).join(",")})`;

const draw = (ctx: CanvasRenderingContext2D, c: Cam, b: number[], roads: number[], g: string, s: any, layers: Record<string, boolean>) => {
    const P = camera(c);
    ctx.fillStyle = C.ground;
    ctx.fillRect(0, 0, W, H);
    const onScreen = (p: readonly number[]) => p[2] > 1 && p[0] > -200 && p[0] < W + 200 && p[1] > -200 && p[1] < H + 200;

    // Water and greenery: merge each row into runs of equal cells, one quad per run.
    for (let cz = 0; g.length === GROUND_N * GROUND_N && cz < GROUND_N; cz++) {
        for (let cx = 0; cx < GROUND_N;) {
            const v = g[cz * GROUND_N + cx];
            let end = cx + 1;
            while (end < GROUND_N && g[cz * GROUND_N + end] === v) end++;
            if (v !== "0") {
                const x0 = GROUND_MIN + cx * GROUND_CELL, x1 = GROUND_MIN + end * GROUND_CELL, z0 = GROUND_MIN + cz * GROUND_CELL, z1 = z0 + GROUND_CELL;
                const q = [P(x0, 0, z0), P(x1, 0, z0), P(x1, 0, z1), P(x0, 0, z1)];
                if (q.every(p => p[2] > 1) && q.some(onScreen)) {
                    ctx.fillStyle = v === "1" ? C.water : C.green;
                    ctx.beginPath(); ctx.moveTo(q[0][0], q[0][1]); for (const p of q) ctx.lineTo(p[0], p[1]); ctx.closePath(); ctx.fill();
                    ctx.strokeStyle = ctx.fillStyle; ctx.lineWidth = 1; ctx.stroke(); // hide seams between rows
                }
            }
            cx = end;
        }
    }

    if (layers.roads) {
        ctx.lineCap = "round"; ctx.lineJoin = "round";
        const SEG = 8, curves: (readonly [number, number, number])[][] = [];
        for (let i = 0; i + 7 < roads.length; i += 8) { // sample each bezier once
            const pts = [];
            for (let k = 0; k <= SEG; k++) {
                const t = k / SEG, u = 1 - t, a = u * u * u, bb = 3 * u * u * t, cc = 3 * u * t * t, d = t * t * t;
                pts.push(P(a * roads[i] + bb * roads[i + 2] + cc * roads[i + 4] + d * roads[i + 6], 0,
                    a * roads[i + 1] + bb * roads[i + 3] + cc * roads[i + 5] + d * roads[i + 7]));
            }
            if (pts.some(onScreen) && pts.every(p => p[2] > 1)) curves.push(pts);
        }
        for (const [color, w] of [[C.roadEdge, 13], [C.road, 10]] as const) {
            ctx.strokeStyle = color;
            for (const pts of curves) {
                ctx.lineWidth = Math.max(1, (w * F) / pts[SEG / 2][2]);
                ctx.beginPath(); ctx.moveTo(pts[0][0], pts[0][1]);
                for (const p of pts) ctx.lineTo(p[0], p[1]);
                ctx.stroke();
            }
        }
        // Basic markings: dashed centre line, only when zoomed in enough to read.
        ctx.strokeStyle = C.marking;
        for (const pts of curves) {
            const px = (10 * F) / pts[SEG / 2][2];
            if (px < 6) continue;
            ctx.lineWidth = Math.max(1, px * 0.06);
            ctx.beginPath();
            for (let k = 0; k < SEG; k += 2) { ctx.moveTo(pts[k][0], pts[k][1]); ctx.lineTo(pts[k + 1][0], pts[k + 1][1]); }
            ctx.stroke();
        }
    }

    if (layers.buildings) {
        // Painter's algorithm: far buildings first. ponytail: O(n log n) per redraw on canvas 2D, move to WebGL if big cities stutter.
        const order: [number, number][] = [];
        for (let i = 0; i < b.length; i += 7) { const p = P(b[i], 0, b[i + 1]); if (onScreen(p)) order.push([p[2], i]); }
        order.sort((x, y) => y[0] - x[0]);
        const lx = -0.53, lz = -0.85; // light from the south-west
        const back = c.dist * Math.cos(c.pitch * RAD), camX = c.tx - back * Math.sin(c.yaw * RAD), camZ = c.tz - back * Math.cos(c.yaw * RAD);
        for (const [depth, i] of order) {
            const [x, z, hw, hd, h, yaw100, kind] = b.slice(i, i + 7);
            if ((Math.max(hw, hd) * F) / depth < 0.6) continue; // sub-pixel
            const a = (yaw100 / 100) * RAD, fx = Math.sin(a), fz = Math.cos(a), rx = Math.cos(a), rz = -Math.sin(a);
            const ground = [[1, 1], [1, -1], [-1, -1], [-1, 1]].map(([u, v]) => [x + rx * hw * u + fx * hd * v, z + rz * hw * u + fz * hd * v]);
            const lo = ground.map(([gx, gz]) => P(gx, 0, gz)), hi = ground.map(([gx, gz]) => P(gx, h, gz));
            if (hi.some(p => p[2] <= 1)) continue;
            for (let k = 0; k < 4; k++) {
                const n = (k + 1) % 4, q = [lo[k], lo[n], hi[n], hi[k]];
                const ex = ground[n][0] - ground[k][0], ez = ground[n][1] - ground[k][1], len = Math.hypot(ex, ez) || 1;
                const nx = -ez / len, nz = ex / len; // outward normal (corners run clockwise seen from above)
                if (nx * (camX - ground[k][0]) + nz * (camZ - ground[k][1]) <= 0) continue; // back face
                const lit = 0.78 + 0.22 * Math.max(0, nx * lx + nz * lz);
                ctx.fillStyle = shade(C.wall[kind] ?? C.wall[0], lit);
                ctx.beginPath(); ctx.moveTo(q[0][0], q[0][1]); for (const p of q) ctx.lineTo(p[0], p[1]); ctx.closePath(); ctx.fill();
            }
            ctx.fillStyle = C.roof[kind] ?? C.roof[0];
            ctx.beginPath(); ctx.moveTo(hi[0][0], hi[0][1]); for (const p of hi) ctx.lineTo(p[0], p[1]); ctx.closePath(); ctx.fill();
        }
    }

    if (layers.ems && s) {
        const pin = (x: number, z: number, color: string, r: number) => {
            const g = P(x, 0, z), t = P(x, 60, z);
            if (!onScreen(g) || t[2] <= 1) return;
            ctx.strokeStyle = color; ctx.lineWidth = 2;
            ctx.beginPath(); ctx.moveTo(g[0], g[1]); ctx.lineTo(t[0], t[1]); ctx.stroke();
            ctx.fillStyle = color; ctx.strokeStyle = C.outline; ctx.lineWidth = 3;
            ctx.beginPath(); ctx.arc(t[0], t[1], r, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
        };
        for (const st of (s.stations ?? []).filter((st: { kind: string }) => st.kind === "rtw")) pin(st.x, st.z, C.depot, 11);
        for (const h of s.hospitals ?? []) pin(h.x, h.z, C.hospital, 13);
        for (const v of s.vehicles ?? []) {
            const g = P(v.x, 3, v.z);
            if (!onScreen(g)) continue;
            ctx.fillStyle = C.amb; ctx.strokeStyle = C.outline; ctx.lineWidth = 2;
            ctx.beginPath(); ctx.arc(g[0], g[1], 6, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
        }
    }
};

export const Map3D = ({ onClose, de, embedded }: { onClose?: () => void; de: boolean; embedded?: boolean }) => {
    const t = (d: string, e: string) => (de ? d : e);
    const b = useValue(buildings$), g = useValue(ground$), roads = useValue(roads$), s = useValue(state$);
    const canvas = useRef<HTMLCanvasElement>(null);
    const home = useMemo(() => bounds(roads, b), [roads, b.length === 0]);
    const [cam, setCam] = useState<Cam>(home);
    const [layers, setLayers] = useState({ buildings: true, roads: true, ems: true });
    const drag = useRef<{ x: number; y: number; rotate: boolean } | null>(null);

    useEffect(() => { // compute on open, free on close
        trigger("realisticEMS", "refreshMap3d");
        return () => trigger("realisticEMS", "closeMap3d");
    }, []);
    useEffect(() => { setCam(home); }, [home]);
    useEffect(() => {
        const ctx = canvas.current?.getContext("2d");
        if (ctx) draw(ctx, cam, b, roads, g, s, layers);
    }, [cam, b, roads, g, s, layers]);

    const scale = (e: MouseEvent<HTMLElement>) => { const r = e.currentTarget.getBoundingClientRect(); return W / Math.min(r.width, (r.height * W) / H); }; // canvas is letterboxed (object-fit: contain)
    const onMove = (e: MouseEvent<HTMLDivElement>) => {
        const d = drag.current;
        if (!d) return;
        const k = scale(e), dx = (e.clientX - d.x) * k, dy = (e.clientY - d.y) * k;
        drag.current = { ...d, x: e.clientX, y: e.clientY };
        setCam(c => {
            if (d.rotate) return { ...c, yaw: c.yaw - dx * 0.25, pitch: Math.min(90, Math.max(20, c.pitch + dy * 0.2)) };
            const m = c.dist / F, cy = Math.cos(c.yaw * RAD), sy = Math.sin(c.yaw * RAD), fy = dy * m / Math.sin(c.pitch * RAD);
            return { ...c, tx: c.tx - cy * dx * m + sy * fy, tz: c.tz + sy * dx * m + cy * fy };
        });
    };
    const zoom = (f: number) => setCam(c => ({ ...c, dist: Math.min(home.dist * 3, Math.max(80, c.dist * f)) }));
    const is2D = cam.pitch >= 89;

    const chips = (
                    <div className={styles.chips}>
            {(["buildings", "roads", "ems"] as const).map(l => (
                <button key={l} className={layers[l] ? styles.chipOn : styles.chip} onClick={() => setLayers({ ...layers, [l]: !layers[l] })}>
                    {layers[l] && "✓ "}{{ buildings: t("Gebäude", "Buildings"), roads: t("Strassen", "Roads"), ems: t("Rettungsdienst", "EMS") }[l]}
                </button>
            ))}
        </div>
    );
    return (
        <div className={embedded ? styles.embed : styles.scrim}>
            <div className={embedded ? styles.embedSheet : styles.sheet}>
                {!embedded && <header className={styles.appBar}>
                    <span className={styles.title}>{t("3D-Karte", "3D map")}</span>
                    {chips}
                    <button className={styles.iconBtn} onClick={onClose} title={t("Schliessen", "Close")}>✕</button>
                </header>}
                <div className={styles.map}
                    onMouseDown={e => { drag.current = { x: e.clientX, y: e.clientY, rotate: e.button !== 0 || e.shiftKey }; }}
                    onMouseMove={onMove} onMouseUp={() => { drag.current = null; }} onMouseLeave={() => { drag.current = null; }}
                    onContextMenu={e => e.preventDefault()}
                    onWheel={e => { e.stopPropagation(); zoom(e.deltaY < 0 ? 0.85 : 1.18); }}>
                    <canvas ref={canvas} width={W} height={H} className={styles.canvas} />
                    {embedded && <div className={styles.layerRow} onMouseDown={e => e.stopPropagation()}>{chips}</div>}
                    {b.length === 0 && <div className={styles.empty}>{t("Berechne…", "Computing…")}</div>}

                    {!embedded && <div className={styles.segmented} onMouseDown={e => e.stopPropagation()}>
                        <button className={is2D ? styles.segOn : styles.seg} onClick={() => setCam({ ...cam, pitch: 90 })}>2D</button>
                        <button className={!is2D ? styles.segOn : styles.seg} onClick={() => setCam({ ...cam, pitch: 55 })}>3D</button>
                    </div>}
                    <div className={styles.rail} onMouseDown={e => e.stopPropagation()}>
                        <button className={styles.iconBtn} onClick={() => zoom(0.8)} title="Zoom in">+</button>
                        <button className={styles.iconBtn} onClick={() => zoom(1.25)} title="Zoom out">−</button>
                        <button className={styles.iconBtn} onClick={() => setCam({ ...cam, yaw: 0 })} title={t("Norden", "North up")}>
                            <span className={styles.compass} style={{ transform: `rotate(${cam.yaw}deg)` }}>N</span>
                        </button>
                        <button className={styles.iconBtn} onClick={() => setCam(home)} title={t("Ganze Stadt", "Whole city")}>◎</button>
                    </div>
                    <button className={styles.fab} onMouseDown={e => e.stopPropagation()} onClick={() => trigger("realisticEMS", "refreshMap3d")}>
                        ↻ {t("Neu berechnen", "Refresh")}
                    </button>
                    <div className={styles.hint}>{t("Ziehen: verschieben · Rechtsklick/Shift ziehen: drehen & neigen · Rad: Zoom",
                        "Drag: pan · Right/Shift-drag: rotate & tilt · Wheel: zoom")}</div>
                </div>
            </div>
        </div>
    );
};
