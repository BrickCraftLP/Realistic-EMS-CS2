import { trigger } from "cs2/api";
import { PanelSection, PanelSectionRow } from "cs2/ui";
import { useEffect, useState } from "react";

// Rendered for RealisticEMS.StationCallsignSection (C#): station callsign, 1-4 letters/digits, empty = number.
export const StationSection = (p: { callsign: string }) => {
    const [value, setValue] = useState(p.callsign);
    useEffect(() => setValue(p.callsign), [p.callsign]);
    const save = () => { if (value !== p.callsign) trigger("StationCallsignSection", "set", value); };
    return (
        <PanelSection>
            <PanelSectionRow left="Funkrufname / Callsign" right={
                <input value={value} maxLength={4} placeholder="1-4" aria-label="Station callsign"
                    style={{ width: "60rem", textAlign: "center", textTransform: "uppercase" }}
                    onChange={e => setValue(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 4))}
                    onBlur={save} onKeyDown={e => e.key === "Enter" && save()} />
            } />
        </PanelSection>
    );
};
