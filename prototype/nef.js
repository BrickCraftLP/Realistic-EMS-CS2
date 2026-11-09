// Emergency doctor screen. One-way street with a 4-way crossing (dummy cars). The ambulance leaves the street and parks on the
// pavement. Left: ambulance alone. Right: the emergency doctor car follows right behind and parks too.
// Uses globals from intro.html (T, lang, sq, rig, DEFS, AMB, clamp) at call time only.
const NROUTE="M163 680V590C163 480 206 490 206 395V215"; // up the one-way street, then onto the right pavement

const dummy=(x,y,a,c)=>`<path d="${sq(70,44,14)}" transform="translate(${x} ${y}) rotate(${a})" fill="${c}"/>`;

const nefScene=m=>`<svg viewBox="0 0 300 500" preserveAspectRatio="xMidYMid slice"><rect width="300" height="500" fill="#2a3a30"/>`
  +`<rect x="69" width="162" height="500" fill="#808080"/><rect y="79" width="300" height="162" fill="#808080"/>`
  +`<rect x="115" width="70" height="500" fill="#262626"/><rect y="125" width="300" height="70" fill="#262626"/>`
  +`<path d="M0 160H115M185 160H300" stroke="#8a8a8a" stroke-width="3" stroke-dasharray="16 14"/>`
  +`<path d="M138 100l12-12 12 12M138 330l12-12 12 12" fill="none" stroke="#555" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>`
  +`<path class="geo" d="${NROUTE}" fill="none"/>${DEFS}<g class="fade">`
  +dummy(45,177,0,"#8a94a6")+dummy(255,143,180,"#a3acba")+dummy(150,50,-90,"#6f7a8d")
  +(m?`<path class="doc" fill="#f2efe6"/><path class="dst" fill="#e87333"/><path class="dws" fill="#9cc3e6"/><path class="dg1" filter="url(#glow)"/><path class="dg2" filter="url(#glow)"/><path class="db1"/><path class="db2"/><path class="dgm" filter="url(#glow)"/><path class="dm" fill="#7cc4ff"/>`:"")
  +`${AMB}</g></svg>`;

const nstage=()=>[0,1].map(m=>`<div class="panel" role="button" tabindex="0" aria-label="${T[lang].nefTags[m]}"><span class="tag">${T[lang].nefTags[m]}</span>${nefScene(m)}</div>`).join("");

// update(t), 10 s loop. The doctor car's head stays 112 behind the ambulance's head, so it parks right behind it.
function initNef(svg,m){
  const r=rig(svg), R=r.ribbon, q=n=>svg.querySelector(n), ease=p=>{p=clamp(p);return p*p*(3-2*p)};
  return t=>{
    q(".fade").setAttribute("opacity",t<.5?t/.5:t>9.3?(10-t)/.7:1);
    const s=ease((t-.8)/4.2)*r.L;
    r.draw(s,t);
    if(m){const d=s-112;
      q(".doc").setAttribute("d",R(d,0,60,19,12));
      q(".dst").setAttribute("d",R(d,30,38,19,0));
      q(".dws").setAttribute("d",R(d,6,15,14,3));
      const ph=Math.floor(t/.3)%2, rd=R(d,42,52,5,5); // round light at the rear of the roof, blinks with the left half
      q(".dm").setAttribute("d",rd);q(".dm").setAttribute("fill",ph?"#7cc4ff":"#123e7a");
      const gm=q(".dgm");gm.setAttribute("d",rd);gm.setAttribute("stroke","#4db2ff");gm.setAttribute("stroke-width",8);gm.setAttribute("fill","#4db2ff");gm.setAttribute("opacity",ph?.6:.08);
      // light bar split in two blue halves, flashing in turns, with a soft glow like the ambulance
      [["1",7.5,ph],["2",-7.5,1-ph]].forEach(([n,o,on])=>{const p=R(d,17,23,7,2,o);
        q(".db"+n).setAttribute("d",p);q(".db"+n).setAttribute("fill",on?"#4db2ff":"#123e7a");
        const g=q(".dg"+n);g.setAttribute("d",p);g.setAttribute("stroke","#4db2ff");g.setAttribute("stroke-width",8);g.setAttribute("fill","#4db2ff");g.setAttribute("opacity",on?.7:.08)});}
  };
}
