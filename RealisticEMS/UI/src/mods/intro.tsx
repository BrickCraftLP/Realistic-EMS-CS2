// @ts-nocheck
// Setup screen: in-game port of prototype/intro.html (+ its scene scripts). Vanilla DOM code mounted into one React div.
// Opened by DispatchUISystem (binding realisticEMS.intro) on the first game load and from the Dashboard.
// Each choice is written with realisticEMS.setSetting(name, value); closing sends closeIntro (sets Setting.IntroSeen).
// Gameface differences vs. the prototype: no CSS grid/gap/variables (inline styles instead), no SVG getPointAtLength
// (own path sampler), listeners on the root only, no localStorage.
import { useEffect, useRef } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import "./intro.scss";

const intro$ = bindValue<boolean>("realisticEMS", "intro", false);
const settings$ = bindValue<{ name: string; bool: boolean; value: number }[]>("realisticEMS", "settings", []);

export const EmsIntro = () => {
    const open = useValue(intro$);
    const settings = useValue(settings$);
    const de = useLocalization().translate("RealisticEMS.Lang") === "de";
    const ref = useRef<HTMLDivElement>(null);
    const ready = open && settings.length > 0; // start from the current settings, not the defaults
    useEffect(() => (ready && ref.current ? mount(ref.current, de ? "de" : "en", settings) : undefined), [ready]);
    return open ? <div className="ems-intro" ref={ref} /> : null;
};

// Polyline sampler for the route paths (absolute M L H V C only). Replaces SVGPathElement.getPointAtLength.
function sampler(d) {
    const tk = d.match(/[A-Za-z]|-?[\d.]+/g), P = [];
    let i = 0, x = 0, y = 0, cmd = "";
    const n = () => +tk[i++];
    while (i < tk.length) {
        if (/[A-Za-z]/.test(tk[i])) cmd = tk[i++];
        if (cmd === "M" || cmd === "L") { x = n(); y = n(); P.push([x, y]); }
        else if (cmd === "H") { x = n(); P.push([x, y]); }
        else if (cmd === "V") { y = n(); P.push([x, y]); }
        else if (cmd === "C") {
            const x1 = n(), y1 = n(), x2 = n(), y2 = n(), x3 = n(), y3 = n();
            for (let k = 1; k <= 24; k++) { const u = k / 24, v = 1 - u;
                P.push([v*v*v*x + 3*v*v*u*x1 + 3*v*u*u*x2 + u*u*u*x3, v*v*v*y + 3*v*v*u*y1 + 3*v*u*u*y2 + u*u*u*y3]); }
            x = x3; y = y3;
        } else i++;
    }
    const C = [0];
    for (let k = 1; k < P.length; k++) C.push(C[k - 1] + Math.hypot(P[k][0] - P[k - 1][0], P[k][1] - P[k - 1][1]));
    const L = C[C.length - 1];
    return { L, at(s) {
        if (P.length < 2) return { x: P[0][0], y: P[0][1] };
        let lo = 0, hi = C.length - 1;
        while (hi - lo > 1) { const m = (lo + hi) >> 1; if (C[m] <= s) lo = m; else hi = m; }
        const f = Math.max(0, Math.min(1, (s - C[lo]) / (C[hi] - C[lo] || 1)));
        return { x: P[lo][0] + (P[hi][0] - P[lo][0]) * f, y: P[lo][1] + (P[hi][1] - P[lo][1]) * f };
    } };
}

