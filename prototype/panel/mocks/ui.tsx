import { ReactNode } from "react";
export const FloatingButton = () => null;
export const Scrollable = ({ className, children }: { className?: string; children: ReactNode; vertical?: boolean }) =>
    <div className={className} style={{ overflowY: "auto", maxHeight: "calc(100vh - 140px)" }}>{children}</div>;
// Stand-in for the game's draggable Panel chrome.
export const Panel = ({ header, onClose, className, children }: any) => (
    <div className={className} style={{ margin: "24px auto", background: "#1b1d22", borderRadius: 16, padding: 10, color: "#fff", boxShadow: "0 8px 40px #0009" }}>
        <div style={{ display: "flex", justifyContent: "space-between", padding: "6px 12px 12px", fontSize: 16 }}>
            <b>{header}</b><button onClick={onClose} style={{ cursor: "pointer" }}>✕</button></div>
        {children}
    </div>
);
