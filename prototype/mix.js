// Call severity screen: three stacked panels (one per CallMix), each with animated bars for the five AMPDS levels.
// Uses globals from intro.html (T, lang) at call time only. No animation loop: bars grow with a CSS animation.
const LV=[["Alpha","#4ade80"],["Bravo","#facc15"],["Charlie","#fb923c"],["Delta","#ef4444"],["Echo","#a855f7"]];
// Level weights Omega,A,B,C,D,E per health band (<15, <40, <60, rest), copied from RealisticEMS/Ampds.cs (Mix).
// ponytail: bands counted equally and Omega left out; the real share depends on the city's patient health. Update if Ampds.cs changes.
const MIXW={
  Realistic:[[0,0,1,4,3,1],[0,2,4,3,1,0],[1,4,3,1,0,0],[3,4,2,0,0,0]],
  Balanced:[[0,0,0,1,2,1],[0,0,1,2,1,0],[0,1,2,2,1,0],[1,2,2,0,0,0]],
  Dramatic:[[0,0,0,0,3,1],[0,0,0,1,2,0],[0,0,1,1,2,0],[1,1,1,0,0,0]]};
const share=m=>{const a=[0,0,0,0,0];
  MIXW[m].forEach(w=>{const s=w.slice(1),t=s.reduce((x,y)=>x+y,0)||1;s.forEach((v,i)=>a[i]+=v/t)});
  const sum=a.reduce((x,y)=>x+y,0);return a.map(v=>Math.round(v/sum*100))};
const mstage=()=>Object.keys(MIXW).map(m=>{const sh=share(m);
  return `<div class="panel mixp" role="button" tabindex="0" data-mix="${m}"><div class="mname">${T[lang].mix[m]}</div><div class="bars">${
    LV.map(([n,c],i)=>`<div><span>${sh[i]}%</span><i style="--p:${sh[i]};--c:${c};--i:${i}"></i><span>${n}</span></div>`).join("")}</div></div>`}).join("");
