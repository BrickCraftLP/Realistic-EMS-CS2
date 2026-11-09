// Dispatch screen scenes. Left: the hospital sends the ambulance (vanilla). Right: the depot does (mod).
// Uses globals from intro.html (T, lang, sq, rig, DEFS, AMB, clamp) at call time only.
const DROUTE="M100 112V590"; // straight down the left (southbound) lane; starts hidden inside the building, leaves through the door

const building=m=>{
  const label=T[lang].bld[m];
  return `<path d="${sq(200,130,30)}" transform="translate(150 75)" fill="${m?"#8aa0bf":"#e6edf5"}"/>`
    +`<path d="${sq(64,34,10)}" transform="translate(100 123)" fill="#14202e"/>`
    +(m?`<path d="M62 30H238" stroke="#e87333" stroke-width="6" stroke-linecap="round"/>`:`<path d="M150 28v26M137 41h26" stroke="#4db2ff" stroke-width="7" stroke-linecap="round"/>`)
    +`<text x="150" y="86" text-anchor="middle" font-size="20" font-weight="700" font-family="system-ui,sans-serif" fill="#1b2a44">${label}</text>`;
};

// The street ends at the building (y 140), so the top of the panel is ground, not road.
const dispatchScene=m=>`<svg viewBox="0 0 300 500" preserveAspectRatio="xMidYMid slice"><rect width="300" height="500" fill="#2a3a30"/><rect y="140" width="300" height="360" fill="#808080"/><rect x="45" y="140" width="210" height="360" fill="#262626"/><path d="M150 140V500" stroke="#e8e8e8" stroke-width="3" stroke-dasharray="16 14"/><path class="geo" d="${DROUTE}" fill="none"/>${DEFS}<g class="fade">${AMB}${building(m)}</g></svg>`;

const dstage=()=>[0,1].map(m=>`<div class="panel" role="button" tabindex="0" aria-label="${T[lang].bld[m]}">${dispatchScene(m)}</div>`).join("");

// update(t), 10 s loop: ambulance rolls out of the door, drives straight down its lane and off the panel.
function initDispatch(svg){
  const r=rig(svg), q=n=>svg.querySelector(n), ease=p=>{p=clamp(p);return p*p*(3-2*p)};
  return t=>{
    q(".fade").setAttribute("opacity",t<.5?t/.5:t>9.3?(10-t)/.7:1);
    r.draw(ease((t-.8)/5)*(r.L+110),t);
  };
}
