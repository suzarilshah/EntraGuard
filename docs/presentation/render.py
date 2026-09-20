"""Validate PPTX structure, render an exported PDF, and create a six-slide contact sheet.

Run in an isolated Python environment with pymupdf and pillow installed.
The PDF should be exported by Keynote or PowerPoint, not recreated as a separate design.
"""
from pathlib import Path
import json
import re
import zipfile
import xml.etree.ElementTree as ET
import pymupdf as fitz
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent
manifest = json.loads((root / 'deck-manifest.json').read_text())
ns = {'a': 'http://schemas.openxmlformats.org/drawingml/2006/main'}
with zipfile.ZipFile(root / 'EntraGuard-Hackathon.pptx') as archive:
    slides = sorted(name for name in archive.namelist() if re.fullmatch(r'ppt/slides/slide\d+\.xml', name))
    notes = [name for name in archive.namelist() if re.fullmatch(r'ppt/notesSlides/notesSlide\d+\.xml', name)]
    assert len(slides) == len(notes) == 6, (len(slides), len(notes))
    for name in slides + notes:
        ET.fromstring(archive.read(name))

pdf = fitz.open(root / 'EntraGuard-Hackathon.pdf')
assert len(pdf) == 6
thumbs = []
report = []
render_dir = root / 'slides'
render_dir.mkdir(exist_ok=True)
for index, page in enumerate(pdf):
    pix = page.get_pixmap(matrix=fitz.Matrix(1.6, 1.6), alpha=False)
    output = render_dir / f'slide-{index + 1:02}.png'
    pix.save(output)
    image = Image.open(output).convert('RGB')
    image.thumbnail((960, 540), Image.Resampling.LANCZOS)
    thumbs.append(image.copy())
    extracted = ' '.join(page.get_text().split())
    missing = []
    for element in manifest['slides'][index]['elements']:
        if element['kind'] != 'text':
            continue
        expected = ' '.join(element['text'].split())
        # Tracking/character spacing can be represented as extra spaces in PDF extraction.
        if expected not in extracted and re.sub(r'\s+', '', expected) not in re.sub(r'\s+', '', extracted):
            missing.append(expected)
    report.append({'slide': index + 1, 'text_to_inspect': missing})

sheet = Image.new('RGB', (1992, 1776), '#e5e9e1')
draw = ImageDraw.Draw(sheet)
for index, image in enumerate(thumbs):
    x = 24 + (index % 2) * 984
    y = 24 + (index // 2) * 584
    sheet.paste(image, (x, y))
    draw.text((x + 4, y + 550), f"{index + 1:02}  {manifest['slides'][index]['title']}", fill='#17382f')
sheet.save(root / 'preview.png')
(root / 'validation.json').write_text(json.dumps(report, indent=2))
print('Validated: 6 slides, 6 speaker-note parts, 6 exported PDF pages.')
print(json.dumps(report, indent=2))
