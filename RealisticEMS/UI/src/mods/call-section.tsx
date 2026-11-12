import { Entity, selectedInfo } from "cs2/bindings";
import { PanelSection, PanelSectionRow } from "cs2/ui";
import styles from "./ems-panel.module.scss";

type Unit = { entity: Entity; callsign: string; status: string; self: boolean };
type Props = { code: string; level: string; info: string; phase: string; times: string; progress: number; units: Unit[] };

// Rendered for RealisticEMS.CallInfoSection (C#) in the selected-vehicle panel.
export const CallSection = (p: Props) => (<>
    <PanelSection>
        <PanelSectionRow left="Einsatzcode" right={<span className={styles["lv" + p.level]} style={{ padding: "2rem 8rem", borderRadius: "3rem", fontWeight: 700 }}>{p.code}</span>} />
        <div style={{ whiteSpace: "pre-line", padding: "6rem 0", opacity: 0.85 }}>{p.info}</div>
        <PanelSectionRow left={p.phase} right={p.progress >= 0 ? `${Math.round(p.progress * 100)} %` : ""} />
        {p.progress >= 0 && <div role="progressbar" aria-valuenow={Math.round(p.progress * 100)} aria-valuemin={0} aria-valuemax={100}
            style={{ height: "6rem", borderRadius: "3rem", backgroundColor: "rgba(255,255,255,0.15)", margin: "2rem 0 6rem" }}>
            <div className={styles["lv" + p.level]} style={{ height: "100%", width: `${p.progress * 100}%`, borderRadius: "3rem", transition: "width 0.5s" }} />
        </div>}
        {p.units.map(u => (
            <PanelSectionRow key={u.entity.index} subRow
                left={<span style={{ cursor: "pointer", fontWeight: u.self ? 700 : 400 }} onClick={() => selectedInfo.selectEntity(u.entity)}>{u.callsign}</span>}
                right={u.status} />
        ))}
    </PanelSection>
    {p.times && <PanelSection>
        {p.times.split("\n").map(line => {
            const [label, value] = line.split(/ (?=\d+ min)/);
            return <PanelSectionRow key={line} left={label} right={value} />;
        })}
    </PanelSection>}
</>);
