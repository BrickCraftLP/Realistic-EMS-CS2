import { ModRegistrar } from "cs2/modding";
import { EmsButton, EmsPanel } from "mods/ems-panel";
import { EmsIntro } from "mods/intro";
import { EmsNews } from "mods/news";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopLeft", EmsButton);
    moduleRegistry.append("Game", EmsPanel);
    moduleRegistry.append("Game", EmsIntro);
    moduleRegistry.append("Game", EmsNews);
    // Sections are picked by C# type name: render VehicleStateSection (replaces VehiclesSection) with the vanilla component.
    moduleRegistry.extend("game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
        "selectedInfoSectionComponents", (components: any) => {
            components["RealisticEMS.VehicleStateSection"] = components["Game.UI.InGame.VehiclesSection"];
            return components;
        });
};

export default register;
