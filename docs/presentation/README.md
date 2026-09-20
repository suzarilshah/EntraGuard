# EntraGuard hackathon presentation

**Six slides, approximately five minutes.** Designed for a mixed business/technical judging panel.

## Presentation files

- `EntraGuard-Hackathon.pptx` — editable widescreen PowerPoint, with speaker notes.
- `EntraGuard-Hackathon.pdf` — shareable slide preview.
- `preview.png` — overview of all six slides.
- `speaker-notes.md` — timed talk track, demo cues and technical Q&A boundaries.

## Story

1. **The hook:** the code can be right while the situation is wrong.
2. **The problem:** impersonation, MFA coaching and payment pressure.
3. **The solution:** one verification call, multiple signals, governed decisions.
4. **Integration:** application API or optional Entra EAM; ACS/Teams, Speech, OpenAI, Graph and Sentinel.
5. **Product and differentiation:** Treasury, policy-controlled authority, transaction binding and durable evidence.
6. **The ask:** a controlled tenant pilot with an identity/SOC partner.

The deck deliberately avoids unverified breach/ROI/accuracy claims. Demo payments are not bank transfers. Voice comparison is optional and observation is the default; it is not voice-clone detection. EAM is opt-in and requires tenant configuration/live validation.

The screenshot is a capture of the Treasury prototype using illustrative financial data. It is a product-design visual, not evidence that a payment was executed.

## Presenting

- Use **Presenter View** to see the embedded notes.
- For a five-minute slot, use the deck alone or a short prepared demo clip. A full live verification call can exceed the time allocated to slide 5.
- For a longer slot, demonstrate both a benign control and a coached scenario, then inspect the actual result/receipt.
- Test the QR code and any live endpoints shortly before presenting.
- Keep “implemented,” “configured” and “live-validated” distinct when answering questions.

## Rebuilding

The presentation generator is isolated from the application. Install `pptxgenjs@4.0.1`, `sharp` and `qrcode` into a separate npm prefix, then run:

```bash
node docs/presentation/build.mjs --tools /path/to/presentation-tool-prefix
```

To replace the prototype screenshot, add `--screenshot /absolute/path/to/full-page-treasury.png`. The generator crops the top overview region and stores the presentation asset locally. The handbook QR code points to `https://docs.entraguard.my`.

Shapes, text and integration diagrams are editable PowerPoint objects. Only the UI screenshot and QR code are raster images. Standard Arial typography avoids a custom-font installation requirement.

On macOS with Keynote installed, export the generated deck with:

```bash
osascript docs/presentation/export.applescript /absolute/path/to/EntraGuard-Hackathon.pptx /absolute/path/to/EntraGuard-Hackathon.pdf
```

Then run `render.py` in a Python environment containing `pymupdf` and `pillow` to check all six pages and regenerate the slide images/contact sheet. The supplied PDF was exported from the PPTX through Keynote; it is not a separately recreated design.

`deck-manifest.json` records source geometry/text for layout validation. The PPTX includes source references and caveats in its speaker notes; these do not clutter the projected slides.
