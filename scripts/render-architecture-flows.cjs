// Offline diagram/document generator. Run: node scripts/render-architecture-flows.cjs
// Content: docs/diagrams/flows/flow-definitions.json; icons: official local Microsoft SVGs.
const fs = require('node:fs/promises');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..');
const sharp = createRequire(path.join(root, 'src/portal/package.json'))('sharp');
const out = path.join(root, 'docs/diagrams/flows');
const icons = new Map();
const esc = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');

function wrap(value, max) {
  const lines = [];
  for (const paragraph of String(value).split('\n')) {
    let line = '';
    for (const word of paragraph.split(/\s+/)) {
      if (line && (line.length + word.length + 1) > max) { lines.push(line); line = word; }
      else line += (line ? ' ' : '') + word;
    }
    lines.push(line);
  }
  return lines;
}
function text(x, y, value, size = 22, color = '#294b63', weight = 400, max = 100, lineHeight = size * 1.25) {
  return `<text x="${x}" y="${y}" font-size="${size}" fill="${color}" font-weight="${weight}">${wrap(value, max).map((s, i) => `<tspan x="${x}" dy="${i ? lineHeight : 0}">${esc(s)}</tspan>`).join('')}</text>`;
}
function icon(name, x, y, size = 48) {
  if (!icons.has(name)) throw new Error(`Unknown icon: ${name}`);
  return `<image href="${icons.get(name)}" x="${x}" y="${y}" width="${size}" height="${size}" preserveAspectRatio="xMidYMid meet"/>`;
}
function arrow(d, color = '#477999', dashed = false) {
  return `<path d="${d}" fill="none" stroke="${color}" stroke-width="2.4" ${dashed ? 'stroke-dasharray="8 6"' : ''} marker-end="url(#arrow)"/>`;
}
function start(title, subtitle, w = 3200, h = 1800) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="3200" height="1800" viewBox="0 0 ${w} ${h}" role="img" aria-labelledby="title desc"><title id="title">${esc(title)}</title><desc id="desc">${esc(subtitle)} Source-verified EntraGuard flow. Official Microsoft architecture icons.</desc><defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10Z" fill="#477999"/></marker></defs><g font-family="Segoe UI, Arial, sans-serif"><rect width="${w}" height="${h}" fill="#f5f9fd"/><rect x="50" y="45" width="6" height="78" rx="3" fill="#0078d4"/>${text(78, 82, title, 43, '#12314b', 700)}${text(79, 119, subtitle, 22, '#486579', 400, w === 2560 ? 135 : 180)}${icon('azure', w - 370, 45, 58)}${text(w - 295, 80, 'Microsoft Azure', 28, '#0078d4', 600)}${text(w - 370, 119, 'EntraGuard · verified logical flows', 19, '#486579')}`;
}
function footer(notes, width = 3200, height = 1800) {
  if (width === 2560) {
    return `<rect x="50" y="1310" width="2460" height="82" rx="12" fill="#eaf2f8"/>${text(74, 1340, notes.join('\n'), 18, '#294b63', 400, 250, 28)}${text(50, 1420, 'Source/configuration review · 20 September 2026 · not a live deployment audit · official Microsoft assets; sources in the companion guide.', 17, '#486579', 400, 300)}</g></svg>`;
  }
  const y = height - 195;
  return `<rect x="50" y="${y}" width="${width - 100}" height="126" rx="12" fill="#eaf2f8"/>${text(74, y + 32, 'IMPLEMENTATION NOTES', 18, '#356582', 700)}${text(74, y + 63, notes.join('\n'), 20, '#294b63', 400, width === 2560 ? 220 : 280, 30)}${text(50, height - 30, 'Source/configuration review · 20 September 2026 · not a live deployment audit · original Microsoft icons; see asset credits and source references in the companion guide.', 18, '#486579', 400, 300)}</g></svg>`;
}

