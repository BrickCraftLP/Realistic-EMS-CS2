import { trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { PanelSection, PanelSectionRow } from "cs2/ui";
import { useEffect, useRef, useState } from "react";

// Rendered for RealisticEMS.StationCallsignSection (C#): station callsign, 1-4 letters/digits, empty = number.
// slots > 0 adds the ambulance slider (StationFleetSystem): fire station = ambulances vs. fire engines, depot = ambulances on duty.
// Material 3 dark scheme, same tokens as .shell.dark in ems-panel.module.scss (the game's info panel is dark).
const M3 = { primary: "#aac7ff", onPrimary: "#0a305f", track: "#3e4759", ink: "#e2e2e9", ink2: "#c4c6d0", outline: "#8e9099", error: "#ffb4ab", onError: "#690005" };

// M3 outlined text field: label notched into the top border, primary outline while focused.
const CallsignField = ({ label, value, onSave }: { label: string; value: string; onSave: (v: string) => void }) => {
    const [v, setV] = useState(value);
    const [focus, setFocus] = useState(false);
    useEffect(() => setV(value), [value]);
    const save = () => { if (v !== value) onSave(v); };
    return (
        <div style={{ position: "relative", padding: "10rem 0 6rem" }}>
            <span style={{ position: "absolute", left: "12rem", top: "2rem", padding: "0 4rem", fontSize: "12rem", lineHeight: "16rem",
                color: focus ? M3.primary : M3.ink2, backgroundColor: "rgba(30,34,39,1)" }}>{label}</span>
            <input value={v} maxLength={4} placeholder="1-4" aria-label={label}
                style={{ width: "100%", height: "48rem", padding: "0 16rem", borderRadius: "4rem", fontSize: "18rem", fontWeight: 700,
                    letterSpacing: "2rem", textTransform: "uppercase", color: M3.ink, backgroundColor: "transparent",
                    border: focus ? `2rem solid ${M3.primary}` : `1rem solid ${M3.outline}` }}
                onFocus={() => setFocus(true)} onBlur={() => { setFocus(false); save(); }}
                onChange={e => setV(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 4))}
                onKeyDown={e => e.key === "Enter" && save()} />
        </div>
    );
};

export const StationSection = (p: {
    callsign: string; ambCallsign: string; slots: number; ambulances: number; fire: boolean; livery: boolean; nawOk: boolean; naw: number; nefOk: boolean;
    bedMax: number; beds: number; nefMax: number; nef: number; carMax: number; cars: number;
    heliDebug: boolean; heliStatus: string;
}) => {
    const de = useLocalization().translate("RealisticEMS.Lang") === "de";
    return (
        <PanelSection>
            <CallsignField label={de ? "Funkkennung" : "Callsign"} value={p.callsign} onSave={v => trigger("StationCallsignSection", "set", v)} />
            {p.fire && p.slots > 0 && <CallsignField label={de ? "Funkkennung RTW" : "Ambulance callsign"} value={p.ambCallsign}
                onSave={v => trigger("StationCallsignSection", "setAmb", v)} />}
            {p.slots > 0 && <FleetSlider slots={p.slots} value={p.ambulances} action="setAmbulances"
                labels={de ? ["Rettungswagen", p.fire ? "Löschfahrzeuge" : "Außer Dienst"] : ["Ambulances", p.fire ? "Fire engines" : "Off duty"]} />}
            {p.bedMax > 0 && <FleetSlider slots={p.bedMax} value={p.beds} action="setBeds" h={28}
                labels={de ? ["Betten", "Gesperrt"] : ["Beds", "Closed"]} />}
            {p.nefMax > 0 && <FleetSlider slots={p.nefMax} value={p.nef} action="setNef"
                labels={de ? ["Notarzt (NEF)", "Außer Dienst"] : ["Emergency doctors", "Off duty"]} />}
            {p.carMax > 0 && <FleetSlider slots={p.carMax} value={p.cars} action="setCars"
                labels={de ? ["Streifenwagen", "Außer Dienst"] : ["Patrol cars", "Off duty"]} />}
            {p.slots > 0 && p.fire && <PanelSectionRow left={de ? "RTW in Feuerwehr-Rot (Beta)" : "Fire brigade red ambulances (beta)"}
                right={<Switch on={p.livery} label={de ? "RTW in Feuerwehr-Rot (Beta)" : "Fire brigade red ambulances (beta)"}
                    onClick={() => trigger("StationCallsignSection", "setLivery", !p.livery)} />} />}
            {p.nawOk && <PanelSectionRow left="NAW (Notarzt-RTW)"
                right={<Switch on={p.naw === 1} label="NAW (Notarzt-RTW)"
                    onClick={() => trigger("StationCallsignSection", "setNaw", p.naw === 1 ? 0 : 1)} />} />}
            {p.nawOk && p.nefOk && <PanelSectionRow left="NEF (Notarzt-Fahrzeug)"
                right={<Switch on={p.naw === 2} label="NEF (Notarzt-Fahrzeug)"
                    onClick={() => trigger("StationCallsignSection", "setNaw", p.naw === 2 ? 0 : 2)} />} />}
            {p.heliDebug && <div style={{ padding: "8rem 0" }}>
                <button onClick={() => trigger("StationCallsignSection", "heliCritical")}
                    style={{ width: "100%", height: "40rem", borderRadius: "20rem", border: "none", fontSize: "16rem", fontWeight: 700,
                        backgroundColor: M3.error, color: M3.onError }}>
                    {de ? "Debug: Patient kritisch → RTH-Verlegung" : "Debug: patient critical → helicopter transfer"}
                </button>
                {p.heliStatus && <div style={{ fontSize: "14rem", opacity: 0.8, marginTop: "6rem" }}>{p.heliStatus}</div>}
            </div>}
        </PanelSection>
    );
};

