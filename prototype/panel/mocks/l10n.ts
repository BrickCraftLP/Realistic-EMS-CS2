const de = new URLSearchParams(location.search).get("lang") === "de";
export const useLocalization = () => ({ translate: (k: string) => (k === "RealisticEMS.Lang" ? (de ? "de" : "en") : k) });
