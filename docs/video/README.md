# EntraGuard — two-minute hackathon video production kit

**Recommended format:** 70 seconds of real demonstration supported by an 18-second opening, a 17-second architecture explanation and a 10-second close. Total **1:55**, leaving five seconds beneath the submission limit.

## Review files

- `index.html` — local review page with the video player, voiceover and six-scene storyboard.
- `EntraGuard-115s-storyboard-preview.mp4` — narrated, captioned **storyboard**, with a persistent preview label. The demo sections are placeholders; this is not the final submission.
- `EntraGuard-115s-Storyboard.pptx` — six editable slides with timed speaker notes and replacement cues.
- `EntraGuard-115s-Storyboard.pdf` — actual presentation export, checked for missing text and image assets.
- `preview.png` — slide contact sheet.
- `slides/` — standalone SVG and 1920×1080 PNG slide assets.
- `narration.wav` — 115-second timeline with synthetic narration and room for original demo audio.
- `captions.srt` — narration captions; timestamps are estimated within each measured speech cue. Original call audio needs its own captions in the final cut.
- `script-and-edit-plan.md` — full script, recording list and editor instructions.
- `narration-timing.json` / `validation.json` — measured cue durations, video specifications and checks.

The numbered demo slides are **editorial placeholders**. Replace them with the actual captured flow and result. The UI screenshot comes from `docs/presentation/assets/treasury-workspace.png`; it shows illustrative Treasury data and is not a recording of this verification.

## Footage needed to finish

Send the original demo video or its local path. The ideal package contains a normal verification and a controlled coaching test, with call audio and the actual outcome/reason. Longer unedited recordings are useful: the edit can remove waiting and keep the strongest evidence. Add any mandatory hackathon judging requirements or team credits.

Your own clear narration is a strong final option. The supplied synthetic voice is a review track you can keep or replace after listening. It does not imitate a participant or create simulated call evidence. No background music is included, so the call can remain intelligible.

## Export target

- 1920×1080, landscape 16:9, 30 fps.
- H.264, yuv420p, fast-start MP4; AAC audio at 48 kHz.
- Target narration mix: approximately −16 LUFS, true peak no higher than −1.5 dBTP.
- Caption-safe layout and persistent storyboard disclosure in the preview.
- 115 seconds; export validation fails if the result exceeds 120 seconds.

Professional video finish is not a claim of application production certification. Claims and planned shots are grounded in the repository implementation; actual demo outcomes must be checked when footage is supplied.

## Rebuild

Use the same isolated presentation-tool prefix as `docs/presentation/build.mjs` (PptxGenJS and sharp):

```sh
node docs/video/build.cjs --tools /absolute/path/to/presentation-tool-prefix
python3 docs/video/build-media.py --work /absolute/path/to/temporary-work-directory
```

The media build uses local macOS `say` (Daniel), FFmpeg and FFprobe. It makes no cloud API calls. Content and timings are in `storyboard.json`. The final SVGs embed original Microsoft architecture icons; [asset credits](../diagrams/azure-icons/README.md) apply.
