# The Tech Community write-up

`EntraGuard-blog.docx` is the article. Everything needed to rebuild it lives here, because a
Word file nobody can regenerate is a dead end the first time a figure needs a correction.

| File | What it is |
|---|---|
| `EntraGuard-blog.docx` | The article. ~4,200 words, five figures, a table of contents. |
| `diagrams.py` | Draws the five figures as SVG. Hand-built rather than a diagramming library: every box is a real component and every arrow a real protocol. |
| `render.sh` | SVG → PNG at 2× through headless Chrome. Uses the SVG's natural window size — doubling the window as well leaves a page of white space under each figure. |
| `build.js` | Assembles the `.docx` with the `docx` npm package. Figure heights are read from each PNG's IHDR chunk rather than guessed. |
| `figures/` | Both the SVG sources and the rendered PNGs. |

## Rebuilding

```bash
python3 diagrams.py && ./render.sh && node build.js
```

`docx` is not vendored. Install it into a scratch directory first; this machine's npm cache
needed `--cache /tmp/npmcache` to get past a permissions conflict.

## A note on the figures

Figure 4 ranks the authentication factors with a strength bar. Those numbers are engineering
judgement, not measurements, and the figure says so — the only number in it that came from an
experiment is the voice one, and it is the weakest of the five.
