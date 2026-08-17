#!/usr/bin/env bash
set -e
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
cd ~/eg-blog-build/figures
for f in *.svg; do
  base="${f%.svg}"
  W=$(grep -o 'width="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*')
  H=$(grep -o 'height="[0-9]*"' "$f" | head -1 | grep -o '[0-9]*')
  # Natural size window; the scale factor alone supplies the extra pixels. Doubling the
  # window as well produced a canvas twice the drawing and a page of white space under it.
  "$CHROME" --headless --disable-gpu --no-sandbox --hide-scrollbars \
    --screenshot="$base.png" --window-size=$W,$H --force-device-scale-factor=2 \
    --default-background-color=FFFFFFFF "file://$PWD/$f" >/dev/null 2>&1
  echo "$base.png  $(sips -g pixelWidth -g pixelHeight "$base.png" 2>/dev/null | tail -2 | tr -d ' \n')"
done
