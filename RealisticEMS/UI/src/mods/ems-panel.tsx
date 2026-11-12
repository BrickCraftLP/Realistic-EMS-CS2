import { MouseEvent, ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { FloatingButton, Panel, Scrollable } from "cs2/ui";
import { useLocalization } from "cs2/l10n";
import styles from "./ems-panel.module.scss";
import { Map3D } from "./map3d";

// Language follows the game: Setting.cs registers RealisticEMS.Lang = "de" for de-DE, "en" otherwise.
// ponytail: module-level flag set by the two root components each render; children render after them.
let DE = false;
const useLang = () => { DE = useLocalization().translate("RealisticEMS.Lang") === "de"; };
const t = (de: string, en: string) => (DE ? de : en);

// Snapshot written by DispatchUISystem.cs (C#). The UI only renders it.
interface Overview { total: number; free: number; onCall: number; returning: number; bedsUsed: number; bedsTotal: number; hospitals: number; depots: number; }
interface Vehicle { callsign: string; status: string; station: string; code: string; level: string; reason: string; units: string; destination: string; call: number; callNo: string; address: string; nef: boolean; id: number; ver: number; x: number; z: number; }
interface Hospital { name: string; used: number; beds: number; accepts: boolean; ambulances: number; x: number; z: number; }
interface Station { name: string; kind: "rtw" | "nef" | "police" | "fire"; total: number; free: number; onCall: number; x: number; z: number; }
// history: 12 ints per sample (oldest first): onCall, returning, total, bedsUsed, bedsTotal, new calls, then new calls per level (E D C B A O).
interface Stats { calls: number; sampleMinutes: number; history: number[]; levels: number[]; response: number[]; reasons: { reason: string; n: number }[]; }
interface Police { callsign: string; status: string; station: string; x: number; z: number; }
interface State { overview: Overview; vehicles: Vehicle[]; hospitals: Hospital[]; stations: Station[]; police: Police[]; stats: Stats; }

const open$ = bindValue<boolean>("realisticEMS", "open", false);
const debug$ = bindValue<boolean>("realisticEMS", "debug", false);
const state$ = bindValue<State | null>("realisticEMS", "state", null);
const roads$ = bindValue<number[]>("realisticEMS", "roads", []); // 8 ints per road: bezier a,b,c,d as x,z
const toggle = () => trigger("realisticEMS", "toggle");

// Levels in urgency order. Colors follow the MPDS convention (Echo darkest red).
const LEVELS = ["E", "D", "C", "B", "A", "O"] as const;
const LEVEL_NAME: Record<string, string> = { E: "Echo", D: "Delta", C: "Charlie", B: "Bravo", A: "Alpha", O: "Omega" };
const levelRank = (l: string) => { const i = LEVELS.indexOf(l as typeof LEVELS[number]); return i < 0 ? 99 : i; };

// Status strings come from DispatchUISystem.Status().
const STATUS_CLASS: Record<string, string> = {
    "frei": styles.sFree, "Anfahrt": styles.sEnRoute, "am Einsatzort": styles.sOnScene,
    "Transport": styles.sTransport, "Rückfahrt": styles.sReturning, "ausser Dienst": styles.sOff,
    "Streife": styles.sPatrol, // police only (DispatchUISystem.PoliceStatus)
};
const STATUS_EN: Record<string, string> = {
    "frei": "free", "Anfahrt": "en route", "am Einsatzort": "on scene", "Transport": "transporting",
    "Rückfahrt": "returning", "ausser Dienst": "out of service", "Streife": "patrol", "unterwegs": "on the move",
};
const statusText = (s: string) => t(s, STATUS_EN[s] ?? s);
const isActive = (v: { status: string }) => v.status === "Anfahrt" || v.status === "am Einsatzort" || v.status === "Transport";


export const EmsButton = () => {
    useLang();
    return <FloatingButton src="Media/Game/Icons/Healthcare.svg" tooltipLabel={t("Leitstelle", "Dispatch")} onSelect={toggle} selected={useValue(open$)} />;
};

const pct = (used: number, total: number) => (total > 0 ? Math.round((used / total) * 100) : 0);
const loadClass = (p: number) => (p >= 100 ? styles.loadFull : p >= 80 ? styles.loadHigh : styles.loadOk);

const LoadBar = ({ value }: { value: number }) => (
    <div className={styles.bar}><div className={`${styles.barFill} ${loadClass(value)}`} style={{ width: `${Math.min(100, value)}%` }} /></div>
);

const LevelBadge = ({ level, code }: { level: string; code: string }) => (
    <span className={`${styles.badge} ${styles["lv" + level]}`}>{code}</span>
);

const Status = ({ status }: { status: string }) => (
    <span className={styles.status}><span className={`${styles.dot} ${STATUS_CLASS[status] ?? styles.sOff}`} />{statusText(status)}</span>
);

const Empty = ({ children }: { children: string }) => <div className={styles.empty}>{children}</div>;

// ---- Lage: the current picture plus the last game day (history from DispatchUISystem) ----

const HISTORY = 256; // samples DispatchUISystem keeps: one game day
const CALL_BAR = 8; // samples per bar in the dispatch chart (~45 game minutes)
// ponytail: fixed 15 min target (Hilfsfrist) in game minutes; make it a setting if cities differ a lot.
const HILFSFRIST = 15;
const RESPONSE_BUCKETS = [5, 10, 15, 20, 30, Infinity];

interface Sample { onCall: number; returning: number; total: number; bedsUsed: number; bedsTotal: number; calls: number; levels: number[]; }

// Time window of all graphs on the Dashboard and Statistics (toggle in the page header).
const WINDOWS = [12, 24] as const;
type Win = typeof WINDOWS[number];

/** Fixed 24 h axis: leading nulls until a full day has been recorded. */
const samples = (st: Stats): (Sample | null)[] => {
    const h = st.history, out: (Sample | null)[] = [];
    for (let i = 0; i + 11 < h.length; i += 12) out.push({ levels: h.slice(i + 6, i + 12), onCall: h[i], returning: h[i + 1], total: h[i + 2], bedsUsed: h[i + 3], bedsTotal: h[i + 4], calls: h[i + 5] });
    return [...Array(Math.max(0, HISTORY - out.length)).fill(null), ...out];
};

const ago = (min: number) => {
    if (min <= 0) return t("jetzt", "now");
    const span = min < 60 ? `${min} min` : `${Math.floor(min / 60)} h ${String(min % 60).padStart(2, "0")} min`;
    return t(`vor ${span}`, `${span} ago`);
};


const TREND_N = 12; // points per trend card
/** Down-sample to TREND_N points (mean, or sum for counts); empty buckets stay null. */
const bucket = (xs: (number | null)[], sum = false) => {
    const k = Math.max(1, Math.ceil(xs.length / TREND_N)), out: (number | null)[] = [];
    for (let i = 0; i < xs.length; i += k) {
        const g = xs.slice(i, i + k).filter((x): x is number => x !== null);
        out.push(g.length ? g.reduce((n, x) => n + x, 0) / (sum ? 1 : g.length) : null);
    }
    return out;
};

/** Small trend card: title, current value, line with dots, dotted band = target range, time axis, in/out of range chip. */
const TrendCard = ({ title, value, series, max, range, hours, unit = "", note }: {
    title: string; value: string; series: (number | null)[]; max: number; range?: [number, number]; hours: number; unit?: string; note?: string;
}) => {
    const n = series.length, last = [...series].reverse().find(x => x !== null) ?? null;
    const ok = !range || last === null || (last >= range[0] && last <= range[1]);
    const color = ok ? "#2aa355" : "#d9372b";
    const y = (v: number) => 92 - (Math.min(v, max) / Math.max(1, max)) * 84; // % from top, padded so dots never clip
    const x = (i: number) => 4 + (i / Math.max(1, n - 1)) * 92;
    const pts = series.map((v, i) => v === null ? null : [x(i), y(v)] as const).filter((p): p is readonly [number, number] => p !== null);
    return (
        <section className={styles.block}>
            <div className={styles.blockHead}><h2>{title}</h2>{note && <span className={styles.figure}>{note}</span>}</div>
            <div className={styles.trendValue}>{value}</div>
            <div className={styles.trend}>
                {range && <div className={styles.trendBand} style={{ top: `${y(range[1]) - 6}%`, bottom: `${100 - y(range[0]) - 6}%` }} />}
                {pts.length > 1 && (
                    <svg className={styles.trendSvg} viewBox="0 0 100 100" preserveAspectRatio="none">
                        <polyline points={pts.map(p => p.join(",")).join(" ")} fill="none" style={{ stroke: color }} strokeWidth="3" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
                    </svg>
                )}
                {series.map((v, i) => v !== null && <div key={i} className={styles.trendDot} style={{ left: `${x(i)}%`, top: `${y(v)}%`, backgroundColor: color }} title={`${Math.round(v)}${unit}`} />)}
            </div>
            <div className={styles.axis}><span>−{hours} h</span><span>−{hours / 2} h</span><span>{t("jetzt", "now")}</span></div>
            {range && <span className={`${styles.rrChip} ${styles[ok ? "chip_ok" : "chip_bad"]} ${styles.trendChip}`}>{ok ? t("Im Bereich", "In range") : t("Ausserhalb", "Out of range")}</span>}
        </section>
    );
};

/** Horizontal bar with label and count: for "how many of each". */
const CountRow = ({ label, n, max, cls, pip, unit }: { label: string; n: number; max: number; cls: string; pip?: string; unit?: string }) => (
    <div className={styles.countRow}>
        <span className={styles.countLabel}>{pip && <span className={`${styles.pip} ${pip}`} />}<span className={styles.clip}>{label}</span></span>
        <div className={styles.bar}><div className={`${styles.barFill} ${cls}`} style={{ width: `${max > 0 ? (n / max) * 100 : 0}%` }} /></div>
        <span className={styles.countNum}>{n}{unit}</span>
    </div>
);

// ---- Statistik, Google Health style: ring + tonal tiles with a fill, range rows with a typical-range band ----

const TONES = ["Teal", "Green", "Purple", "Amber", "Blue", "Rose"] as const;
type Tone = typeof TONES[number];

/** Tonal pill tile: icon disc, label, value; the darker fill behind shows progress. */
const PillTile = ({ tone, icon, label, value, note, fill }: { tone: Tone; icon: string; label: string; value: string | number; note?: string; fill?: number }) => (
    <div className={`${styles.ptile} ${styles["tone" + tone]}`}>
        {!!fill && fill > 0 && <div className={styles.ptileFill} style={{ width: `${Math.min(100, fill)}%` }} />}
        <span className={styles.ptileIcon}><NavIcon id={icon} /></span>
        <span className={styles.ptileBody}>
            <span className={styles.ptileLabel}>{label}</span>
            <span className={styles.ptileValue}>{value}{note && <span className={styles.ptileNote}>{note}</span>}</span>
        </span>
    </div>
);

const RING_C = 2 * Math.PI * 64;
const Ring = ({ pct: value, title, note }: { pct: number | null; title: string; note: string }) => {
    const p = value ?? 0;
    return (
        <section className={`${styles.card} ${styles.ringCard}`}>
            <div className={styles.cardHead}><h2>{title}</h2></div>
            <div className={styles.ringWrap}>
                <svg viewBox="0 0 160 160" style={{ width: "190rem", height: "190rem" }}>
                    <circle cx="80" cy="80" r="64" fill="none" style={{ stroke: "var(--surface-hi)" }} strokeWidth="16" />
                    {p > 0 && <circle cx="80" cy="80" r="64" fill="none" stroke={availColor(p)} strokeWidth="16" strokeLinecap="round"
                        strokeDasharray={`${(RING_C * Math.min(100, p)) / 100} ${RING_C}`} transform="rotate(-90 80 80)" />}
                </svg>
                <div className={styles.ringCenter}><span className={styles.ringValue}>{value === null ? "–" : `${value}%`}</span></div>
            </div>
            <div className={styles.ringNote}>{note}</div>
        </section>
    );
};

type RangeState = "ok" | "warn" | "bad";
// Wave long enough for any bar width (clipped by .rrWave); slides by one period per loop, same animation as WavyBar.
const WAVE_LONG = "M0 8" + " q6 -8 12 0 q6 8 12 0".repeat(90);
const WAVE_COLOR: Record<RangeState, string> = { ok: "#2aa355", warn: "#e0a30a", bad: "#d9372b" };
/** Sleep-quality style row: value on a bar, dashed band = the target range, dark dot = where we are. */
const RangeRow = ({ label, value, pos, lo, hi, state }: { label: string; value: string; pos: number | null; lo: number; hi: number; state: RangeState }) => (
    <div className={styles.rr}>
        <div className={styles.rrHead}>
            <span className={styles.rrLabel}>{label} · {value}</span>
            <span className={`${styles.rrChip} ${styles["chip_" + state]}`}>{state === "ok" ? t("Im Bereich", "In range") : t("Ausserhalb", "Out of range")}</span>
        </div>
        <div className={styles.rrBar}>
            <div className={styles.rrTrack} style={{ left: pos === null ? 0 : `calc(${Math.min(100, Math.max(0, pos))}% + 10rem)` }} />
            {[lo, hi].filter(x => x > 0 && x < 100).map(x => <div key={x} className={styles.rrMark} style={{ left: `${x}%` }} />)}
            {pos !== null && <>
                <div className={styles.rrWave} style={{ width: `${Math.min(100, Math.max(0, pos))}%` }}>
                    <svg className={styles.waveSvg} viewBox="0 0 2160 16" style={{ width: "2160rem", height: "16rem" }}>
                        <path d={WAVE_LONG} fill="none" stroke={WAVE_COLOR[state]} strokeWidth="4" strokeLinecap="round" />
                    </svg>
                </div>
                <div className={`${styles.rrThumb} ${styles["fill_" + state]}`} style={{ left: `${Math.min(100, Math.max(0, pos))}%` }} />
            </>}
        </div>
    </div>
);

/** Numbers shared by the Statistics tab and the Dashboard summary card. */
const kpis = (s: State, win: Win) => {
    const hist = samples(s.stats).slice(-Math.round((HISTORY * win) / 24)), recorded = hist.filter((x): x is Sample => x !== null);
    const resp = [...s.stats.response].sort((a, b) => a - b);
    const levels = [0, 1, 2, 3, 4, 5].map(k => recorded.reduce((n, x) => n + (x.levels[k] ?? 0), 0)); // E D C B A O
    return {
        hist, recorded, resp, levels,
        fleetMax: Math.max(1, ...recorded.map(x => x.total)),
        peakOnCall: Math.max(0, ...recorded.map(x => x.onCall)),
        callsWin: recorded.reduce((n, x) => n + x.calls, 0),
        inTime: resp.length ? pct(resp.filter(x => x <= HILFSFRIST).length, resp.length) : null,
    };
};

const LageTab = ({ s, win }: { s: State; win: Win }) => {
    const o = s.overview, st = s.stats;
    const calls = s.vehicles.filter(v => v.code && isActive(v)).sort((a, b) => levelRank(a.level) - levelRank(b.level));
    const bedPct = pct(o.bedsUsed, o.bedsTotal);
    const fullHospitals = s.hospitals.filter(h => !h.accepts).length;

    const { hist, recorded, resp, levels, fleetMax, peakOnCall, callsWin, inTime } = kpis(s, win);
    const minutesAgo = (i: number) => (hist.length - 1 - i) * st.sampleMinutes;
    const peakBeds = Math.max(0, ...recorded.map(x => pct(x.bedsUsed, x.bedsTotal)));

    const callBars: (number | null)[] = [];
    for (let i = 0; i < hist.length; i += CALL_BAR) {
        const group = hist.slice(i, i + CALL_BAR).filter((x): x is Sample => x !== null);
        callBars.push(group.length ? group.reduce((n, x) => n + x.calls, 0) : null);
    }

    const avgResp = resp.length ? Math.round(resp.reduce((n, x) => n + x, 0) / resp.length) : null;
    const medianResp = resp.length ? resp[Math.floor(resp.length / 2)] : null;
    const buckets = RESPONSE_BUCKETS.map((hi, k) => resp.filter(x => x >= (k ? RESPONSE_BUCKETS[k - 1] : 0) && x < hi).length);
    const bucketLabel = (k: number) => k === RESPONSE_BUCKETS.length - 1 ? `${RESPONSE_BUCKETS[k - 1]}+` : `${k ? RESPONSE_BUCKETS[k - 1] : 0}–${RESPONSE_BUCKETS[k]}`;

    const levelTotal = levels.reduce((n, x) => n + x, 0), levelShare = levels.map(n => pct(n, levelTotal)); // shares of the window's volume, 100% in total
    const reasonMax = Math.max(0, ...st.reasons.map(r => r.n));
    const waiting = recorded.length === 0 ? t("Wird aufgezeichnet …", "Recording …") : "";

    const onTime = resp.filter(x => x <= HILFSFRIST).length;
    const freePct = pct(o.free, o.total), admitting = o.hospitals - fullHospitals;
    // One home per fact: ring = response target, tiles = plain counts, range rows = verdicts, charts = trends.
    return (
        <div className={styles.lage}>
            <div className={styles.statTop}>
                <Ring pct={inTime} title={t("Hilfsfrist", "Response target")}
                    note={resp.length ? t(`${onTime} von ${resp.length} Einsätzen in ≤ ${HILFSFRIST} min`, `${onTime} of ${resp.length} calls in ≤ ${HILFSFRIST} min`) : t("noch kein Einsatz", "no call yet")} />
                <div className={styles.heroPills}>
                    <PillTile tone="Rose" icon="Dispatch" label={t("Laufende Einsätze", "Active calls")} value={activeCalls(s).length} />
                    <PillTile tone="Teal" icon="Resources" label={t("Alarmierungen", "Dispatches")} value={callsWin} note={`${win} h`} />
                    <PillTile tone="Purple" icon="Gauge" label={t("Spitzenlast", "Peak load")} value={peakOnCall} note={`/ ${fleetMax}`} fill={(peakOnCall / fleetMax) * 100} />
                </div>
                <section className={`${styles.card} ${styles.rangeCard}`}>
                    <div className={styles.cardHead}><h2>{t("Kennzahlen im Soll", "Key figures")}</h2><span className={styles.figure}>{t("gestrichelt = Zielbereich", "dashed = target range")}</span></div>
                    <RangeRow label={t("Ø Eintreffzeit", "Avg. response")} value={avgResp !== null ? `${avgResp} min` : "–"} pos={avgResp !== null ? (avgResp / 30) * 100 : null}
                        lo={0} hi={(HILFSFRIST / 30) * 100} state={avgResp === null || avgResp <= HILFSFRIST ? "ok" : "warn"} />
                    <RangeRow label={t("Freie RTW", "Free ambulances")} value={`${o.free} / ${o.total} · ${freePct}%`} pos={o.total ? freePct : null} lo={40} hi={100} state={!o.total || freePct >= 40 ? "ok" : freePct >= 20 ? "warn" : "bad"} />
                    <RangeRow label={t("Bettenbelegung", "Bed occupancy")} value={`${bedPct}% · ${o.bedsUsed} / ${o.bedsTotal}`} pos={o.bedsTotal ? bedPct : null} lo={0} hi={80} state={bedPct < 80 ? "ok" : bedPct < 100 ? "warn" : "bad"} />
                    <RangeRow label={t("Spitäler nehmen auf", "Hospitals admitting")} value={`${admitting} / ${o.hospitals}`} pos={o.hospitals ? pct(admitting, o.hospitals) : null}
                        lo={80} hi={100} state={!o.hospitals || fullHospitals === 0 ? "ok" : "warn"} />
                </section>
            </div>

            <div className={styles.pair}>
                <TrendCard title={t("Freie Rettungsmittel", "Free ambulances")} value={`${o.free} / ${o.total}`} hours={win} unit="%" range={[40, 100]} max={100}
                    series={bucket(hist.map(x => x && pct(x.total - x.onCall - x.returning, x.total)))} />

                <TrendCard title={t("Betten", "Beds")} value={`${bedPct}%`} hours={win} unit="%" range={[0, 80]} max={100}
                    series={bucket(hist.map(x => x && pct(x.bedsUsed, x.bedsTotal)))} note={waiting || t(`Höchststand ${peakBeds}%`, `peak ${peakBeds}%`)} />
            </div>

            <div className={styles.pair}>
                <TrendCard title={t("Alarmierungen", "Dispatches")} value={String(callsWin)} hours={win} max={Math.max(1, ...bucket(callBars, true).map(n => n ?? 0))}
                    series={bucket(callBars, true)} note={t(`je ${CALL_BAR * st.sampleMinutes} min`, `per ${CALL_BAR * st.sampleMinutes} min`)} />

                <section className={styles.block}>
                    <div className={styles.blockHead}>
                        <h2>{t("Eintreffzeit", "Response time")}</h2>
                        <span className={styles.figure}>{resp.length ? t(`letzte ${resp.length} Einsätze · Spielminuten`, `last ${resp.length} calls · game minutes`) : ""}</span>
                    </div>
                    {resp.length === 0 ? <Empty>{t("Noch kein RTW am Einsatzort eingetroffen", "No ambulance has reached a scene yet")}</Empty> : (
                        <div className={styles.histogram}>
                            {buckets.map((n, k) => (
                                <div key={k} className={styles.histCol} title={`${bucketLabel(k)} min: ${n} ${t("Einsätze", "calls")}`}>
                                    <span className={styles.histNum}>{n || ""}</span>
                                    <div className={styles.histTrack}>
                                        <div className={RESPONSE_BUCKETS[k] <= HILFSFRIST ? styles.loadOk : styles.loadHigh} style={{ height: `${pct(n, Math.max(1, ...buckets))}%` }} />
                                    </div>
                                    <span className={styles.histLabel}>{bucketLabel(k)}</span>
                                </div>
                            ))}
                        </div>
                    )}
                    {resp.length > 0 && (
                        <div className={styles.legend}>
                            <span className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.dot} ${styles.loadOk}`} />{t("innerhalb Hilfsfrist", "within target")} ({HILFSFRIST} min)</span>
                            <span className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.dot} ${styles.loadHigh}`} />{t("darüber", "over")}</span>
                        </div>
                    )}
                </section>
            </div>

            <div className={styles.pair}>
                <section className={styles.block}>
                    <div className={styles.blockHead}>
                        <h2>{t("Einsatzstufen", "Call levels")}</h2>
                        <span className={styles.figure}>{win} h</span>
                    </div>
                    {levelTotal === 0 ? <Empty>{t("Noch keine codierten Einsätze", "No coded calls yet")}</Empty> : LEVELS.map((l, k) => (
                        <CountRow key={l} label={LEVEL_NAME[l]} n={levelShare[k]} max={100} unit="%" cls={styles["lv" + l]} pip={styles["lv" + l]} />
                    ))}
                </section>
                <section className={styles.block}>
                    <div className={styles.blockHead}>
                        <h2>{t("Häufigste Gründe", "Top reasons")}</h2>
                        <span className={styles.figure}>{t("seit Laden", "since load")}</span>
                    </div>
                    {st.reasons.length === 0 ? <Empty>{t("Noch keine codierten Einsätze", "No coded calls yet")}</Empty> : st.reasons.map(r => (
                        <CountRow key={r.reason} label={r.reason} n={r.n} max={reasonMax} cls={styles.segNeutral} />
                    ))}
                </section>
            </div>

            <section className={styles.block}>
                <div className={styles.blockHead}><h2>{t("Laufende Einsätze", "Active calls")}</h2></div>
                {calls.length === 0 ? <Empty>{t("Keine laufenden Einsätze", "No active calls")}</Empty> : calls.slice(0, 8).map(v => (
                    <div key={v.callsign} className={styles.callRow}>
                        <LevelBadge level={v.level} code={v.code} />
                        <span className={styles.callReason}>{v.reason}</span>
                        <span className={styles.callUnit}>{v.callsign}</span>
                        <Status status={v.status} />
                    </div>
                ))}
                {calls.length > 8 && <div className={styles.note}>+ {calls.length - 8} {t("weitere unter Fahrzeuge", "more under Vehicles")}</div>}
            </section>
        </div>
    );
};

