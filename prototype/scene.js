// On-scene time (3 panels stacked, a timeline per option) and parking (2 panels side by side).
// Uses globals from intro.html (lang, sq, rig, DEFS, AMB, clamp, SV, PANELS, FADE, EIO, EO, place) at call time only.

// ---- On-scene time: 0 / 30 / 60 game minutes. Care is shown as 0 / 2.5 / 5 s so the difference is visible.
const SCN_TXT={en:["Vanilla: drives straight on","30 min on scene","60 min on scene"],de:["Standard: fährt direkt weiter","30 Min. vor Ort","60 Min. vor Ort"]};
const SDUR=[0,2.5,5];
const sceneSvg=()=>SV(`<rect width="620" height="155" fill="#808080"/><rect y="25" width="620" height="80" fill="#262626"/><path d="M0 65H620" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="M-160 82H900" fill="none"/>${DEFS}<g class="fade"><circle cx="330" cy="130" r="7" fill="#ffd166"/><circle class="ring" cx="330" cy="130" r="14" fill="none" stroke="#4ade80" stroke-width="4" transform="rotate(-90 330 130)"/>${AMB}</g>`,"0 0 620 155");
const sstage=()=>PANELS(3,SCN_TXT[lang],sceneSvg);
// update(t): ambulance arrives at the patient, stays while the ring fills, then drives off. 0 min = it just passes.
function initScene(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), D=SDUR[m], tl=2.1+D;
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    const s=!D?clamp((t-.8)/2.4)*1060:t<tl?EO((t-.8)/1.3)*580:580+EIO((t-tl)/1.6)*480;
    r.draw(s,t);
    const p=D&&t>2.1&&t<tl?clamp((t-2.1)/D):0;
    q(".ring").setAttribute("stroke-dasharray",`${p*88} 88`);
  };
}

// ---- Parking: on the road (blocks the lane) vs on the pavement.
const PARK_TXT={en:["Parks on the road","Parks on the pavement"],de:["Parkt auf der Straße","Parkt auf dem Gehweg"]};
const PARK_ROUTE=["M170 760V250","M170 760V640C170 500 235 520 235 390V250"];
const parkSvg=m=>SV(`<rect width="300" height="500" fill="#808080"/><rect x="20" width="180" height="500" fill="#262626"/><path d="M110 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${PARK_ROUTE[m]}" fill="none"/>${DEFS}<g class="fade"><path class="car" d="${sq(70,44,14)}" fill="#8a94a6"/>${AMB}</g>`);
const pstage=()=>PANELS(2,PARK_TXT[lang],parkSvg);
// Left: a car queues up behind the ambulance. Right: a car just drives past the parked ambulance.
function initPark(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), car=q(".car");
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    r.draw(EO((t-.8)/2.6)*r.L,t);
    place(car,170,m?640-EIO((t-3.2)/3.4)*760:700-EO((t-2.2)/2)*300);
  };
}