function sequence(flow) {
  let s = start(flow.title, flow.subtitle);
  const centers = flow.lanes.map((_, i) => 260 + i * 400);
  s += text(55, 163, 'NUMBERED INTERACTIONS · time runs downward · dashed = response / conditional interaction (not every branch runs)', 21, '#356582', 600, 200);
  s += '<rect x="2475" y="190" width="675" height="1370" rx="14" fill="#fff" stroke="#c2d7e7"/>';
  s += text(2500, 229, 'STEP DETAILS / DECISION RULES', 23, '#12314b', 700);
  s += text(2500, 266, 'Numbers match the interaction on the left.', 21, '#486579');
  for (let i = 0; i < flow.lanes.length; i++) {
    const x = centers[i];
    s += `<rect x="${x - 164}" y="190" width="328" height="110" rx="12" fill="#fff" stroke="#c2d7e7"/>`;
    s += icon(flow.lanes[i].icon, x - 142, 218, 48);
    s += text(x - 76, 238, flow.lanes[i].name, 25, '#12314b', 700, 20, 29);
    s += `<path d="M${x} 300 V1550" stroke="#b7ccdd" stroke-dasharray="5 7" stroke-width="2"/>`;
  }
  flow.steps.forEach((step, i) => {
    const y = 357 + i * 88;
    const a = centers[step.from], b = centers[step.to];
    const conditional = step.kind === 'conditional';
    const color = conditional ? '#886000' : '#356582';
    if (i % 2 === 0) s += `<rect x="45" y="${y - 61}" width="2380" height="86" fill="#eaf2f8" opacity="0.6"/>`;
    s += `<circle cx="62" cy="${y - 7}" r="22" fill="${conditional ? '#fff0cb' : '#0078d4'}"/>`;
    s += `<text x="62" y="${y + 1}" text-anchor="middle" font-size="22" font-weight="700" fill="${conditional ? '#805800' : '#fff'}">${i + 1}</text>`;
    const labelX = a === b ? a + 18 : Math.min(a, b) + 18;
    const available = a === b ? 330 : Math.abs(b - a) - 35;
    const labelLines = wrap(step.label, Math.max(20, Math.floor(available / 12)));
    if (labelLines.length > 2) throw new Error(`${flow.id}: arrow label too long: ${step.label}`);
    s += text(labelX, y - 18 - (labelLines.length - 1) * 26, step.label, 22, color, 600, Math.max(20, Math.floor(available / 12)), 26);
    s += a === b ? arrow(`M${a} ${y} H${a + 115} V${y + 17} H${a}`, color, !!step.kind) : arrow(`M${a} ${y} H${b}`, color, !!step.kind);
    s += text(2500, y - 36, `${String(i + 1).padStart(2, '0')}  ${step.detail}`, 20, '#294b63', 400, 65, 22);
    if (wrap(`${i + 1}  ${step.detail}`, 65).length > 4) throw new Error(`${flow.id} step ${i + 1}: detail exceeds its row`);
  });
  return s + footer(flow.notes);
}

