import { MouseEvent, useEffect, useMemo, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { FloatingButton, Panel, Scrollable } from "cs2/ui";
import { useLocalization } from "cs2/l10n";
import styles from "./ems-panel.module.scss";

// Language follows the game: Setting.cs registers RealisticEMS.Lang = "de" for de-DE, "en" otherwise.
// ponytail: module-level flag set by the two root components each render; children render after them.
let DE = false;
const useLang = () => { DE = useLocalization().translate("RealisticEMS.Lang") === "de"; };
const t = (de: string, en: string) => (DE ? de : en);

// Snapshot written by DispatchUISystem.cs (C#). The UI only renders it.
interface Overview { total: number; free: number; onCall: number; returning: number; bedsUsed: number; bedsTotal: number; hospitals: number; depots: number; }
interface Vehicle { callsign: string; status: string; station: string; code: string; level: string; reason: string; units: string; destination: string; call: number; nef: boolean; x: number; z: number; }
interface Hospital { name: string; used: number; beds: number; accepts: boolean; ambulances: number; x: number; z: number; }
interface Station { name: string; total: number; free: number; onCall: number; x: number; z: number; }
// history: 6 ints per sample (oldest first): onCall, returning, total, bedsUsed, bedsTotal, new calls.
interface Stats { calls: number; sampleMinutes: number; history: number[]; levels: number[]; response: number[]; reasons: { reason: string; n: number }[]; }
interface Police { callsign: string; status: string; station: string; x: number; z: number; }
interface State { overview: Overview; vehicles: Vehicle[]; hospitals: Hospital[]; stations: Station[]; police: Police[]; stats: Stats; }

const open$ = bindValue<boolean>("realisticEMS", "open", false);
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
    "frei": "available", "Anfahrt": "en route", "am Einsatzort": "on scene", "Transport": "transporting",
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

/** Fleet split by status as one segmented bar: the whole picture in one line. */
const FleetBar = ({ o }: { o: Overview }) => {
    const out = Math.max(0, o.total - o.free - o.onCall - o.returning);
    const seg = (n: number, cls: string) => n > 0 && <div className={cls} style={{ flexGrow: n }} />;
    return (
        <div className={styles.fleet}>
            {seg(o.onCall, styles.sOnScene)}{seg(o.returning, styles.sReturning)}{seg(o.free, styles.sFree)}{seg(out, styles.sOff)}
        </div>
    );
};

// ---- Lage: the current picture plus the last game day (history from DispatchUISystem) ----

const HISTORY = 256; // samples DispatchUISystem keeps: one game day
const CALL_BAR = 8; // samples per bar in the dispatch chart (~45 game minutes)
// ponytail: fixed 15 min target (Hilfsfrist) in game minutes; make it a setting if cities differ a lot.
const HILFSFRIST = 15;
const RESPONSE_BUCKETS = [5, 10, 15, 20, 30, Infinity];

interface Sample { onCall: number; returning: number; total: number; bedsUsed: number; bedsTotal: number; calls: number; }

/** Fixed 24 h axis: leading nulls until a full day has been recorded. */
const samples = (st: Stats): (Sample | null)[] => {
    const h = st.history, out: (Sample | null)[] = [];
    for (let i = 0; i + 5 < h.length; i += 6) out.push({ onCall: h[i], returning: h[i + 1], total: h[i + 2], bedsUsed: h[i + 3], bedsTotal: h[i + 4], calls: h[i + 5] });
    return [...Array(Math.max(0, HISTORY - out.length)).fill(null), ...out];
};

const ago = (min: number) => {
    if (min <= 0) return t("jetzt", "now");
    const span = min < 60 ? `${min} min` : `${Math.floor(min / 60)} h ${String(min % 60).padStart(2, "0")} min`;
    return t(`vor ${span}`, `${span} ago`);
};

interface Seg { value: number; cls: string; }

/** Time series as one thin column per sample, segments stacked bottom-up. Hovering a column shows its values in the readout line. */
const Columns = ({ data, max, summary, readout, gap }: { data: (Seg[] | null)[]; max: number; summary: string; readout: (i: number) => string; gap?: boolean }) => {
    const [hover, setHover] = useState<number | null>(null);
    return (
        <div className={styles.chart}>
            <div className={styles.readout}>{hover !== null && data[hover] ? readout(hover) : summary}</div>
            <div className={styles.columns} onMouseLeave={() => setHover(null)}>
                {data.map((segs, i) => (
                    <div key={i} className={`${styles.col} ${gap ? styles.colGap : ""} ${i === hover ? styles.colHover : ""}`} onMouseEnter={() => setHover(i)}>
                        {segs?.map((sg, k) => sg.value > 0 && <div key={k} className={sg.cls} style={{ height: `${Math.min(100, (sg.value / Math.max(1, max)) * 100)}%` }} />)}
                    </div>
                ))}
            </div>
            <div className={styles.axis}><span>−24 h</span><span>−12 h</span><span>{t("jetzt", "now")}</span></div>
        </div>
    );
};

/** Horizontal bar with label and count: for "how many of each". */
const CountRow = ({ label, n, max, cls, pip }: { label: string; n: number; max: number; cls: string; pip?: string }) => (
    <div className={styles.countRow}>
        <span className={styles.countLabel}>{pip && <span className={`${styles.pip} ${pip}`} />}<span className={styles.clip}>{label}</span></span>
        <div className={styles.bar}><div className={`${styles.barFill} ${cls}`} style={{ width: `${max > 0 ? (n / max) * 100 : 0}%` }} /></div>
        <span className={styles.countNum}>{n}</span>
    </div>
);

const Tile = ({ label, value, unit, note }: { label: string; value: string | number; unit?: string; note: string }) => (
    <div className={styles.tile}>
        <span className={styles.tileLabel}>{label}</span>
        <span className={styles.tileValue}>{value}{unit && <span className={styles.tileUnit}>{unit}</span>}</span>
        <span className={styles.tileNote}>{note}</span>
    </div>
);

const LageTab = ({ s }: { s: State }) => {
    const o = s.overview, st = s.stats;
    const calls = s.vehicles.filter(v => v.code && isActive(v)).sort((a, b) => levelRank(a.level) - levelRank(b.level));
    const bedPct = pct(o.bedsUsed, o.bedsTotal);
    const fullHospitals = s.hospitals.filter(h => !h.accepts).length;

    const hist = samples(st);
    const recorded = hist.filter((x): x is Sample => x !== null);
    const minutesAgo = (i: number) => (hist.length - 1 - i) * st.sampleMinutes;
    const fleetMax = Math.max(1, ...recorded.map(x => x.total));
    const peakOnCall = Math.max(0, ...recorded.map(x => x.onCall));
    const peakBeds = Math.max(0, ...recorded.map(x => pct(x.bedsUsed, x.bedsTotal)));

    const callBars: (number | null)[] = [];
    for (let i = 0; i < hist.length; i += CALL_BAR) {
        const group = hist.slice(i, i + CALL_BAR).filter((x): x is Sample => x !== null);
        callBars.push(group.length ? group.reduce((n, x) => n + x.calls, 0) : null);
    }
    const calls24h = recorded.reduce((n, x) => n + x.calls, 0);

    const resp = [...st.response].sort((a, b) => a - b);
    const avgResp = resp.length ? Math.round(resp.reduce((n, x) => n + x, 0) / resp.length) : null;
    const medianResp = resp.length ? resp[Math.floor(resp.length / 2)] : null;
    const inTime = resp.length ? pct(resp.filter(x => x <= HILFSFRIST).length, resp.length) : null;
    const buckets = RESPONSE_BUCKETS.map((hi, k) => resp.filter(x => x >= (k ? RESPONSE_BUCKETS[k - 1] : 0) && x < hi).length);
    const bucketLabel = (k: number) => k === RESPONSE_BUCKETS.length - 1 ? `${RESPONSE_BUCKETS[k - 1]}+` : `${k ? RESPONSE_BUCKETS[k - 1] : 0}–${RESPONSE_BUCKETS[k]}`;

    const levelTotal = st.levels.reduce((n, x) => n + x, 0);
    const levelMax = Math.max(0, ...st.levels);
    const reasonMax = Math.max(0, ...st.reasons.map(r => r.n));
    const waiting = recorded.length === 0 ? t("Wird aufgezeichnet …", "Recording …") : "";

    return (
        <div className={styles.lage}>
            <div className={styles.tiles}>
                <Tile label={t("Alarmierungen", "Dispatches")} value={calls24h} note={t(`letzte 24 h · ${st.calls} seit Laden`, `last 24 h · ${st.calls} since load`)} />
                <Tile label={t("Ø Eintreffzeit", "Avg. response")} value={avgResp ?? "–"} unit={avgResp !== null ? "min" : undefined}
                    note={medianResp !== null ? t(`Median ${medianResp} min · ${resp.length} Einsätze`, `median ${medianResp} min · ${resp.length} calls`) : t("noch kein Einsatz", "no call yet")} />
                <Tile label={t("Hilfsfrist", "Response target")} value={inTime ?? "–"} unit={inTime !== null ? "%" : undefined} note={t(`vor Ort in ≤ ${HILFSFRIST} min`, `on scene in ≤ ${HILFSFRIST} min`)} />
                <Tile label={t("Spitzenlast", "Peak load")} value={peakOnCall} unit={`/ ${fleetMax}`} note={t("RTW gleichzeitig im Einsatz, 24 h", "ambulances busy at once, 24 h")} />
            </div>

            <section className={styles.block}>
                <div className={styles.blockHead}>
                    <h2>{t("Rettungsmittel", "Fleet")}</h2>
                    <span className={styles.figure}><b>{o.free}</b> {t(`von ${o.total} frei`, `of ${o.total} available`)}</span>
                </div>
                <FleetBar o={o} />
                <div className={styles.legend}>
                    <span className={styles.status}><span className={`${styles.dot} ${styles.sOnScene}`} />{t("im Einsatz", "on call")}</span><span className={styles.num}>{o.onCall}</span>
                    <span className={styles.status}><span className={`${styles.dot} ${styles.sReturning}`} />{statusText("Rückfahrt")}</span><span className={styles.num}>{o.returning}</span>
                    <span className={styles.status}><span className={`${styles.dot} ${styles.sFree}`} />{statusText("frei")}</span><span className={styles.num}>{o.free}</span>
                </div>
                <Columns
                    data={hist.map(x => x && [
                        { value: x.onCall, cls: styles.sOnScene },
                        { value: x.returning, cls: styles.sReturning },
                        { value: x.total - x.onCall - x.returning, cls: styles.segIdle },
                    ])}
                    max={fleetMax}
                    summary={waiting || t(`Verlauf 24 h · Spitze ${peakOnCall} von ${fleetMax} im Einsatz`, `24 h history · peak ${peakOnCall} of ${fleetMax} on call`)}
                    readout={i => {
                        const x = hist[i]!, idle = x.total - x.onCall - x.returning;
                        return `${ago(minutesAgo(i))} · ` + t(`${x.onCall} im Einsatz · ${x.returning} Rückfahrt · ${idle} frei`, `${x.onCall} on call · ${x.returning} returning · ${idle} available`);
                    }}
                />
            </section>

            <section className={styles.block}>
                <div className={styles.blockHead}>
                    <h2>{t("Betten", "Beds")}</h2>
                    <span className={styles.figure}><b>{bedPct}%</b> {t("belegt", "occupied")} · {o.bedsUsed} / {o.bedsTotal}</span>
                </div>
                <LoadBar value={bedPct} />
                <div className={styles.note}>
                    {fullHospitals > 0
                        ? t(`${fullHospitals} von ${o.hospitals} Spitälern nehmen niemanden mehr auf`, `${fullHospitals} of ${o.hospitals} hospitals are no longer admitting`)
                        : t(`Alle ${o.hospitals} Spitäler nehmen auf`, `All ${o.hospitals} hospitals are admitting`)}
                </div>
                <Columns
                    data={hist.map(x => x && [{ value: pct(x.bedsUsed, x.bedsTotal), cls: loadClass(pct(x.bedsUsed, x.bedsTotal)) }])}
                    max={100}
                    summary={waiting || t(`Verlauf 24 h · Höchststand ${peakBeds}%`, `24 h history · peak ${peakBeds}%`)}
                    readout={i => { const x = hist[i]!; return `${ago(minutesAgo(i))} · ${pct(x.bedsUsed, x.bedsTotal)}% ${t("belegt", "occupied")} · ${x.bedsUsed} / ${x.bedsTotal}`; }}
                />
            </section>

            <section className={styles.block}>
                <div className={styles.blockHead}>
                    <h2>{t("Alarmierungen", "Dispatches")}</h2>
                    <span className={styles.figure}><b>{calls24h}</b> {t("in 24 h", "in 24 h")}</span>
                </div>
                <Columns
                    data={callBars.map(n => n === null ? null : [{ value: n, cls: styles.segNeutral }])}
                    max={Math.max(1, ...callBars.map(n => n ?? 0))}
                    gap
                    summary={waiting || t(`RTW-Alarmierungen je ${CALL_BAR * st.sampleMinutes} min`, `Ambulance dispatches per ${CALL_BAR * st.sampleMinutes} min`)}
                    readout={i => `${ago(((callBars.length - 1 - i) * CALL_BAR) * st.sampleMinutes)} · ` + t(`${callBars[i]} Alarmierungen in ${CALL_BAR * st.sampleMinutes} min`, `${callBars[i]} dispatches in ${CALL_BAR * st.sampleMinutes} min`)}
                />
            </section>

            <div className={styles.pair}>
                <section className={styles.block}>
                    <div className={styles.blockHead}>
                        <h2>{t("Einsatzstufen", "Call levels")}</h2>
                        <span className={styles.figure}>{t("seit Laden", "since load")}</span>
                    </div>
                    {levelTotal === 0 ? <Empty>{t("Noch keine codierten Einsätze", "No coded calls yet")}</Empty> : LEVELS.map((l, k) => (
                        <CountRow key={l} label={LEVEL_NAME[l]} n={st.levels[k] ?? 0} max={levelMax} cls={styles["lv" + l]} pip={styles["lv" + l]} />
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

            <section className={styles.block}>
                <div className={styles.blockHead}>
                    <h2>{t("Laufende Einsätze", "Active calls")}</h2>
                    <span className={styles.levels}>
                        {LEVELS.map(l => {
                            const n = calls.filter(v => v.level === l).length;
                            return <span key={l} className={n ? styles.levelCount : styles.levelZero} title={LEVEL_NAME[l]}>
                                <span className={`${styles.pip} ${styles["lv" + l]}`} />{n}
                            </span>;
                        })}
                    </span>
                </div>
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
        <div>
            <div className={styles.toolbar}>
                <button className={onlyActive ? styles.chipOn : styles.chip} onClick={() => setOnlyActive(!onlyActive)}>{t("Nur im Einsatz", "On call only")}</button>
                <span className={styles.note}>{rows.length} {t("Fahrzeuge", "vehicles")}</span>
            </div>
            <div className={`${styles.row} ${styles.head} ${styles.colsVehicle}`}>
                <span>{t("Einheit", "Unit")}</span><span>Status</span><span>Code</span><span>{t("Einsatzgrund", "Reason")}</span><span>{t("Einheiten", "Units")}</span><span>{t("Ziel", "Destination")}</span>
            </div>
            {rows.length === 0 ? <Empty>{onlyActive ? t("Kein Fahrzeug im Einsatz", "No vehicle on call") : t("Noch keine Rettungsmittel", "No ambulances yet")}</Empty> : rows.map(v => (
                <div key={v.callsign} className={`${styles.row} ${styles.colsVehicle}`}>
                    <span className={styles.strong}>{v.callsign}<span className={styles.sub}>{v.station}</span></span>
                    <Status status={v.status} />
                    <span>{v.code ? <LevelBadge level={v.level} code={v.code} /> : <span className={styles.dim}>–</span>}</span>
                    <span className={styles.clip}>{v.reason || <span className={styles.dim}>–</span>}</span>
                    <span className={styles.clip}>{v.units}</span>
                    <span className={styles.clip}>{v.destination}</span>
                </div>
            ))}
        </div>
    );
};

const SpitaelerTab = ({ s }: { s: State }) => s.hospitals.length === 0 ? <Empty>{t("Kein Spital gebaut", "No hospital built")}</Empty> : (
    <div>
        <div className={`${styles.row} ${styles.head} ${styles.colsHospital}`}>
            <span>{t("Spital", "Hospital")}</span><span>{t("Auslastung", "Load")}</span><span className={styles.right}>{t("Betten", "Beds")}</span><span>{t("Aufnahme", "Admission")}</span>
        </div>
        {s.hospitals.map(h => {
            const p = pct(h.used, h.beds);
            return (
                <div key={h.name} className={`${styles.row} ${styles.colsHospital}`}>
                    <span className={styles.strong}>{h.name}</span>
                    <span className={styles.barCell}><LoadBar value={p} /><span className={styles.num}>{p}%</span></span>
                    <span className={`${styles.num} ${styles.right}`}>{h.used} / {h.beds}</span>
                    <span className={h.accepts ? styles.ok : styles.full}>{h.accepts ? t("nimmt auf", "admitting") : t("voll", "full")}</span>
                </div>
            );
        })}
    </div>
);

const WachenTab = ({ s }: { s: State }) => s.stations.length === 0
    ? <Empty>{t("Keine Rettungswache gebaut. Ohne Wache schicken die Spitäler ihre eigenen RTW.", "No ambulance depot built. Without a depot, hospitals send their own ambulances.")}</Empty> : (
    <div>
        <div className={`${styles.row} ${styles.head} ${styles.colsStation}`}>
            <span>{t("Wache", "Station")}</span><span>{t("Verfügbarkeit", "Availability")}</span><span className={styles.right}>{statusText("frei")}</span>
            <span className={styles.right}>{t("im Einsatz", "on call")}</span><span className={styles.right}>{t("gesamt", "total")}</span>
        </div>
        {s.stations.map(st => (
            <div key={st.name} className={`${styles.row} ${styles.colsStation}`}>
                <span className={styles.strong}>{st.name}</span>
                <span className={styles.barCell}><div className={styles.bar}><div className={`${styles.barFill} ${styles.sFree}`} style={{ width: `${pct(st.free, st.total)}%` }} /></div></span>
                <span className={`${styles.num} ${styles.right}`}>{st.free}</span>
                <span className={`${styles.num} ${styles.right}`}>{st.onCall}</span>
                <span className={`${styles.num} ${styles.right}`}>{st.total}</span>
            </div>
        ))}
    </div>
);

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
    }, [roads, view]);
    if (roads.length === 0) return <Empty>{t("Noch keine Strassen gebaut", "No roads built yet")}</Empty>;
    // Active units last so they are drawn on top of parked ones.
    const vehicles = [...s.vehicles].sort((a, b) => Number(isActive(a)) - Number(isActive(b)));
    return (
        <div>
            <div className={compact ? `${styles.map} ${styles.mapCompact}` : styles.map}
                onWheel={e => { e.stopPropagation(); const [mx, my] = toMap(e); zoomAt(mx, my, e.deltaY < 0 ? 1.25 : 0.8); }}
                onMouseDown={e => { drag.current = toMap(e); }}
                onMouseMove={onMove}
                onMouseUp={() => { drag.current = null; }}
                onMouseLeave={() => { drag.current = null; }}>
                <canvas ref={canvas} width={MAP_W} height={MAP_H} className={styles.mapCanvas} />
                {s.stations.map(st => <Marker key={"w" + st.name} at={project(st.x, st.z)} className={styles.mkStation} title={t(`Wache ${st.name}: ${st.free}/${st.total} frei`, `Station ${st.name}: ${st.free}/${st.total} available`)} />)}
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
            </div>
            {!compact && <div className={`${styles.legend} ${styles.mapLegend}`}>
                {["Anfahrt", "am Einsatzort", "Transport", "Rückfahrt", "frei"].map(st => (
                    <span key={st} className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.dot} ${STATUS_CLASS[st]}`} />{statusText(st)}</span>
                ))}
                <span className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.mkNef} ${styles.sReturning}`} />{t("NEF", "Doctor")}</span>
                <span className={`${styles.status} ${styles.legendItem}`}><span className={styles.mkHospital} />{t("Spital", "Hospital")}</span>
                <span className={`${styles.status} ${styles.legendItem}`}><span className={styles.mkStation} />{t("Wache", "Station")}</span>
                <span className={`${styles.status} ${styles.legendItem}`}><span className={`${styles.mkPolice} ${styles.sPatrol}`} />{t("Polizei", "Police")}</span>
                <span className={styles.zoom}>
                    <button className={styles.chip} onClick={() => zoomAt(MAP_W / 2, MAP_H / 2, 0.8)}>−</button>
                    <button className={styles.chip} onClick={() => zoomAt(MAP_W / 2, MAP_H / 2, 1.25)}>+</button>
                    <button className={view.k === 1 ? styles.chipOn : styles.chip} onClick={() => setView(HOME)}>{t("Ganze Stadt", "Whole city")}</button>
                </span>
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
    if (s.stations.length === 0) out.push({ sev: "low", title: t("Keine Rettungswache", "No ambulance depot"), text: t("Spitäler schicken ihre eigenen RTW", "Hospitals send their own ambulances") });
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

interface Call { key: string; code: string; level: string; reason: string; destination: string; units: Vehicle[]; }

// Call id = patient entity index from DispatchUISystem; units without one (0) each stay their own row.
const activeCalls = (s: State): Call[] => {
    const calls = new Map<string, Call>();
    for (const v of s.vehicles.filter(isActive)) {
        const key = v.call ? String(v.call) : v.callsign;
        const c = calls.get(key) ?? { key, code: v.code, level: v.level, reason: v.reason, destination: v.destination, units: [] };
        if (!c.code && v.code) Object.assign(c, { code: v.code, level: v.level, reason: v.reason }); // NEF may carry no code
        if (!c.destination) c.destination = v.destination;
        c.units.push(v);
        calls.set(key, c);
    }
    return [...calls.values()].sort((a, b) => levelRank(a.level) - levelRank(b.level));
};

const DashboardTab = ({ s, go }: { s: State; go: (p: Page) => void }) => {
    const list = alerts(s);
    const calls = activeCalls(s);
    const [open, setOpen] = useState<string | null>(null);
    return (
        <div className={styles.grid}>
            <div className={styles.gridLeft}>
                <Card title={t("Einsatzkarte", "Live map")} action={t("Vollbild", "Full map")} onAction={() => go("Karte")}>
                    <KarteTab s={s} compact />
                </Card>
                <Card title={`${t("Laufende Einsätze", "Active calls")} (${calls.length})`} action={t("Alle Fahrzeuge", "All vehicles")} onAction={() => go("Fahrzeuge")}>
                    <div className={`${styles.row} ${styles.head} ${styles.colsCall}`}>
                        <span>Code</span><span>{t("Einsatzgrund", "Reason")}</span><span>{t("Ziel", "Destination")}</span><span>{t("Einheiten", "Units")}</span><span />
                    </div>
                    {calls.length === 0 ? <Empty>{t("Keine laufenden Einsätze", "No active calls")}</Empty> : calls.map(c => (
                        <div key={c.key}>
                            <button className={`${styles.row} ${styles.colsCall} ${styles.callToggle}`} onClick={() => setOpen(open === c.key ? null : c.key)}>
                                <span>{c.code ? <LevelBadge level={c.level} code={c.code} /> : <span className={styles.dim}>–</span>}</span>
                                <span className={styles.clip}>{c.reason || t("Krankentransport", "Patient transfer")}</span>
                                <span className={styles.clip}>{c.destination}</span>
                                <span>{c.units.length}</span>
                                <span className={styles.caret}>{open === c.key ? "▴" : "▾"}</span>
                            </button>
                            {open === c.key && (
                                <div className={styles.callUnits}>
                                    {c.units.map(v => (
                                        <div key={v.callsign} className={styles.callUnitRow}>
                                            <span className={styles.strong}>{v.callsign}<span className={styles.sub}>{v.station}{v.nef ? " · NEF" : ""}</span></span>
                                            <span className={styles.clip}>{v.units}</span>
                                            <span className={`${styles.pill} ${STATUS_CLASS[v.status] ?? styles.sOff}`}>{statusText(v.status)}</span>
                                        </div>
                                    ))}
                                </div>
                            )}
                        </div>
                    ))}
                </Card>
            </div>
            <div className={styles.gridRight}>
                <Card title={t("Warnungen", "Risk alerts")} action={t("Statistik", "Statistics")} onAction={() => go("Statistik")}>
                    {list.length === 0 ? <Empty>{t("Keine Warnungen", "No alerts")}</Empty> : list.slice(0, 5).map((a, i) => (
                        <div key={i} className={styles.alert}>
                            <span className={`${styles.alertIcon} ${styles["sev_" + a.sev]}`}>!</span>
                            <div className={styles.alertBody}>
                                <div className={styles.alertTitle}>{a.title}<span className={`${styles.sevTag} ${styles["sevTag_" + a.sev]}`}>{t(SEV_TEXT[a.sev][0], SEV_TEXT[a.sev][1])}</span></div>
                                <div className={`${styles.alertText} ${styles.clip}`}>{a.text}</div>
                            </div>
                        </div>
                    ))}
                    {list.length > 5 && <div className={styles.note}>+ {list.length - 5} {t("weitere", "more")}</div>}
                </Card>
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
    Fahrverhalten: { en: "Driving", names: ["ParkOnSidewalk", "ParkAtCrossing", "PushBlockers", "EmergencyLaneChange", "TurnFromAnyLane", "ClearRedLight", "ClearRedLightMaxCars", "EmergencyCorridor", "MovingCorridor", "PoliceEmergencyDriving", "DriveThrough", "DriveThroughSeconds"] },
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

type Page = "Dashboard" | "Karte" | "Statistik" | "Fahrzeuge" | "Wachen" | "Spitäler" | "Debug" | `Einstellungen/${SettingGroup}`;
const NAV: { de: string; en: string; items: { id: Page; de: string; en: string }[] }[] = [
    { de: "Leitstelle", en: "Dispatch", items: [
        { id: "Dashboard", de: "Übersicht", en: "Dashboard" }, { id: "Karte", de: "Karte", en: "Map" }, { id: "Statistik", de: "Statistik", en: "Statistics" }] },
    { de: "Ressourcen", en: "Resources", items: [
        { id: "Fahrzeuge", de: "Fahrzeuge", en: "Vehicles" }, { id: "Wachen", de: "Wachen", en: "Stations" }, { id: "Spitäler", de: "Spitäler", en: "Hospitals" }] },
    { de: "Einstellungen", en: "Settings", items: (Object.keys(SETTING_GROUPS) as SettingGroup[]).map(g => ({ id: `Einstellungen/${g}` as Page, de: g, en: SETTING_GROUPS[g].en })) },
    { de: "System", en: "System", items: [{ id: "Debug", de: "Debug", en: "Debug" }] },
];

export const EmsPanel = () => {
    const open = useValue(open$);
    const s = useValue(state$);
    const [page, setPage] = useState<Page>("Dashboard");
    const [closed, setClosed] = useState<Record<string, boolean>>({});
    useLang();
    if (!open) return null;
    const active = s ? s.vehicles.filter(isActive).length : 0;
    const current = NAV.flatMap(g => g.items).find(x => x.id === page)!;
    return (
        <Panel draggable initialPosition={{ x: 0.5, y: 0.5 }} className={styles.panel} header={t("Leitstelle", "Dispatch")} onClose={toggle}>
            <div className={styles.shell}>
                <nav className={styles.sidebar}>
                    {NAV.map(g => (
                        <div key={g.en} className={styles.navGroup}>
                            <button className={styles.navGroupHead} onClick={() => setClosed({ ...closed, [g.en]: !closed[g.en] })}>
                                {t(g.de, g.en)}<span className={styles.caret}>{closed[g.en] ? "+" : "−"}</span>
                            </button>
                            {!closed[g.en] && g.items.map(it => (
                                <button key={it.id} className={it.id === page ? styles.navItemActive : styles.navItem} onClick={() => setPage(it.id)}>
                                    {t(it.de, it.en)}{it.id === "Fahrzeuge" && active > 0 && <span className={styles.tabCount}>{active}</span>}
                                </button>
                            ))}
                        </div>
                    ))}
                </nav>
                <div className={styles.main}>
                    <div className={styles.pageHead}>
                        <h1>{t(current.de, current.en)}</h1>
                        <span className={styles.note}>{t("Einsätze und Rettungsmittel in Echtzeit", "Calls and units in real time")}</span>
                    </div>
                    <Scrollable className={styles.content} vertical>
                        {page.startsWith("Einstellungen/") ? <SettingsTab group={page.split("/")[1] as SettingGroup} />
                            : page === "Debug" ? <DebugTab />
                            : !s ? <Empty>{t("Warte auf Daten aus der Simulation …", "Waiting for simulation data …")}</Empty>
                            : page === "Dashboard" ? <DashboardTab s={s} go={setPage} />
                            : page === "Statistik" ? <LageTab s={s} />
                            : page === "Karte" ? <KarteTab s={s} />
                            : page === "Fahrzeuge" ? <FahrzeugeTab s={s} />
                            : page === "Spitäler" ? <SpitaelerTab s={s} />
                            : <WachenTab s={s} />}
                    </Scrollable>
                </div>
            </div>
        </Panel>
    );
};
