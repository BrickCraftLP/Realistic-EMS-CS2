import { useSyncExternalStore } from "react";
// Fake game bindings: values live in `store`, trigger() mutates it like DispatchUISystem would.
export const store: Record<string, any> = {};
const subs = new Set<() => void>();
export const set = (k: string, v: any) => { store[k] = v; subs.forEach(f => f()); };
export const bindValue = <T,>(_g: string, name: string, def: T) => ({ name, def });
export const useValue = (b: any) => useSyncExternalStore(f => (subs.add(f), () => subs.delete(f)), () => (b.name in store ? store[b.name] : b.def));
export const trigger = (_g: string, name: string, ...a: any[]) => {
    console.log("trigger", name, ...a);
    if (name === "toggle") set("open", !store.open);
    if (name === "setSetting") set("settings", store.settings.map((s: any) => (s.name === a[0] ? { ...s, value: a[1], bool: s.bool } : s)));
    if (name === "refreshMap3d") (window as any).__loadBuildings?.();
};