function mount(root, lang0, cur) {
const now = () => (window.performance ? performance.now() : Date.now());
let lang = lang0, step = 0, raf = 0;
const ENUMS = { CallMix: ["Realistic", "Balanced", "Dramatic"], CallVolume: ["Few", "Balanced", "Many"] };
const S = { OnlyDepotsDispatch: true, AvailableOnlyAtDepot: true, CallVolume: "Balanced", CallMix: "Realistic", EnableNef: false, OnSceneMinutes: 60, ParkOnSidewalk: true,
    EmergencyCorridor: true, OncomingOvertake: true, EmergencyLaneChange: true, ClearRedLight: true, BedCapacityPercent: 25, RejectFullHospitals: true };
cur.forEach(it => { if (it.name in S) S[it.name] = ENUMS[it.name] ? ENUMS[it.name][it.value] ?? S[it.name] : it.bool ? !!it.value : it.value; });

const T = {
 en:{back:"Back",next:"Next",done:"Finish",close:"Close",skip:"Skip",
  mix:{Realistic:"Realistic",Balanced:"Balanced",Dramatic:"Dramatic"},
  tags:["Vanilla","Realistic EMS"],bld:["Hospital","Depot"],nefTags:["Ambulance only","With emergency doctor"],
  steps:[
   {t:"Realistic EMS",s:""},
   {t:"Welcome to Realistic EMS",s:""},
   {t:"Dispatch",s:"Who sends the ambulance, and how severe are the calls?"},
   {t:"Call volume",s:"How many emergency calls do you want?"},
   {t:"Call severity",s:"How serious are the emergencies you get? Pick a mix."},
   {t:"Emergency doctor",s:"Should hospitals also send an emergency doctor car? (experimental)"},
   {t:"On-scene time",s:"How long do crews care for the patient?"},
   {t:"Parking",s:"Where does the ambulance park while caring?"},
   {t:"Emergency corridor",s:"Do cars make room when the ambulance is stuck?"},
   {t:"Oncoming lane",s:"May the ambulance overtake the queue on the oncoming lane?"},
   {t:"Lane change",s:"May the ambulance switch to a free neighbouring lane?"},
   {t:"Red lights",s:"May cars ahead run the red light to clear the way?"},
   {t:"Hospital beds",s:"How many beds do hospitals have compared to vanilla?"},
   {t:"Full hospitals",s:"Should ambulances skip hospitals that are full?"},
   {t:"Saved",s:""}]},
 de:{back:"Zurück",next:"Weiter",done:"Fertig",close:"Schließen",skip:"Überspringen",
  mix:{Realistic:"Realistisch",Balanced:"Ausgewogen",Dramatic:"Dramatisch"},
  tags:["Standard","Realistic EMS"],bld:["Krankenhaus","Rettungswache"],nefTags:["Nur Rettungswagen","Mit Notarzt"],
  steps:[
   {t:"Realistic EMS",s:""},
   {t:"Willkommen bei Realistic EMS",s:""},
   {t:"Disposition",s:"Wer schickt den Rettungswagen, und wie schwer sind die Einsätze?"},
   {t:"Einsatzhäufigkeit",s:"Wie viele Notrufe möchtest du?"},
   {t:"Einsatzschwere",s:"Wie schwer sind die Notfälle? Wähle einen Mix."},
   {t:"Notarzt",s:"Sollen Krankenhäuser zusätzlich einen Notarzt schicken? (experimentell)"},
   {t:"Versorgungszeit",s:"Wie lange versorgt die Besatzung den Patienten?"},
   {t:"Parken",s:"Wo parkt der Rettungswagen während der Versorgung?"},
   {t:"Rettungsgasse",s:"Machen Autos Platz, wenn der Rettungswagen feststeckt?"},
   {t:"Gegenfahrbahn",s:"Darf der Rettungswagen die Schlange auf der Gegenfahrbahn überholen?"},
   {t:"Spurwechsel",s:"Darf der Rettungswagen auf eine freie Nachbarspur wechseln?"},
   {t:"Rote Ampeln",s:"Dürfen Autos davor bei Rot fahren, um Platz zu machen?"},
   {t:"Krankenhausbetten",s:"Wie viele Betten haben Krankenhäuser im Vergleich zum Standard?"},
   {t:"Volle Krankenhäuser",s:"Soll der Rettungswagen volle Krankenhäuser überspringen?"},
   {t:"Gespeichert",s:""}]}
};

root.innerHTML = `<div class="card"><div class="top"><div class="head"><h2 id="h"></h2><p class="sub" id="sub"></p></div><div class="corner"><button id="skip"></button></div></div>`
    + `<div class="stage" id="stage"></div><div class="lower"><div class="body" id="body"></div><div class="nav"><button id="back"></button><button id="next" class="go"></button><span></span></div><div class="dots" id="dots"></div></div></div>`;

const N = 14;
const $ = s => root.querySelector(s);
const sq=(w,h,k)=>{const x=w/2,y=h/2;return `M${-x+k} ${-y}H${x-k}C${x} ${-y} ${x} ${-y} ${x} ${-y+k}V${y-k}C${x} ${y} ${x} ${y} ${x-k} ${y}H${-x+k}C${-x} ${y} ${-x} ${y} ${-x} ${y-k}V${-y+k}C${-x} ${-y} ${-x} ${-y} ${-x+k} ${-y}Z`};
const CARS=[[100,100],[100,204],[100,308],[200,100],[200,204],[200,308]];
const AP=["M200 540C200 480 150 470 150 375","M200 540C200 450 150 440 150 330L150 -80"];
const clamp=p=>Math.max(0,Math.min(1,p));
const EIO=p=>{p=clamp(p);return p*p*(3-2*p)}, EO=p=>1-(1-clamp(p))**3, FADE=t=>t<.5?t/.5:t>9.3?(10-t)/.7:1;
const SV=(body,vb="0 0 300 500")=>`<svg viewBox="${vb}" preserveAspectRatio="xMidYMid slice">${body}</svg>`;
const PANELS=(n,tags,svg)=>Array.from({length:n},(_,m)=>`<div class="panel"><span class="tag">${tags[m]}</span>${svg(m)}</div>`).join("");
const place=(el,x,y,a=-90)=>el.setAttribute("transform",`translate(${x} ${y}) rotate(${a})`);
let scenes=[],start=0,prev=-1,pk="r",stageKey="";
const DEFS=`<defs><linearGradient id="cone"><stop offset="0" stop-color="#ffe14d" stop-opacity=".5"/><stop offset=".5" stop-color="#ffe14d" stop-opacity=".18"/><stop offset="1" stop-color="#ffe14d" stop-opacity="0"/></linearGradient><filter id="glow" x="-500%" y="-500%" width="1100%" height="1100%"><feGaussianBlur stdDeviation="9"/></filter><filter id="soft" x="-30%" y="-60%" width="160%" height="220%"><feGaussianBlur stdDeviation="7"/></filter></defs>`, AMB=`<path class="amb" fill="#e87333"/><path class="cross" fill="#fff"/><path class="ws" fill="#ffe3cf"/><path class="g1" filter="url(#glow)"/><path class="g2" filter="url(#glow)"/><path class="b1"/><path class="b2"/>`;
const scene=m=>`<svg viewBox="0 0 300 500" preserveAspectRatio="xMidYMid slice"><rect width="300" height="500" fill="#262626"/><rect width="45" height="500" fill="#808080"/><rect x="255" width="45" height="500" fill="#808080"/><path d="M150 0V500" stroke="#e8e8e8" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${AP[m]}" fill="none"/>${DEFS}<g class="fade">${CARS.map(()=>`<path class="cone" d="M43 -16L130 -46V46L43 16Z" fill="url(#cone)" filter="url(#soft)"/>`).join("")}${CARS.map(()=>`<path class="car" d="${sq(90,60,20)}" fill="#fff"/>`).join("")}${AMB}</g></svg>`;
const stage=()=>[0,1].map(m=>`<div class="panel"><span class="tag">${T[lang].tags[m]}</span>${scene(m)}</div>`).join("");
// Bendable ambulance: body rebuilt every frame as a ribbon along the .geo route, extended straight past both ends.
function rig(svg){
  const geo=sampler(svg.querySelector(".geo").getAttribute("d")), L=geo.L, q=n=>svg.querySelector(n);
  const a=geo.at(0),a1=geo.at(1),z=geo.at(L),z1=geo.at(L-1);
  const pt=d=>d<0?[a.x+(a1.x-a.x)*d,a.y+(a1.y-a.y)*d]:d>L?[z.x+(z.x-z1.x)*(d-L),z.y+(z.y-z1.y)*(d-L)]:(p=>[p.x,p.y])(geo.at(d));
  const frame=(d,o)=>{const [x,y]=pt(d),[x1,y1]=pt(d+.5),[x0,y0]=pt(d-.5),m=Math.hypot(x1-x0,y1-y0)||1;return [x-(y1-y0)/m*o,y+(x1-x0)/m*o]};
  const ribbon=(s,a,b,hw,k,c=0)=>{const n=30,len=b-a,kk=Math.min(k,hw,len/2),A=[],B=[];
    for(let i=0;i<=n;i++){const u=i/n,e=Math.min(u,1-u)*len,w=e<kk?hw-kk+kk*Math.cbrt(1-(1-e/kk)**3):hw;
      A.push(frame(s-a-u*len,c+w));B.push(frame(s-a-u*len,c-w));}
    return "M"+A.concat(B.reverse()).map(p=>p[0].toFixed(1)+" "+p[1].toFixed(1)).join("L")+"Z"};
  return {L,ribbon,draw(s,t){
    q(".amb").setAttribute("d",ribbon(s,0,100,22,14));
    q(".cross").setAttribute("d",ribbon(s,49,77,4,1)+ribbon(s,59,67,14,1));
    q(".ws").setAttribute("d",ribbon(s,7,19,16,4));
    const ph=Math.floor(t/.3)%2;
    [["1",9.5,ph],["2",-9.5,1-ph]].forEach(([n,o,on])=>{const d=ribbon(s,22,29,9,3,o);
      q(".b"+n).setAttribute("d",d);q(".b"+n).setAttribute("fill",on?"#4db2ff":"#123e7a");
      const g=q(".g"+n);g.setAttribute("d",d);g.setAttribute("stroke","#4db2ff");g.setAttribute("stroke-width",10);g.setAttribute("fill","#4db2ff");g.setAttribute("opacity",on?.75:.08)});
  }};
}
// Road scene (welcome + corridor), t = seconds in a 10 s loop. Cars only slide sideways.
function init(svg,mod){
  const r=rig(svg), L=r.L, q=n=>svg.querySelector(n);
  const cones=svg.querySelectorAll(".cone"),cars=Array.from(svg.querySelectorAll(".car")).map((el,i)=>({el,co:cones[i],x:CARS[i][0],y:CARS[i][1],dx:CARS[i][0]<150?-22:22,t0:.9+(2-i%3)*.3}));
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    cars.forEach(c=>{const tr=`translate(${c.x+(mod?c.dx*EIO((t-c.t0)/1.2):0)} ${c.y}) rotate(${c.x<150?90:-90})`;c.el.setAttribute("transform",tr);c.co.setAttribute("transform",tr)});
    r.draw(mod?clamp((t-.5)/5.8)*(L+80):(1-(1-clamp((t-.5)/3))**3)*L,t);
  };
}