const FahrzeugeTab = ({ s }: { s: State }) => {
    const [onlyActive, setOnlyActive] = useState(false);
    const rows = s.vehicles
        .filter(v => !onlyActive || isActive(v))
        .sort((a, b) => Number(isActive(b)) - Number(isActive(a)) || levelRank(a.level) - levelRank(b.level) || a.callsign.localeCompare(b.callsign));
    return (
        <section className={styles.card}>
            <div className={styles.cardHead}><h2>{t("Fahrzeuge", "Vehicles")}</h2></div>
            <div className={styles.toolbar}>
                <button className={onlyActive ? styles.chipOn : styles.chip} onClick={() => setOnlyActive(!onlyActive)}>{t("Nur im Einsatz", "On call only")}</button>
                <span className={styles.note}>{rows.length} {t("Fahrzeuge", "vehicles")}</span>
            </div>
            <div className={`${styles.row} ${styles.head} ${styles.colsVehicle}`}>
                <span>{t("Einheit", "Unit")}</span><span>Status</span><span>{t("Einsatz-Nr.", "Call no.")}</span><span>Code</span><span>{t("Einsatzgrund", "Reason")}</span><span>{t("Einheiten", "Units")}</span><span>{t("Ziel", "Destination")}</span>
            </div>
            {rows.length === 0 ? <Empty>{onlyActive ? t("Kein Fahrzeug im Einsatz", "No vehicle on call") : t("Noch keine Rettungsmittel", "No ambulances yet")}</Empty> : rows.map(v => (
                <div key={v.callsign} className={`${styles.row} ${styles.colsVehicle}`}>
                    <span className={styles.strong}>{v.callsign}<span className={styles.sub}>{v.station}</span></span>
                    <Status status={v.status} />
                    <span className={styles.num}>{v.callNo || <span className={styles.dim}>–</span>}</span>
                    <span>{v.code ? <LevelBadge level={v.level} code={v.code} /> : <span className={styles.dim}>–</span>}</span>
                    <span className={styles.clip}>{v.reason || <span className={styles.dim}>–</span>}</span>
                    <span className={styles.clip}>{v.units}</span>
                    <span className={styles.clip}>{v.destination}</span>
                </div>
            ))}
        </section>
    );
};

