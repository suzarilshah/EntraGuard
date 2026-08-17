/**
 * Builds the EntraGuard write-up as a Word document.
 *
 * Headings use the built-in HeadingLevel values so the table of contents picks them up.
 * Figures are sized to the printable width of an A4 page with 1" margins (451pt), and the
 * height is derived from each PNG's own aspect ratio rather than guessed.
 */

const fs = require('fs');
const path = require('path');
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType, ImageRun,
  Table, TableRow, TableCell, WidthType, ShadingType, BorderStyle, TableOfContents,
  PageBreak, ExternalHyperlink, LevelFormat, convertInchesToTwip,
} = require('docx');

const FIG = path.join(__dirname, 'figures');
const PRINT_WIDTH_PT = 451;      // A4 (595pt) less 1" margins each side
const INK = '1B1A19';
const MUTED = '605E5C';
const BLUE = '0F6CBD';
const RED = 'A4262C';
const GREEN = '0E700E';
const RULE = 'D2D0CE';

/** PNG dimensions, read from the IHDR chunk — no guessing at aspect ratios. */
function pngSize(file) {
  const buf = fs.readFileSync(file);
  return { w: buf.readUInt32BE(16), h: buf.readUInt32BE(20) };
}

function figure(name, caption) {
  const file = path.join(FIG, `${name}.png`);
  const { w, h } = pngSize(file);
  const width = PRINT_WIDTH_PT;
  const height = Math.round((h / w) * width);

  return [
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { before: 240, after: 80 },
      children: [new ImageRun({
        data: fs.readFileSync(file),
        transformation: { width, height },
        type: 'png',
      })],
    }),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { after: 280 },
      children: [new TextRun({ text: caption, size: 17, italics: true, color: MUTED })],
    }),
  ];
}

const p = (text, opts = {}) => new Paragraph({
  spacing: { after: opts.after ?? 160, line: 300 },
  children: [new TextRun({ text, size: 21, color: INK, ...opts.run })],
  ...opts.para,
});

/** A paragraph built from mixed runs — for inline bold or code. */
const rich = (runs, opts = {}) => new Paragraph({
  spacing: { after: opts.after ?? 160, line: 300 },
  children: runs.map((r) => (typeof r === 'string'
    ? new TextRun({ text: r, size: 21, color: INK })
    : new TextRun({
        text: r.t, size: 21, color: r.color ?? INK, bold: r.b, italics: r.i,
        font: r.code ? 'Consolas' : undefined,
      }))),
});

const h1 = (text) => new Paragraph({
  heading: HeadingLevel.HEADING_1,
  spacing: { before: 420, after: 180 },
  children: [new TextRun({ text, size: 32, bold: true, color: INK })],
});

const h2 = (text) => new Paragraph({
  heading: HeadingLevel.HEADING_2,
  spacing: { before: 320, after: 140 },
  children: [new TextRun({ text, size: 25, bold: true, color: INK })],
});

const h3 = (text) => new Paragraph({
  heading: HeadingLevel.HEADING_3,
  spacing: { before: 260, after: 120 },
  children: [new TextRun({ text, size: 22, bold: true, color: BLUE })],
});

const bullet = (text) => new Paragraph({
  numbering: { reference: 'dots', level: 0 },
  spacing: { after: 90, line: 290 },
  children: [new TextRun({ text, size: 21, color: INK })],
});

/** A pull-out box: the lesson, separated from the story. */
function callout(title, body, accent = BLUE) {
  return new Table({
    columnWidths: [9360],
    width: { size: 9360, type: WidthType.DXA },
    borders: {
      top: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      bottom: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      left: { style: BorderStyle.SINGLE, size: 18, color: accent },
      right: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      insideHorizontal: { style: BorderStyle.NONE },
      insideVertical: { style: BorderStyle.NONE },
    },
    rows: [new TableRow({
      children: [new TableCell({
        width: { size: 9360, type: WidthType.DXA },
        shading: { type: ShadingType.CLEAR, fill: 'FAF9F8' },
        margins: { top: 160, bottom: 160, left: 220, right: 220 },
        children: [
          new Paragraph({
            spacing: { after: 70 },
            children: [new TextRun({ text: title, size: 20, bold: true, color: accent })],
          }),
          new Paragraph({
            spacing: { after: 0, line: 290 },
            children: [new TextRun({ text: body, size: 20, color: INK })],
          }),
        ],
      })],
    })],
  });
}