// ---- Dispatch: hospital sends the ambulance (vanilla) vs. the depot (mod).
const DROUTE="M100 112V590";
const building=m=>`<path d="${sq(200,130,30)}" transform="translate(150 75)" fill="${m?"#8aa0bf":"#e6edf5"}"/>`
    +`<path d="${sq(64,34,10)}" transform="translate(100 123)" fill="#14202e"/>`
    +(m?`<path d="M62 30H238" stroke="#e87333" stroke-width="6" stroke-linecap="round"/>`:`<path d="M150 28v26M137 41h26" stroke="#4db2ff" stroke-width="7" stroke-linecap="round"/>`)
    +`<text x="150" y="86" text-anchor="middle" font-size="20" font-weight="700" fill="#1b2a44">${T[lang].bld[m]}</text>`;
const dstage=()=>[0,1].map(m=>`<div class="panel">${SV(`<rect width="300" height="500" fill="#2a3a30"/><rect y="140" width="300" height="360" fill="#808080"/><rect x="45" y="140" width="210" height="360" fill="#262626"/><path d="M150 140V500" stroke="#e8e8e8" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${DROUTE}" fill="none"/>${DEFS}<g class="fade">${AMB}${building(m)}</g>`)}</div>`).join("");
function initDispatch(svg){
  const r=rig(svg), q=n=>svg.querySelector(n);
  return t=>{q(".fade").setAttribute("opacity",FADE(t));r.draw(EIO((t-.8)/5)*(r.L+110),t)};
}

// ---- Call volume: 3 stacked panels, call markers pop up and ping. Few halves, Many doubles vanilla calls (CallVolumeSystem).
const CALL_N=[4,9,18];
const CALL_TXT={en:["Few calls","Balanced","Many calls"],de:["Wenige Einsätze","Ausgewogen","Viele Einsätze"]};
const callPos=i=>[((i*.61803)%1)*90+5,((i*.3247+.17)%1)*70+15];
const cstage=()=>CALL_N.map((n,m)=>`<div class="panel mixp"><div class="mname">${CALL_TXT[lang][m]}</div><div class="calls">${
  Array.from({length:n},(_,i)=>{const [x,y]=callPos(i);return `<i class="call" style="left:${x}%;top:${y}%;animation-delay:${(.8+i*.08).toFixed(2)}s"><b style="animation-delay:${(1.2+i*.23).toFixed(2)}s"></b></i>`}).join("")}</div></div>`).join("");

// ---- Call severity: bars for the five AMPDS levels. Weights copied from Ampds.cs (Mix); update if that changes.
const LV=[["Alpha","#4ade80"],["Bravo","#facc15"],["Charlie","#fb923c"],["Delta","#ef4444"],["Echo","#a855f7"]];
const MIXW={
  Realistic:[[0,0,1,4,3,1],[0,2,4,3,1,0],[1,4,3,1,0,0],[3,4,2,0,0,0]],
  Balanced:[[0,0,0,1,2,1],[0,0,1,2,1,0],[0,1,2,2,1,0],[1,2,2,0,0,0]],
  Dramatic:[[0,0,0,0,3,1],[0,0,0,1,2,0],[0,0,1,1,2,0],[1,1,1,0,0,0]]};
const share=m=>{const a=[0,0,0,0,0];
  MIXW[m].forEach(w=>{const s=w.slice(1),t=s.reduce((x,y)=>x+y,0)||1;s.forEach((v,i)=>a[i]+=v/t)});
  const sum=a.reduce((x,y)=>x+y,0);return a.map(v=>Math.round(v/sum*100))};
const mstage=()=>Object.keys(MIXW).map(m=>{const sh=share(m);
  return `<div class="panel mixp"><div class="mname">${T[lang].mix[m]}</div><div class="bars">${
    LV.map(([n,c],i)=>`<div><span>${sh[i]}%</span><i style="height:${sh[i]*2}rem;background-color:${c};animation-delay:${(.8+i*.09).toFixed(2)}s"></i><span>${n}</span></div>`).join("")}</div></div>`}).join("");

// ---- Emergency doctor: right panel adds the doctor car following 112 behind the ambulance.
const NROUTE="M163 680V590C163 480 206 490 206 395V215";
const dummy=(x,y,a,c)=>`<path d="${sq(70,44,14)}" transform="translate(${x} ${y}) rotate(${a})" fill="${c}"/>`;
const nefScene=m=>SV(`<rect width="300" height="500" fill="#2a3a30"/>`
  +`<rect x="69" width="162" height="500" fill="#808080"/><rect y="79" width="300" height="162" fill="#808080"/>`
  +`<rect x="115" width="70" height="500" fill="#262626"/><rect y="125" width="300" height="70" fill="#262626"/>`
  +`<path d="M0 160H115M185 160H300" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/>`
  +`<path d="M138 100l12-12 12 12M138 330l12-12 12 12" fill="none" stroke="#555" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>`
  +`<path class="geo" d="${NROUTE}" fill="none"/>${DEFS}<g class="fade">`
  +dummy(45,177,0,"#8a94a6")+dummy(255,143,180,"#a3acba")+dummy(150,50,-90,"#6f7a8d")
  +(m?`<path class="doc" fill="#f2efe6"/><path class="dst" fill="#e87333"/><path class="dws" fill="#9cc3e6"/><path class="dg1" filter="url(#glow)"/><path class="dg2" filter="url(#glow)"/><path class="db1"/><path class="db2"/><path class="dgm" filter="url(#glow)"/><path class="dm" fill="#7cc4ff"/>`:"")
  +`${AMB}</g>`);
const nstage=()=>PANELS(2,T[lang].nefTags,nefScene);
function initNef(svg,m){
  const r=rig(svg), R=r.ribbon, q=n=>svg.querySelector(n);
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    const s=EIO((t-.8)/4.2)*r.L;
    r.draw(s,t);
    if(m){const d=s-112;
      q(".doc").setAttribute("d",R(d,0,60,19,12));
      q(".dst").setAttribute("d",R(d,30,38,19,0));
      q(".dws").setAttribute("d",R(d,6,15,14,3));
      const ph=Math.floor(t/.3)%2, rd=R(d,42,52,5,5);
      q(".dm").setAttribute("d",rd);q(".dm").setAttribute("fill",ph?"#7cc4ff":"#123e7a");
      const gm=q(".dgm");gm.setAttribute("d",rd);gm.setAttribute("stroke","#4db2ff");gm.setAttribute("stroke-width",8);gm.setAttribute("fill","#4db2ff");gm.setAttribute("opacity",ph?.6:.08);
      [["1",7.5,ph],["2",-7.5,1-ph]].forEach(([n,o,on])=>{const p=R(d,17,23,7,2,o);
        q(".db"+n).setAttribute("d",p);q(".db"+n).setAttribute("fill",on?"#4db2ff":"#123e7a");
        const g=q(".dg"+n);g.setAttribute("d",p);g.setAttribute("stroke","#4db2ff");g.setAttribute("stroke-width",8);g.setAttribute("fill","#4db2ff");g.setAttribute("opacity",on?.7:.08)});}
  };
}

