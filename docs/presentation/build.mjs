/**
 * EntraGuard: a six-slide, editable hackathon deck.
 * Dependencies are presentation tooling only; no application dependency changes.
 * node build.mjs --tools /path/to/tool-prefix [--screenshot /path/to/treasury.png]
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const option = key => args.includes(key) ? args[args.indexOf(key) + 1] : undefined;
const require = createRequire(path.join(option('--tools') ?? here, 'package.json'));
const pptxgen = require('pptxgenjs');
const sharp = require('sharp');
const QRCode = require('qrcode');
const assets = path.join(here, 'assets');
fs.mkdirSync(assets, { recursive: true });

const screenshot = path.join(assets, 'treasury-workspace.png');
if (option('--screenshot')) {
  const input = option('--screenshot');
  const metadata = await sharp(input).metadata();
  // Show the actual prototype's overview, rather than shrinking a long scrolling ledger.
  const height = Math.min(metadata.height, Math.round(metadata.width / 1.88));
  await sharp(input).extract({ left: 0, top: 0, width: metadata.width, height }).png().toFile(screenshot);
}
if (!fs.existsSync(screenshot)) throw new Error('Supply --screenshot for the first build.');
const qrPath = path.join(assets, 'handbook-qr.png');
await QRCode.toFile(qrPath, 'https://docs.entraguard.my', {
  width: 400, margin: 2, errorCorrectionLevel: 'M', color: { dark: '#102C30', light: '#F6F7F1' },
});

const p = new pptxgen();
p.layout = 'LAYOUT_WIDE';
p.author = 'EntraGuard';
p.subject = 'Hackathon pitch: voice-aware identity verification and social-engineering defence';
p.title = 'EntraGuard — The code can be right. The situation can be wrong.';
p.company = 'EntraGuard';
p.lang = 'en-GB';
p.theme = { headFontFace: 'Arial', bodyFontFace: 'Arial', lang: 'en-GB' };
const W = 13.333333, H = 7.5;
const C = {
  navy: '0C252D', panel: '163A3B', panel2: '204849', lime: 'D5EDAE', paper: 'F5F6F0', white: 'FFFFFF',
  ink: '17382F', muted: '566D62', border: 'DCE4D6', soft: 'EAF0E1', darkMuted: 'B4C8BE', darkBorder: '36554E',
  coral: 'FFD0B8', rust: '523D36', azure: '0078D4', gold: 'DBC88D',
};
const manifest = [];
const notes = [];
let current;

function track(kind, x, y, w, h, extra = {}) {
  if (![x, y, w, h].every(Number.isFinite) || x < -0.001 || y < -0.001 || x + w > W + 0.015 || y + h > H + 0.015 || w < 0 || h < 0)
    throw new Error(`Out-of-bounds ${kind}: ${JSON.stringify({ x, y, w, h, ...extra })}`);
  current.elements.push({ kind, x, y, w, h, ...extra });
}
function box(s, x, y, w, h, fill, stroke = fill, rounded = false, extras = {}) {
  track('shape', x, y, w, h);
  s.addShape(rounded ? p.ShapeType.roundRect : p.ShapeType.rect, {
    x, y, w, h, fill: { color: fill }, line: { color: stroke, width: 0.8 }, ...extras,
  });
}
function text(s, value, x, y, w, h, size = 18, color = C.ink, extras = {}) {
  track('text', x, y, w, h, { text: value, fontSize: size });
  s.addText(value, { x, y, w, h, margin: 0, fontFace: 'Arial', fontSize: size, color,
    breakLine: false, valign: 'mid', paraSpaceAfterPt: 0, fit: 'shrink', ...extras });
}
function line(s, x1, y1, x2, y2, color = C.border, width = 1, arrow = false) {
  track('line', Math.min(x1, x2), Math.min(y1, y2), Math.abs(x2 - x1), Math.abs(y2 - y1));
  s.addShape(p.ShapeType.line, { x: Math.min(x1, x2), y: Math.min(y1, y2), w: Math.abs(x2 - x1), h: Math.abs(y2 - y1),
    flipH: x2 < x1, flipV: y2 < y1, line: { color, width, beginArrowType: 'none', endArrowType: arrow ? 'triangle' : 'none' } });
}
function circle(s, x, y, d, fill, stroke = fill, extras = {}) {
  track('circle', x, y, d, d);
  s.addShape(p.ShapeType.ellipse, { x, y, w: d, h: d, fill: { color: fill }, line: { color: stroke, width: 1 }, ...extras });
}
function shield(s, x, y, size, color) {
  const pts = [[.5,.03],[.9,.2],[.84,.63],[.5,.95],[.16,.63],[.1,.2],[.5,.03]];
  for (let i = 1; i < pts.length; i++) line(s, x + pts[i-1][0]*size, y + pts[i-1][1]*size, x + pts[i][0]*size, y + pts[i][1]*size, color, 1.8);
  [0.25, 0.43, 0.6, 0.4, 0.22].forEach((height, i) => box(s, x + size*(.28+i*.09), y+size*(.5-height/2), size*.035, size*height, color));
}
function wave(s, x, y, w, h, color = C.lime) {
  const bars = [.2,.4,.72,.43,.9,.6,1,.48,.77,.38,.58,.22];
  bars.forEach((height, i) => box(s, x+i*w/bars.length, y+(h-h*height)/2, w/bars.length*.34, h*height, color));
}
function pill(s, label, x, y, w, fill, color, size = 11) {
  box(s, x, y, w, .34, fill, fill, true);
  text(s, label, x+.08, y+.02, w-.16, .29, size, color, { align: 'center', bold: true });
}
function microsoft(s, x, y, size = .24) {
  const gap = size*.1, tile=(size-gap)/2;
  ['F25022','7FBA00','00A4EF','FFB900'].forEach((color,i) => box(s,x+(i%2)*(tile+gap),y+Math.floor(i/2)*(tile+gap),tile,tile,color));
}
function image(s, file, x, y, w, h, altText, extras = {}) {
  track('image', x, y, w, h);
  s.addImage({ path: file, x, y, w, h, altText, ...extras });
}
function slide(title, chapter, dark = false) {
  const s = p.addSlide();
  current = { number: manifest.length + 1, title, dark, elements: [] };
  manifest.push(current);
  s.background = { color: dark ? C.navy : C.paper };
  const color = dark ? C.lime : C.ink;
  shield(s, .64, .33, .27, color);
  text(s, 'ENTRAGUARD', 1.02, .35, 2.3, .25, 11, color, { bold: true, charSpacing: 2 });
  text(s, chapter, 8.2, .36, 4.47, .23, 9.5, dark ? C.darkMuted : C.muted, { align: 'right', charSpacing: 1.3 });
  line(s, .65, 7.05, 12.68, 7.05, dark ? C.darkBorder : C.border, .7);
  text(s, 'MICROSOFT GARAGE HACKATHON  /  MVP', .66, 7.15, 10, .16, 8.2, dark ? C.darkMuted : C.muted, { charSpacing: 1 });
  text(s, `${String(current.number).padStart(2,'0')} / 06`, 11.9, 7.13, .77, .2, 9, dark ? C.darkMuted : C.muted, { align: 'right' });
  return s;
}
function speaker(s, timing, script, demonstration, evidence) {
  const body = `${timing}\n\n${script}\n\nPRESENTER CUE\n${demonstration}\n\nEVIDENCE / BOUNDARIES\n${evidence}`;
  s.addNotes(body);
  notes.push({ title: current.title, timing, script, demonstration, evidence });
}

// 01 — Open with the tension, not an infrastructure inventory.
{
  const s = slide('The code can be right. The situation can be wrong.', '01  /  THE HUMAN GAP', true);
  text(s, 'EntraGuard', .65, 1.44, 7.1, .82, 57, C.lime, { bold: true, charSpacing: -1.7 });
  text(s, 'The code can be right.\nThe situation can be wrong.', .68, 2.57, 7.0, 1.65, 32, C.white, { bold: true, charSpacing: -.7 });
  text(s, 'Voice-aware identity verification\nfor Microsoft Entra ID.', .7, 4.7, 6.8, .88, 22, C.darkMuted);
  text(s, 'VERIFY THE PERSON. ASSESS THE PRESSURE.', .7, 6.29, 6.9, .32, 11, C.lime, { charSpacing: 1.6, bold: true });

  box(s, 8.12, 1.49, 4.54, 5.1, C.panel, C.darkBorder, true);
  text(s, 'A VALID CREDENTIAL', 8.52, 1.9, 3.74, .3, 11, C.darkMuted, { charSpacing: 1.8, align: 'center' });
  box(s, 8.66, 2.52, 3.46, 1.5, C.paper, C.paper, true);
  text(s, 'NUMBER MATCH', 9.04, 2.73, 2.7, .2, 9, C.muted, { charSpacing: 1.8, align: 'center' });
  text(s, '47', 9.08, 2.97, 2.61, .9, 65, C.ink, { bold: true, align: 'center', charSpacing: 6 });
  text(s, 'AN UNSAFE CONVERSATION', 8.52, 4.29, 3.74, .22, 10.5, C.coral, { charSpacing: 1.3, align: 'center' });
  box(s, 8.49, 4.78, 3.79, .97, C.rust, C.rust, true);
  text(s, '“Just enter the number.\nI’ll stay on the line.”', 8.72, 4.96, 3.34, .59, 19, C.white, { italic: true, align: 'center' });
  text(s, 'Correct code ≠ trusted intent', 8.46, 6.0, 3.85, .3, 16, C.lime, { bold: true, align: 'center' });
  speaker(s, '00:00–00:25 · 25 seconds',
    'Imagine an employee enters the right MFA code while a convincing caller tells them exactly what to do. The credential is valid. The situation is not. EntraGuard brings evidence from that conversation into the verification decision.',
    'Pause after “the situation is not.” The number and quotation are an illustrative scenario, not a recording of a real victim.',
    'Grounded in the repository’s outbound verification and coercion-adjudication flow. Do not describe EntraGuard as proof of intent or a guarantee that coercion is absent.');
}

// 02 — A concrete problem, without invented breach statistics.
{
  const s = slide('A valid sign-in can hide an unsafe conversation.', '02  /  WHAT WE SOLVE');
  text(s, 'A valid sign-in can hide\nan unsafe conversation.', .65, 1.05, 11.9, 1.27, 42, C.ink, { bold: true, charSpacing: -1.2 });
  text(s, 'Social engineering can turn legitimate authentication into an attacker’s advantage.', .68, 2.51, 11.7, .46, 19, C.muted);
  const cards = [
    ['01', 'Impersonation', '“I’m from your IT team.”', 'Authority becomes pressure.'],
    ['02', 'MFA coaching', '“Just approve the prompt.”', 'The user follows instructions.'],
    ['03', 'Payment pressure', '“Change the beneficiary.”', 'An unsafe approval looks legitimate.'],
  ];
  cards.forEach(([number, title, quote, detail], i) => {
    const x=.65+i*4.09;
    box(s,x,3.3,3.85,2.52,C.white,C.border,true);
    text(s,number,x+.27,3.56,.6,.3,12,C.muted,{bold:true,charSpacing:1.3});
    line(s,x+.28,4.0,x+.86,4.0,C.ink,2.4);
    text(s,title,x+.28,4.2,3.29,.44,24,C.ink,{bold:true,charSpacing:-.5});
    text(s,quote,x+.28,4.98,3.29,.39,17,C.ink);
    text(s,detail,x+.28,5.5,3.29,.2,12.5,C.muted);
  });
  box(s,.65,6.2,12.03,.55,C.ink);
  text(s,'Credentials are validated. The conversation still needs scrutiny.',.88,6.3,11.56,.33,19,C.white,{bold:true});
  speaker(s, '00:25–01:05 · 40 seconds',
    'The gap is not just another stolen password. A help-desk impersonator, a caller coaching an MFA approval, or someone pressuring a payment change can work through the legitimate user. Identity controls see the authentication. EntraGuard adds conversation evidence at the moment an approval is being made.',
    'Point to one scenario that resonates with the judges. Do not spend time reading all three quotations.',
    'Scam taxonomy in src/EntraGuard.Shared/Detection/ScamVector.cs. Scope is calls deliberately routed through monitored ACS identities and calls EntraGuard originates. It does not listen to every phone or Teams call. No breach statistics are asserted.');
}

// 03 — Explain the product before the implementation.
{
  const s = slide('One call. More context. A governed decision.', '03  /  THE EXPERIENCE', true);
  text(s,'One call. More context.\nA governed decision.',.65,1.03,11.9,1.26,42,C.white,{bold:true,charSpacing:-1.1});
  const steps = [
    ['1','Start with\nwork identity','Entra SSO.\nTeams or browser call.'],
    ['2','Match the number.\nAnswer in context.','Number matching.\nAvailable identity facts.'],
    ['3','Assess the\nconversation.','Coaching detection.\nOptional voice comparison.'],
    ['4','Return a\ngoverned result.','Allow, step up or refuse\nwith an auditable reason.'],
  ];
  steps.forEach(([number,title,detail],i) => {
    const x=.65+i*3.075;
    box(s,x,2.91,2.8,2.96,C.panel,C.darkBorder,true);
    circle(s,x+.26,3.2,.42,C.lime);
    text(s,number,x+.26,3.27,.42,.24,13,C.ink,{align:'center',bold:true});
    text(s,title,x+.25,3.99,2.3,.76,20,C.white,{bold:true});
    text(s,detail,x+.25,5.02,2.3,.53,14.3,C.darkMuted);
    if(i<3) line(s,x+2.85,4.41,x+3.03,4.41,C.lime,1.8,true);
  });
  box(s,.65,6.22,12.03,.5,C.lime);
  text(s,'Correct digits are necessary. Detected coercion can still stop the attempt.',.86,6.29,11.6,.35,17.8,C.ink,{bold:true});
  speaker(s, '01:05–02:00 · 55 seconds',
    'The user signs in with a Microsoft work account and receives a verification call. They match a number and answer questions drawn from the identity or activity sources available to their tenant. During the call, the Analyst looks for coaching. Optional voice comparison adds speaker-similarity evidence. The result is governed: access, an additional check, or refusal—with an explanation.',
    'Emphasize “available” sources and “optional” voice. These are distinct signals, not four independently certified authentication factors.',
    'VerificationEndpoint / VerificationLauncher / VerificationCoordinator, EvidenceAssurance and VerificationAdjudicator. Coercion refusal threshold: risk ≥60 and confidence ≥0.75. Voice observation is the default. Enforced weak voice matches require server-validated fresh MFA; no voice-clone or PAD claim.');
}

// 04 — Two entry points, one Microsoft-aligned security layer.
{
  const s = slide('Fits the Microsoft stack. Extends the decision.', '04  /  HOW IT INTEGRATES');
  text(s,'Fits the Microsoft stack.\nExtends the decision.',.65,1.02,11.95,1.28,42,C.ink,{bold:true,charSpacing:-1.1});
  microsoft(s,.68,2.48,.22);
  text(s,'TWO INTEGRATION ROUTES',1.02,2.47,3.5,.25,10,C.muted,{bold:true,charSpacing:1.25});

  box(s,.65,2.94,2.77,1.08,C.white,C.border,true);
  text(s,'Application API',.9,3.15,2.28,.3,18,C.ink,{bold:true});
  text(s,'Entra SSO → Treasury step-up',.9,3.59,2.3,.22,12.3,C.muted);
  box(s,.65,4.63,2.77,1.08,C.white,C.border,true);
  text(s,'Entra EAM / OIDC',.9,4.84,2.28,.3,18,C.ink,{bold:true});
  text(s,'Optional tenant integration',.9,5.28,2.3,.22,12.3,C.muted);
  line(s,3.43,3.48,3.7,3.48,C.muted,1.1);
  line(s,3.43,5.17,3.7,5.17,C.muted,1.1);
  line(s,3.7,3.48,3.7,5.17,C.muted,1.1);
  line(s,3.7,4.31,3.94,4.31,C.muted,1.4,true);

  box(s,3.99,2.85,5.1,2.96,C.navy,C.navy,true);
  text(s,'EntraGuard',4.25,3.1,4.58,.36,24,C.lime,{bold:true,align:'center'});
  text(s,'AZURE CONTAINER APPS',4.25,3.57,4.58,.2,9.5,C.darkMuted,{align:'center',charSpacing:1.5});
  ['ACS / Teams','AI Speech','Azure OpenAI'].forEach((label,i)=>{
    const x=4.22+i*1.57;
    box(s,x,3.98,1.48,.56,C.panel2,C.darkBorder,true);
    text(s,label,x+.05,4.11,1.38,.26,12.2,C.white,{align:'center',bold:true});
  });
  box(s,4.22,4.84,4.63,.49,C.lime,C.lime,true);
  text(s,'Deterministic gate + verified sessions',4.37,4.93,4.33,.27,14,C.ink,{align:'center',bold:true});
  text(s,'Graph context  •  Optional voiceprint  •  Table receipts',4.22,5.51,4.63,.17,9.5,C.darkMuted,{align:'center'});

  line(s,9.09,4.31,9.34,4.31,C.muted,1.1);
  line(s,9.34,3.34,9.34,5.3,C.muted,1.1);
  const outputs=[['App access','Grant / step up / refuse'],['Microsoft Graph','Permission-gated containment'],['Microsoft Sentinel','Incidents + audit evidence']];
  outputs.forEach(([title,body],i)=>{
    const y=2.93+i*.98;
    line(s,9.34,y+.41,9.59,y+.41,C.muted,1.3,true);
    box(s,9.65,y,3.03,.82,C.white,C.border,true);
    text(s,title,9.87,y+.12,2.57,.28,17,C.ink,{bold:true});
    text(s,body,9.87,y+.5,2.57,.17,11.7,C.muted);
  });
  text(s,'Entra-first identity. Azure-native services. Evidence in Sentinel.',.68,6.16,12,.39,19,C.ink,{bold:true});
  text(s,'EAM is opt-in; tenant consent, policy and calling prerequisites apply. Graph actions depend on licensing and permissions.',.68,6.7,12,.18,9.8,C.muted);
  speaker(s, '02:00–03:05 · 65 seconds',
    'There are two integration routes. An application such as Treasury can explicitly request step-up after Entra sign-in. The optional External Authentication Method uses OIDC to fit into Entra’s MFA flow, when configured by a tenant. Both use the call pipeline: ACS and Teams, AI Speech, and Azure OpenAI. Deterministic rules govern the result. The appropriate flow can return an application decision, execute permitted Graph containment, and send evidence to Microsoft Sentinel.',
    'Trace the diagram left to right once. Say “optional and configuration-dependent” when pointing to EAM. Distinguish verification decisions from containment actions on monitored attack calls.',
    'Sources: README.md; docs/architecture.md; docs/external-auth-method.md; infra/modules/*.bicep. EAM is implemented but not established as a live-validated cross-tenant rollout by this deck. Requires tenant configuration/licensing and deliverable calls. Voice scorer is open-source SpeechBrain/PyTorch on Azure, not a Microsoft biometric service. Managed identity is used for Azure service access; encryption/signing keys still exist.');
}

// 05 — Real product surface + three reasons a judge should care.
{
  const s = slide('See why access was granted. Or why it was refused.', '05  /  PRODUCT + DIFFERENTIATION');
  text(s,'See why access was granted.\nOr why it was refused.',.65,1.03,12,1.25,40,C.ink,{bold:true,charSpacing:-1});
  box(s,.64,2.69,7.64,4.09,C.white,C.border);
  image(s,screenshot,.72,2.79,7.48,3.978,'Contoso Treasury prototype overview; sample financial data, not a real bank ledger.');
  pill(s,'CONTOSO TREASURY DEMO',.82,2.48,2.92,C.ink,C.white,10);
  const points=[
    ['01','AI advises.\nPolicy decides.','A model assessment does not\ndirectly grant access or contain users.'],
    ['02','Bind trust to the action.','Fresh verification binds to\nexact demo-payment details.'],
    ['03','Keep an evidence trail.','Durable receipts, policy versions\nand owner-scoped history.'],
  ];
  points.forEach(([n,title,body],i)=>{
    const y=[2.66,4.33,5.72][i];
    text(s,n,8.69,y,.42,.23,10.5,C.muted,{bold:true});
    line(s,9.22,y+.12,12.65,y+.12,C.border,.8);
    text(s,title,8.69,y+.3,3.94,i===0?.65:.42,21,C.ink,{bold:true,charSpacing:-.25});
    text(s,body,8.69,y+(i===0?1.02:.8),3.94,.43,13.5,C.muted);
  });
  speaker(s, '03:05–04:10 · 65 seconds',
    'This is Contoso Treasury, our relying-party demonstration. What matters is not just the risk meter. The model supplies evidence while policy owns authority. A demo-payment approval can require verification bound to the exact payment details. And the outcome is explainable through receipts, policy versions and owner-scoped history. The decisive demo beat is a correct number match that is still refused when coaching is detected.',
    'The screenshot is the Treasury prototype with illustrative data. For a five-minute slot, show a short rehearsed recording or stay on this slide; a full verification call can take substantially longer. In a longer slot, switch to a prepared benign/coerced pair and show the actual returned receipt. Never promise a particular live model verdict.',
    'Sources: TreasuryDashboard.tsx; GrantService.cs; PaymentService.cs; VerificationLedger.cs; TenantPolicyService.cs. Approval is implemented for a protected demo ledger, not a bank connector. Local tests cover ownership, replay, concurrency and policy checks; local tests are not proof of live Azure/Entra deployment readiness. The screenshot demonstrates UI design, not the completion of a specific transaction.');
}

// 06 — Honest maturity and a concrete ask.
{
  const s=slide('Protect the approval. Not just the credential.','06  /  THE NEXT STEP',true);
  text(s,'Protect the approval.',.65,1.03,12,.65,44,C.white,{bold:true,charSpacing:-1});
  text(s,'Not just the credential.',.65,1.77,12,.65,44,C.lime,{bold:true,charSpacing:-1});
  text(s,'BUILT',.7,2.91,5.8,.24,11,C.lime,{bold:true,charSpacing:2});
  const built=[
    ['Voice-aware verification','Calls, context and coercion analysis.'],
    ['Durable trust + policy','Sessions, receipts and bound demo approvals.'],
    ['Two integration routes','Application API + opt-in Entra EAM.'],
  ];
  built.forEach(([title,body],i)=>{
    const y=3.43+i*.81;
    circle(s,.72,y+.06,.2,C.lime);
    text(s,title,1.12,y,5.7,.32,21,C.white,{bold:true});
    text(s,body,1.12,y+.4,5.8,.23,14.5,C.darkMuted);
  });
  box(s,7.85,2.87,4.82,2.97,C.panel,C.darkBorder,true);
  text(s,'NEXT: A CONTROLLED TENANT PILOT',8.16,3.2,4.19,.32,12,C.lime,{bold:true,charSpacing:.5});
  ['Completion rate','False refusals','Time to intervention'].forEach((item,i)=>{
    const y=3.92+i*.49;
    circle(s,8.19,y+.08,.11,C.panel,C.lime);
    text(s,item,8.48,y,3.78,.3,19,C.white);
  });
  text(s,'Measure on real calling channels.',8.16,5.53,4.18,.2,12.5,C.darkMuted);
  text(s,'MVP · Demo ledger only · Voice scoring observes by default · No voice-clone / PAD claim',.7,6.02,11.95,.22,10,C.darkMuted);
  text(s,'Looking for an identity / SOC partner\nand a test tenant.',.7,6.4,8.9,.49,19,C.lime,{bold:true});
  image(s,qrPath,11.8,6.1,.85,.85,'QR code linking to the EntraGuard handbook at https://docs.entraguard.my',{hyperlink:{url:'https://docs.entraguard.my'}});
  text(s,'Explore the solution',9.15,6.38,2.39,.22,11,C.white,{align:'right'});
  text(s,'docs.entraguard.my',9.15,6.7,2.39,.21,12,C.lime,{align:'right'});
  speaker(s,'04:10–04:45 · 35 seconds, then pause for questions',
    'We have built the verification pipeline and the trust controls around it: sessions, durable receipts, policy and transaction-bound demo approvals. The next step is a controlled tenant pilot. We want to measure completion, false refusals and time to intervention on real calling channels. We are looking for an identity or SOC partner and a test tenant. Protect the approval—not just the credential.',
    'End on the ask. The QR code opens the public handbook. Keep any live-demo segment separate from this five-minute pitch. Do not call the prototype production-ready.',
    'Q&A: This is not voice-clone detection; ECAPA compares speaker similarity and has no PAD. It does not monitor every call. Tenant policies and permissions constrain actions. EAM is opt-in and has deployment prerequisites. Demo approvals move no funds. Media remains single-replica until distributed live-call routing is implemented. Repository sources: docs/security-migration.md, docs/standards-and-threat-model.md, docs/external-auth-method.md. No unvalidated breach, accuracy, latency or ROI statistic appears on these slides.');
}

await p.writeFile({ fileName: path.join(here, 'EntraGuard-Hackathon.pptx') });
fs.writeFileSync(path.join(here, 'deck-manifest.json'), JSON.stringify({ title: p.title, width: W, height: H, slides: manifest }, null, 2));
fs.writeFileSync(path.join(here, 'speaker-notes.md'), '# EntraGuard — five-minute pitch\n\nSix slides · about 4 minutes 45 seconds of speech, plus transitions. Add a live demo only if your slot permits it.\n\n' + notes.map((n,i)=>`## ${i+1}. ${n.title}\n\n**${n.timing}**\n\n${n.script}\n\n**Presenter cue:** ${n.demonstration}\n\n**Evidence and Q&A:** ${n.evidence}\n`).join('\n'));
console.log(`Created ${manifest.length} slides; ${manifest.reduce((sum,s)=>sum+s.elements.length,0)} checked elements.`);
console.log(path.join(here, 'EntraGuard-Hackathon.pptx'));
