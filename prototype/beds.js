// Hospital pages: bed capacity (3 stacked panels, CSS only) and full hospitals (2 side by side).
// Uses globals from intro.html (lang, sq, rig, DEFS, AMB, clamp, SV, PANELS, FADE, EO) at call time only.

// ---- Bed capacity: 5 %, 10 % of the vanilla beds or a custom value (slider 5-50 %, starts at the mod default 25 %).
// 10 bed icons on a compressed scale so small values stay visible: 5 % = 2 beds, 10 % = 4 beds, 50 % = all 10.
const BED_V=[5,10,"custom"];
let BED_CUSTOM=25, bedCustom=true; // custom value and whether the custom panel is the active choice
const BED_TXT={en:["5 %","10 %","Custom"],de:["5 %","10 %","Eigener Wert"]};
const BED_PTS=[[0,0],[5,2],[10,4],[50,10]]; // percent -> filled beds, linear in between, rounded
const bedsOn=v=>{const k=BED_PTS.findIndex(p=>p[0]>=v),[x0,y0]=BED_PTS[k-1]||BED_PTS[0],[x1,y1]=BED_PTS[k];return Math.round(y0+(y1-y0)*(v-x0)/(x1-x0||1))};
const bedIcons=(v,anim)=>Array.from({length:10},(_,i)=>`<i class="bed${i<bedsOn(v)?" on":""}" style="--i:${i}${anim?"":";animation:none"}"></i>`).join("");
const bstage=()=>BED_V.map((v,m)=>{const c=v==="custom";
  return `<div class="panel mixp bedp" role="button" tabindex="0"><div class="mname">${BED_TXT[lang][m]}${c?`<span class="bedval"> ${BED_CUSTOM} %</span><div class="bslider" role="slider" tabindex="0" aria-label="${BED_TXT[lang][m]}" aria-valuemin="5" aria-valuemax="50" aria-valuenow="${BED_CUSTOM}" style="--p:${(BED_CUSTOM-5)/45*100}%"><div class="bfill"></div><div class="bthumb"></div></div>`:""}</div><div class="beds">${bedIcons(c?BED_CUSTOM:v,true)}</div></div>`}).join("");
// Hand-made slider (5-50 %, steps of 5): drag, click or arrow keys. Using it selects the custom panel.
const bedSet=(v,el)=>{
  v=Math.max(5,Math.min(50,Math.round(v/5)*5));BED_CUSTOM=v;bedCustom=true;S.BedCapacityPercent=v;
  const p=el.closest(".panel");el.style.setProperty("--p",(v-5)/45*100+"%");el.setAttribute("aria-valuenow",v);
  p.querySelector(".bedval").textContent=` ${v} %`;p.querySelector(".beds").innerHTML=bedIcons(v,false);syncSel();
};
const bedAt=(el,x)=>{const r=el.getBoundingClientRect();return 5+clamp((x-r.left)/r.width)*45};
document.addEventListener("pointerdown",e=>{const el=e.target.closest(".bslider");if(el){el.setPointerCapture(e.pointerId);bedSet(bedAt(el,e.clientX),el)}});
document.addEventListener("pointermove",e=>{const el=e.target.closest(".bslider");if(el&&el.hasPointerCapture(e.pointerId))bedSet(bedAt(el,e.clientX),el)});
document.addEventListener("keydown",e=>{const el=e.target.closest(".bslider"),d={ArrowLeft:-5,ArrowDown:-5,ArrowRight:5,ArrowUp:5}[e.key];
  if(!el)return;
  if(d||e.key==="Home"||e.key==="End"){e.preventDefault();bedSet(e.key==="Home"?5:e.key==="End"?50:BED_CUSTOM+d,el)}});

// ---- Full hospitals: the left hospital is full, the right one has beds. Vanilla drives to the full one, the mod skips it.
const FULL_TXT={en:["Sent to full hospitals too","Skips full hospitals"],de:["Auch zu vollen Häusern","Überspringt volle Häuser"]};
const FULL_LBL={en:["FULL","FREE"],de:["VOLL","FREI"]};
const HOSP_ROUTE=["M150 760V470C150 340 85 360 85 235","M150 760V470C150 340 215 360 215 235"];
const hosp=(cx,full)=>`<path d="${sq(110,130,24)}" transform="translate(${cx} 145)" fill="#e6edf5"/><path d="${sq(40,24,8)}" transform="translate(${cx} 198)" fill="#14202e"/><path d="M${cx} 92v20M${cx-10} 102h20" stroke="#4db2ff" stroke-width="6" stroke-linecap="round"/><text x="${cx}" y="152" text-anchor="middle" font-size="22" font-weight="700" font-family="system-ui,sans-serif" fill="${full?"#d33":"#16a34a"}">${FULL_LBL[lang][full?0:1]}</text>`;
const hospSvg=m=>SV(`<rect width="300" height="500" fill="#2a3a30"/><rect x="40" y="220" width="220" height="45" fill="#262626"/><rect x="115" y="265" width="70" height="235" fill="#262626"/><path class="geo" d="${HOSP_ROUTE[m]}" fill="none"/>${DEFS}${hosp(85,true)}${hosp(215,false)}<g class="fade">${AMB}</g><g class="mk" opacity="0" transform="translate(${m?215:85} 66)"><circle r="18" fill="#14202e"/>${m?`<path d="M-9 1l6 7 12-14" fill="none" stroke="#4ade80" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>`:`<path d="M-8 -8l16 16M8 -8l-16 16" stroke="#ff4d4d" stroke-width="5" stroke-linecap="round"/>`}</g>`);
const hstage=()=>PANELS(2,FULL_TXT[lang],hospSvg);
function initHosp(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n);
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    r.draw(EO((t-.8)/3.4)*r.L,t);
    q(".mk").setAttribute("opacity",clamp((t-4.2)/.4)*FADE(t));
  };
}