// ---- On-scene time: 0 / 30 / 60 game minutes, shown as 0 / 2.5 / 5 s of care.
const SCN_TXT={en:["Vanilla: drives straight on","30 min on scene","60 min on scene"],de:["Standard: fährt direkt weiter","30 Min. vor Ort","60 Min. vor Ort"]};
const SDUR=[0,2.5,5];
const sceneSvg=()=>SV(`<rect width="620" height="155" fill="#808080"/><rect y="25" width="620" height="80" fill="#262626"/><path d="M0 65H620" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="M-160 82H900" fill="none"/>${DEFS}<g class="fade"><circle cx="330" cy="130" r="7" fill="#ffd166"/><circle class="ring" cx="330" cy="130" r="14" fill="none" stroke="#4ade80" stroke-width="4" transform="rotate(-90 330 130)"/>${AMB}</g>`,"0 0 620 155");
const sstage=()=>PANELS(3,SCN_TXT[lang],sceneSvg);
function initScene(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), D=SDUR[m], tl=2.1+D;
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    r.draw(!D?clamp((t-.8)/2.4)*1060:t<tl?EO((t-.8)/1.3)*580:580+EIO((t-tl)/1.6)*480,t);
    const p=D&&t>2.1&&t<tl?clamp((t-2.1)/D):0;
    q(".ring").setAttribute("stroke-dasharray",`${p*88} 88`);
  };
}