const SpitaelerTab = ({ s }: { s: State }) => s.hospitals.length === 0 ? <Empty>{t("Kein Spital gebaut", "No hospital built")}</Empty> : (
    <section className={styles.card}>
        <div className={styles.cardHead}><h2>{t("Spitäler", "Hospitals")}</h2></div>
        <div className={`${styles.row} ${styles.head} ${styles.colsHospital}`}>
            <span>{t("Spital", "Hospital")}</span><span>{t("Auslastung", "Load")}</span><span className={styles.right}>{t("Betten", "Beds")}</span><span>{t("Aufnahme", "Admission")}</span>
        </div>
        {s.hospitals.map(h => {
            const p = pct(h.used, h.beds);
            return (
                <div key={h.name} className={`${styles.row} ${styles.colsHospital}`}>
                    <span className={styles.strong}>{h.name}</span>
                    <span className={styles.barCell}><WavyBar pct={p} width={240} color={p >= 100 ? "#e8473e" : p >= 80 ? "#f08a24" : "#4fae6a"} /><span className={styles.num}>{p}%</span></span>
                    <span className={`${styles.num} ${styles.right}`}>{h.used} / {h.beds}</span>
                    <span><span className={`${styles.rrChip} ${styles[h.accepts ? "chip_ok" : "chip_bad"]}`}>{h.accepts ? t("nimmt auf", "admitting") : t("voll", "full")}</span></span>
                </div>
            );
        })}
    </section>
);

