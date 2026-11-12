// Builds prototype/intro-game.html from the REAL in-game source (intro.tsx + intro.scss), so the web copy never drifts.
// Run: node prototype/build-game.mjs   (edit the .tsx/.scss, rebuild, refresh the browser)
import { readFileSync, writeFileSync } from "fs";
import { execSync } from "child_process";
const ui = "RealisticEMS/UI/";
const css = execSync(`node ${ui}node_modules/sass/sass.js --no-source-map ${ui}src/mods/intro.scss`).toString().replace(/(\d)rem\b/g, "$1px");
const src = readFileSync(ui + "src/mods/intro.tsx", "utf8").split("\n").slice(24).join("\n"); // from "function sampler" on
writeFileSync("prototype/intro-game.html", `<!doctype html><meta charset=utf-8><title>EMS Intro (game UI)</title>
<style>html,body{margin:0;height:100%;background:#35404a;font-family:system-ui,sans-serif}body{position:relative;overflow:hidden}
#re{position:absolute;left:16px;top:16px;display:none}.ems-intro .card{zoom:var(--z,1)}${css}</style>
<button id=re>Reopen</button><div class=ems-intro id=root></div>
<script>
const trigger=(...a)=>{console.log("trigger",...a);if(a[1]==="closeIntro"){stop();root.style.display="none";re.style.display="block"}};
const fit=()=>root.style.setProperty("--z",Math.min(1,innerHeight/880));addEventListener("resize",fit);fit();
let stop=()=>{};const go=()=>{root.style.display="";re.style.display="none";stop=mount(root,navigator.language.startsWith("de")?"de":"en",[])};
re.onclick=go;
${src}
go();
</script>`);
console.log("wrote prototype/intro-game.html");
