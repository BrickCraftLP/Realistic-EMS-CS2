import { MouseEvent, useEffect, useMemo, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import styles from "./map3d.module.scss";
import pro from "./map3d-pro.module.scss";
import { Cam, camera } from "./map3d";

// Experimental "3D Beta" map (Setting.EnableMap3dPro). Data from Map3dProSystem.cs; the classic map3d.tsx is untouched.
// The game UI has no WebGL, so this is a Canvas 2D low-poly renderer: painter-sorted flat-shaded shapes, Apple-Maps palette.
// Layers arrive as separate strings (one per frame from C#): b/t/r = base64 int16, w/g = 512² char fields.
const b$ = bindValue<string>("realisticEMS", "m3dB", ""), t$ = bindValue<string>("realisticEMS", "m3dT", ""), r$ = bindValue<string>("realisticEMS", "m3dR", "");
const w$ = bindValue<string>("realisticEMS", "m3dW", ""), g$ = bindValue<string>("realisticEMS", "m3dG", "");
const status$ = bindValue<number>("realisticEMS", "m3dStatus", 0); // 0 closed, 1 waiting for framerate headroom, 2 building, 3 ready
const live$ = bindValue<boolean>("realisticEMS", "m3dLive", false); // true = animating is free (sim paused / headroom)
const state$ = bindValue<any>("realisticEMS", "state", null);

// base64 -> Int16Array (little-endian, matches C# Buffer.BlockCopy).
const B64 = new Uint8Array(128);
"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/".split("").forEach((c, i) => { B64[c.charCodeAt(0)] = i; });
const unpack = (s: string) => {
    let n = s.length;
    while (n && s[n - 1] === "=") n--;
    const out = new Uint8Array((n * 3) >> 2);
    for (let i = 0, j = 0; i < n; i += 4) {
        const v = (B64[s.charCodeAt(i)] << 18) | (B64[s.charCodeAt(i + 1)] << 12) | ((B64[s.charCodeAt(i + 2)] | 0) << 6) | (B64[s.charCodeAt(i + 3)] | 0);
        if (j < out.length) out[j++] = v >> 16;
        if (j < out.length) out[j++] = (v >> 8) & 255;
        if (j < out.length) out[j++] = v & 255;
    }
    return new Int16Array(out.buffer, 0, out.length >> 1);
};

// Session cache outside React: decoded + precomputed layers survive closing the map, so reopening draws instantly.
// A layer is only recomputed when C# sends a different string for it ("" = nothing yet, keep what we have).
const memo = new Map<string, { src: string; val: any }>();
const cached = <T,>(key: string, src: string, make: (s: string) => T): T | undefined => {
    const m = memo.get(key);
    if (!src || m?.src === src) return m?.val;
    const val = make(src);
    memo.set(key, { src, val });
    return val;
};
const NONE = new Int16Array(0);

const BATCH = 300;
const W = 1600, H = 900, F = 1100, RAD = Math.PI / 180;
const N = 512, MIN = -7168, CELL = 14336 / N; // ground fields, see Map3dProSystem.WaterN

const C = {
    ground: "#F1EFE9", green: "#C7E5B3", water: "#A9D5F3", deep: "#93C7EE", shore: "#CFE8F9",
    sidewalk: "#E0DFD8", asphalt: "#6F737B", marking: "#FFFFFF", pillar: "#C4C3BD", shadow: "rgba(60, 72, 60, 0.13)",
    wall: [[233, 230, 223], [238, 200, 194], [192, 207, 238]], roof: ["#FAF9F6", "#F9E3DF", "#E3EAFF"], // kind 0 generic, 1 hospital, 2 depot
    canopy: ["#86C271", "#78B765", "#94CC7E"], light: "rgba(178, 222, 152, 0.9)", trunk: "#8C6E52",
    hospital: "#D93A2E", depot: "#415F91", outline: "#FFFFFF", text: "#2B2D31",
};
const STATUS: Record<string, string> = { frei: "#2E7D32", Anfahrt: "#D32F2F", "am Einsatzort": "#EF6C00", Transport: "#1565C0", "Rückfahrt": "#78909C" };
const SUN_X = 0.53, SUN_Z = 0.85, SHADOW = 0.55; // shadows fall away from the south-west light

/** Marching squares over a char field ('0'..'9'): filled polygons (world x,z pairs) + boundary segments. Full cells merge into row runs. */
const contour = (f: string, th: number) => {
    const polys: number[][] = [], edges: number[] = [];
    if (!f || f.length !== N * N) return { polys, edges };
    const v = (x: number, z: number) => f.charCodeAt(z * N + x) - 48;
    const p = (i: number) => MIN + (i + 0.5) * CELL;
    for (let z = 0; z < N - 1; z++) {
        let run = -1;
        const flush = (x: number) => { if (run >= 0) polys.push([p(run), p(z), p(x), p(z), p(x), p(z + 1), p(run), p(z + 1)]); run = -1; };
        for (let x = 0; x < N - 1; x++) {
            const c = [v(x, z), v(x + 1, z), v(x + 1, z + 1), v(x, z + 1)];
            if (c[0] >= th && c[1] >= th && c[2] >= th && c[3] >= th) {
                if (run < 0) run = x; else if (x - run >= 16) { flush(x); run = x; } // short runs: long quads get clipped near the camera
                continue;
            }
            flush(x);
            if (c[0] < th && c[1] < th && c[2] < th && c[3] < th) continue;
            const cx = [x, x + 1, x + 1, x], cz = [z, z, z + 1, z + 1], poly: number[] = [], cross: number[] = [];
            for (let k = 0; k < 4; k++) {
                const n = (k + 1) % 4, a = c[k], b = c[n];
                if (a >= th) poly.push(p(cx[k]), p(cz[k]));
                if ((a >= th) !== (b >= th)) {
                    const t = (th - a) / (b - a), X = p(cx[k]) + (p(cx[n]) - p(cx[k])) * t, Z = p(cz[k]) + (p(cz[n]) - p(cz[k])) * t;
                    poly.push(X, Z); cross.push(X, Z);
                }
            }
            polys.push(poly);
            for (let k = 0; k + 3 < cross.length; k += 4) edges.push(cross[k], cross[k + 1], cross[k + 2], cross[k + 3]);
        }
        flush(N - 1);
    }
    return { polys, edges };
};

interface Road { pts: number[]; w: number; e: number; }
const SEG = 8;
const roadsOf = (r: ArrayLike<number>): Road[] => {
    const out: Road[] = [];
    for (let i = 0; i + 9 < r.length; i += 10) {
        const pts: number[] = [];
        for (let k = 0; k <= SEG; k++) {
            const t = k / SEG, u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            pts.push(a * r[i] + b * r[i + 2] + c * r[i + 4] + d * r[i + 6], a * r[i + 1] + b * r[i + 3] + c * r[i + 5] + d * r[i + 7]);
        }
        out.push({ pts, w: Math.max(4, r[i + 8]), e: r[i + 9] >= 4 ? r[i + 9] : 0 });
    }
    return out.sort((a, b) => a.e - b.e);
};

const home = (r: ArrayLike<number>): Cam => {
    let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
    for (let i = 0; i + 9 < r.length; i += 10) for (const k of [0, 6]) { x0 = Math.min(x0, r[i + k]); x1 = Math.max(x1, r[i + k]); z0 = Math.min(z0, r[i + k + 1]); z1 = Math.max(z1, r[i + k + 1]); }
    if (!isFinite(x0)) return { tx: 0, tz: 0, yaw: 0, pitch: 55, dist: 2000 };
    return { tx: (x0 + x1) / 2, tz: (z0 + z1) / 2, yaw: 0, pitch: 55, dist: Math.max(600, Math.max(x1 - x0, z1 - z0) * 0.9) };
};

const hash = (x: number, z: number) => ((x * 73856093) ^ (z * 19349663)) >>> 0;
const rgb = (c: number[], k: number) => `rgb(${Math.round(c[0] * k)},${Math.round(c[1] * k)},${Math.round(c[2] * k)})`;

interface Veh { x0: number; z0: number; x1: number; z1: number; t0: number; a: number; }
const MOVE_MS = 500; // state arrives every 30 sim frames
const at = (v: Veh, now: number, live: boolean) => { const t = live ? Math.min(1, (now - v.t0) / MOVE_MS) : 1; return [v.x0 + (v.x1 - v.x0) * t, v.z0 + (v.z1 - v.z0) * t] as const; };

type Contour = ReturnType<typeof contour>;
interface Ground { green: Contour; water: Contour; deep: Contour; roads: Road[]; }
interface Scene { cam: Cam; b: Int16Array; t: Int16Array; s: any; layers: Record<string, boolean>; live: boolean; ground: Ground; veh: Map<number, Veh>; }

const draw = (ctx: CanvasRenderingContext2D, sc: Scene, now: number) => {
    const { cam, b, t, s, layers, ground, veh, live } = sc;
    const P = camera(cam);
    const close = cam.dist < 1200;
    const vis = (q: readonly number[]) => q[2] > 1 && q[0] > -300 && q[0] < W + 300 && q[1] > -300 && q[1] < H + 300;
    const px = (m: number, depth: number) => (m * F) / depth;
    // Gameface tessellates each canvas path natively; one path with the whole city crashed the game. Paint every BATCH subpaths.
    // ponytail: fixed batch size; lower BATCH if very large cities still crash.
    let sub = 0;
    const batch = (paint: () => void) => { if (++sub >= BATCH) { paint(); ctx.beginPath(); sub = 0; } };
    const begin = () => { ctx.beginPath(); sub = 0; };
    const poly = (pts: (readonly number[])[]) => { ctx.moveTo(pts[0][0], pts[0][1]); for (let k = 1; k < pts.length; k++) ctx.lineTo(pts[k][0], pts[k][1]); ctx.closePath(); };

    ctx.fillStyle = C.ground; ctx.fillRect(0, 0, W, H);

    // Ground fields: one path per colour, so neighbouring cells have no seams.
    const fill = (polys: number[][], color: string) => {
        ctx.fillStyle = color; begin();
        for (const p of polys) {
            const q: (readonly number[])[] = [];
            for (let k = 0; k < p.length; k += 2) q.push(P(p[k], 0, p[k + 1]));
            if (q.every(x => x[2] > 1) && q.some(vis)) { poly(q); batch(() => ctx.fill()); }
        }
        ctx.fill();
    };
    fill(ground.green.polys, C.green);
    fill(ground.water.polys, C.water);
    fill(ground.deep.polys, C.deep);
    ctx.strokeStyle = C.shore; ctx.lineWidth = 2.5; ctx.lineCap = "round"; begin();
    for (let k = 0; k + 3 < ground.water.edges.length; k += 4) {
        const e = ground.water.edges, a = P(e[k], 0, e[k + 1]), b = P(e[k + 2], 0, e[k + 3]);
        if (a[2] > 1 && b[2] > 1 && (vis(a) || vis(b))) { ctx.moveTo(a[0], a[1]); ctx.lineTo(b[0], b[1]); batch(() => ctx.stroke()); }
    }
    ctx.stroke();

    // Roads: projected once per frame; ground roads first, elevated later above the shadows.
    ctx.lineCap = "round"; ctx.lineJoin = "round";
    const proj = layers.roads ? ground.roads.map(r => {
        const q: (readonly number[])[] = [];
        for (let k = 0; k < r.pts.length; k += 2) q.push(P(r.pts[k], r.e, r.pts[k + 1]));
        return q.every(x => x[2] > 1) && q.some(vis) ? q : null;
    }) : [];
    const stroke = (q: (readonly number[])[], color: string, lw: number) => {
        ctx.strokeStyle = color; ctx.lineWidth = Math.max(1, lw); ctx.beginPath(); ctx.moveTo(q[0][0], q[0][1]);
        for (const p of q) ctx.lineTo(p[0], p[1]);
        ctx.stroke();
    };
    const markings = (q: (readonly number[])[], w: number) => {
        const lw = px(w, q[SEG / 2][2]);
        if (lw < 9) return;
        ctx.strokeStyle = C.marking; ctx.lineWidth = Math.max(1, lw * 0.05); ctx.beginPath();
        for (let k = 0; k < SEG; k += 2) { ctx.moveTo(q[k][0], q[k][1]); ctx.lineTo(q[k + 1][0], q[k + 1][1]); }
        ctx.stroke();
    };
    const road = (r: Road, q: (readonly number[])[]) => {
        const m = q[SEG / 2][2];
        stroke(q, C.sidewalk, px(r.w + 3, m));
        stroke(q, C.asphalt, px(r.w - 1, m));
        markings(q, r.w);
    };
    ground.roads.forEach((r, i) => { const q = proj[i]; if (q && r.e === 0) stroke(q, C.sidewalk, px(r.w + 3, q[SEG / 2][2])); });
    ground.roads.forEach((r, i) => { const q = proj[i]; if (q && r.e === 0) { stroke(q, C.asphalt, px(r.w - 1, q[SEG / 2][2])); markings(q, r.w); } });

    // Shadows: one translucent path (union, no double-darkening).
    const B = layers.buildings ? b : NONE, T = layers.trees ? t : NONE;
    const corners = (i: number) => {
        const [x, z, hw, hd, , yaw100] = B.slice(i, i + 6);
        const a = (yaw100 / 100) * RAD, fx = Math.sin(a), fz = Math.cos(a), rx = Math.cos(a), rz = -Math.sin(a);
        return [[1, 1], [1, -1], [-1, -1], [-1, 1]].map(([u, v]) => [x + rx * hw * u + fx * hd * v, z + rz * hw * u + fz * hd * v]);
    };
    ctx.fillStyle = C.shadow; begin();
    for (let i = 0; i < B.length; i += 7) {
        if (!vis(P(B[i], 0, B[i + 1]))) continue;
        const g = corners(i), h = B[i + 4] * SHADOW, lo = g.map(([x, z]) => P(x, 0, z)), hi = g.map(([x, z]) => P(x + SUN_X * h, 0, z + SUN_Z * h));
        if (lo.concat(hi).some(p => p[2] <= 1)) continue;
        poly(lo); poly(hi);
        for (let k = 0; k < 4; k++) { const n = (k + 1) % 4; poly([lo[k], lo[n], hi[n], hi[k]]); }
        batch(() => ctx.fill());
    }
    if (close) for (let i = 0; i < T.length; i += 4) {
        const h = T[i + 3] * SHADOW * 0.7, g = P(T[i] + SUN_X * h, 0, T[i + 1] + SUN_Z * h);
        if (!vis(g)) continue;
        const r = px(T[i + 2], g[2]);
        ctx.moveTo(g[0] + r, g[1]); ctx.arc(g[0], g[1], r, 0, Math.PI * 2);
        batch(() => ctx.fill());
    }
    ctx.fill();

    // Elevated roads (bridges / interchanges): ground shadow, pillars, deck. Sorted low to high.
    ground.roads.forEach((r, i) => {
        const q = proj[i];
        if (!q || r.e === 0) return;
        const sh: (readonly number[])[] = [];
        for (let k = 0; k < r.pts.length; k += 2) sh.push(P(r.pts[k] + SUN_X * r.e * SHADOW, 0, r.pts[k + 1] + SUN_Z * r.e * SHADOW));
        if (sh.every(p => p[2] > 1)) stroke(sh, C.shadow, px(r.w, sh[SEG / 2][2]));
        for (const k of [0, SEG / 2, SEG]) {
            const g = P(r.pts[k * 2], 0, r.pts[k * 2 + 1]);
            if (g[2] > 1) { ctx.strokeStyle = C.pillar; ctx.lineWidth = Math.max(1, px(2, g[2])); ctx.beginPath(); ctx.moveTo(g[0], g[1]); ctx.lineTo(q[k][0], q[k][1]); ctx.stroke(); }
        }
        road(r, q);
    });

    // Box (building / vehicle body): visible walls lit by the sun, then the roof.
    const back = cam.dist * Math.cos(cam.pitch * RAD), camX = cam.tx - back * Math.sin(cam.yaw * RAD), camZ = cam.tz - back * Math.cos(cam.yaw * RAD);
    const box = (g: number[][], y0: number, h: number, wall: number[], roof: string, tint: number) => {
        const lo = g.map(([x, z]) => P(x, y0, z)), hi = g.map(([x, z]) => P(x, y0 + h, z));
        if (hi.some(p => p[2] <= 1) || lo.some(p => p[2] <= 1)) return;
        for (let k = 0; k < 4; k++) {
            const n = (k + 1) % 4, ex = g[n][0] - g[k][0], ez = g[n][1] - g[k][1], len = Math.hypot(ex, ez) || 1, nx = -ez / len, nz = ex / len;
            if (nx * (camX - g[k][0]) + nz * (camZ - g[k][1]) <= 0) continue;
            ctx.fillStyle = rgb(wall, tint * (0.8 + 0.2 * Math.max(0, -(nx * SUN_X + nz * SUN_Z))));
            ctx.beginPath(); poly([lo[k], lo[n], hi[n], hi[k]]); ctx.fill();
        }
        ctx.fillStyle = roof; ctx.beginPath(); poly(hi); ctx.fill();
        ctx.strokeStyle = "rgba(0, 0, 0, 0.06)"; ctx.lineWidth = 1; ctx.stroke();
    };

    const tree = (i: number) => {
        const [x, z, r, h] = T.slice(i, i + 4), cy = Math.max(r, h - r);
        const g = P(x, 0, z), c = P(x, cy, z);
        if (c[2] <= 1) return;
        const rp = px(r, c[2]);
        ctx.strokeStyle = C.trunk; ctx.lineWidth = Math.max(1, rp * 0.18);
        ctx.beginPath(); ctx.moveTo(g[0], g[1]); ctx.lineTo(c[0], c[1]); ctx.stroke();
        ctx.fillStyle = C.canopy[hash(x, z) % 3]; ctx.beginPath(); ctx.arc(c[0], c[1], rp, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = C.light; ctx.beginPath(); ctx.arc(c[0] - rp * 0.3, c[1] - rp * 0.35, rp * 0.5, 0, Math.PI * 2); ctx.fill();
    };

    const vehicles = layers.ems && s ? (s.vehicles ?? []) as any[] : [];
    const vehicle = (v: any) => {
        const st = veh.get(v.id);
        if (!st) return;
        const [x, z] = at(st, now, live), color = STATUS[v.status] ?? C.hospital;
        const g = P(x, 0, z);
        if (!vis(g)) return;
        if (px(3, g[2]) < 5) { // too small for a model: Apple-style dot
            ctx.fillStyle = color; ctx.strokeStyle = C.outline; ctx.lineWidth = 2.5;
            ctx.beginPath(); ctx.arc(g[0], g[1] - 4, 6, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
            return;
        }
        const fx = Math.sin(st.a), fz = Math.cos(st.a), rx = Math.cos(st.a), rz = -Math.sin(st.a);
        const fp = (hw: number, hd: number, off: number) => [[1, 1], [1, -1], [-1, -1], [-1, 1]].map(([u, w]) => [x + rx * hw * u + fx * (hd * w + off), z + rz * hw * u + fz * (hd * w + off)]);
        box(fp(1.2, 2.2, -0.8), 0.4, 2.4, [246, 246, 244], color, 1);  // patient box with status-coloured roof
        box(fp(1.15, 0.9, 2.2), 0.4, 1.7, [236, 236, 234], "#FFFFFF", 1); // cab
        if (close) {
            const t = P(x, 5, z);
            ctx.font = "600 13px sans-serif"; ctx.textAlign = "center";
            ctx.lineWidth = 3; ctx.strokeStyle = C.outline; ctx.strokeText(v.callsign, t[0], t[1]);
            ctx.fillStyle = color; ctx.fillText(v.callsign, t[0], t[1]);
        }
    };

    // Painter's algorithm over buildings, (close-up) trees and vehicles. ponytail: O(n log n) per frame; spatial bucketing if huge cities stutter.
    const items: number[] = []; // depth, kind, index triples
    for (let i = 0; i < B.length; i += 7) { const p = P(B[i], 0, B[i + 1]); if (vis(p) && px(Math.max(B[i + 2], B[i + 3]), p[2]) >= 0.6) items.push(p[2], 0, i); }
    if (close) for (let i = 0; i < T.length; i += 4) { const p = P(T[i], 0, T[i + 1]); if (vis(p)) items.push(p[2], 1, i); }
    vehicles.forEach((v, i) => { const st = veh.get(v.id); if (st) { const [x, z] = at(st, now, live); items.push(P(x, 0, z)[2], 2, i); } });
    const order = Array.from({ length: items.length / 3 }, (_, k) => k).sort((a, b) => items[b * 3] - items[a * 3]);

    if (!close) { // far: batch every canopy into three fills, no sorting
        for (let c = 0; c < 3; c++) {
            ctx.fillStyle = C.canopy[c]; begin();
            for (let i = 0; i < T.length; i += 4) {
                if (hash(T[i], T[i + 1]) % 3 !== c) continue;
                const p = P(T[i], Math.max(T[i + 2], T[i + 3] - T[i + 2]), T[i + 1]);
                if (!vis(p)) continue;
                const r = Math.max(0.8, px(T[i + 2], p[2]));
                ctx.moveTo(p[0] + r, p[1]); ctx.arc(p[0], p[1], r, 0, Math.PI * 2);
                batch(() => ctx.fill());
            }
            ctx.fill();
        }
    }
    for (const k of order) {
        const kind = items[k * 3 + 1], i = items[k * 3 + 2];
        if (kind === 0) {
            const t = B[i + 6], tint = 0.97 + (hash(B[i], B[i + 1]) % 7) / 200;
            box(corners(i), 0, B[i + 4], C.wall[t] ?? C.wall[0], C.roof[t] ?? C.roof[0], tint);
        } else if (kind === 1) tree(i);
        else vehicle(vehicles[i]);
    }

    // Landmark pins for hospitals and depots, always on top.
    if (layers.ems && s) {
        const pin = (x: number, z: number, color: string, glyph: string, name: string) => {
            const g = P(x, 0, z), t = P(x, 45, z);
            if (!vis(g) || t[2] <= 1) return;
            ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(g[0], g[1]); ctx.lineTo(t[0], t[1]); ctx.stroke();
            ctx.fillStyle = color; ctx.strokeStyle = C.outline; ctx.lineWidth = 3;
            ctx.beginPath(); ctx.arc(t[0], t[1] - 13, 13, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
            ctx.fillStyle = C.outline; ctx.font = "700 14px sans-serif"; ctx.textAlign = "center"; ctx.textBaseline = "middle";
            ctx.fillText(glyph, t[0], t[1] - 12);
            ctx.textBaseline = "alphabetic";
            if (cam.dist < 2500 && name) {
                ctx.font = "600 13px sans-serif"; ctx.lineWidth = 3; ctx.strokeStyle = C.outline;
                ctx.strokeText(name, t[0], t[1] + 18); ctx.fillStyle = C.text; ctx.fillText(name, t[0], t[1] + 18);
            }
        };
        for (const st of (s.stations ?? []).filter((st: { kind: string }) => st.kind === "rtw")) pin(st.x, st.z, C.depot, "R", st.name);
        for (const h of s.hospitals ?? []) pin(h.x, h.z, C.hospital, "H", h.name);
    }

    // Aerial haze toward the horizon.
    if (cam.pitch < 75) {
        const fog = ctx.createLinearGradient(0, 0, 0, H * 0.4);
        fog.addColorStop(0, "rgba(241, 239, 233, 0.85)"); fog.addColorStop(1, "rgba(241, 239, 233, 0)");
        ctx.fillStyle = fog; ctx.fillRect(0, 0, W, H * 0.4);
    }
};

export const Map3DPro = ({ de }: { de: boolean }) => {
    const t = (a: string, b: string) => (de ? a : b);
    const sb = useValue(b$), stt = useValue(t$), sr = useValue(r$), sw = useValue(w$), sg = useValue(g$);
    const status = useValue(status$), live = useValue(live$), s = useValue(state$);
    const canvas = useRef<HTMLCanvasElement>(null);
    const b = useMemo(() => cached("b", sb, unpack) ?? NONE, [sb]);
    const tr = useMemo(() => cached("t", stt, unpack) ?? NONE, [stt]);
    const r = useMemo(() => cached("r", sr, x => { const raw = unpack(x); return { raw, roads: roadsOf(raw) }; }) ?? { raw: NONE, roads: [] }, [sr]);
    const wat = useMemo(() => cached("w", sw, x => ({ water: contour(x, 0.5), deep: contour(x, 3.5) })) ?? { water: contour("", 0), deep: contour("", 0) }, [sw]);
    const green = useMemo(() => cached("g", sg, x => contour(x, 0.5)) ?? contour("", 0), [sg]);
    const ground = useMemo<Ground>(() => ({ green, water: wat.water, deep: wat.deep, roads: r.roads }), [green, wat, r]);
    const hasCache = b.length > 0 || r.raw.length > 0;
    const start = useMemo(() => home(r.raw), [r.raw.length]);
    const [cam, setCam] = useState<Cam>(start);
    const [layers, setLayers] = useState({ buildings: true, trees: true, roads: true, ems: true });
    const drag = useRef<{ x: number; y: number; rotate: boolean } | null>(null);
    const veh = useRef(new Map<number, Veh>());
    const scene = useRef<Scene | null>(null);
    const dirty = useRef(true), lastVeh = useRef(0);

    scene.current = { cam, b, t: tr, s, layers, live, ground, veh: veh.current };
    useEffect(() => { trigger("realisticEMS", "m3dOpen"); return () => trigger("realisticEMS", "m3dClose"); }, []);
    useEffect(() => { setCam(start); }, [start]);
    useEffect(() => { dirty.current = true; }, [cam, b, tr, ground, layers, live]);
    useEffect(() => { // new vehicle snapshot: start interpolating from where each one is drawn now
        const now = performance.now(), seen = new Set<number>();
        for (const v of s?.vehicles ?? []) {
            seen.add(v.id);
            const old = veh.current.get(v.id);
            const [x, z] = old ? at(old, now, live) : [v.x, v.z];
            const dx = v.x - x, dz = v.z - z;
            veh.current.set(v.id, { x0: x, z0: z, x1: v.x, z1: v.z, t0: now, a: dx * dx + dz * dz > 0.25 ? Math.atan2(dx, dz) : old?.a ?? 0 });
        }
        for (const id of [...veh.current.keys()]) if (!seen.has(id)) veh.current.delete(id);
        // Throttled while the framerate gate is closed: vehicles jump every 2 s instead of animating.
        if (live || now - lastVeh.current > 2000) { dirty.current = true; lastVeh.current = now; }
    }, [s]);
    useEffect(() => { // redraw on demand; animate only while live and vehicles are moving
        let raf = 0;
        const loop = (now: number) => {
            const sc = scene.current, ctx = canvas.current?.getContext("2d");
            if (sc && ctx && (dirty.current || (sc.live && now - lastVeh.current < MOVE_MS + 50))) { dirty.current = false; draw(ctx, sc, now); }
            raf = requestAnimationFrame(loop);
        };
        raf = requestAnimationFrame(loop);
        return () => cancelAnimationFrame(raf);
    }, []);

    const scale = (e: MouseEvent<HTMLElement>) => { const r = e.currentTarget.getBoundingClientRect(); return W / Math.min(r.width, (r.height * W) / H); };
    const onMove = (e: MouseEvent<HTMLDivElement>) => {
        const dr = drag.current;
        if (!dr) return;
        const k = scale(e), dx = (e.clientX - dr.x) * k, dy = (e.clientY - dr.y) * k;
        drag.current = { ...dr, x: e.clientX, y: e.clientY };
        setCam(c => {
            if (dr.rotate) return { ...c, yaw: c.yaw - dx * 0.25, pitch: Math.min(90, Math.max(20, c.pitch + dy * 0.2)) };
            const m = c.dist / F, cy = Math.cos(c.yaw * RAD), sy = Math.sin(c.yaw * RAD), fy = dy * m / Math.sin(c.pitch * RAD);
            return { ...c, tx: c.tx - cy * dx * m + sy * fy, tz: c.tz + sy * dx * m + cy * fy };
        });
    };
    const zoom = (f: number) => setCam(c => ({ ...c, dist: Math.min(start.dist * 3, Math.max(60, c.dist * f)) }));
    const names = { buildings: t("Gebäude", "Buildings"), trees: t("Bäume", "Trees"), roads: t("Strassen", "Roads"), ems: t("Rettungsdienst", "EMS") };

    return (
        <div className={styles.embed}>
            <div className={styles.embedSheet}>
                <div className={styles.map}
                    onMouseDown={e => { drag.current = { x: e.clientX, y: e.clientY, rotate: e.button !== 0 || e.shiftKey }; }}
                    onMouseMove={onMove} onMouseUp={() => { drag.current = null; }} onMouseLeave={() => { drag.current = null; }}
                    onContextMenu={e => e.preventDefault()}
                    onWheel={e => { e.stopPropagation(); zoom(e.deltaY < 0 ? 0.85 : 1.18); }}>
                    <canvas ref={canvas} width={W} height={H} className={styles.canvas} />
                    <div className={styles.layerRow} onMouseDown={e => e.stopPropagation()}>
                        <div className={styles.chips}>
                            {(Object.keys(names) as (keyof typeof names)[]).map(l => (
                                <button key={l} className={layers[l] ? styles.chipOn : styles.chip} onClick={() => setLayers({ ...layers, [l]: !layers[l] })}>
                                    {layers[l] && "✓ "}{names[l]}
                                </button>
                            ))}
                        </div>
                    </div>
                    {hasCache && (status === 1 || status === 2) && (
                        <div className={styles.hint} style={{ top: "60rem", bottom: "auto" }}>
                            {status === 1 ? t("Aktualisierung wartet auf freie Leistung", "Update waits for spare performance") : t("Aktualisiere…", "Updating…")}
                        </div>
                    )}
                    {!hasCache && status !== 3 && (
                        <div className={status === 1 ? `${pro.loader} ${pro.paused}` : pro.loader}>
                            <div className={pro.indicator}><div className={pro.shape} /></div>
                            <div className={pro.label}>{status === 1 ? t("Wartet auf freie Leistung", "Waiting for spare performance") : t("Baue 3D-Karte…", "Building 3D map…")}</div>
                            {status === 1 && <div className={pro.sub}>{t("Pausiere die Simulation oder stelle sie langsam, damit die Bildrate nicht leidet.",
                                "Pause or slow down the simulation so the framerate is not affected.")}</div>}
                        </div>
                    )}
                    <div className={styles.rail} onMouseDown={e => e.stopPropagation()}>
                        <button className={styles.iconBtn} onClick={() => zoom(0.8)} title="Zoom in">+</button>
                        <button className={styles.iconBtn} onClick={() => zoom(1.25)} title="Zoom out">−</button>
                        <button className={styles.iconBtn} onClick={() => setCam({ ...cam, yaw: 0 })} title={t("Norden", "North up")}>
                            <span className={styles.compass} style={{ transform: `rotate(${cam.yaw}deg)` }}>N</span>
                        </button>
                        <button className={styles.iconBtn} onClick={() => setCam({ ...cam, pitch: cam.pitch >= 89 ? 55 : 90 })} title="2D / 3D">{cam.pitch >= 89 ? "3D" : "2D"}</button>
                        <button className={styles.iconBtn} onClick={() => setCam(start)} title={t("Ganze Stadt", "Whole city")}>◎</button>
                    </div>
                    <button className={styles.fab} onMouseDown={e => e.stopPropagation()} onClick={() => trigger("realisticEMS", "m3dOpen")}>
                        ↻ {t("Neu berechnen", "Refresh")}
                    </button>
                    <div className={styles.hint}>{t("Beta · Ziehen: verschieben · Rechtsklick/Shift ziehen: drehen & neigen · Rad: Zoom",
                        "Beta · Drag: pan · Right/Shift-drag: rotate & tilt · Wheel: zoom")}</div>
                </div>
            </div>
        </div>
    );
};
