// "What's new" screen: page 1 = last 3 changelogs, page 2 = animated "report bugs on GitHub" with a button that opens the issues page.
// Opened by DispatchUISystem (binding realisticEMS.news) after the setup screen or once per version; closing sends closeNews.
// Same window as the setup screen (styles come from intro.scss). Gameface: no CSS grid/gap/variables, SVG animated through attributes.
import { useEffect, useRef, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import "./intro.scss";
import "./news.scss";

const news$ = bindValue<boolean>("realisticEMS", "news", false);
const newsBug$ = bindValue<boolean>("realisticEMS", "newsBug", false);

type Section = { kind: "new" | "fixed" | "removed"; items: string[] };
type Entry = { v: string; en: Section[]; de: Section[] };

// Newest first. Keep only the last 3 here; older ones live on GitHub. When adding a version, also bump Mod.Version (Mod.cs).
const LOG: Entry[] = ([
    {
        v: "26.0.2b",
        en: [
            { kind: "new", items: [
                "Rettungsgasse reworked: on highways every traffic jam forms the corridor, on multi-lane roads as soon as an emergency vehicle comes. Left lane goes left, all others right, trucks included. Ramps stay clear, convoys pass together.",
                "Narrow roads: both directions pull to the kerb and the emergency vehicle drives on the centre line.",
                "Oncoming overtake: with trucks or buses in the queue the oncoming lane is closed and the whole queue is passed.",
                "Driving aids now cover every vehicle with lights: ambulance, emergency doctor, police and fire brigade.",
                "Option \"Siren only when needed\": in jams, overtakes and at red lights.",
                "Left-hand traffic cities are mirrored automatically.",
                "Dispatch panel in Material 3 with light and dark mode, plus a 3D city map.",
                "Call info (code, phase, units, times) in the selected vehicle panel.",
            ] },
            { kind: "fixed", items: ["\"Export log\" now writes a SHA-256 hash file instead of a signature."] },
        ],
        de: [
            { kind: "new", items: [
                "Rettungsgasse überarbeitet: Auf der Autobahn bildet jeder Stau die Gasse, auf mehrspurigen Straßen sobald ein Einsatzfahrzeug kommt. Linke Spur nach links, alle anderen nach rechts, Lkw inklusive. Rampen bleiben frei, Kolonnen fahren gemeinsam durch.",
                "Schmale Straßen: Beide Richtungen fahren an den Rand, das Einsatzfahrzeug fährt auf der Mittellinie.",
                "Überholen auf der Gegenfahrbahn: Stehen Lkw oder Busse in der Kolonne, wird die Gegenfahrbahn gesperrt und die ganze Kolonne überholt.",
                "Die Fahrhilfen gelten für alle Fahrzeuge mit Blaulicht: RTW, NEF, Polizei und Feuerwehr.",
                "Option \"Sirene nur bei Bedarf\": im Stau, beim Überholen und an roten Ampeln.",
                "Städte mit Linksverkehr werden automatisch gespiegelt.",
                "Einsatzpanel in Material 3 mit hellem und dunklem Modus, dazu eine 3D-Stadtkarte.",
                "Einsatzinfo (Code, Phase, Einheiten, Zeiten) im Fahrzeugfenster.",
            ] },
            { kind: "fixed", items: ["\"Protokoll exportieren\" schreibt jetzt eine SHA-256-Hashdatei statt einer Signatur."] },
        ],
    },
    {
        v: "26.0.1b",
        en: [
            { kind: "new", items: [
                "Call volume option: Few, Balanced or Many sick and injured calls (about half, as vanilla, about twice). Deaths are not affected.",
                "Setup screen on the first game load, and again from the Dashboard of the EMS panel.",
                "This \"What's new\" screen after every update.",
                "Fire engines get no emergency corridor (too big). They overtake on a free lane, or on the oncoming lane for any standing queue.",
                "Anything on the oncoming lane ahead of an overtaking ambulance now stops and keeps right.",
                "\"Export log\" button in the options for signed bug-report logs. The Debug page now needs the debug password.",
            ] },
            { kind: "fixed", items: [
                "Blue lights stayed on after a call or while driving home without a critical patient.",
                "Ambulances slid sideways the whole way out of a queue. They now pull out diagonally.",
                "Parked vehicles could teleport far away and reappear somewhere else. Parking is now limited to a short distance.",
                "Emergency corridor on roads with two or more lanes: cars now move far enough aside, and a green light no longer breaks it up.",
                "Ambulances hopped between lanes when overtaking a queue and could not turn from the wrong lane on wide junctions.",
                "Emergency doctor cars from a saved game were dispatched and sent home in an endless loop.",
            ] },
            { kind: "removed", items: ["Drive-through last resort and its two options. Police emergency driving no longer includes it."] },
        ],
        de: [
            { kind: "new", items: [
                "Einsatzhäufigkeit: Wenige, Ausgewogen oder Viele Kranken- und Verletztenmeldungen (etwa halb so viele, wie Vanilla, etwa doppelt so viele). Todesfälle sind nicht betroffen.",
                "Einrichtungsassistent beim ersten Spielstart, erneut über das Dashboard des EMS-Fensters.",
                "Dieser \"Was ist neu\"-Bildschirm nach jedem Update.",
                "Feuerwehrautos bekommen keine Rettungsgasse (zu groß). Sie überholen auf einer freien Spur oder bei stehender Kolonne auf der Gegenfahrbahn.",
                "Alles auf der Gegenfahrbahn vor einem überholenden RTW hält jetzt an und fährt nach rechts.",
                "Schaltfläche \"Protokoll exportieren\" in den Optionen für signierte Fehlerberichte. Die Debug-Seite braucht jetzt das Debug-Passwort.",
            ] },
            { kind: "fixed", items: [
                "Blaulicht blieb nach dem Einsatz oder auf der Heimfahrt ohne kritischen Patienten an.",
                "RTW rutschten beim Ausscheren aus der Schlange seitlich entlang. Sie fahren jetzt diagonal heraus.",
                "Parkende Fahrzeuge konnten weit wegspringen und woanders auftauchen. Das Parken ist jetzt auf kurze Distanz begrenzt.",
                "Rettungsgasse auf Straßen mit zwei oder mehr Spuren: Autos weichen jetzt weit genug aus, eine grüne Ampel löst sie nicht mehr auf.",
                "RTW sprangen beim Überholen einer Kolonne zwischen den Spuren hin und her und konnten auf breiten Kreuzungen nicht von der falschen Spur abbiegen.",
                "Notarztwagen aus einem Spielstand wurden endlos alarmiert und wieder heimgeschickt.",
            ] },
            { kind: "removed", items: ["Durchfahren als letzter Ausweg samt zwei Optionen. Polizei-Einsatzfahrt enthält es nicht mehr."] },
        ],
    },
    {
        v: "26.0.0b",
        en: [{ kind: "new", items: [
            "First public beta: ambulance depots, AMPDS call codes, emergency doctor cars, on-scene care, scene parking and emergency driving.",
        ] }],
        de: [{ kind: "new", items: [
            "Erste öffentliche Beta: Rettungswachen, AMPDS-Einsatzcodes, Notarztwagen, Versorgung vor Ort, Parken am Einsatzort und Einsatzfahrt.",
        ] }],
    },
] as Entry[]).slice(0, 3);

const TXT = {
    en: {
        logTitle: "What's new", logSub: "Latest changes in Realistic EMS", latest: "Latest", ok: "OK",
        older: "Older versions are listed on GitHub.", releases: "Releases",
        kinds: { new: "New", fixed: "Fixed", removed: "Removed" },
        bugTitle: "Found a bug?", bugSub: "Please report it on GitHub.",
        bug1: "Open the Issues page and describe what happened.", bug2: "Please attach the log file:", bug3: "Turn on Debug logging in the options, then press Export log and attach that folder.",
        open: "Open GitHub Issues", close: "Close",
    },
    de: {
        logTitle: "Was ist neu", logSub: "Neuigkeiten in Realistic EMS", latest: "Neu", ok: "OK",
        older: "Ältere Versionen findest du auf GitHub.", releases: "Releases",
        kinds: { new: "Neu", fixed: "Behoben", removed: "Entfernt" },
        bugTitle: "Fehler gefunden?", bugSub: "Bitte melde ihn auf GitHub.",
        bug1: "Öffne die Issues-Seite und beschreibe, was passiert ist.", bug2: "Hänge bitte die Logdatei an:", bug3: "Schalte in den Optionen Debug-Protokoll ein, klicke auf Protokoll exportieren und hänge den neuen Ordner an.",
        open: "GitHub Issues öffnen", close: "Schließen",
    },
};
const KIND_COLOR = { new: "#4ade80", fixed: "#4db2ff", removed: "#fb923c" };

// ---- Bug report animation: a bug runs in, writes an issue (title, text, log file), a green tick closes the loop.
const clamp = (p: number) => Math.max(0, Math.min(1, p));
const EO = (p: number) => 1 - (1 - clamp(p)) ** 3;
const BUG_SVG = `<svg viewBox="0 -100 600 500" preserveAspectRatio="xMidYMid meet">
<rect class="ncard" x="210" y="34" width="360" height="232" rx="24" fill="#131c2b" stroke="#2a374d" stroke-width="3"/>
<circle class="o" cx="248" cy="78" r="14" fill="none" stroke="#4ade80" stroke-width="5" transform="rotate(-90 248 78)"/><circle class="od" cx="248" cy="78" r="4" fill="#4ade80"/>
<path class="l l1" d="M278 78H520" stroke="#e8eef7" stroke-width="10" stroke-linecap="round" fill="none"/>
<path class="l l2" d="M238 128H540" stroke="#3a465c" stroke-width="8" stroke-linecap="round" fill="none"/>
<path class="l l3" d="M238 156H500" stroke="#3a465c" stroke-width="8" stroke-linecap="round" fill="none"/>
<path class="l l4" d="M238 184H460" stroke="#3a465c" stroke-width="8" stroke-linecap="round" fill="none"/>
<rect class="lchip" x="238" y="212" width="210" height="34" rx="17" fill="#182131" stroke="#3ea6ff" stroke-width="2"/>
<text class="lchip" x="343" y="235" text-anchor="middle" font-size="16" fill="#8fa0b8">RealisticEMS.log</text>
<circle class="ok" cx="530" cy="228" r="22" fill="none" stroke="#4ade80" stroke-width="5" stroke-linecap="round" transform="rotate(-90 530 228)"/>
<path class="tick" d="M519 228l8 8 15-17" fill="none" stroke="#4ade80" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
<g class="bug">
<g class="lg"><path d="M-14 -14L-8 -34" /><path d="M0 -14L6 -34" /><path d="M14 -14L20 -34" /><path d="M-14 14L-8 34" /><path d="M0 14L6 34" /><path d="M14 14L20 34" /></g>
<path d="M36 -6L54 -22M36 6L54 22" stroke="#7a1f1f" stroke-width="4" stroke-linecap="round" fill="none"/>
<ellipse rx="30" ry="22" fill="#ff5d5d"/><circle cx="34" r="12" fill="#c43b3b"/>
<circle cx="-8" cy="-8" r="5" fill="#8f2323"/><circle cx="6" cy="6" r="5" fill="#8f2323"/><circle cx="-14" cy="8" r="4" fill="#8f2323"/>
<circle cx="39" cy="-5" r="2.6" fill="#fff"/><circle cx="39" cy="5" r="2.6" fill="#fff"/>
</g></svg>`;

function mountBug(root: HTMLElement) {
    root.innerHTML = BUG_SVG;
    const q = (s: string) => root.querySelector(s) as SVGElement;
    const lines: [SVGElement, number, number][] = [[q(".l1"), 242, 1.9], [q(".l2"), 302, 2.4], [q(".l3"), 262, 2.8], [q(".l4"), 222, 3.2]];
    const legs = Array.from(root.querySelectorAll(".lg path")) as SVGElement[];
    legs.forEach(l => { l.setAttribute("stroke", "#7a1f1f"); l.setAttribute("stroke-width", "5"); l.setAttribute("stroke-linecap", "round"); l.setAttribute("fill", "none"); });
    const draw = (el: SVGElement, len: number, p: number) => {
        el.setAttribute("stroke-dasharray", `${p * len} ${len}`);
        el.setAttribute("opacity", p > 0 ? "1" : "0");
    };
    const t0 = performance.now();
    let raf = 0;
    const frame = () => {
        const t = (performance.now() - t0) / 1000;
        q(".ncard").setAttribute("opacity", String(clamp(t / 0.4)));
        const walking = t < 1.5;
        const x = -60 + 170 * EO((t - 0.1) / 1.4);
        const y = 150 - (t > 1.6 ? Math.abs(Math.sin((t - 1.6) * 2.2)) * 8 : Math.sin(t * 14) * 2);
        q(".bug").setAttribute("transform", `translate(${x.toFixed(1)} ${y.toFixed(1)})`);
        legs.forEach((l, i) => l.setAttribute("transform", `translate(${(Math.sin(t * (walking ? 20 : 4) + i * 1.6) * (walking ? 4 : 1.2)).toFixed(2)} 0)`));
        draw(q(".o"), 88, clamp((t - 1.5) / 0.5));
        q(".od").setAttribute("opacity", t > 1.9 ? "1" : "0");
        lines.forEach(([el, len, at]) => draw(el, len, clamp((t - at) / 0.5)));
        root.querySelectorAll(".lchip").forEach(c => c.setAttribute("opacity", String(clamp((t - 3.7) / 0.4))));
        draw(q(".ok"), 138, clamp((t - 4.3) / 0.5));
        draw(q(".tick"), 40, clamp((t - 4.8) / 0.35));
        raf = requestAnimationFrame(frame);
    };
    frame();
    return () => { cancelAnimationFrame(raf); root.innerHTML = ""; };
}

const BugAnim = () => {
    const ref = useRef<HTMLDivElement>(null);
    useEffect(() => (ref.current ? mountBug(ref.current) : undefined), []);
    return <div className="bugstage" ref={ref} />;
};

export const EmsNews = () => {
    const open = useValue(news$);
    const bug = useValue(newsBug$);
    const de = useLocalization().translate("RealisticEMS.Lang") === "de";
    const [page, setPage] = useState(0);
    useEffect(() => { if (open) setPage(bug ? 1 : 0); }, [open]);
    if (!open) return null;
    const T = de ? TXT.de : TXT.en;
    const close = () => trigger("realisticEMS", "closeNews");
    return (
        <div className="ems-intro ems-news">
            <div className="card">
                <div className="top welcome">
                    <div className="head">
                        <h2>{page === 0 ? T.logTitle : T.bugTitle}</h2>
                        <p className="sub">{page === 0 ? T.logSub : T.bugSub}</p>
                    </div>
                </div>
                {page === 0 ? (
                    <div className="nbody">
                        {LOG.map((e, n) => (
                            <div className="entry" key={e.v}>
                                <div className="ver">{e.v}{n === 0 && <span className="tagnew">{T.latest}</span>}</div>
                                {(de ? e.de : e.en).map(s => (
                                    <div key={s.kind}>
                                        <div className="sec" style={{ color: KIND_COLOR[s.kind] }}>{T.kinds[s.kind]}</div>
                                        {s.items.map(t => <div className="li" key={t}><i /><span>{t}</span></div>)}
                                    </div>
                                ))}
                            </div>
                        ))}
                        <div className="older">
                            <span>{T.older}</span>
                            <button className="chip" onClick={() => trigger("realisticEMS", "openReleases")}>{T.releases}</button>
                        </div>
                    </div>
                ) : (
                    <>
                        <BugAnim />
                        <div className="bugtext">
                            <div>{T.bug1}</div>
                            <div>{T.bug2}</div>
                            <div className="file">Logs/RealisticEMS.log</div>
                            <div>{T.bug3}</div>
                            <div className="url">github.com/BrickCraftLP/Realistic-EMS-CS2/issues</div>
                        </div>
                    </>
                )}
                <div className="nav">
                    {page === 0
                        ? <button className="go" onClick={() => setPage(1)}>{T.ok}</button>
                        : <>
                            <button className="go" onClick={() => trigger("realisticEMS", "openIssues")}>{T.open}</button>
                            <button className="ghost" onClick={close}>{T.close}</button>
                        </>}
                </div>
                <div className="dots"><i className="on" /><i className={page === 1 ? "on" : ""} /></div>
            </div>
        </div>
    );
};
