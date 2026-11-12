// Siren page: both panels show the same drive, bottom to top on a fixed road: free road -> queue passed on the oncoming lane
// -> red light at the front of the queue. Left: siren sounds the whole way. Right: only in the jam, on the oncoming lane and at the red light.
// Uses globals from intro.html (sq, rig, DEFS, AMB, SV, PANELS, FADE, EIO, clamp, place).
const SIR_TXT={
  en:{tags:["Siren always on","Siren only when needed"],on:"Siren on",off:"Siren off",jam:"Siren: traffic jam",onc:"Siren: oncoming",red:"Siren: red light"},
  de:{tags:["Sirene immer an","Sirene nur bei Bedarf"],on:"Sirene an",off:"Sirene aus",jam:"Sirene: Stau",onc:"Sirene: Gegenverkehr",red:"Sirene: rote Ampel"}};
// Ambulance centre-line: right lane, long gentle S to the oncoming lane (y 400-270), straight past the queue and the junction, S back (y 70 to -60).
const SIR_ROUTE="M205 700V520C205 420 124 430 124 330V40C124 -60 205 -50 205 -150V-350";
const SIR_ZONES=[[520,330,"jam"],[330,160,"onc"],[160,30,"red"]]; // ambulance head y range per reason (right panel)
const SIR_HEAD=560, SIR_END=-130; // head y at the start and at the end of the drive
const sirSvg=()=>SV(
  `<rect width="300" height="500" fill="#808080"/><rect x="40" width="220" height="500" fill="#262626"/>${DEFS}`
  +`<path d="M150 0V500" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/>`
  // junction in front of the queue, stop lines, red light
  +`<rect x="0" y="60" width="300" height="80" fill="#262626"/><path d="M0 100H40M260 100H300" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/>`
  +`<path d="M150 150H260M40 52H150" stroke="#e8e8e8" stroke-width="4"/>`
  +`<path d="${sq(16,40,6)}" transform="translate(268 168)" fill="#14202e"/><circle cx="268" cy="158" r="5" fill="#ff4d4d"/><circle cx="268" cy="178" r="5" fill="#4ade80" opacity=".2"/>`
  +`<path class="geo" d="${SIR_ROUTE}" fill="none"/><g class="fade">`
  +[0,1].map(()=>`<path class="q" d="${sq(70,44,14)}" fill="#8a94a6"/>`).join("")
  +`<path class="o1" d="${sq(70,44,14)}" fill="#a3acba"/><path class="o2" d="${sq(70,44,14)}" fill="#6f7a8d"/>`
  +AMB
  +`<g class="wv" fill="none" stroke="#ffe14d" stroke-width="5" stroke-linecap="round">${[0,1,2].map(()=>`<path d="M-26 -22Q0 -40 26 -22"/>`).join("")}</g>`
  // state pill, bottom left: speaker icon + current reason
  +`<g transform="translate(22 458)"><rect class="pl" width="150" height="30" rx="15" fill="#000" opacity=".65"/>`
  +`<g transform="translate(16 15)"><g class="son"><path d="M-6 -3h3l5 -4v14l-5 -4h-3z" fill="#ffe14d"/><path d="M6 -5q4 5 0 10" fill="none" stroke="#ffe14d" stroke-width="2" stroke-linecap="round"/></g>`
  +`<g class="soff"><path d="M-6 -3h3l5 -4v14l-5 -4h-3z" fill="#8fa0b8"/><path d="M-9 9L9 -9" stroke="#ff4d4d" stroke-width="2.5" stroke-linecap="round"/></g></g>`
  +`<text class="rt" x="32" y="20" font-size="14" font-weight="600" fill="#fff"></text></g></g>`);
const sirStage=()=>PANELS(2,SIR_TXT[lang].tags,sirSvg);
function initSiren(svg,m){
  const r=rig(svg), L=r.L, q=n=>svg.querySelector(n), geo=sampler(SIR_ROUTE), wv=q(".wv"), arcs=[...wv.children];
  const qs=[...svg.querySelectorAll(".q")], o=[q(".o1"),q(".o2")], rt=q(".rt"), pl=q(".pl"), TX=SIR_TXT[lang];
  qs.forEach((el,i)=>place(el,205,290-i*76));
  const at=s=>geo.at(Math.max(0,Math.min(L,s)));
  const sAt=y=>{let a=0,b=L;for(let i=0;i<30;i++){const c=(a+b)/2;if(at(c).y>y)a=c;else b=c}return a}; // route length where the head is at y
  const s0=sAt(SIR_HEAD), s1=sAt(SIR_END);
  const win=(h,a,b)=>EIO((a-h)/20)*EIO((h-b)/20); // 0..1 while the head y is between a (above) and b
  let last="";
  return t=>{
    q(".fade").setAttribute("opacity",FADE(t));
    const p=clamp((t-.6)/8.8), s=s0+(s1-s0)*(.6*p+.4*EIO(p)), P=at(s), P1=at(s+1), P0=at(s-1), hy=P.y;
    r.draw(s,t);
    // two oncoming cars meet the ambulance and pull to the kerb
    [-130,-297].forEach((c0,i)=>{const yc=c0+1.38*(SIR_HEAD-hy),k=EIO(2*(1-Math.abs(yc-hy)/220));place(o[i],95-37*k,yc,90)});
    // siren: waves at the ambulance's front, state pill
    let amt=1,key="on";
    if(m){amt=win(hy,520,30);key=amt>.5?(SIR_ZONES.find(z=>hy<=z[0]&&hy>z[1])||[0,0,"off"])[2]:"off"}
    wv.setAttribute("opacity",amt);
    wv.setAttribute("transform",`translate(${P.x} ${P.y}) rotate(${Math.atan2(P1.y-P0.y,P1.x-P0.x)*180/Math.PI+90})`);
    arcs.forEach((a,i)=>{const u=(t/.9+i/3)%1;a.setAttribute("opacity",(1-u)*.9);a.setAttribute("transform",`translate(0 ${-6-u*34}) scale(${.7+.5*u})`)});
    q(".son").setAttribute("opacity",amt>.5?1:0);q(".soff").setAttribute("opacity",amt>.5?0:1);
    if(key!==last){last=key;rt.textContent=TX[key];pl.setAttribute("width",44+TX[key].length*7.4)}
  };
}
