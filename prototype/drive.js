// Driving pages: lane change and red lights (both 2 panels side by side). The emergency corridor page reuses the road scene
// from intro.html. Uses globals from intro.html (sq, rig, DEFS, AMB, clamp, SV, PANELS, FADE, EIO, EO, place) at call time only.

// ---- Lane change: a blocker stands in the ambulance's lane, the neighbouring lane is free.
const LANE_TXT={en:["Stuck behind the blocker","Switches to the free lane"],de:["Steckt hinter dem Hindernis","Wechselt auf die freie Spur"]};
const LANE_ROUTE=["M100 760V300","M100 760V600C100 470 200 490 200 340V-250"];
const laneSvg=m=>SV(`<rect width="300" height="500" fill="#808080"/><rect x="50" width="200" height="500" fill="#262626"/><path d="M150 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${LANE_ROUTE[m]}" fill="none"/>${DEFS}<g class="fade"><path class="blk" d="${sq(70,44,14)}" fill="#8a94a6"/>${AMB}</g>`);
const lstage=()=>PANELS(2,LANE_TXT[lang],laneSvg);
function initLane(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n);
  place(q(".blk"),100,250);
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    r.draw(m?EIO((t-.8)/5)*(r.L+110):EO((t-.8)/2.4)*460,t);
  };
}

// ---- Red light: two cars wait at the stop line with the ambulance behind them. Mod: they drive through the red to clear the way.
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

// ---- Oncoming lane: a queue blocks the ambulance lane. Vanilla: oncoming traffic keeps flowing, so it stays in the queue.
// Mod: oncoming cars pull to the kerb and the ambulance overtakes the whole queue on the oncoming lane, then returns.
const ONC_TXT={en:["Stuck in the queue","Overtakes on the oncoming lane"],de:["Steckt in der Schlange","Überholt auf der Gegenfahrbahn"]};
const ONC_ROUTE=["M205 1000V350","M205 1000V700C205 560 114 580 114 420V60C114 -60 205 -40 205 -200V-400"];
const oncSvg=m=>SV(`<rect width="300" height="500" fill="#808080"/><rect x="40" width="220" height="500" fill="#262626"/><path d="M150 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${ONC_ROUTE[m]}" fill="none"/>${DEFS}<g class="fade">${[0,1,2].map(()=>`<path class="q" d="${sq(70,44,14)}" fill="#8a94a6"/>`).join("")}<path class="o1" d="${sq(70,44,14)}" fill="#a3acba"/><path class="o2" d="${sq(70,44,14)}" fill="#6f7a8d"/>${AMB}</g>`);
const onstage=()=>PANELS(2,ONC_TXT[lang],oncSvg);
function initOnc(svg,m){
  const r=rig(svg), q=n=>svg.querySelector(n), o=[q(".o1"),q(".o2")];
  svg.querySelectorAll(".q").forEach((el,i)=>place(el,205,300-i*80));
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    o.forEach((el,i)=>{const t0=.1+i*.9;
      place(el,m?95-37*EIO((t-2-i*.3)/1):95,m?-120+(160+i*110+120)*EO((t-t0)/1.4):-120+760*clamp((t-t0)/4.2),90)});
    r.draw(300+(m?clamp((t-.8)/7)*(r.L+110-300):EO((t-.8)/3)*350),t);
  };
}