/** Fixed-width block for errors and payloads. */
function code(lines) {
  return new Table({
    columnWidths: [9360],
    width: { size: 9360, type: WidthType.DXA },
    borders: {
      top: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      bottom: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      left: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      right: { style: BorderStyle.SINGLE, size: 2, color: RULE },
      insideHorizontal: { style: BorderStyle.NONE },
      insideVertical: { style: BorderStyle.NONE },
    },
    rows: [new TableRow({
      children: [new TableCell({
        width: { size: 9360, type: WidthType.DXA },
        shading: { type: ShadingType.CLEAR, fill: 'F3F2F1' },
        margins: { top: 140, bottom: 140, left: 200, right: 200 },
        children: lines.map((line) => new Paragraph({
          spacing: { after: 30, line: 260 },
          children: [new TextRun({ text: line, size: 18, font: 'Consolas', color: INK })],
        })),
      })],
    })],
  });
}

function table(headers, rows, widths) {
  const total = widths.reduce((a, b) => a + b, 0);
  return new Table({
    columnWidths: widths,
    width: { size: total, type: WidthType.DXA },
    rows: [
      new TableRow({
        tableHeader: true,
        children: headers.map((head, i) => new TableCell({
          width: { size: widths[i], type: WidthType.DXA },
          shading: { type: ShadingType.CLEAR, fill: 'EDEBE9' },
          margins: { top: 100, bottom: 100, left: 140, right: 140 },
          children: [new Paragraph({
            children: [new TextRun({ text: head, size: 18, bold: true, color: INK })],
          })],
        })),
      }),
      ...rows.map((cells) => new TableRow({
        children: cells.map((cell, i) => new TableCell({
          width: { size: widths[i], type: WidthType.DXA },
          margins: { top: 100, bottom: 100, left: 140, right: 140 },
          children: [new Paragraph({
            spacing: { line: 260 },
            children: [new TextRun({ text: cell, size: 18, color: INK })],
          })],
        })),
      })),
    ],
  });
}

const children = [];

// ── Title block ─────────────────────────────────────────────────────────────
children.push(new Paragraph({
  spacing: { after: 60 },
  children: [new TextRun({
    text: 'I built an MFA factor that listens for the scam',
    size: 44, bold: true, color: INK,
  })],
}));
children.push(new Paragraph({
  spacing: { after: 200 },
  children: [new TextRun({
    text: 'Voice verification on Azure Communication Services and Microsoft Entra ID — and the thirteen things that went wrong on the way',
    size: 24, color: MUTED,
  })],
}));
children.push(new Paragraph({
  spacing: { after: 60 },
  border: { bottom: { style: BorderStyle.SINGLE, size: 6, color: RULE, space: 8 } },
  children: [new TextRun({
    text: 'Suzaril Shah  ·  Microsoft Certified Trainer  ·  Built for the Microsoft Garage Hackathon',
    size: 19, color: MUTED,
  })],
}));
children.push(new Paragraph({ spacing: { after: 260 }, children: [] }));

children.push(p(
  'This is a long post, and most of it is about failure. If you only want the architecture, '
  + 'Figure 1 is two pages down and you can stop there. If you want the part that was actually '
  + 'useful to me — the thirteen ways this thing broke, several of which were platform '
  + 'behaviour I would not have guessed in a year — start at "The hiccups".'));

children.push(new Paragraph({
  spacing: { before: 200, after: 120 },
  children: [new TextRun({ text: 'Contents', size: 24, bold: true, color: INK })],
}));
children.push(new TableOfContents('Contents', { hyperlink: true, headingStyleRange: '1-2' }));
children.push(new Paragraph({ children: [new PageBreak()] }));