// ---- Parking: on the road (blocks the lane) vs on the pavement.
const PARK_TXT={en:["Parks on the road","Parks on the pavement"],de:["Parkt auf der Straße","Parkt auf dem Gehweg"]};
const PARK_ROUTE=["M170 760V250","M170 760V640C170 500 235 520 235 390V250"];
const parkSvg=m=>SV(`<rect width="300" height="500" fill="#808080"/><rect x="20" width="180" height="500" fill="#262626"/><path d="M110 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${PARK_ROUTE[m]}" fill="none"/>${DEFS}<g class="fade"><path class="car" d="${sq(70,44,14)}" fill="#8a94a6"/>${AMB}</g>`);
const pstage=()=>PANELS(2,PARK_TXT[lang],parkSvg);
function initPark(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), car=q(".car");
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    r.draw(EO((t-.8)/2.6)*r.L,t);
    place(car,170,m?640-EIO((t-3.2)/3.4)*760:700-EO((t-2.2)/2)*300);
  };
}

// ---- Lane change: a blocker in the ambulance's lane, the neighbouring lane is free.
const LANE_TXT={en:["Stuck behind the blocker","Switches to the free lane"],de:["Steckt hinter dem Hindernis","Wechselt auf die freie Spur"]};
const LANE_ROUTE=["M100 760V300","M100 760V600C100 470 200 490 200 340V-250"];
const laneSvg=m=>SV(`<rect width="300" height="500" fill="#808080"/><rect x="50" width="200" height="500" fill="#262626"/><path d="M150 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${LANE_ROUTE[m]}" fill="none"/>${DEFS}<g class="fade"><path class="blk" d="${sq(70,44,14)}" fill="#8a94a6"/>${AMB}</g>`);
const lstage=()=>PANELS(2,LANE_TXT[lang],laneSvg);
function initLane(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n);
  place(q(".blk"),100,250);
  return t=>{q(".fade").setAttribute("opacity",FADE(t));r.draw(m?EIO((t-.8)/5)*(r.L+110):EO((t-.8)/2.4)*460,t)};
}

