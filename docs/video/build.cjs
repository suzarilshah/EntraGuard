// Build the video-specific deck, matching SVG/PNG slides, and narration materials.
// node docs/video/build.cjs --tools /absolute/path/to/presentation-tool-prefix
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const here = __dirname;
const root = path.resolve(here, '../..');
const args = process.argv.slice(2);
const index = args.indexOf('--tools');
const tools = index >= 0 ? args[index + 1] : here;
const toolRequire = createRequire(path.join(tools, 'package.json'));
const pptxgen = toolRequire('pptxgenjs');
const sharp = toolRequire('sharp');
const story = JSON.parse(fs.readFileSync(path.join(here, 'storyboard.json'), 'utf8'));
const p = new pptxgen();
p.layout = 'LAYOUT_WIDE';
p.author = 'EntraGuard'; p.title = story.title; p.subject = '115-second hackathon video storyboard';
p.lang = 'en-GB'; p.theme = {headFontFace:'Arial',bodyFontFace:'Arial',lang:'en-GB'};
const C = {navy:'0C252D',panel:'163A3B',lime:'D5EDAE',paper:'F5F6F0',white:'FFFFFF',ink:'17382F',muted:'566D62',border:'DCE4D6',subtle:'B4C8BE',azure:'0078D4',coral:'FFD0B8',rust:'523D36'};
const e = s => String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const slides = path.join(here,'slides');
const pptxAssets = path.join(here,'assets');
fs.mkdirSync(slides,{recursive:true});
fs.mkdirSync(pptxAssets,{recursive:true});
let current, svg, slide;
const manifest = [];
function bounds(x,y,w,h) { if(x<0||y<0||w<0||h<0||x+w>1920.1||y+h>1080.1) throw new Error(`Out-of-bounds ${[x,y,w,h]}`); }
function box(x,y,w,h,fill,stroke=fill,r=0) {
  bounds(x,y,w,h);
  svg += `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${r}" fill="#${fill}" stroke="#${stroke}"/>`;
  slide.addShape(r ? p.ShapeType.roundRect:p.ShapeType.rect,{x:x/144,y:y/144,w:w/144,h:h/144,rectRadius:r/144,fill:{color:fill},line:{color:stroke,width:.5},radius:r/144});
}
function text(value,x,y,w,size=36,color=C.ink,bold=false,align='left') {
  const lines=String(value).split('\n'),h=lines.length*size*1.23;
  bounds(x,y,w,h);
  current.elements.push({kind:'text',text:value,x,y,w,h,size});
  const tx=align==='center'?x+w/2:align==='right'?x+w:x;
  svg+=`<text x="${tx}" y="${y+size*.91}" text-anchor="${align==='center'?'middle':align==='right'?'end':'start'}" font-size="${size}" fill="#${color}" font-weight="${bold?700:400}">${lines.map((line,i)=>`<tspan x="${tx}" dy="${i?size*1.23:0}">${e(line)}</tspan>`).join('')}</text>`;
  slide.addText(value,{x:x/144,y:y/144,w:w/144,h:h/144,fontFace:'Arial',fontSize:size/2,color,bold,align,margin:0,breakLine:false,valign:'top',paraSpaceAfterPt:0,fit:'shrink'});
}
function line(x1,y1,x2,y2,color=C.border,arrow=false) {
  svg+=`<path d="M${x1} ${y1} L${x2} ${y2}" stroke="#${color}" stroke-width="3" fill="none" ${arrow?'marker-end="url(#arrow)"':''}/>`;
  slide.addShape(p.ShapeType.line,{x:Math.min(x1,x2)/144,y:Math.min(y1,y2)/144,w:Math.abs(x2-x1)/144,h:Math.abs(y2-y1)/144,flipH:x2<x1,flipV:y2<y1,line:{color,width:1.5,endArrowType:arrow?'triangle':'none'}});
}
function image(file,x,y,w,h) {
  bounds(x,y,w,h);
  current.elements.push({kind:'image',file:path.basename(file),x,y,w,h});
  const bytes=fs.readFileSync(file), mime=file.endsWith('.svg')?'image/svg+xml':'image/png';
  svg+=`<image href="data:${mime};base64,${bytes.toString('base64')}" x="${x}" y="${y}" width="${w}" height="${h}" preserveAspectRatio="xMidYMid meet"/>`;
  const deckImage=file.endsWith('.svg')?path.join(pptxAssets,path.basename(file,'.svg')+'.png'):file;
  slide.addImage({path:deckImage,x:x/144,y:y/144,w:w/144,h:h/144,altText:path.basename(file)});
}
function icon(name,x,y,size=64) { image(path.join(root,'docs/diagrams/azure-icons',name+'.svg'),x,y,size,size); }
function badge(value,x,y,w,dark=false) {box(x,y,w,42,dark?C.panel:C.ink,dark?C.panel:C.ink,8);text(value,x+12,y+9,w-24,19,dark?C.lime:C.white,true);}
function base(scene,dark=false) {
  current={id:scene.id,title:scene.title,start:scene.start,end:scene.end,elements:[]};manifest.push(current);
  slide=p.addSlide();slide.background={color:dark?C.navy:C.paper};
  svg=`<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080" role="img" aria-labelledby="title"><title id="title">${e(scene.title)}</title><defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M0 0 L10 5 L0 10Z" fill="#566D62"/></marker></defs><g font-family="Arial, sans-serif"><rect width="1920" height="1080" fill="#${dark?C.navy:C.paper}"/>`;
  text('ENTRAGUARD',96,52,700,25,dark?C.lime:C.ink,true);
  text(`${String(scene.start).padStart(2,'0')}–${scene.end}s  /  HACKATHON FILM`,1240,55,580,19,dark?C.subtle:C.muted,false,'right');
  line(96,1008,1824,1008,dark?'36554E':C.border);
  text('MICROSOFT GARAGE HACKATHON',96,1030,1200,17,dark?C.subtle:C.muted);
  text(`${scene.id.slice(0,2)} / 06`,1670,1030,154,17,dark?C.subtle:C.muted,false,'right');
  const cues=story.cues.filter(c=>c.start>=scene.start&&c.start<scene.end);
  slide.addNotes(`${scene.start}–${scene.end} seconds\n\n${cues.map(c=>`${c.start}s: ${c.text}`).join('\n\n')}\n\nVISUAL\n${scene.visual}\n\nFINAL EDIT\n${scene.finalUse}\n\nClaims checked against the current source architecture. Demo sections are storyboard placeholders, not recorded evidence. Voice scoring observes by default. Treasury is a demo ledger.`);
}
async function save() {
  const complete=svg+'</g></svg>';
  fs.writeFileSync(path.join(slides,current.id+'.svg'),complete);
  await sharp(Buffer.from(complete)).png().toFile(path.join(slides,current.id+'.png'));
}
async function main() {
  // Keynote can omit PPTX SVG images; use high-resolution raster fallbacks in the
  // editable deck, while the standalone slides retain the original vector assets.
  const iconDir=path.join(root,'docs/diagrams/azure-icons');
  for(const file of fs.readdirSync(iconDir).filter(f=>f.endsWith('.svg'))) {
    await sharp(path.join(iconDir,file)).resize(384,384,{fit:'contain',background:{r:0,g:0,b:0,alpha:0}}).png()
      .toFile(path.join(pptxAssets,path.basename(file,'.svg')+'.png'));
  }
  base(story.scenes[0],true);
  text('The code is right.\nThe situation isn’t.',96,235,1080,86,C.white,true);
  text('A valid credential can still hide\na coached approval.',100,500,1000,39,C.subtle);
  badge('VERIFY THE PERSON. ASSESS THE PRESSURE.',98,798,840,true);
  box(1220,218,590,650,C.panel,'36554E',28);
  text('NUMBER MATCH',1270,276,490,22,C.subtle,true,'center');
  box(1320,345,390,212,C.paper,C.paper,24);text('47',1330,357,370,150,C.ink,true,'center');
  text('“Just enter the number.\nI’ll stay on the line.”',1270,620,490,35,C.coral,false,'center');
  text('Illustrative scenario',1270,798,490,20,C.subtle,false,'center');
  await save();

  base(story.scenes[1]);
  text('Verify the person.\nAssess the pressure.',96,182,1730,76,C.ink,true);
  const cards=[['entra-id','Work identity','Start with Microsoft Entra ID.'],['code','Available context','Ask about recent activity.'],['openai','Conversation evidence','Assess signs of coaching.']];
  cards.forEach(([asset,title,body],i)=>{const x=96+i*592;box(x,454,544,330,C.white,C.border,24);icon(asset,x+34,491,70);text(title,x+34,599,476,38,C.ink,true);text(body,x+34,682,490,27,C.muted);});
  box(96,852,1728,74,C.ink);text('AI supplies evidence. Code controls the outcome.',130,870,1660,34,C.white,true);
  await save();

  base(story.scenes[2]);
  text('A familiar sign-in. A richer verification.',96,155,1740,61,C.ink,true);
  badge('DEMO INSERT  /  00:18–00:48',96,258,530);
  box(96,330,1240,660,C.white,C.border,8);
  image(path.join(root,'docs/presentation/assets/treasury-workspace.png'),104,338,1224,650);
  box(1390,330,434,570,C.ink,C.ink,20);
  text('RECORD THESE MOMENTS',1420,373,374,22,C.lime,true);
  ['01  Work-account sign-in','02  Match the number','03  One contextual question','04  Result + server grant'].forEach((v,i)=>text(v,1420,458+i*87,380,25,C.white,true));
  text('Reference screenshot.\nReplace with actual footage.',1392,922,430,23,C.muted);
  await save();

  base(story.scenes[3],true);
  text('Correct digits.\nCoaching still matters.',96,167,1730,76,C.white,true);
  badge('DEMO INSERT  /  00:48–01:28',98,395,530,true);
  const steps=[['speech','Conversation','Coaching evidence'],['openai','Analysis','Risk + confidence'],['code','Decision','Can refuse verification']];
  steps.forEach(([asset,title,detail],i)=>{let x=96+i*592;box(x,485,544,294,C.panel,'36554E',22);box(x+32,515,86,86,C.paper,C.paper,14);icon(asset,x+43,526,64);text(title,x+32,624,476,39,C.white,true);text(detail,x+32,698,476,29,C.subtle);if(i<2)line(x+548,628,x+586,628,C.subtle,true);});
  text('Show the actual returned result and reason.',98,842,1690,42,C.lime,true);
  text('Conceptual sequence — not a recorded detection or result.',100,922,1650,25,C.subtle);
  await save();

  base(story.scenes[4]);
  text('AI supplies evidence.\nCode owns the decision.',96,157,1730,72,C.ink,true);
  const services=[['communication-services','Call','Azure Communication\nServices'],['speech','Transcribe','Azure AI Speech'],['openai','Assess risk','Azure OpenAI'],['code','Decide','Deterministic\nverification code']];
  services.forEach(([asset,title,detail],i)=>{let x=96+i*440;box(x,425,408,347,C.white,C.border,22);icon(asset,x+30,461,76);text(title,x+30,580,350,41,C.ink,true);text(detail,x+30,659,350,28,C.muted);if(i<3)line(x+414,598,x+434,598,C.muted,true);});
  icon('azure',98,841,56);text('Built on Microsoft Azure',174,845,820,36,C.ink,true);
  text('Microsoft Graph: context  ·  Durable receipts: Treasury policy  ·  Azure monitoring: evidence',98,938,1710,27,C.muted);
  await save();

  base(story.scenes[5],true);
  text('Protect the approval.\nNot just the credential.',96,235,1730,89,C.white,true);
  text('EntraGuard',99,527,1500,65,C.lime,true);
  text('Conversation evidence for identity verification.',101,628,1680,37,C.subtle);
  badge('NEXT: A CONTROLLED TENANT PILOT',99,790,760,true);
  text('entraguard.my  ·  docs.entraguard.my',99,900,1650,32,C.lime);
  await save();

  await p.writeFile({fileName:path.join(here,'EntraGuard-115s-Storyboard.pptx')});
  fs.writeFileSync(path.join(here,'slide-manifest.json'),JSON.stringify(manifest,null,2));
  const thumbs=await Promise.all(story.scenes.map(s=>sharp(path.join(slides,s.id+'.png')).resize(960,540).toBuffer()));
  await sharp({create:{width:1920,height:1620,channels:3,background:'#F5F6F0'}}).composite(thumbs.map((input,i)=>({input,left:(i%2)*960,top:Math.floor(i/2)*540}))).png().toFile(path.join(here,'preview.png'));
  const words=story.cues.reduce((sum,c)=>sum+c.text.split(/\s+/).length,0);
  const stamp=s=>`${String(Math.floor(s/60)).padStart(2,'0')}:${String(s%60).padStart(2,'0')}`;
  let md=`# EntraGuard — 1:55 hackathon video script\n\n**Target: 115 seconds; hard maximum: 120 seconds.** ${words} narration words. This is a storyboard until real demo footage is supplied.\n\n`;
  md+='## Recommended edit\n\n| Time | Picture | Purpose |\n|---|---|---|\n'+story.scenes.map(s=>`| ${stamp(s.start)}–${stamp(s.end)} | ${s.title} | ${s.finalUse} |`).join('\n')+'\n\n';
  md+='## Read-aloud script\n\n'+story.cues.map(c=>`### ${c.start.toFixed(1)}–${c.end.toFixed(1)} seconds\n\n> ${c.text}\n`).join('\n');
  md+='\n## Use the gaps for the real call\n\n- Around 29–32 seconds: one short prompt/answer from the normal verification.\n- Around 62–65 seconds: a short controlled coaching excerpt.\n- Around 79–81 seconds: let the actual result appear before explaining it.\n- Actual synthesized speech can finish before its cue window ends. `narration-timing.json` records the measured durations; use those gaps for original call audio.\n- The narrator and the call should not compete. Duck original call audio while narration is active, then restore it for the evidence moments.\n\n';
  md+='## What to send for the final edit\n\n1. Original landscape MP4/MOV recordings, ideally 1920×1080 or higher, including system/call audio. Uncut clips are fine.\n2. A normal verification: sign-in, visible number, incoming call, one contextual answer, and the actual result.\n3. A controlled coaching attempt: correct number, coaching excerpt, assessment, returned result and reason. Send the real outcome even if it did not refuse.\n4. Optional operator-console recording synchronized with the attempt; identify which clip belongs to which verification.\n5. Any judging rules, mandatory team names/credits or required logo/end card.\n\n';
  md+='## Editing rules\n\n- Use 70 seconds of demonstration, 18 seconds of opening, 17 seconds of architecture and 10 seconds of closing.\n- Compress waiting with visible jump cuts; keep spoken evidence and the result at normal speed. Do not make edited elapsed time look like a latency benchmark.\n- Show real UI outcomes. Label simulator footage as simulated; do not substitute the verification simulator for evidence that the live Analyst detected coaching.\n- Keep the demo ledger label. No bank execution, universal call interception, voice-clone protection, guaranteed detection or independent accuracy claims.\n- Match contextual narration to questions actually asked in the recording. Questions depend on permissions and available data.\n- Outbound verification can refuse a result; session revocation/quarantine belongs to the separate monitored-call path.\n- Distinguish professional production quality of this video from claims that the application is production-certified.\n\n';
  md+='## Source checks\n\n- `src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs`: questions, fallbacks, voice and completion.\n- `src/EntraGuard.MediaService/Endpoints/VerificationAdjudicator.cs`: code/coercion decision.\n- `src/EntraGuard.MediaService/Sessions/GrantService.cs`: separate session grant.\n- `src/EntraGuard.MediaService/Endpoints/MediaSocketEndpoint.cs`: analysis and verification-call remediation suppression.\n- `src/EntraGuard.MediaService/Agents/AnalystClient.cs`: Azure OpenAI analysis.\n- [Detailed verified architecture](../architecture-flows.md) and [official icon credits](../diagrams/azure-icons/README.md).\n';
  fs.writeFileSync(path.join(here,'script-and-edit-plan.md'),md);
  console.log(`Built six slides, editable PPTX with notes, and ${words}-word script for ${story.durationSeconds}s.`);
}
main().catch(error=>{console.error(error);process.exitCode=1;});