// ── Inspiration ─────────────────────────────────────────────────────────────
children.push(h1('Why I built this'));

children.push(p(
  'In September 2023, someone phoned the MGM Resorts IT help desk, said they were an employee, '
  + 'and asked for a password reset. They got one. The group behind it — Scattered Spider — did '
  + 'the same thing to Caesars, and later to Marks & Spencer. No malware, no zero-day. A phone '
  + 'call and a convincing story.'));

children.push(p(
  'What has always bothered me about that class of attack is that multi-factor authentication '
  + 'does not help, and it is not supposed to. MFA answers one question very well: is the person '
  + 'approving this the person who owns the account? It has nothing at all to say about a second '
  + 'question, which in these attacks is the only one that matters — does that person actually '
  + 'want this to happen, or is somebody on the phone telling them to press approve?'));

children.push(rich([
  'A number-matching prompt proves possession of a device. It cannot prove ',
  { t: 'free will', i: true },
  '. A victim being walked through a sign-in by a confident stranger produces exactly the same '
  + 'telemetry as a user signing in normally, because they are doing exactly the same thing. That '
  + 'gap is the entire premise of what I built.']));

children.push(callout(
  'The idea in one sentence',
  'Place the step-up challenge as a real voice call, and while the user answers it, listen to '
  + 'the call for evidence that somebody is coaching them through it.',
  BLUE));

children.push(p(
  'The name is EntraGuard. It is a hackathon project, it runs on real Azure, and everything I '
  + 'describe below happened.', { after: 260 }));

// ── Architecture ────────────────────────────────────────────────────────────
children.push(h1('What it actually is'));

children.push(...figure('fig1-architecture',
  'Figure 1 — Component and protocol view. Every arrow is a real wire protocol, not a conceptual link.'));

children.push(p(
  'A relying party — in the demo, a fake payments app called Contoso Treasury — asks EntraGuard '
  + 'for a step-up. EntraGuard places an outbound call through Azure Communication Services Call '
  + 'Automation, which rings the user in Microsoft Teams through cross-tenant federation. The '
  + 'user sees a two-digit number in their browser and keys it on their phone.'));

children.push(p(
  'While that is happening, the call audio is streaming both ways over a WebSocket as unmixed '
  + 'PCM. Unmixed matters: it means each participant arrives on their own channel, so I can tell '
  + 'the difference between the person I called and anybody else who happens to be talking. A '
  + 'transcript is built from that, and every three seconds an Azure OpenAI model scores the last '
  + '45 seconds for signs of coercion — someone reading digits aloud to the user, an authority '
  + 'claim, manufactured urgency.'));

children.push(p(
  'Then there are the questions. Instead of stored security questions, EntraGuard asks about the '
  + 'user’s own recent sign-in activity, pulled live from Microsoft Graph: which town were '
  + 'you in the last time you signed in, what device did you use. Nothing is stored, nothing can '
  + 'be researched in advance, and the answers expire on their own. NIST SP 800-63 rejects stored '
  + 'security questions as an authenticator, and it is right to.'));

children.push(p(
  'Voice biometrics came last, and I will come back to why I now think it is the weakest part of '
  + 'the system rather than the headline.'));

children.push(...figure('fig2-call-flow',
  'Figure 2 — The sequence of a single verification call. The coercion analyst runs continuously underneath all of it.'));

children.push(callout(
  'The design decision I am most confident about',
  'Voice never denies access on its own. A poor match asks for a stronger factor; it does not '
  + 'refuse you. A speaker model running over a phone codec is not accurate enough to lock '
  + 'somebody out of their own money, and pretending otherwise is how you ship a product that '
  + 'fails honest users.',
  GREEN));

// ── Hiccups ─────────────────────────────────────────────────────────────────
children.push(new Paragraph({ children: [new PageBreak()] }));
children.push(h1('The hiccups'));

