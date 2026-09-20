"""Validate the editable PPTX and inspect its actual Keynote-exported PDF."""
from pathlib import Path
import json
import re
import zipfile
import xml.etree.ElementTree as ET
import pymupdf as fitz

here = Path(__file__).resolve().parent
manifest = json.loads((here / 'slide-manifest.json').read_text())
with zipfile.ZipFile(here / 'EntraGuard-115s-Storyboard.pptx') as archive:
    slides = [n for n in archive.namelist() if re.fullmatch(r'ppt/slides/slide\d+\.xml', n)]
    notes = [n for n in archive.namelist() if re.fullmatch(r'ppt/notesSlides/notesSlide\d+\.xml', n)]
    assert len(slides) == len(notes) == len(manifest) == 6
    for name in slides + notes:
        ET.fromstring(archive.read(name))

pdf = fitz.open(here / 'EntraGuard-115s-Storyboard.pdf')
assert len(pdf) == 6
destination = here / 'pptx-render'
destination.mkdir(exist_ok=True)
report = []
for i, page in enumerate(pdf):
    assert abs(page.rect.width / page.rect.height - 16 / 9) < 0.01
    extracted = re.sub(r'\s+', '', page.get_text())
    missing = [item['text'] for item in manifest[i]['elements']
               if item['kind'] == 'text' and re.sub(r'\s+', '', item['text']) not in extracted]
    page.get_pixmap(matrix=fitz.Matrix(2, 2), alpha=False).save(destination / f'slide-{i+1:02}.png')
    expected_images = sum(item['kind'] == 'image' for item in manifest[i]['elements'])
    rendered_images = len(page.get_images(full=True))
    assert rendered_images >= expected_images, f'Slide {i+1}: image asset missing from exported PDF'
    report.append({'slide': i + 1, 'missingText': missing, 'aspectRatio': page.rect.width / page.rect.height,
                   'expectedImages': expected_images, 'renderedImages': rendered_images})
(here / 'deck-validation.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
assert not any(page['missingText'] for page in report), 'Inspect missing text in exported slide render.'
