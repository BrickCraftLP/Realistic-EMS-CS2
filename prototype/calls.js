// Call volume page: 3 stacked panels (few / balanced / many calls). Each panel is a small city map where call markers pop up and ping.
// Uses globals from intro.html (lang) at call time only.
// Saved as Setting.CallVolume (RealisticEMS/CallVolumeSystem.cs: Few halves, Many doubles vanilla sick/injured calls).
const CALL_V=["Few","Balanced","Many"];
const CALL_N=[4,9,18];
const CALL_TXT={en:["Few calls","Balanced","Many calls"],de:["Wenige Einsätze","Ausgewogen","Viele Einsätze"]};
// Same scattered positions in every panel, so "many" contains the markers of "few" and adds more.
const callPos=i=>[((i*.61803)%1)*90+5,((i*.3247+.17)%1)*70+15];
const cstage=()=>CALL_N.map((n,m)=>`<div class="panel mixp" role="button" tabindex="0" aria-label="${CALL_TXT[lang][m]}"><div class="mname">${CALL_TXT[lang][m]}</div><div class="calls">${
  Array.from({length:n},(_,i)=>{const [x,y]=callPos(i);return `<i class="call" style="left:${x}%;top:${y}%;--i:${i}"></i>`}).join("")}</div></div>`).join("");