children.push(p(
  'I kept a list. Some of these are my own carelessness, some are platform behaviour that is '
  + 'documented but easy to miss, and two of them I would call genuinely surprising. In rough '
  + 'order of how much time they cost me.'));

children.push(h2('1. Teams federation wants a GUID, not the thing that looks like an ID'));

children.push(rich([
  'Calling a Teams user from ACS needs the Teams tenant to allow-list your Communication '
  + 'Services resource. The cmdlet is ',
  { t: 'Set-CsTeamsAcsFederationConfiguration', code: true },
  ', and it takes the resource’s ',
  { t: 'immutable resource ID', b: true },
  ' — a bare GUID. I spent a while feeding it the full ARM resource path, which is the thing '
  + 'that looks like an identifier and is right there in the portal. It is rejected outright.']));

children.push(code([
  '403#10124  the ACS resource is not allow-listed in the Teams tenant',
  '403#10391  the user is not Enterprise Voice enabled',
  '487#10004  it rang, nobody answered',
  '412#10119  personal Microsoft account — no Teams identity to call',
]));

children.push(p(
  'The second one deserves its own sentence, because it cost me an afternoon: a Teams Phone '
  + 'licence is not the same thing as being Enterprise Voice enabled. You can have the licence '
  + 'and still get a 403.'));

children.push(h2('2. Two AI agents, talking to each other, on my authentication call'));

children.push(rich([
  'I wired the Azure OpenAI Realtime API in to make the call conversational. The result was two '
  + 'voices having a polite exchange while the actual human waited. The cause is a single default: '
  + 'with server-side voice activity detection, ',
  { t: 'create_response', code: true },
  ' defaults to ',
  { t: 'true', code: true },
  ', so the model answers anything it hears — including its own previous utterance echoing back '
  + 'off the caller’s speakerphone.']));

children.push(p(
  'Setting it to false and driving every utterance explicitly fixed it. But the underlying '
  + 'lesson took longer to land, and it is the next entry.'));

children.push(h2('3. The system confidently answered its own question'));

children.push(p(
  'Log line, from a real call: heard "OK, which town, city or?" — judged against the expected '
  + 'answer "Petaling Jaya". Twice, eight seconds apart, burning both of the user’s attempts '
  + 'before they had finished listening to the question.'));

children.push(p(
  'On a phone call, the prompt you play comes back to you. Teams echoes it, or the handset '
  + 'speaker feeds its own microphone, and speech recognition attributes it to the person you '
  + 'called — because it arrived on their channel. If you start listening for an answer the '
  + 'moment you request playback, the first thing you "hear" is yourself.'));

children.push(p(
  'The fix is to wait for the PlayCompleted callback and then a further 900 milliseconds for the '
  + 'echo to drain, and to discard any reply that is mostly words from the question just asked. '
  + 'Both parts are needed. I will return to that second part, because it later ate a real '
  + 'answer from a real user.'));

children.push(h2('4. amr: three rounds with a claim that will not do what you want'));

children.push(p(
  'Registering a voiceprint should require MFA — otherwise a stolen password lets somebody bind '
  + 'their own voice to your account, and unlike a password you cannot change your voice '
  + 'afterwards. The obvious way to check is the amr claim, which lists the authentication '
  + 'methods used. Three attempts, three different walls:'));

children.push(bullet(
  'Round one: I added amr as an optional claim on the access token. Entra accepts this, stores '
  + 'it, shows it in the manifest — and ignores it. amr is not deliverable in an access token. The '
  + 'check looked configured and could never have passed.'));
children.push(bullet(
  'Round two: I moved it to the ID token, which does carry amr, and validated that token '
  + 'server-side. Correct — but I was also still sending a claims challenge asking for amr.'));
children.push(bullet(
  'Round three: that challenge broke sign-in entirely.'));

children.push(code([
  'AADSTS901001: The \'amr\' values request parameter value \'Empty\' is invalid.',
]));