// ---- Red light: cars at the stop line in front of the ambulance. Mod: they drive through the red to clear the way.
const RED_TXT={en:["Waits at the red light","Cars clear the way"],de:["Wartet an der roten Ampel","Autos machen Platz"]};
const redSvg=()=>SV(`<rect width="300" height="500" fill="#2a3a30"/><rect x="69" width="162" height="500" fill="#808080"/><rect y="49" width="300" height="162" fill="#808080"/><rect x="115" width="70" height="500" fill="#262626"/><rect y="95" width="300" height="70" fill="#262626"/><path d="M0 130H115M185 130H300" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path d="M115 220H185" stroke="#e8e8e8" stroke-width="4"/><path d="${sq(16,40,6)}" transform="translate(208 195)" fill="#14202e"/><circle cx="208" cy="185" r="5" fill="#ff4d4d"/><path class="geo" d="M150 700V-250" fill="none"/>${DEFS}<g class="fade"><path class="c1" d="${sq(70,44,14)}" fill="#8a94a6"/><path class="c2" d="${sq(70,44,14)}" fill="#a3acba"/>${AMB}</g>`);
const rstage=()=>PANELS(2,RED_TXT[lang],redSvg);
function initRed(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), c=[[q(".c1"),257,2.8],[q(".c2"),337,3.2]];
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    c.forEach(([el,y,t0])=>place(el,150,m&&t>t0?y-(y+150)*EIO((t-t0)/2.4):y+400*(1-EO((t-.1)/1.3))));
    r.draw(!m||t<3.6?EO((t-.8)/2.2)*318:318+EIO((t-3.6)/5)*(r.L+110-318),t);
  };
}

// ---- Oncoming lane: mod overtakes the queue on the oncoming lane while oncoming cars pull to the kerb.
const ONC_TXT={en:["Stuck in the queue","Overtakes on the oncoming lane"],de:["Steckt in der Schlange","Überholt auf der Gegenfahrbahn"]};
const ONC_ROUTE=["M205 1000V350","M205 1000V700C205 560 114 580 114 420V60C114 -60 205 -40 205 -200V-400"];
const oncSvg=m=>SV(`<rect width="300" height="500" fill="#808080"/><rect x="40" width="220" height="500" fill="#262626"/><path d="M150 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${ONC_ROUTE[m]}" fill="none"/>${DEFS}<g class="fade">${[0,1,2].map(()=>`<path class="q" d="${sq(70,44,14)}" fill="#8a94a6"/>`).join("")}<path class="o1" d="${sq(70,44,14)}" fill="#a3acba"/><path class="o2" d="${sq(70,44,14)}" fill="#6f7a8d"/>${AMB}</g>`);
const onstage=()=>PANELS(2,ONC_TXT[lang],oncSvg);
function initOnc(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), o=[q(".o1"),q(".o2")];
  Array.from(svg.querySelectorAll(".q")).forEach((el,i)=>place(el,205,300-i*80));
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    o.forEach((el,i)=>{const t0=.1+i*.9;
      place(el,m?95-37*EIO((t-2-i*.3)/1):95,m?-120+(160+i*110+120)*EO((t-t0)/1.4):-120+760*clamp((t-t0)/4.2),90)});
    r.draw(300+(m?clamp((t-.8)/7)*(r.L+110-300):EO((t-.8)/3)*350),t);
  };
}

