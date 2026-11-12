import { execSync } from "child_process";
const ui = "RealisticEMS/UI/node_modules/";
execSync(`node ${ui}webpack/bin/webpack.js --config prototype/panel/webpack.config.cjs`, { stdio: "inherit" });
console.log("built prototype/panel/dist/bundle.js -> open prototype/panel/index.html");