children.push(rich([
  'amr is not a requestable claim. The claims parameter exists for ',
  { t: 'acrs', code: true },
  ' — Conditional Access authentication context — and asking it for amr produces a request Entra '
  + 'cannot parse. It refuses the sign-in before it starts. The fix was to delete the challenge '
  + 'entirely: amr arrives because it is an optional claim on the ID token, and ',
  { t: 'prompt=login', code: true },
  ' is what makes it describe the sign-in that just happened.']));

children.push(callout(
  'What I would tell my past self',
  'A claim being accepted by the registration UI is not evidence it will ever be issued. I '
  + 'burned three of my tester’s sign-in attempts on a check that was unsatisfiable by '
  + 'construction, and I could not test it myself because I cannot mint a real Entra token.',
  RED));

children.push(h2('5. The data collection rule that quietly ate my telemetry'));

children.push(p(
  'This is my favourite one, in the way that only a bug you have finished fixing can be a '
  + 'favourite. I had a voice score being computed on every call, written to a Log Analytics '
  + 'custom table, and displayed on a dashboard tile that read zero. Forever.'));

children.push(rich([
  'The table had the columns. The service was sending them. Ingestion returned success. But the '
  + 'data collection rule’s ',
  { t: 'streamDeclarations', code: true },
  ' block did not list them — and the Logs Ingestion API ',
  { t: 'silently drops any column a stream does not declare', b: true },
  '. No error, no warning, no partial-success response. Just an empty column.']));

children.push(p(
  'The whole point of that column was to accumulate real scores so I could replace my synthetic '
  + 'thresholds with measured ones. That dataset never existed. The comment above the DCR in my '
  + 'own Bicep file said "streamDeclarations must mirror the table schemas exactly". It had '
  + 'drifted anyway.'));

children.push(h2('6. Four different ways a deployment can lie to you'));

children.push(p(
  'I claimed something was fixed and deployed, three separate times, and was wrong each time. '
  + 'Not because the code was wrong — because the code was not running.'));

children.push(table(
  ['What happened', 'Why nothing looked wrong'],
  [
    ['A comment inside a backslash-continued az command silently ended it',
     'Every earlier stage printed success; set -e aborted the rest'],
    ['A compile error broke the image build',
     'Health checks stayed green — the old container was perfectly healthy'],
    ['An invalid --no-cache flag meant no image was ever pushed',
     'Revisions failed to pull and traffic fell back to the previous one'],
    ['A draining revision answered probes after the new tag went live',
     'The tag matched. The code answering did not.'],
  ],
  [4400, 4960]));

children.push(rich([
  'The habit I ended up with: never trust an image tag, never trust a green health check, and '
  + 'never trust a serving revision. Call the endpoint whose ',
  { t: 'behaviour', i: true },
  ' changed and assert the new value, two or three times a few seconds apart. I eventually built '
  + 'this into the product as ',
  { t: '/api/build', code: true },
  ', which reports what is compiled in rather than what was requested.']));

children.push(h2('7. A genuine speaker scored 0.07 against their own voiceprint'));

children.push(p(
  'My calibration run put genuine speakers between 0.65 and 0.88, and impostors below 0.30. My '
  + 'tester — the enrolled user, on their own phone, saying their own words — scored 0.0004, then '
  + '0.071. And because I had enforcement switched on at their request, it refused them access to '
  + 'their own account.'));

children.push(...figure('fig3-voice-bug',
  'Figure 3 — The buffer was never cleared on a verification call, so most of what got compared was our own text-to-speech.'));

children.push(p(
  'The audio buffer filled from the moment the media socket opened. On a verification call that '
  + 'is a minute of prompts, questions and retries — all of which echo back on the caller’s '
  + 'channel, which is mapped to the caller, because it is theirs. So the comparison was mostly '
  + 'the enrolled template against synthesised speech. The model was not wrong. It correctly '
  + 'reported a different speaker, because a different speaker is what it was given.'));

children.push(p(
  'The enrolment path had solved this months earlier — it clears the buffer before each recording '
  + 'window, with a comment explaining exactly this failure. The verification path never got the '
  + 'same treatment. Two more faults were hiding underneath: the 60-second cap dropped new frames '
  + 'once full, so it kept the prompts and discarded the answers; and scoring only ran from one '
  + 'call site, so a verification that passed on the code alone compared nothing at all.'));