const STATION_KINDS = [
    { kind: "all", de: "Alle", en: "All" }, { kind: "rtw", de: "RTW", en: "Ambulance" }, { kind: "nef", de: "NEF", en: "Doctor" },
    { kind: "police", de: "Polizei", en: "Police" }, { kind: "fire", de: "Feuerwehr", en: "Fire" },
] as const;

const WachenTab = ({ s }: { s: State }) => {
    const [kind, setKind] = useState<Station["kind"] | "all">("all");
    const rows = s.stations.filter(st => kind === "all" || st.kind === kind);
    const kindName = (k: string) => { const x = STATION_KINDS.find(x => x.kind === k); return x ? t(x.de, x.en) : k; };
    return (
    <section className={styles.card}>
        <div className={styles.cardHead}><h2>{t("Wachen", "Stations")}</h2></div>
        <div className={styles.toolbar}>
            {STATION_KINDS.map(k => <button key={k.kind} className={kind === k.kind ? styles.chipOn : styles.chip} onClick={() => setKind(k.kind)}>{t(k.de, k.en)}</button>)}
        </div>
        {rows.length === 0 ? <Empty>{kind === "rtw" ? t("Keine Rettungswache gebaut. Ohne Wache schicken die Spitäler ihre eigenen RTW.", "No ambulance depot built. Without a depot, hospitals send their own ambulances.") : t("Keine Fahrzeuge", "No vehicles")}</Empty> : <>
        <div className={`${styles.row} ${styles.head} ${styles.colsStation}`}>
            <span>{t("Wache", "Station")}</span><span>{t("Verfügbarkeit", "Availability")}</span><span className={styles.right}>{t("verfügbar", "available")}</span>
            <span className={styles.right}>{t("im Einsatz", "on call")}</span><span className={styles.right}>{t("gesamt", "total")}</span>
        </div>
        {rows.map(st => (
            <div key={st.name + st.kind} className={`${styles.row} ${styles.colsStation}`}>
                <span className={styles.strong}>{st.name}{kind === "all" && <span className={styles.sub}>{kindName(st.kind)}</span>}</span>
                <span className={styles.barCell}><WavyBar pct={pct(st.free, st.total)} width={240} /></span>
                <span className={`${styles.num} ${styles.right}`}>{st.free}</span>
                <span className={`${styles.num} ${styles.right}`}>{st.onCall}</span>
                <span className={`${styles.num} ${styles.right}`}>{st.total}</span>
            </div>
        ))}</>}
    </section>
    );
};

// ---- Debug: raise calls by hand (DispatchUISystem.DebugCall) ----

const DEBUG_KINDS = [
    { kind: "sick", de: "Erkrankung", en: "Illness" },
    { kind: "injured", de: "Verletzung", en: "Injury" },
    { kind: "critical", de: "Kritisch (Delta/Echo)", en: "Critical (Delta/Echo)" },
    { kind: "09E01", de: "09-E-01 Atemstillstand", en: "09-E-01 Not breathing" },
    { kind: "25", de: "Psychiatrie (25, zufällige Stufe)", en: "Psychiatric (25, random level)" },
    { kind: "door", de: "Türöffnung (Tür verschlossen)", en: "Door opening (locked door)" },
] as const;

const DebugTab = () => (
    <section className={styles.block}>
        <div className={styles.blockHead}><h2>{t("Einsatz auslösen", "Raise a call")}</h2></div>
        <div className={styles.note}>{t("Zufällige Bürger in einem Gebäude werden krank oder verletzt.", "Random citizens inside a building get sick or injured.")}</div>
        {DEBUG_KINDS.map(d => (
            <div key={d.kind} className={styles.toolbar}>
                <span className={styles.countLabel}>{t(d.de, d.en)}</span>
                {[1, 5, 20].map(n => <button key={n} className={styles.chip} onClick={() => trigger("realisticEMS", "debugCall", d.kind, n)}>× {n}</button>)}
            </div>
        ))}
    </section>
);

// Map canvas in drawing units; markers are placed in % of it, so the CSS size can differ.
const MAP_W = 1000, MAP_H = 560, MAP_PAD = 16;

/** World x/z -> canvas units. Fits the road network's bounding box, north (+z) up. */
const projection = (roads: number[]) => {
    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (let i = 0; i < roads.length; i += 2) {
        minX = Math.min(minX, roads[i]); maxX = Math.max(maxX, roads[i]);
        minZ = Math.min(minZ, roads[i + 1]); maxZ = Math.max(maxZ, roads[i + 1]);
    }
    const k = Math.min((MAP_W - 2 * MAP_PAD) / Math.max(1, maxX - minX), (MAP_H - 2 * MAP_PAD) / Math.max(1, maxZ - minZ));
    const ox = (MAP_W - (maxX - minX) * k) / 2, oy = (MAP_H - (maxZ - minZ) * k) / 2;
    return (x: number, z: number) => [ox + (x - minX) * k, oy + (maxZ - z) * k] as const;
};

const Marker = ({ at, className, title, label }: { at: readonly [number, number]; className: string; title: string; label?: string }) => (
    <div className={styles.marker} style={{ left: `${(at[0] / MAP_W) * 100}%`, top: `${(at[1] / MAP_H) * 100}%` }} title={title}>
        <div className={className} />
        {label && <span className={styles.markerLabel}>{label}</span>}
    </div>
);

const MAX_ZOOM = 40;
const HOME = { k: 1, x: 0, y: 0 };