// Material 3 expressive slider: two rounded tracks split by a thin bar handle with a gap on each side.
// No CSS gap/calc in Gameface, so the gaps are margins and the tracks share the rest by flex-grow.
const BAR = 4, GAP = 6, INNER = 6; // rem; track height h is per slider, corner radius h/2
const track = (grow: number, left: boolean, on: boolean, h: number): React.CSSProperties => { const R = h / 2; return {
    flexGrow: grow, flexBasis: 0, height: `${h}rem`, display: "flex", alignItems: "center", overflow: "hidden",
    justifyContent: left ? "flex-start" : "flex-end", padding: `0 ${R / 1.5}rem`,
    backgroundColor: on ? M3.primary : M3.track, color: on ? M3.onPrimary : M3.ink,
    fontSize: h < 36 ? "15rem" : "18rem", fontWeight: 700,
    borderRadius: left ? `${R}rem ${INNER}rem ${INNER}rem ${R}rem` : `${INNER}rem ${R}rem ${R}rem ${INNER}rem`,
}; };

const FleetSlider = ({ slots, value, labels, action, h = 40 }: { slots: number; value: number; labels: string[]; action: string; h?: number }) => {
    const [v, setV] = useState(value);
    const ref = useRef<HTMLDivElement>(null);
    const dragging = useRef(false);
    useEffect(() => { if (!dragging.current) setV(value); }, [value]);
    const at = (x: number) => {
        const r = ref.current!.getBoundingClientRect();
        return Math.round(Math.max(0, Math.min(1, (x - r.left) / r.width)) * slots);
    };
    const down = (e: React.MouseEvent) => {
        dragging.current = true;
        let cur = at(e.clientX);
        setV(cur);
        const move = (ev: MouseEvent) => { cur = at(ev.clientX); setV(cur); };
        const up = () => {
            dragging.current = false;
            window.removeEventListener("mousemove", move);
            window.removeEventListener("mouseup", up);
            trigger("StationCallsignSection", action, cur);
        };
        window.addEventListener("mousemove", move);
        window.addEventListener("mouseup", up);
    };
    const rest = slots - v;
    return (
        <div style={{ padding: "8rem 0 12rem" }}>
            <div style={{ display: "flex", justifyContent: "space-between", fontSize: "14rem", opacity: 0.8, marginBottom: "8rem" }}>
                <span>{labels[0]}</span><span>{labels[1]}</span>
            </div>
            <div ref={ref} onMouseDown={down} role="slider" aria-valuemin={0} aria-valuemax={slots} aria-valuenow={v} aria-label={labels[0]}
                style={{ display: "flex", alignItems: "center", height: `${h + 16}rem` }}>
                {v > 0 && <div style={track(v, true, true, h)}>{v}</div>}
                <div style={{ width: `${BAR}rem`, height: `${h + 16}rem`, borderRadius: `${BAR / 2}rem`, backgroundColor: M3.primary,
                    marginLeft: v > 0 ? `${GAP}rem` : 0, marginRight: rest > 0 ? `${GAP}rem` : 0 }} />
                {rest > 0 && <div style={track(rest, false, false, h)}>{rest}</div>}
            </div>
        </div>
    );
};

// Material 3 switch: primary track + on-primary handle when on, outlined when off.
const Switch = ({ on, label, onClick }: { on: boolean; label: string; onClick: () => void }) => (
    <div role="switch" aria-checked={on} aria-label={label} onClick={onClick}
        style={{ width: "52rem", height: "32rem", borderRadius: "16rem", display: "flex", alignItems: "center",
            justifyContent: on ? "flex-end" : "flex-start", padding: "0 4rem",
            backgroundColor: on ? M3.primary : M3.track, border: on ? "none" : `2rem solid ${M3.outline}` }}>
        <div style={{ width: on ? "24rem" : "16rem", height: on ? "24rem" : "16rem", margin: on ? 0 : "0 4rem",
            borderRadius: "12rem", backgroundColor: on ? M3.onPrimary : M3.outline }} />
    </div>
);