children.push(h2('8. The echo guard started eating real answers'));

children.push(p(
  'Remember the echo defence from hiccup three — discard any reply that is mostly words from the '
  + 'question. Here is the question, and here is a completely honest answer to it:'));

children.push(code([
  'Q: "Which town, city, or country were you in the last time you signed in?"',
  'A: "I signed in from Kuala Lumpur last time"',
  '',
  'words in the answer: signed, from, kuala, lumpur, last, time   (6)',
  'words also in the question: signed, last, time                 (3)',
  '3 of 6 = 50%  ->  discarded as an echo',
]));

children.push(p(
  'People answer in the question’s own words. That is how conversation works. The user was '
  + 'told nothing had been heard, spent both attempts that way, and was refused — having answered '
  + 'correctly, twice. The rule now requires the reply to contain at least two words the question '
  + 'does not have, because an echo cannot introduce content the question never contained.'));

children.push(h2('9. The risk score that was always zero'));

children.push(p(
  'The coercion analyst was running the whole time and scoring correctly. Its output was copied '
  + 'onto the verification record in exactly one place: during keypad entry, a few seconds into '
  + 'the call, before the caller had said a word. The later re-checks — after the spoken questions, '
  + 'which is precisely where coaching would be audible — computed a score and recorded nothing.'));

children.push(p(
  'So the product’s headline claim was unevidenced in its own audit trail. Every row said '
  + 'risk zero.'));

children.push(h2('10. Four smaller ones, quickly'));

children.push(bullet(
  'Every single sign-in was filtered out of the telemetry questions, because the filter excluded '
  + 'apps beginning "EntraGuard" — and in a tenant used to demo EntraGuard, that is all of them. '
  + '25 returned, 25 discarded, zero questions built, silent fallback to a stored question. The '
  + 'user reasonably concluded I had deleted the feature.'));
children.push(bullet(
  'One of my three questions asked for a different city than the previous answer. Entra records '
  + 'the city an IP resolves to, and one desk resolves to several neighbouring cities across a '
  + 'week, so "a different city" is usually the same place under another name. Unanswerable, so it '
  + 'was asked again, verbatim.'));
children.push(bullet(
  'The infrastructure script defaults all three container apps to the Azure hello-world image. '
  + 'Running it out of habit would have wiped a working deployment.'));
children.push(bullet(
  'The verification list endpoint returned the live two-digit match code, unauthenticated, for '
  + 'every in-flight attempt — with the target’s UPN. A code comment three lines away '
  + 'asserted that the code is never transmitted to the device.'));

// ── What I got wrong about the design ──────────────────────────────────────
children.push(new Paragraph({ children: [new PageBreak()] }));
children.push(h1('What I got wrong about the design'));

children.push(p(
  'Voice biometrics is the part of this project everybody asks about, and after building it I '
  + 'think it is the weakest component in the system.'));

children.push(...figure('fig4-factors',
  'Figure 4 — Ranked by how well each factor resists a cloned voice. The ordering surprised me.'));

children.push(rich([
  'The number match is the strong one, and it is strong for an unglamorous reason: ',
  { t: 'a cloned voice cannot see the screen', b: true },
  '. The telemetry questions are strong because the answers come from activity minutes old and '
  + 'cannot be researched. The voiceprint is the part that degrades over a phone codec, has no '
  + 'anti-spoofing whatsoever, and would be defeated by a good clone.']));

children.push(p(
  'SpeechBrain’s ECAPA-TDNN is a speaker verification model. It is not a liveness detector '
  + 'and does not claim to be. A recording of the enrolled speaker scores as the enrolled speaker, '
  + 'because it is one. Whatever replay resistance the system has comes from the questions being '
  + 'unpredictable, not from anything in the audio.'));