const KarteTab = ({ s, compact }: { s: State; compact?: boolean }) => {
    const roads = useValue(roads$);
    const canvas = useRef<HTMLCanvasElement>(null);
    // View = zoom k and pan x/y in canvas units, applied on top of the fit-to-city projection.
    const [view, setView] = useState(HOME);
    const [mode3d, setMode3d] = useState(false);
    const drag = useRef<readonly [number, number] | null>(null);
    const fit = useMemo(() => projection(roads), [roads]);
    const project = (x: number, z: number) => { const [px, py] = fit(x, z); return [px * view.k + view.x, py * view.k + view.y] as const; };

    const toMap = (e: MouseEvent<HTMLDivElement>) => {
        const r = e.currentTarget.getBoundingClientRect();
        return [((e.clientX - r.left) / r.width) * MAP_W, ((e.clientY - r.top) / r.height) * MAP_H] as const;
    };
    // Zoom keeping the point (mx, my) under the cursor fixed.
    const zoomAt = (mx: number, my: number, f: number) => setView(v => {
        const k = Math.min(MAX_ZOOM, Math.max(1, v.k * f)), r = k / v.k;
        return k === 1 ? HOME : { k, x: mx - (mx - v.x) * r, y: my - (my - v.y) * r };
    });
    const onMove = (e: MouseEvent<HTMLDivElement>) => {
        if (!drag.current) return;
        const [mx, my] = toMap(e), [dx, dy] = drag.current;
        drag.current = [mx, my];
        setView(v => ({ ...v, x: v.x + mx - dx, y: v.y + my - dy }));
    };

    // ponytail: full redraw per pan/zoom step; cache to an offscreen bitmap if dragging stutters in huge cities.
    useEffect(() => {
        const ctx = canvas.current?.getContext("2d");
        if (!ctx) return;
        ctx.clearRect(0, 0, MAP_W, MAP_H);
        ctx.strokeStyle = "rgba(20, 24, 31, 0.22)";
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        for (let i = 0; i + 7 < roads.length; i += 8) {
            const [ax, ay] = project(roads[i], roads[i + 1]), [bx, by] = project(roads[i + 2], roads[i + 3]);
            const [cx, cy] = project(roads[i + 4], roads[i + 5]), [dx, dy] = project(roads[i + 6], roads[i + 7]);
            ctx.moveTo(ax, ay);
            ctx.bezierCurveTo(bx, by, cx, cy, dx, dy);
        }
        ctx.stroke();
    }, [roads, view, mode3d]);
    if (roads.length === 0) return <Empty>{t("Noch keine Strassen gebaut", "No roads built yet")}</Empty>;
    // Active units last so they are drawn on top of parked ones.
    const vehicles = [...s.vehicles].sort((a, b) => Number(isActive(a)) - Number(isActive(b)));
    return (
        <div className={compact ? styles.fillCol : `${styles.card} ${styles.mapCard}`}>
            {!compact && (
                <div className={styles.cardHead}>
                    <h2>{t("Einsatzkarte", "Live map")}</h2>
                    <span className={styles.zoom}>
                        {!mode3d && <>
                            <button className={styles.chip} onClick={() => zoomAt(MAP_W / 2, MAP_H / 2, 0.8)}>−</button>
                            <button className={styles.chip} onClick={() => zoomAt(MAP_W / 2, MAP_H / 2, 1.25)}>+</button>
                            <button className={view.k === 1 ? styles.chipOn : styles.chip} onClick={() => setView(HOME)}>{t("Ganze Stadt", "Whole city")}</button>
                        </>}
                        <button className={mode3d ? styles.chip : styles.chipOn} onClick={() => setMode3d(false)}>2D</button>
                        <button className={mode3d ? styles.chipOn : styles.chip} onClick={() => setMode3d(true)}>3D</button>
                    </span>
                </div>
            )}
            {mode3d && !compact ? <Map3D de={DE} embedded /> : <div className={compact ? `${styles.map} ${styles.mapCompact}` : styles.map}
                onWheel={e => { e.stopPropagation(); const [mx, my] = toMap(e); zoomAt(mx, my, e.deltaY < 0 ? 1.25 : 0.8); }}
                onMouseDown={e => { drag.current = toMap(e); }}
                onMouseMove={onMove}
                onMouseUp={() => { drag.current = null; }}
                onMouseLeave={() => { drag.current = null; }}>
                <canvas ref={canvas} width={MAP_W} height={MAP_H} className={styles.mapCanvas} />
                {s.stations.filter(st => st.kind === "rtw").map(st => <Marker key={"w" + st.name} at={project(st.x, st.z)} className={styles.mkStation} title={t(`Wache ${st.name}: ${st.free}/${st.total} frei`, `Station ${st.name}: ${st.free}/${st.total} available`)} />)}
                {s.hospitals.map(h => <Marker key={"h" + h.name} at={project(h.x, h.z)} className={h.accepts ? styles.mkHospital : styles.mkHospitalFull} title={`${h.name}: ${h.used}/${h.beds} ${t("Betten", "beds")}`} />)}
                {s.police.map(p => (
                    <Marker key={p.callsign} at={project(p.x, p.z)} className={`${styles.mkPolice} ${STATUS_CLASS[p.status] ?? styles.sOff}`}
                        title={`${p.callsign} · ${statusText(p.status)} · ${p.station}`} label={isActive(p) ? p.callsign : undefined} />
                ))}
                {vehicles.map(v => (
                    <Marker key={v.callsign} at={project(v.x, v.z)} className={`${v.nef ? styles.mkNef : styles.mkRtw} ${STATUS_CLASS[v.status] ?? styles.sOff}`}
                        title={`${v.callsign} · ${statusText(v.status)}${v.reason ? " · " + v.reason : ""}`} label={isActive(v) ? v.callsign : undefined} />
                ))}
                {compact && (
                    <div className={styles.mapControls} onMouseDown={e => e.stopPropagation()}>
                        <button onClick={() => zoomAt(MAP_W / 2, MAP_H / 2, 1.25)}>+</button>
                        <button onClick={() => zoomAt(MAP_W / 2, MAP_H / 2, 0.8)}>−</button>
                        <button onClick={() => setView(HOME)} title={t("Ganze Stadt", "Whole city")}>◎</button>
                    </div>
                )}
            </div>}
            {!compact && !mode3d && <div className={`${styles.legend} ${styles.mapLegend}`}>
                {["Anfahrt", "am Einsatzort", "Transport", "Rückfahrt", "frei"].map(st => (
                    <span key={st} className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.dot} ${STATUS_CLASS[st]}`} />{statusText(st)}</span>
                ))}
                <span className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.mkNef} ${styles.sReturning}`} />{t("NEF", "Doctor")}</span>
                <span className={`${styles.status} ${styles.legendItem}`}><span className={styles.mkHospital} />{t("Spital", "Hospital")}</span>
                <span className={`${styles.status} ${styles.legendItem}`}><span className={styles.mkStation} />{t("Wache", "Station")}</span>
                <span className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.mkPolice} ${styles.sPatrol}`} />{t("Polizei", "Police")}</span>
            </div>}
        </div>
    );
};

// ---- Dashboard: map, alerts, assignments, demand in one grid ----

interface Alert { sev: "high" | "medium" | "low"; title: string; text: string; }
const SEV_TEXT = { high: ["Hoch", "High"], medium: ["Mittel", "Medium"], low: ["Niedrig", "Low"] } as const;

/** Derived from the snapshot only; nothing new is simulated. */
const alerts = (s: State): Alert[] => {
    const out: Alert[] = [], o = s.overview;
    for (const v of s.vehicles.filter(v => isActive(v) && (v.level === "E" || v.level === "D")))
        out.push({ sev: "high", title: t("Kritischer Einsatz", "Critical call"), text: `${v.code} ${v.reason} · ${v.callsign} ${statusText(v.status)}` });
    for (const h of s.hospitals) {
        const p = pct(h.used, h.beds);
        if (!h.accepts) out.push({ sev: "high", title: t("Spital voll", "Hospital full"), text: t(`${h.name} nimmt niemanden mehr auf`, `${h.name} is no longer admitting`) });
        else if (p >= 80) out.push({ sev: "medium", title: t("Bettenengpass", "Bed shortage"), text: `${h.name} · ${p}% ${t("belegt", "occupied")}` });
    }
    const freePct = pct(o.free, o.total);
    if (o.total > 0 && freePct < 20) out.push({ sev: "high", title: t("Wenig freie RTW", "Few ambulances free"), text: t(`nur ${o.free} von ${o.total} frei`, `only ${o.free} of ${o.total} available`) });
    else if (o.total > 0 && freePct < 40) out.push({ sev: "medium", title: t("Auslastung steigt", "Load rising"), text: t(`${o.free} von ${o.total} RTW frei`, `${o.free} of ${o.total} ambulances available`) });
    const resp = s.stats.response, late = resp.filter(x => x > HILFSFRIST).length;
    if (resp.length >= 5 && pct(late, resp.length) > 20)
        out.push({ sev: "medium", title: t("Hilfsfrist verfehlt", "Response target missed"), text: t(`${late} von ${resp.length} Einsätzen über ${HILFSFRIST} min`, `${late} of ${resp.length} calls over ${HILFSFRIST} min`) });
    if (!s.stations.some(st => st.kind === "rtw")) out.push({ sev: "low", title: t("Keine Rettungswache", "No ambulance depot"), text: t("Spitäler schicken ihre eigenen RTW", "Hospitals send their own ambulances") });
    return out;
};

const Card = ({ title, action, onAction, children }: { title: string; action?: string; onAction?: () => void; children: any }) => (
    <section className={styles.card}>
        <div className={styles.cardHead}>
            <h2>{title}</h2>
            {action && <button className={styles.link} onClick={onAction}>{action}</button>}
        </div>
        {children}
    </section>
);

interface Call { key: string; callNo: string; address: string; code: string; level: string; reason: string; destination: string; units: Vehicle[]; }

// Call id = patient entity index from DispatchUISystem; units without one (0) each stay their own row.
const activeCalls = (s: State): Call[] => {
    const calls = new Map<string, Call>();
    for (const v of s.vehicles.filter(isActive)) {
        const key = v.call ? String(v.call) : v.callsign;
        const c = calls.get(key) ?? { key, callNo: v.callNo, address: v.address, code: v.code, level: v.level, reason: v.reason, destination: v.destination, units: [] };
        if (!c.code && v.code) Object.assign(c, { code: v.code, level: v.level, reason: v.reason }); // NEF may carry no code
        if (!c.destination) c.destination = v.destination;
        if (!c.callNo) Object.assign(c, { callNo: v.callNo, address: v.address });
        c.units.push(v);
        calls.set(key, c);
    }
    return [...calls.values()].sort((a, b) => levelRank(a.level) - levelRank(b.level));
};

const jumpTo = (v: Vehicle) => trigger("realisticEMS", "jumpTo", v.id, v.ver);

/** Active calls; click one to list its units, click a unit to follow it in game. */
const EinsaetzeTab = ({ s }: { s: State }) => {
    const [open, setOpen] = useState<string | null>(null);
    const calls = activeCalls(s);
    return (
        <section className={styles.card}>
            <div className={styles.cardHead}><h2>{t("Laufende Einsätze", "Active calls")}</h2></div>
            {calls.length === 0 ? <Empty>{t("Kein laufender Einsatz", "No active call")}</Empty> : calls.map(c => (
                <div key={c.key}>
                    <div className={styles.row} style={{ cursor: "pointer" }} onClick={() => setOpen(open === c.key ? null : c.key)}>
                        <span>{open === c.key ? "▾" : "▸"}</span>
                        <span>{c.code ? <LevelBadge level={c.level} code={c.code} /> : <span className={styles.dim}>–</span>}</span>
                        <span className={styles.strong}>{c.callNo || "–"}</span>
                        <span className={styles.clip} style={{ flex: 1 }}>{c.reason || t("Krankentransport", "Patient transfer")}{c.address && <span className={styles.dim}> · {c.address}</span>}</span>
                        <span className={styles.dim}>{c.units.length} {t("Einheiten", "units")}</span>
                    </div>
                    {open === c.key && c.units.map(u => (
                        <div key={u.callsign} className={styles.row} style={{ cursor: "pointer", paddingLeft: "48rem" }} title={t("Zum Fahrzeug springen", "Jump to unit")} onClick={() => jumpTo(u)}>
                            <span className={styles.strong} style={{ width: "160rem" }}>{u.callsign}<span className={styles.sub}>{u.station}</span></span>
                            <Status status={u.status} />
                            <span className={styles.clip} style={{ flex: 1 }}>{u.destination}</span>
                        </div>
                    ))}
                </div>
            ))}
        </section>
    );
};

const SetupCard = () => (
    <Card title={t("Einrichtung", "Setup")}>
        <div className={styles.toolbar}>
            <span className={styles.countLabel}>{t("Einrichtungsassistent erneut zeigen", "Show the setup screen again")}</span>
            <button className={styles.chip} onClick={() => trigger("realisticEMS", "openIntro")}>{t("Öffnen", "Open")}</button>
        </div>
        <div className={styles.toolbar}>
            <span className={styles.countLabel}>{t("\"Was ist neu\" erneut zeigen", "Show what's new again")}</span>
            <button className={styles.chip} onClick={() => trigger("realisticEMS", "openNews")}>{t("Öffnen", "Open")}</button>
        </div>
        <div className={styles.toolbar}>
            <span className={styles.countLabel}>{t("Fehler melden (GitHub) zeigen", "Show the bug report page")}</span>
            <button className={styles.chip} onClick={() => trigger("realisticEMS", "openBugReport")}>{t("Öffnen", "Open")}</button>
        </div>
    </Card>
);

// ---- Dashboard widgets: severity chart + wavy availability bars ----

const BAR_W = 240, PERIOD = 24; // rem; the wave path is PERIOD wide and slides by one PERIOD per loop (seamless)
const WAVE = "M0 8" + " q6 -8 12 0 q6 8 12 0".repeat(BAR_W / PERIOD + 3);
const availColor = (p: number) => (p >= 50 ? "#4fae6a" : p >= 20 ? "#f08a24" : "#e8473e");

/** M3 expressive wavy progress: moving wave for the filled part, flat track for the rest. */
const WavyBar = ({ pct: value, width = BAR_W, color }: { pct: number; width?: number; color?: string }) => {
    const p = Math.min(100, Math.max(0, value)), fill = Math.round((width * p) / 100), c = color ?? availColor(p);
    return (
        <div className={styles.wavy} style={{ width: `${width}rem` }}>
            {fill > 0 && (
                <div className={styles.waveClip} style={{ width: `${fill}rem` }}>
                    <svg className={styles.waveSvg} viewBox={`0 0 ${BAR_W + 3 * PERIOD} 16`} style={{ width: `${BAR_W + 3 * PERIOD}rem`, height: "16rem" }}>
                        <path d={WAVE} fill="none" stroke={c} strokeWidth="4" strokeLinecap="round" />
                    </svg>
                </div>
            )}
            {fill > 0 && <div className={styles.waveHead} style={{ left: `${fill}rem`, backgroundColor: c }} />}
            {fill < width && <div className={styles.waveTrack} style={fill > 0 ? { marginLeft: "10rem" } : undefined} />}
        </div>
    );
};

const InfoRow = ({ title, pct: p }: { title: string; pct: number }) => (
    <div className={styles.infoRow}>
        <div className={styles.infoHead}><span className={styles.infoTitle}>{title}</span><span className={styles.infoVal}>{p}%</span></div>
        <WavyBar pct={p} />
    </div>
);

const MINI_C = 2 * Math.PI * 64;
/** Dashboard summary: small response-target ring plus the three plain counts (same numbers as the Statistics tab). */
const SummaryCard = ({ s, win }: { s: State; win: Win }) => {
    const k = kpis(s, win), p = k.inTime ?? 0;
    const stats: [string, string][] = [
        [t("Laufende Einsätze", "Active calls"), String(activeCalls(s).length)],
        [t(`Alarmierungen ${win} h`, `Dispatches ${win} h`), String(k.callsWin)],
        [t("Spitzenlast", "Peak load"), `${k.peakOnCall} / ${k.fleetMax}`],
    ];
    return (
        <section className={`${styles.card} ${styles.sq} ${styles.sqSummary}`}>
            <div className={styles.miniRow}>
                <div className={styles.miniRing}>
                    <div className={styles.miniRingWrap}>
                        <svg viewBox="0 0 160 160" style={{ width: "96rem", height: "96rem" }}>
                            <circle cx="80" cy="80" r="64" fill="none" style={{ stroke: "var(--surface-hi)" }} strokeWidth="18" />
                            {p > 0 && <circle cx="80" cy="80" r="64" fill="none" stroke={availColor(p)} strokeWidth="18" strokeLinecap="round"
                                strokeDasharray={`${(MINI_C * Math.min(100, p)) / 100} ${MINI_C}`} transform="rotate(-90 80 80)" />}
                        </svg>
                        <div className={styles.ringCenter}><span className={styles.miniRingValue}>{k.inTime === null ? "–" : `${k.inTime}%`}</span></div>
                    </div>
                    <span className={styles.miniCaption}>{t("Hilfsfrist", "Response target")}</span>
                </div>
                <div className={styles.miniStats}>
                    {stats.map(([label, value]) => (
                        <div key={label} className={styles.miniStat}><span className={styles.miniLabel}>{label}</span><span className={styles.miniValue}>{value}</span></div>
                    ))}
                </div>
            </div>
        </section>
    );
};

/** Share of the dispatches of the last 12 / 24 h per severity level: the bars add up to 100%. */
const SeverityCard = ({ s, win }: { s: State; win: Win }) => {
    const { levels: counts } = kpis(s, win), total = counts.reduce((n, x) => n + x, 0);
    const order = [5, 4, 3, 2, 1, 0]; // O A B C D E, least to most severe
    return (
        <section className={`${styles.card} ${styles.sq}`}>
            <div className={styles.cardHead}><h2>{t("Einsätze nach Stufe", "Calls by severity")}</h2><span className={styles.figure}>{win} h</span></div>
            <div className={styles.sevChart}>
                {order.map(k => {
                    const l = LEVELS[k], share = pct(counts[k], total);
                    return (
                        <div key={l} className={styles.sevCol} title={`${LEVEL_NAME[l]}: ${counts[k]}`}>
                            <span className={styles.sevNum}>{share}%</span>
                            <div className={styles.sevTrack}><div className={`${styles.sevBar} ${styles["lv" + l]}`} style={{ transform: `scaleY(${share / 100})` }} /></div>
                            <span className={styles.sevLbl}>{l === "O" ? "Ω" : l}</span>
                        </div>
                    );
                })}
            </div>
        </section>
    );
};

const InfoCard = ({ s }: { s: State }) => {
    const o = s.overview;
    return (
        <section className={`${styles.card} ${styles.sq} ${styles.sqSmall}`}>
            <InfoRow title={t("Freie Rettungsmittel", "Free ambulances")} pct={pct(o.free, o.total)} />
            <InfoRow title={t("Freie Betten", "Available beds")} pct={pct(Math.max(0, o.bedsTotal - o.bedsUsed), o.bedsTotal)} />
        </section>
    );
};

const DashboardTab = ({ s, go, win }: { s: State; go: (p: Page) => void; win: Win }) => {
    return (
        <div>
        <div className={styles.dashTop}>
            <div className={styles.dashSide}>
                <SummaryCard s={s} win={win} />
                <SeverityCard s={s} win={win} />
                <InfoCard s={s} />
            </div>
            <section className={`${styles.card} ${styles.dashMap}`}>
                <div className={styles.cardHead}>
                    <h2>{t("Einsatzkarte", "Live map")}</h2>
                    <button className={styles.link} onClick={() => go("Karte")}>{t("Vollbild", "Full map")}</button>
                </div>
                <KarteTab s={s} compact />
            </section>
        </div>
        </div>
    );
};

// ---- Settings: same options as the game's Options menu, written back via DispatchUISystem.SetSetting ----

interface SettingItem { name: string; label: string; desc: string; bool: boolean; value: number; min: number; max: number; step: number; }
const settings$ = bindValue<SettingItem[]>("realisticEMS", "settings", []);
const SETTING_GROUPS = {
    Disposition: { en: "Dispatch", names: ["RejectFullHospitals", "AvailableOnlyAtDepot", "OnlyDepotsDispatch", "DepotAmbulances", "BedCapacityPercent"] },
    Notarzt: { en: "Doctor & care", names: ["EnableNef", "NefPerHospital", "NefMaxWaitMinutes", "OnSceneMinutes"] },
    Fahrverhalten: { en: "Driving", names: ["ParkOnSidewalk", "ParkAtCrossing", "PushBlockers", "EmergencyLaneChange","ClearRedLight", "ClearRedLightMaxCars", "EmergencyCorridor", "OncomingOvertake", "SirenOnlyWhenNeeded"] },
    Anzeige: { en: "Display & debug", names: ["ShowUnitNames", "LogAmbulanceStates"] },
} as const;
type SettingGroup = keyof typeof SETTING_GROUPS;

const SettingsTab = ({ group }: { group: SettingGroup }) => {
    const all = useValue(settings$);
    const { translate } = useLocalization();
    const items = (SETTING_GROUPS[group].names as readonly string[]).map(n => all.find(x => x.name === n)).filter((x): x is SettingItem => !!x);
    const set = (name: string, v: number) => trigger("realisticEMS", "setSetting", name, v);
    if (items.length === 0) return <Empty>{t("Lade Einstellungen …", "Loading settings …")}</Empty>;
    return (
        <Card title={t(group, SETTING_GROUPS[group].en)}>
            {items.map(it => (
                <div key={it.name} className={styles.setting}>
                    <div className={styles.settingText}>
                        <div className={styles.settingLabel}>{translate(it.label) ?? it.name}</div>
                        <div className={styles.settingDesc}>{translate(it.desc) ?? ""}</div>
                    </div>
                    {it.bool
                        ? <button className={it.value ? styles.switchOn : styles.switch} onClick={() => set(it.name, it.value ? 0 : 1)}><span /></button>
                        : <span className={styles.stepper}>
                            <button className={styles.chip} onClick={() => set(it.name, it.value - it.step)} disabled={it.value <= it.min}>−</button>
                            <span className={styles.stepValue}>{it.value}</span>
                            <button className={styles.chip} onClick={() => set(it.name, it.value + it.step)} disabled={it.value >= it.max}>+</button>
                        </span>}
                </div>
            ))}
            <div className={styles.note}>{t("Wird sofort gespeichert. Auch unter Optionen › Mods verfügbar.", "Saved immediately. Also available under Options › Mods.")}</div>
        </Card>
    );
};

// ---- Shell: sidebar with menus and submenus ----

type Page = "Dashboard" | "Einsätze" | "Karte" | "Statistik" | "Fahrzeuge" | "Wachen" | "Spitäler" | "Debug" | `Einstellungen/${SettingGroup}`;
const NAV: { de: string; en: string; items: { id: Page; de: string; en: string }[] }[] = [
    { de: "Übersicht", en: "Dashboard", items: [{ id: "Dashboard", de: "Übersicht", en: "Dashboard" }] },
    { de: "Einsätze", en: "Calls", items: [{ id: "Einsätze", de: "Einsätze", en: "Calls" }] },
    { de: "Karte", en: "Map", items: [{ id: "Karte", de: "Karte", en: "Map" }] },
    { de: "Statistik", en: "Statistics", items: [{ id: "Statistik", de: "Statistik", en: "Statistics" }] },
    { de: "Ressourcen", en: "Resources", items: [
        { id: "Fahrzeuge", de: "Fahrzeuge", en: "Vehicles" }, { id: "Wachen", de: "Wachen", en: "Stations" }, { id: "Spitäler", de: "Spitäler", en: "Hospitals" }] },
];
// Not in the dock: opened from the cog menu (top right).
const MENU_GROUPS: typeof NAV = [
    { de: "Einstellungen", en: "Settings", items: (Object.keys(SETTING_GROUPS) as SettingGroup[]).map(g => ({ id: `Einstellungen/${g}` as Page, de: g, en: SETTING_GROUPS[g].en })) },
    { de: "System", en: "System", items: [{ id: "Debug", de: "Debug", en: "Debug" }] },
];

// Google Material Icons (filled, 24 grid), Apache License 2.0, https://github.com/google/material-design-icons
// Inlined as SVG paths: nothing is loaded from Google at runtime.
const ICONS: Record<string, string> = {
    Dashboard: "M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z",
    Map: "M20.5 3l-.16.03L15 5.1 9 3 3.36 4.9c-.21.07-.36.25-.36.48V20.5c0 .28.22.5.5.5l.16-.03L9 18.9l6 2.1 5.64-1.9c.21-.07.36-.25.36-.48V3.5c0-.28-.22-.5-.5-.5zM15 19l-6-2.11V5l6 2.11V19z",
    Statistics: "M5 9.2h3V19H5zM10.6 5h2.8v14h-2.8zm5.6 8H19v6h-2.8z",
    Resources: "M20 8h-3V4H3c-1.1 0-2 .9-2 2v11h2c0 1.66 1.34 3 3 3s3-1.34 3-3h6c0 1.66 1.34 3 3 3s3-1.34 3-3h2v-5l-3-4zM6 18.5c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm13.5-9l1.96 2.5H17V9.5h2.5zm-1.5 9c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5z",
    Dispatch: "M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z",
    Calls: "M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z",
    Search: "M15.5 14h-.79l-.28-.27A6.471 6.471 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z",
    Clock: "M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z",
    Gauge: "M20.38 8.57l-1.23 1.85a8 8 0 0 1-.22 7.58H5.07A8 8 0 0 1 15.58 6.85l1.85-1.23A10 10 0 0 0 3.35 19a2 2 0 0 0 1.72 1h13.85a2 2 0 0 0 1.74-1 10 10 0 0 0-.27-10.44zm-9.79 6.84a2 2 0 0 0 2.83 0l5.66-8.49-8.49 5.66a2 2 0 0 0 0 2.83z",
    Bed: "M7 13c1.66 0 3-1.34 3-3S8.66 7 7 7s-3 1.34-3 3 1.34 3 3 3zm12-6h-8v7H3V5H1v15h2v-3h18v3h2v-9c0-2.21-1.79-4-4-4z",
    Hospital: "M19 3H5c-1.1 0-1.99.9-1.99 2L3 19c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-1 11h-4v4h-4v-4H6v-4h4V6h4v4h4v4z",
    Close: "M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z",
    Cog: "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.488.488 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z",
    Moon: "M12 3a9 9 0 1 0 9 9c0-.46-.04-.92-.1-1.36a5.389 5.389 0 0 1-4.4 2.26 5.403 5.403 0 0 1-3.14-9.8c-.44-.06-.9-.1-1.36-.1z",
    Sun: "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58a.996.996 0 0 0-1.41 0 .996.996 0 0 0 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37a.996.996 0 0 0-1.41 0 .996.996 0 0 0 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0a.996.996 0 0 0 0-1.41l-1.06-1.06zm1.06-10.96a.996.996 0 0 0 0-1.41.996.996 0 0 0-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36a.996.996 0 0 0 0-1.41.996.996 0 0 0-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z",
};
const NavIcon = ({ id }: { id: string }) => (
    <svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d={ICONS[id]} /></svg>
);

// Slot widths of the two dock pills; the sliding indicators move by whole slots.
const GROUP_W = 190, SUB_W = 124;

export const EmsPanel = () => {
    const open = useValue(open$);
    const s = useValue(state$);
    const debug = useValue(debug$);
    const [page, setPage] = useState<Page>("Dashboard");
    const [searching, setSearching] = useState(false);
    const [menu, setMenu] = useState(false);
    const [dark, setDark] = useState(() => { try { return localStorage.getItem("realisticEMS.dark") === "1"; } catch { return false; } });
    const toggleDark = () => { setDark(!dark); try { localStorage.setItem("realisticEMS.dark", dark ? "0" : "1"); } catch { /* ignore */ } };
    const [win, setWin] = useState<Win>(24);
    const [q, setQ] = useState("");
    const [focusCall, setFocusCall] = useState<{ key: string } | null>(null);
    useLang();
    if (!open) return null;
    const active = s ? s.vehicles.filter(isActive).length : 0;
    const menuGroups = debug ? MENU_GROUPS : MENU_GROUPS.filter(g => g.en !== "System");
    const nav = NAV;
    if (!debug && page === "Debug") setPage("Dashboard");
    const current = [...NAV, ...MENU_GROUPS].flatMap(g => g.items).find(x => x.id === page)!;
    const query = q.trim().replace(/^#/, "").toLowerCase();
    const hits = s && query ? activeCalls(s).filter(c => c.key.includes(query) || c.callNo.toLowerCase().includes(query) || c.address.toLowerCase().includes(query) || c.reason.toLowerCase().includes(query) || c.code.toLowerCase().includes(query)
        || c.units.some(u => u.callsign.toLowerCase().includes(query))).slice(0, 5) : [];
    const pick = (c: Call) => { setPage("Dashboard"); setFocusCall({ key: c.key }); setSearching(false); setQ(""); };
    const group = [...NAV, ...MENU_GROUPS].find(g => g.items.some(i => i.id === page))!, gi = nav.indexOf(group), si = Math.max(0, group.items.findIndex(i => i.id === page));
    const view = (
                        <div key={page} className={styles.page}>
                        {page.startsWith("Einstellungen/") ? <SettingsTab group={page.split("/")[1] as SettingGroup} />
                            : page === "Debug" && debug ? <DebugTab />
                            : !s ? <Empty>{t("Warte auf Daten aus der Simulation …", "Waiting for simulation data …")}</Empty>
                            : page === "Dashboard" ? <DashboardTab s={s} go={setPage} win={win} />
                            : page === "Statistik" ? <LageTab s={s} win={win} />
                            : page === "Einsätze" ? <EinsaetzeTab s={s} />
                            : page === "Karte" ? <KarteTab s={s} />
                            : page === "Fahrzeuge" ? <FahrzeugeTab s={s} />
                            : page === "Spitäler" ? <SpitaelerTab s={s} />
                            : <WachenTab s={s} />}
                        </div>
    );
    return (
        <Panel draggable initialPosition={{ x: 0.5, y: 0.5 }} className={styles.panel} header={t("Leitstelle", "Dispatch")} onClose={toggle}>
            <div className={`${styles.shell} ${dark ? styles.dark : ""}`}>
                <div className={styles.main}>
                    <div className={styles.topRow}>
                    <div key={page} className={`${styles.pageHead} ${styles.page}`}>
                        <h1>{t(current.de, current.en)}</h1>
                        <span className={styles.note}>{t("Einsätze und Rettungsmittel in Echtzeit", "Calls and units in real time")}</span>
                    </div>
                    <div className={styles.topRight}>
                        {(page === "Dashboard" || page === "Statistik") && (
                            <div className={styles.winDock}>
                                <div className={styles.winPill}>
                                    <div className={styles.winInd} style={{ transform: `translateX(${WINDOWS.indexOf(win) * 56}rem)` }} />
                                    {WINDOWS.map(w => <button key={w} className={`${styles.winBtn} ${w === win ? styles.winBtnOn : ""}`} onClick={() => setWin(w)}>{w} h</button>)}
                                </div>
                            </div>
                        )}
                        <button key={String(dark)} className={styles.themeBtn} title={t("Dunkelmodus", "Dark mode")} onClick={toggleDark}><NavIcon id={dark ? "Sun" : "Moon"} /></button>
                        <button className={`${styles.cogBtn} ${menu ? styles.cogBtnOn : ""}`} title={t("Einstellungen & System", "Settings & system")} onClick={() => setMenu(!menu)}>
                            <NavIcon id="Cog" />
                        </button>
                        {menu && (
                            <div className={styles.menu}>
                                {menuGroups.map(g => (
                                    <div key={g.en}>
                                        <div className={styles.menuHead}>{t(g.de, g.en)}</div>
                                        {g.items.map(it => (
                                            <button key={it.id} className={`${styles.menuItem} ${it.id === page ? styles.menuItemOn : ""}`}
                                                onClick={() => { setPage(it.id); setMenu(false); }}>{t(it.de, it.en)}</button>
                                        ))}
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>
                    {group.items.length > 1 && (<div className={styles.topCenter}>
                        <div key={group.en} className={styles.subPill}>
                            <div className={styles.subInd} style={{ width: `${SUB_W - 8}rem`, transform: `translateX(${si * SUB_W}rem)` }} />
                            {group.items.map(it => (
                                <button key={it.id} className={`${styles.subBtn} ${it.id === page ? styles.subBtnOn : ""}`} style={{ width: `${SUB_W}rem` }} onClick={() => setPage(it.id)}>
                                    {t(it.de, it.en)}
                                </button>
                            ))}
                        </div>
                    </div>)}
                    </div>
                    {(page === "Dashboard" || page === "Karte") && s
                        ? <div className={styles.content}>{view}</div> // fits without scrolling
                        : <Scrollable className={styles.content} vertical>
                            {view}
                            <div style={{ height: "150rem" }} />
                        </Scrollable>}
                </div>
                <div className={styles.dockFade} />
                <nav className={styles.dock}>
                    {searching && query && (
                        <div className={styles.results}>
                            {hits.length === 0 ? <div className={styles.hitEmpty}>{t(`Kein laufender Einsatz für „${q.trim()}“`, `No active call matches “${q.trim()}”`)}</div> : hits.map(c => (
                                <button key={c.key} className={styles.hit} onClick={() => pick(c)}>
                                    {c.code ? <LevelBadge level={c.level} code={c.code} /> : <span className={styles.dim}>–</span>}
                                    <span>{c.callNo && <b>{c.callNo} · </b>}{c.reason || t("Krankentransport", "Patient transfer")}{c.address && <span className={styles.dim}> · {c.address}</span>}</span>
                                    <span className={styles.dim}>{c.units.map(u => u.callsign).join(", ")}</span>
                                </button>
                            ))}
                        </div>
                    )}
                    <div className={styles.dockRow}>
                        {searching ? (
                            <div className={styles.searchPill}>
                                <NavIcon id="Search" />
                                <input autoFocus value={q} placeholder={t("Einsatz-ID oder Grund suchen", "Search call ID or reason")} onChange={e => setQ(e.target.value)}
                                    onKeyDown={e => { if (e.key === "Escape") { setSearching(false); setQ(""); } else if (e.key === "Enter" && hits[0]) pick(hits[0]); }} />
                            </div>
                        ) : (
                            <div className={styles.groupPill}>
                                <div className={styles.groupInd} style={{ opacity: gi < 0 ? 0 : 1, width: `${GROUP_W - 16}rem`, transform: `translateX(${Math.max(0, gi) * GROUP_W}rem)` }} />
                                {nav.map((g, i) => (
                                    <button key={g.en} className={`${styles.groupBtn} ${i === gi ? styles.groupBtnOn : ""}`} style={{ width: `${GROUP_W}rem` }}
                                        onClick={() => i !== gi && setPage(g.items[0].id)}>
                                        {i === gi && <NavIcon id={g.en} />}
                                        <span>{t(g.de, g.en)}</span>
                                        {g.en === "Resources" && active > 0 && <span className={styles.navBadge}>{active}</span>}
                                    </button>
                                ))}
                            </div>
                        )}
                        <button className={styles.searchBtn} title={t("Einsatz suchen", "Search calls")} onClick={() => { setSearching(!searching); setQ(""); }}>
                            <NavIcon id={searching ? "Close" : "Search"} />
                        </button>
                    </div>
                </nav>
            </div>
        </Panel>
    );
};