function box(n) {
  const fill = n.kind === 'decision' ? '#fff5db' : n.kind === 'failure' ? '#fff0ef' : n.kind === 'success' ? '#e6f5ef' : '#fff';
  const border = n.kind === 'decision' ? '#d6b85b' : n.kind === 'failure' ? '#dda7a4' : n.kind === 'success' ? '#8ecabd' : '#c2d7e7';
  let s = `<rect x="${n.x}" y="${n.y}" width="${n.w}" height="${n.h}" rx="12" fill="${fill}" stroke="${border}" stroke-width="1.6"/>`;
  if (n.icon) s += icon(n.icon, n.x + 20, n.y + 18, 40);
  const offset = n.icon ? 76 : 20;
  s += text(n.x + offset, n.y + 43, n.title, 25, '#12314b', 700, Math.floor((n.w - offset - 18) / 13), 28);
  const headingLines = wrap(n.title, Math.floor((n.w - offset - 18) / 13)).length;
  const bodyMax = Math.floor((n.w - 42) / 10);
  const bodyY = n.y + 73 + (headingLines - 1) * 28;
  const bodyLines = wrap(n.body, bodyMax);
  if (bodyY + (bodyLines.length - 1) * 24 + 6 > n.y + n.h) throw new Error(`Text overflows logical block: ${n.id}`);
  s += text(n.x + 20, bodyY, n.body, 20, '#294b63', 400, bodyMax, 24);
  return s;
}
const logical = {
  id: '00-logical-architecture', title: 'EntraGuard — detailed logical architecture',
  subtitle: 'Logical responsibilities and authority boundaries. Internal modules share one Media Service process; they are not independent agent containers.',
  nodes: [
    {id:'interfaces', x:50,y:230,w:500,h:245,icon:'entra-id',title:'Identity and application entry',body:'Microsoft Entra ID: EAM sign-in\nContoso Treasury: owner/session APIs\nTreasury uses the Next.js BFF.\nEAM uses browser-mediated OIDC.'},
    {id:'operators', x:50,y:675,w:500,h:235,icon:'browser',title:'Operator and documentation UI',body:'Authorized home-tenant operators\nLive SignalR + server-side queries\nPublic handbook is a separate app.\nThree UI modes share one image.'},
    {id:'incoming', x:50,y:990,w:500,h:180,icon:'event-grid-subscriptions',title:'Incoming monitored calls',body:'Event Grid IncomingCall webhook\nSeparate secret + event deduplication\nAnswers the configured ACS identity.'},
    {id:'entry', x:760,y:245,w:465,h:170,icon:'code',title:'1. Entry and identity',body:'Token / owner / session validation\nEAM hint + requested factor checks\nPath-bound callback/media capabilities'},
    {id:'orchestration', x:1260,y:245,w:485,h:170,icon:'communication-services',title:'2. Call orchestration',body:'VerificationLauncher + Coordinator\nACS setup, prompts, questions, DTMF\nLive call registries are in memory.'},
    {id:'analysis', x:760,y:490,w:985,h:170,icon:'openai',title:'3. Perception, contextual evidence and analysis',body:'Per-participant audio → AI Speech → attributed transcript → AnalystClient\nGraph sources → question selection → answer checks / optional follow-ups\nAI produces structured risk, confidence, stage and evidence; it does not grant.'},
    {id:'verdict', x:760,y:740,w:465,h:165,kind:'success',icon:'code',title:'4A. Verification authority',body:'Coordinator + VerificationAdjudicator\nThen: Treasury grant/payment checks\nOr: EAM result / signed response'},
    {id:'remediation', x:1260,y:740,w:485,h:165,kind:'decision',icon:'code',title:'4B. Remediation authority',body:'PolicyGate → ordered Actuator tools\nOnly on monitored-call live path\nVerification calls skip the Actuator.'},
    {id:'delivery', x:760,y:980,w:985,h:190,icon:'table-storage',title:'5. Evidence, delivery and operator visibility',body:'Treasury: durable receipt / history / policy / grants / outbox\nEAM: in-memory flow and result; direct verification telemetry\nLocal SignalR hub; sink writes; 15-second recovery/outbox worker'},
    {id:'media', x:1950,y:230,w:560,h:185,icon:'speech',title:'ACS + Azure AI Speech',body:'Teams / ACS browser or handset web\nDuplex 24 kHz PCM + DTMF\nLinked AI services: scripted prompts\nAI Speech: ongoing STT / synthesis'},
    {id:'ai', x:1950,y:490,w:560,h:170,icon:'openai',title:'Azure OpenAI + Microsoft Graph',body:'Configured Analyst model + answer judge\nGraph reads: sign-ins / directory / activity\nCross-tenant reads need consent/federation.'},
    {id:'security', x:1950,y:740,w:560,h:170,icon:'key-vault',title:'Scoring, signing and containment',body:'Internal voiceprint app: scores only\nKey Vault: EAM remote RS256 signing\nHome Graph writes: allowed containment'},
    {id:'persistence', x:1950,y:980,w:560,h:190,icon:'sentinel',title:'Azure data and operations',body:'Tables: state, voice, knowledge fallback\nLog Analytics via DCE / DCR; Sentinel\nApplication Insights: diagnostics\nACR + managed identity: deployment/access'}
  ],
  edges: [
    {from:'interfaces',to:'entry',d:'M550 325 H760',label:'Identity / API',x:576,y:308},
    {from:'entry',to:'orchestration',d:'M1225 330 H1260'},
    {from:'orchestration',to:'media',d:'M1745 325 H1950',label:'Calls / WSS',x:1776,y:308},
    {from:'orchestration',to:'analysis',d:'M1500 415 V490',label:'Audio / answers',x:1520,y:461},
    {from:'analysis',to:'ai',d:'M1745 572 H1950',label:'Text / context',x:1767,y:550},
    {from:'analysis',to:'verdict',d:'M980 660 V740',label:'Checks + risk',x:998,y:706},
    {from:'analysis',to:'remediation',d:'M1500 660 V740',label:'Risk assessment',x:1520,y:706},
    {from:'verdict',to:'delivery',d:'M980 905 V980'},
    {from:'remediation',to:'delivery',d:'M1500 905 V980'},
    {from:'remediation',to:'security',d:'M1745 824 H1950',label:'Allowed tools',x:1772,y:805},
    {from:'verdict',to:'security',d:'M1225 871 H1240 V931 H2230 V910',label:'Verification: scoring / EAM signing',x:1410,y:956,dashed:true},
    {from:'delivery',to:'persistence',d:'M1745 1060 H1950',label:'State / telemetry',x:1752,y:1035},
    {from:'delivery',to:'operators',d:'M760 1060 H675 V805 H550',label:'SignalR',x:573,y:783},
    {from:'incoming',to:'orchestration',d:'M550 1075 H620 V445 H1500 V415',label:'AnswerCall setup',x:905,y:469},
    {from:'operators',to:'persistence',d:'M50 790 H25 V1220 H2230 V1170',label:'Operator server-side reads: KQL; also Graph and Resource Graph (not shown as separate nodes)',x:650,y:1247,dashed:true}
  ],
  notes: ['Solid arrows: primary logical interactions. Dashed arrows: supporting calls / reads. Graph and Azure dependencies return data on the same request paths.', 'Five Container Apps / three images. One media replica. Voice observes by default. Key Vault signs EAM; Treasury grants require durable receipts.'],
  sources: ['infra/modules/compute.bicep','src/EntraGuard.MediaService/Program.cs','src/EntraGuard.MediaService/Auth/ApiAccessMiddleware.cs','src/EntraGuard.MediaService/Endpoints/MediaSocketEndpoint.cs','src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs','src/EntraGuard.MediaService/Sessions/GrantService.cs','src/EntraGuard.MediaService/Tools/CrossTenantGraph.cs']
};
const decisions = {
  id:'03-shared-verification-decisions',title:'Shared verification — step-by-step decision logic',
  subtitle:'Used by EAM and Treasury. The code separates call outcomes from downstream authorization and records the evidence actually obtained.',
  nodes: [
    {id:'start',x:800,y:205,w:900,h:125,icon:'communication-services',title:'1. Connect call and request the two-digit number',body:'ACS callbacks / media must validate their signed capability. Speak the prompt.\nAccept ACS recognition, streamed DTMF or owner-bound browser-device entry.'},
    {id:'code',x:800,y:410,w:900,h:145,kind:'decision',title:'2. First adjudication: coercion before code match',body:'Coercion: current raw risk ≥60 AND confidence ≥0.75 → BlockedCoercion.\nOtherwise compare code; deduplicate input within each prompt round.\nCorrect code continues. Incorrect code retries until three entries are used.'},
    {id:'retry',x:50,y:410,w:540,h:150,kind:'failure',title:'Wrong number / bounded retry',body:'Attempts 1–2: prompt again; no closeness hint.\nThird wrong entry: Failed.\nNo code or failed call: non-authorizing outcome.'},
    {id:'coerced',x:1940,y:410,w:570,h:145,kind:'failure',title:'BlockedCoercion',body:'A correct answer does not override coercion.\nQuestion-failure paths also recheck coercion.\nFresh MFA cannot override this refusal.'},
    {id:'sources',x:800,y:620,w:900,h:145,icon:'code',title:'3. Build questions from available sources',body:'Graph: recent sign-ins + directory / calendar / mail / chat / files.\nSelect distinct facets; prefer an expiring source when one is available.\nUp to four questions total; a readable registered rider occupies one seat.'},
    {id:'fallback',x:1940,y:625,w:570,h:155,kind:'decision',title:'Source fallback is real behavior',body:'No Graph questions → use registered knowledge.\nNo registered question either → skip questions.\nNumber-match result may complete as Passed\nwith Low assurance; no voice scoring on this path.'},
    {id:'questions',x:800,y:830,w:900,h:160,kind:'decision',title:'4. Ask, judge and record actual question outcomes',body:'Two or fewer selected questions: all required; three or four: allow one miss.\nSpoken-answer matching, bounded retries/follow-ups; stop if passing is impossible.\nRecord source/facet/asked/correct. Coercion checks continue while waiting.\nRegistered-only fallback uses its own bounded knowledge-challenge loop.'},
    {id:'questionfail',x:50,y:830,w:540,h:160,kind:'failure',title:'Questions cannot be completed',body:'Wrong / unanswered / interrupted challenge\n→ Failed, or BlockedCoercion if detected.\nNeither outcome gives a session grant.\nModel/source outages are not proof of safety.'},
    {id:'final',x:800,y:1060,w:900,h:155,kind:'decision',title:'5. Score eligible voice and adjudicate again',body:'When the question path produced speech: compare with enrolled template.\nRecheck the current assessment before completion.\nNo coercion + satisfied checks → Passed, unless enforced voice requires step-up.'},
    {id:'voice',x:1940,y:1060,w:570,h:155,kind:'decision',icon:'code',title:'Voice decision boundary',body:'Observe (default): score cannot refuse.\nEnforce + weak match: StepUpRequired.\nUnavailable / insufficient audio: NotAssessed.\nInternal scorer has no access authority.'},
    {id:'complete',x:50,y:1060,w:540,h:155,kind:'success',icon:'table-storage',title:'6. Persist result + assurance',body:'Treasury: durable result → separate grant checks.\nEAM: registry result → signed token or error.\nAssurance uses correct source evidence.\nAvailable questions alone do not raise assurance.'}
  ],
  edges: [
    {from:'start',to:'code',d:'M1250 330 V410',label:'Receive code',x:1270,y:376},
    {from:'code',to:'retry',d:'M800 472 H590',label:'Wrong code',x:620,y:451},
    {from:'retry',to:'code',d:'M315 410 V365 H950 V410',label:'Retry: fresh prompt round',x:376,y:354,dashed:true},
    {from:'code',to:'coerced',d:'M1700 472 H1940',label:'Coercion gate',x:1734,y:451},
    {from:'code',to:'sources',d:'M1250 555 V620',label:'Code correct',x:1270,y:596},
    {from:'sources',to:'fallback',d:'M1700 693 H1940',label:'No Graph pool',x:1734,y:672},
    {from:'sources',to:'questions',d:'M1250 765 V830',label:'Questions selected',x:1270,y:805},
    {from:'fallback',to:'questions',d:'M2225 780 V801 H1570 V830',label:'Registered question exists',x:1770,y:791,dashed:true},
    {from:'questions',to:'questionfail',d:'M800 910 H590',label:'Cannot pass',x:619,y:888},
    {from:'questions',to:'final',d:'M1250 990 V1060',label:'Checks satisfied',x:1270,y:1030},
    {from:'final',to:'voice',d:'M1700 1135 H1940',label:'Configured mode',x:1720,y:1115},
    {from:'final',to:'coerced',d:'M1700 1080 H1860 V545 H1940',label:'Final coercion',x:1720,y:1013,dashed:true},
    {from:'final',to:'complete',d:'M800 1140 H590',label:'Final result',x:626,y:1118},
    {from:'fallback',to:'complete',d:'M2510 697 H2535 V1280 H315 V1215',label:'No questions available: complete the code verdict; downstream policy still applies',x:550,y:1270,dashed:true}
  ],
  notes: ['Assurance: no useful knowledge / directory-only → Low; correct SignIn / Activity / Registered → Substantial; correct SignIn + corroboration → High.', 'Corroboration = confirmed follow-up or voice Match. Non-successful challenge → None. StepUpRequired still grants nothing. These are product-specific levels.'],
  sources:['src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs','src/EntraGuard.MediaService/Endpoints/VerificationAdjudicator.cs','src/EntraGuard.Shared/Verification/ChallengeSelection.cs','src/EntraGuard.Shared/Verification/QuestionEvidence.cs','src/EntraGuard.MediaService/Agents/PerceptionAgent.cs','src/EntraGuard.MediaService/Agents/VoiceprintClient.cs']
};