children.push(p(
  'Which leads somewhere slightly uncomfortable for a hackathon pitch. Banks have had voice '
  + 'biometrics for a decade and several are quietly retiring it because of cloning. The genuinely '
  + 'novel thing here is not that EntraGuard recognises your voice. It is that it listens to '
  + 'whether somebody is talking you into it.'));

// ── Supportability ──────────────────────────────────────────────────────────
children.push(h1('The pattern behind almost all of it'));

children.push(...figure('fig5-supportability',
  'Figure 5 — Four unrelated bugs, one shape: it worked, with a weaker mechanism, and nothing said so.'));

children.push(p(
  'Look back at the list and most of the expensive ones are the same bug wearing different '
  + 'clothes. The system did its job using a weaker mechanism than intended, and there was no '
  + 'signal anywhere that it had done so. The call still completed. The user still got an answer. '
  + 'Nothing in the response distinguished the strong path from the weak one.'));

children.push(p(
  'A missing telemetry column and a quiet week look identical on a dashboard. A verification that '
  + 'never compared a voice and one that compared a voice and scored zero were the same row. '
  + 'Falling back from live telemetry to a stored security question — a materially weaker '
  + 'authentication — was one Information-level log line on one replica.'));

children.push(rich([
  'So I added a type. A fault carries four things and none of them is optional: what failed, what '
  + 'the ',
  { t: 'user', b: true },
  ' experienced, the probable cause, and the next action. If I cannot fill in the last three, the '
  + 'condition is probably not worth recording. The most important severity is not "broken" — it '
  + 'is "degraded", which means it worked and you should know how.']));

children.push(code([
  'FAULT telemetry.signins_unavailable [Telemetry/Degraded]',
  '  failed : Entra sign-in logs could not be read; no live questions were built.',
  '  impact : The caller was asked only their stored security question - a secret',
  '           an attacker can research - instead of facts from their own activity.',
  '  likely : Tenant has not consented to AuditLog.Read.All, or has no Entra ID P1.',
  '  do     : GET /api/verify/telemetry-probe/{tenantId}/{objectId}',
]));

children.push(p(
  'Alongside that, a small circuit breaker around the one dependency that genuinely falls over — '
  + 'the model sidecar — which sheds calls after repeated failure and reopens on the next success. '
  + 'The shedding is the self-healing part, not the retry. Retrying survives a dropped packet; '
  + 'shedding stops a dead dependency turning every subsequent call into a timeout, with nobody '
  + 'paged and nothing restarted.'));

children.push(callout(
  'The self-test that immediately earned its keep',
  'There is one check nothing else can make: does the reporting path itself work? Everything '
  + 'reports failures through the fault recorder, so if the recorder is broken the system goes '
  + 'quiet in exactly the way it would if all were well. I added an endpoint that raises a '
  + 'deliberate test fault and tells you where to look for it. The first time I ran it, it caught '
  + 'a real gap between the in-memory buffer and Sentinel.',
  GREEN));

// ── Standards ───────────────────────────────────────────────────────────────
children.push(h1('Where this sits against the standards'));

children.push(p(
  'Worth writing down, because "you are doing biometrics" invites a specific set of questions and '
  + 'it is better to have answers than opinions.'));

children.push(table(
  ['Standard', 'What it requires', 'Where EntraGuard stands'],
  [
    ['NIST SP 800-63B-4 (final, July 2025)',
     'Biometrics must be paired with a possession factor; PAD mandatory at AAL3; FMR 1 in 1000; alternatives for users who cannot enrol',
     'Possession pairing and alternatives: met. Presentation attack detection: not implemented. FMR on real telephony: unmeasured.'],
    ['ISO/IEC 30107-3:2023',
     'The testing and reporting standard for presentation attack detection',
     'Not certified, and I use its vocabulary rather than inventing my own. Stated plainly rather than glossed.'],
    ['GDPR Article 9',
     'A voiceprint used to identify is special-category data; explicit, versioned, revocable consent',
     'Versioned consent recorded, immediate user-initiated deletion, raw audio never stored — only a 192-dimension template.'],
    ['EU AI Act (biometric provisions from 2 August 2026)',
     'Distinguishes 1-to-many identification (high risk) from 1-to-1 verification',
     'One-to-one verification against the already-identified account, so outside the high-risk category — but still fully subject to Article 9.'],
  ],
  [2200, 3400, 3760]));