// ---- Bed capacity: 5 %, 10 % or a custom value (slider 5-50 %). 10 bed icons on a compressed scale.
const BED_V=[5,10,"custom"];
let bedCustom=S.BedCapacityPercent!==5&&S.BedCapacityPercent!==10, BED_CUSTOM=bedCustom?Math.max(5,Math.min(50,S.BedCapacityPercent)):25;
const BED_TXT={en:["5 %","10 %","Custom"],de:["5 %","10 %","Eigener Wert"]};
const BED_PTS=[[0,0],[5,2],[10,4],[50,10]];
const bedsOn=v=>{const k=BED_PTS.findIndex(p=>p[0]>=v),[x0,y0]=BED_PTS[k-1]||BED_PTS[0],[x1,y1]=BED_PTS[k];return Math.round(y0+(y1-y0)*(v-x0)/(x1-x0||1))};
const bedIcons=(v,anim)=>Array.from({length:10},(_,i)=>i<bedsOn(v)?`<i class="bed on" style="${anim?`animation-delay:${(.8+i*.07).toFixed(2)}s`:"animation:none"}"><b></b></i>`:`<i class="bed"></i>`).join("");
const pct=v=>(v-5)/45*100+"%";
const bstage=()=>BED_V.map((v,m)=>{const c=v==="custom";
  return `<div class="panel mixp bedp"><div class="mname">${BED_TXT[lang][m]}${c?`<span class="bedval"> ${BED_CUSTOM} %</span><div class="bslider"><div class="bfill" style="width:${pct(BED_CUSTOM)}"></div><div class="bthumb" style="left:${pct(BED_CUSTOM)}"></div></div>`:""}</div><div class="beds">${bedIcons(c?BED_CUSTOM:v,true)}</div></div>`}).join("");
const bedSet=(v,el)=>{
  v=Math.max(5,Math.min(50,Math.round(v/5)*5));BED_CUSTOM=v;bedCustom=true;S.BedCapacityPercent=v;
  const p=el.parentNode.parentNode;el.querySelector(".bfill").style.width=pct(v);el.querySelector(".bthumb").style.left=pct(v);
  p.querySelector(".bedval").textContent=` ${v} %`;p.querySelector(".beds").innerHTML=bedIcons(v,false);syncSel();
};
const bedAt=(el,x)=>{const r=el.getBoundingClientRect();return 5+clamp((x-r.left)/r.width)*45};

// ---- Full hospitals: vanilla drives to the full one, the mod skips it.
const FULL_TXT={en:["Sent to full hospitals too","Skips full hospitals"],de:["Auch zu vollen Häusern","Überspringt volle Häuser"]};
const FULL_LBL={en:["FULL","FREE"],de:["VOLL","FREI"]};
const HOSP_ROUTE=["M150 760V470C150 340 85 360 85 235","M150 760V470C150 340 215 360 215 235"];
const hosp=(cx,full)=>`<path d="${sq(110,130,24)}" transform="translate(${cx} 145)" fill="#e6edf5"/><path d="${sq(40,24,8)}" transform="translate(${cx} 198)" fill="#14202e"/><path d="M${cx} 92v20M${cx-10} 102h20" stroke="#4db2ff" stroke-width="6" stroke-linecap="round"/><text x="${cx}" y="152" text-anchor="middle" font-size="22" font-weight="700" fill="${full?"#d33":"#16a34a"}">${FULL_LBL[lang][full?0:1]}</text>`;
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