function graph(g) {
  let s = start(g.title, g.subtitle, 2560, 1440);
  if (g === logical) s += '<rect x="720" y="190" width="1065" height="1003" rx="18" fill="#eaf5ff" stroke="#0078d4" stroke-width="2"/>' + text(748, 222, 'AZURE CONTAINER APPS · MEDIA SERVICE PROCESS / SHARED POLICY CODE', 18, '#0068b6', 700);
  for (const edge of g.edges) s += arrow(edge.d, '#477999', edge.dashed);
  for (const n of g.nodes) s += box(n);
  for (const e of g.edges) if (e.label) s += text(e.x, e.y, e.label, 18, '#356582', 600);
  return s + footer(g.notes, 2560, 1440);
}
function mermaidSequence(flow) {
  const lines = ['sequenceDiagram', '    autonumber'];
  flow.lanes.forEach((lane, i) => lines.push(`    participant P${i} as ${lane.name}`));
  flow.steps.forEach(step => {
    if (step.kind === 'conditional') lines.push(`    opt Conditional: ${step.label}`);
    lines.push(`    P${step.from}${step.kind ? '-->>' : '->>'}P${step.to}: ${step.label}`);
    if (step.kind === 'conditional') lines.push('    end');
  });
  return lines.join('\n');
}
function mermaidGraph(g) {
  return ['flowchart TD', ...g.nodes.map(n => `    ${n.id}["${n.title.replaceAll('"', "'")}"]`), ...g.edges.map(e => `    ${e.from} ${e.dashed ? '-.->' : '-->'}${e.label ? `|"${e.label}"|` : ''} ${e.to}`)].join('\n');
}
async function exportDiagram(id, svg) {
  await fs.writeFile(path.join(out, id + '.svg'), svg);
  const result = await sharp(Buffer.from(svg)).png().toFile(path.join(out, id + '.png'));
  if (result.width !== 3200 || result.height !== 1800) throw new Error('Unexpected diagram dimensions');
  console.log(`${id}: ${result.width} × ${result.height}`);
}