children.push(p(
  'The honest summary is that two of those rows contain a "not". I would rather write that down '
  + 'than have someone find it.', { after: 240 }));

// ── What next ───────────────────────────────────────────────────────────────
children.push(h1('What I would do differently, and what is next'));

children.push(p(
  'If I started again tomorrow, the first thing I would build is not the call. It would be the '
  + 'fault type and the build-provenance endpoint. Nearly every day I lost was spent establishing '
  + 'facts a well-instrumented system would have handed me in a single request: is my code '
  + 'running, did that mechanism actually fire, and if it fell back, to what.'));

children.push(p(
  'The second thing is a discipline rather than a component. Ship every new decision in observe '
  + 'mode first — computing, recording, and changing nothing — until real data justifies the '
  + 'thresholds. I did that for the risk score and it was clearly right. I did not do it for voice '
  + 'enforcement, and it locked my own tester out of his account using numbers measured on '
  + 'synthesised speech.'));

children.push(p('Still on the list:'));
children.push(bullet(
  'Challenge-response liveness — a phrase generated during the call, which no pre-recorded clone '
  + 'can have prepared. This is more valuable than any detector, and I had it backwards: '
  + 'randomised phrases are currently used at enrolment, the flow where replay matters least.'));
children.push(bullet(
  'A presentation attack detection model as a second head on the existing sidecar, reported as a '
  + 'separate signal. With the caveat that ASVspoof-trained detectors degrade badly over telephony '
  + 'codecs — it is a layer, not a solution.'));
children.push(bullet(
  'Thresholds derived from real calls rather than synthesised voices. Until then voice stays in '
  + 'observe mode, where it records and refuses nobody.'));

children.push(callout(
  'If you take one thing from this',
  'Build the thing that tells you when your system quietly did something weaker than you '
  + 'intended. Not the thing that tells you when it crashed — crashes announce themselves. The '
  + 'expensive failures are the ones where everything looks fine, because from the outside, '
  + 'everything does.',
  BLUE));

children.push(p(
  'Happy to answer questions in the comments, particularly on the ACS and Teams federation parts '
  + '— that is where the documentation is thinnest and where I would have most liked to find a '
  + 'post like this one.', { after: 200 }));

children.push(new Paragraph({
  spacing: { before: 200 },
  border: { top: { style: BorderStyle.SINGLE, size: 6, color: RULE, space: 10 } },
  children: [new TextRun({
    text: 'Built on Azure Communication Services, Microsoft Entra ID, Azure OpenAI, Azure '
        + 'Container Apps, Microsoft Sentinel and SpeechBrain (Apache 2.0). Names and figures in '
        + 'this post are from the running system.',
    size: 17, color: MUTED, italics: true,
  })],
}));

// ── Document ────────────────────────────────────────────────────────────────
const doc = new Document({
  creator: 'Suzaril Shah',
  title: 'I built an MFA factor that listens for the scam',
  description: 'EntraGuard — voice verification and coercion detection on Azure',
  numbering: {
    config: [{
      reference: 'dots',
      levels: [{
        level: 0,
        format: LevelFormat.BULLET,
        text: '•',
        alignment: AlignmentType.LEFT,
        style: { paragraph: { indent: { left: convertInchesToTwip(0.3), hanging: convertInchesToTwip(0.18) } } },
      }],
    }],
  },
  styles: {
    default: {
      document: { run: { font: 'Segoe UI', size: 21, color: INK } },
    },
  },
  sections: [{
    properties: {
      page: { margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } },
    },
    children,
  }],
});

Packer.toBuffer(doc).then((buf) => {
  const out = path.join(__dirname, 'EntraGuard-blog.docx');
  fs.writeFileSync(out, buf);
  console.log(`wrote ${out} (${(buf.length / 1024).toFixed(0)} KB)`);
});