// ---- Shell ----
const settingPairs=()=>Object.entries(S).map(([k,v])=>[k,ENUMS[k]?ENUMS[k].indexOf(v):+v]);
const saveSettings=()=>settingPairs().forEach(([k,v])=>trigger("realisticEMS","setSetting",k,v));
const close=()=>trigger("realisticEMS","closeIntro"); // Skip changes nothing: the setup starts from the current settings.
let shown=false,scr=0;
const GL="ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#%&*+?<>/";
function heading(txt){
  const h=$("#h");cancelAnimationFrame(scr);
  if(step!==0||shown){h.textContent=txt;if(step!==0)shown=false;return}
  shown=true;const t0=now();
  (function f(){const p=(now()-t0)/1500;
    if(p>=1){h.textContent=txt;return}
    h.textContent=Array.from(txt).map((c,i)=>c===" "||p>.2+.8*i/txt.length?c:GL[Math.random()*GL.length|0]).join("");
    scr=requestAnimationFrame(f)})();
}
// Choice pages: key = setting, vals = value of panel 0,1,(2), stack = 3 panels below each other.
const PG={
  2:{key:"OnlyDepotsDispatch",vals:[false,true],stage:dstage,init:initDispatch},
  3:{key:"CallVolume",vals:["Few","Balanced","Many"],stage:cstage,stack:1},
  4:{key:"CallMix",vals:["Realistic","Balanced","Dramatic"],stage:mstage,stack:1},
  5:{key:"EnableNef",vals:[false,true],stage:nstage,init:initNef},
  6:{key:"OnSceneMinutes",vals:[0,30,60],stage:sstage,init:initScene,stack:1},
  7:{key:"ParkOnSidewalk",vals:[false,true],stage:pstage,init:initPark},
  8:{key:"EmergencyCorridor",vals:[false,true],stage,init},
  9:{key:"OncomingOvertake",vals:[false,true],stage:onstage,init:initOnc},
  10:{key:"EmergencyLaneChange",vals:[false,true],stage:lstage,init:initLane},
  11:{key:"ClearRedLight",vals:[false,true],stage:rstage,init:initRed},
  12:{key:"BedCapacityPercent",vals:BED_V,stage:bstage,stack:1,
     set:v=>{bedCustom=v==="custom";S.BedCapacityPercent=bedCustom?BED_CUSTOM:v},isSel:v=>v==="custom"?bedCustom:!bedCustom&&S.BedCapacityPercent===v},
  13:{key:"RejectFullHospitals",vals:[false,true],stage:hstage,init:initHosp}
};
const panels=()=>Array.from($("#stage").children);
function syncSel(){const g=PG[step];panels().forEach((p,i)=>p.classList.toggle("sel",!!g&&(g.isSel?g.isSel(g.vals[i]):S[g.key]===g.vals[i])))}
function render(){
  const t=T[lang], card=$(".card"), top=$(".top");
  $("#dots").innerHTML=Array.from({length:N},(_,i)=>`<i class="${i<=step?"on":""}"></i>`).join("");
  const st=$("#stage"), pg=PG[step], kind=pg?step:"r";
  if(stageKey!==lang+kind){stageKey=lang+kind;st.className="stage"+(pg&&pg.stack?" stack":"");st.innerHTML=(pg?pg.stage:stage)();
    scenes=pg&&!pg.init?[]:Array.from(st.querySelectorAll("svg")).map(pg?pg.init:init);}
  syncSel();
  if(step>0&&(prev===0||kind!==pk))start=now()+800;pk=kind; // each scene starts 0.8 s after Continue
  prev=step;const fresh=!shown;
  card.classList.toggle("intro",step===0||step===N);card.classList.toggle("bare",step===1||!!pg);
  top.classList.toggle("welcome",step<2||step===N);top.classList.toggle("big",step===0||step===N);
  heading(t.steps[step].t);$("#sub").textContent=t.steps[step].s;
  let h="";
  if(step===0) h=`<div class="langs">${[["en","English"],["de","Deutsch"]].map(([l,nm])=>`<button class="lbox ${lang===l?"on":""}" data-l="${l}"${fresh?"":' style="animation:none"'}>${nm}</button>`).join("")}</div>`;
  else if(step===N) h=`<div class="done"><svg viewBox="0 0 120 120" width="150" height="150"><circle class="ring" cx="60" cy="60" r="45" fill="none" stroke="#4ade80" stroke-width="7" stroke-linecap="round" transform="rotate(-90 60 60)"/><path class="tick" d="M38 62l15 15 30-34" fill="none" stroke="#4ade80" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/></svg></div>`;
  $("#body").innerHTML=h;
  $("#back").textContent=t.back;$("#back").style.visibility=step&&step<N?"visible":"hidden";
  $("#skip").textContent=t.skip;$("#skip").style.visibility=step<N?"visible":"hidden";
  $("#next").textContent=step===N?t.close:step===N-1?t.done:t.next;
}
const up=(el,f)=>{for(;el&&el!==root;el=el.parentNode)if(el.nodeType===1&&f(el))return el;return null};
const isSlider=el=>el.classList.contains("bslider");
let drag=null;
root.addEventListener("mousedown",e=>{const el=up(e.target,isSlider);if(el){drag=el;bedSet(bedAt(el,e.clientX),el)}});
root.addEventListener("mousemove",e=>{if(drag)bedSet(bedAt(drag,e.clientX),drag)});
const stopDrag=()=>{drag=null};
window.addEventListener("mouseup",stopDrag);
root.addEventListener("click",e=>{
  const st=$("#stage"), pn=up(e.target,el=>el.parentNode===st);
  if(pn&&PG[step]){const g=PG[step],v=g.vals[panels().indexOf(pn)];g.set?g.set(v):S[g.key]=v;syncSel();return}
  const b=up(e.target,el=>el.tagName.toLowerCase()==="button"); if(!b) return;
  const l=b.getAttribute("data-l");
  if(l){lang=l;render();}
  else if(b.id==="back"){step--;render();}
  else if(b.id==="next"){if(step===N)return close();if(step<N-1)step++;else{saveSettings();step=N}render();}
  else if(b.id==="skip")close();
});
(function loop(){const n=now();scenes.forEach(f=>f(Math.max(0,n-start)/1000%10));raf=requestAnimationFrame(loop)})();
render();
return ()=>{cancelAnimationFrame(raf);cancelAnimationFrame(scr);window.removeEventListener("mouseup",stopDrag);root.innerHTML=""};
}