async function main() {
  const data = JSON.parse(await fs.readFile(path.join(out, 'flow-definitions.json'), 'utf8'));
  for (const file of await fs.readdir(path.join(out, '../azure-icons'))) {
    if (file.endsWith('.svg')) icons.set(file.slice(0, -4), `data:image/svg+xml;base64,${(await fs.readFile(path.join(out, '../azure-icons', file))).toString('base64')}`);
  }
  const all = [logical, ...data.sequences.slice(0, 2), decisions, ...data.sequences.slice(2)];
  for (const flow of all) {
    for (const source of flow.sources) await fs.access(path.join(root, source));
    await exportDiagram(flow.id, flow.nodes ? graph(flow) : sequence(flow));
  }
  let md = '# EntraGuard — detailed logical and step-by-step flow diagrams\n\n';
  md += `Verified against repository implementation and deployment configuration on **${data.reviewed}**. Live Azure settings/health were not audited.\n\n`;
  md += '## Open the diagram pack\n\n[Browsable gallery](diagrams/flows/index.html) · All nine diagrams are **3200 × 1800 (16:9)** PNGs with self-contained SVG versions and official Microsoft assets.\n\n';
  md += '| Diagram | PNG | SVG |\n|---|---|---|\n' + all.map(f => `| ${f.id.slice(0,2)} — ${f.title} | [PNG](diagrams/flows/${f.id}.png) | [SVG](diagrams/flows/${f.id}.svg) |`).join('\n') + '\n\n';
  md += '## How to read the pack\n\n- Start with **00** for logical responsibilities and **03** for actual verification decisions.\n- **01** establishes the Treasury session. **04** starts verification, calls the shared logic in **03**, then performs the separate grant checks. **05** adds a payment-bound attempt.\n- **02 → 03 → 02** covers EAM: it shares the call engine but has its own outcome/signing lifecycle.\n- **06** covers incoming monitored calls; **07** is enrollment; **08** explains durability and monitoring.\n- Sequence diagrams read from top to bottom. Dashed arrows are responses or conditional interactions; conditional steps are not mandatory stages. Audio, polling and background analysis can overlap.\n- A line crossing is not a junction. Grouped endpoints are logical participants, not extra deployed containers. BFF means Next.js backend-for-frontend.\n\n';
  for (const flow of all) {
    md += `## ${flow.id.slice(0,2)} — ${flow.title}\n\n${flow.subtitle}\n\n![${flow.title}](diagrams/flows/${flow.id}.png)\n\n`;
    if (flow.nodes) {
      md += '| Logical block | Implementation behavior |\n|---|---|\n' + flow.nodes.map(n => `| ${n.title} | ${n.body.replaceAll('\n',' ')} |`).join('\n') + '\n\n';
    } else {
      md += '| Step | Interaction | Checks / outcomes |\n|---|---|---|\n' + flow.steps.map((s,i) => `| ${i+1} | ${s.label}${s.kind === 'conditional' ? ' *(conditional)*' : ''} | ${s.detail} |`).join('\n') + '\n\n';
    }
    md += flow.notes.map(n => '- ' + n).join('\n') + '\n\n';
    md += '<details>\n<summary>Editable Mermaid view</summary>\n\n```mermaid\n' + (flow.nodes ? mermaidGraph(flow) : mermaidSequence(flow)) + '\n```\n\n</details>\n\n';
    md += '**Source evidence:**\n\n' + flow.sources.map(s => `- [\`${s}\`](../${s})`).join('\n') + '\n\n';
  }
  md += '## Authoring and assets\n\n- Sequence content: `docs/diagrams/flows/flow-definitions.json`.\n- Logical/decision layouts and renderer: `scripts/render-architecture-flows.cjs`.\n- Rebuild: `node scripts/render-architecture-flows.cjs` (uses existing `src/portal` sharp dependency; no network needed).\n- [Official Microsoft icon sources and usage credits](diagrams/azure-icons/README.md). Browser/Code symbols represent generic web/API/custom code, not additional Azure managed services.\n- Generated SVGs embed the original SVG icon bytes; PNG/SVG files do not require external asset downloads.\n';
  await fs.writeFile(path.join(root, 'docs/architecture-flows.md'), md);
  const cards = all.map(f => `<section id="${f.id}"><h2>${esc(f.id.slice(0,2))} · ${esc(f.title)}</h2><p>${esc(f.subtitle)}</p><p><a href="${f.id}.png" download>Download PNG</a> · <a href="${f.id}.svg" download>Download SVG</a></p><a href="${f.id}.svg"><img src="${f.id}.png" alt="${esc(f.title)}" loading="lazy" width="3200" height="1800"/></a><ul>${f.notes.map(n => `<li>${esc(n)}</li>`).join('')}</ul></section>`).join('\n');
  await fs.writeFile(path.join(out, 'index.html'), `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>EntraGuard — architecture flow pack</title><style>body{margin:0;background:#f5f9fd;color:#12314b;font:17px/1.6 system-ui,sans-serif}main{max-width:1500px;margin:auto;padding:36px}h1,h2{line-height:1.2}a{color:#0068b6}nav{display:grid;gap:8px}section{margin:56px 0;padding-top:16px;border-top:1px solid #c2d7e7}img{width:100%;height:auto;border:1px solid #c2d7e7;border-radius:12px;background:white}li{margin:8px 0}@media print{nav{display:none}section{break-before:page}main{padding:0}a{color:inherit}}</style><main><h1>EntraGuard — detailed logical and step-by-step flows</h1><p>Source-verified · ${esc(data.reviewed)} · official Microsoft architecture assets · 3200 × 1800 PNG + standalone SVG.</p><p><a href="../../architecture-flows.md">Step-by-step guide and source evidence</a> · <a href="../azure-icons/README.md">Asset credits</a></p><nav>${all.map(f=>`<a href="#${f.id}">${f.id.slice(0,2)} · ${esc(f.title)}</a>`).join('')}</nav>${cards}</main></html>`);
  // A low-resolution review sheet; the full-resolution diagrams remain the deliverables.
  const thumbs = await Promise.all(all.map(async f => sharp(path.join(out, f.id + '.png')).resize(960,540).toBuffer()));
  await sharp({create:{width:2880,height:1620,channels:3,background:'#f5f9fd'}}).composite(thumbs.map((input,i)=>({input,left:(i%3)*960,top:Math.floor(i/3)*540}))).png().toFile(path.join(out,'contact-sheet.png'));
  console.log('Generated guide, gallery, Mermaid views and review contact sheet. All referenced source files exist.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
