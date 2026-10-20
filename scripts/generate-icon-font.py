"""Build our bundled compatibility font from MIT-licensed Microsoft Fluent icons.

Requires fonttools==4.67.0. Segoe's proprietary font is never an input.
"""
import hashlib
import json
from pathlib import Path
from urllib.request import urlretrieve
from fontTools import subset
from fontTools.merge import Merger
from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parent.parent
COMMIT = "83b26348c811f072429427ff48362238656524fe"
BASE = f"https://raw.githubusercontent.com/microsoft/fluentui-system-icons/{COMMIT}"
CACHE = ROOT / "tools" / "fluent-font"
OUTPUT = ROOT / "NetEaseNEXT" / "Assets" / "Fonts" / "NetEaseNEXTIcons.ttf"
LICENSE = ROOT / "third-party" / "fluent-system-icons"
mapping = json.loads((ROOT / "scripts" / "icon-font-map.json").read_text(encoding="utf-8"))
CACHE.mkdir(parents=True, exist_ok=True)
parts = []
sources = {}
for style in ("Regular", "Filled"):
    filename = f"FluentSystemIcons-{style}"
    for extension in ("ttf", "json"):
        path = CACHE / f"{filename}.{extension}"
        if not path.exists():
            urlretrieve(f"{BASE}/fonts/{path.name}", path)
    font_path = CACHE / f"{filename}.ttf"
    sources[font_path.name] = hashlib.sha256(font_path.read_bytes()).hexdigest()
    names = json.loads((CACHE / f"{filename}.json").read_text(encoding="utf-8"))
    font = TTFont(font_path, recalcTimestamp=False)
    original = font.getBestCmap()
    cmap = {}
    for code, name in mapping.items():
        if name.endswith("_" + style.lower()):
            cmap[int(code, 16)] = original[names["ic_fluent_" + name]]
    # Preserve legacy Symbol enum aliases used by native WinUI templates.
    aliases = {0xE10F: 0xE80F, 0xE1D3: 0xE8F1, 0xE189: 0xE8D6,
               0xE125: 0xE77D, 0xE142: 0xE770, 0xE115: 0xE713}
    for alias, target in aliases.items():
        if target in cmap:
            cmap[alias] = cmap[target]
    for table in font["cmap"].tables:
        if table.isUnicode():
            table.cmap = cmap.copy()
    options = subset.Options()
    options.name_IDs = ["*"]
    options.name_legacy = True
    options.name_languages = ["*"]
    job = subset.Subsetter(options=options)
    job.populate(unicodes=cmap)
    job.subset(font)
    part = CACHE / f"subset-{style}.ttf"
    font.save(part)
    parts.append(str(part))
result = Merger().merge(parts)
# Subsetting recalculates real outline bounds. The upstream fonts have zero
# left bearings with padded bounds; retaining those bearings shifts WinUI's
# visible glyphs left by a different amount for each icon. Restore the design
# tile origin, rather than compensating each button with a margin.
for glyph_name in result.getGlyphOrder():
    glyph = result["glyf"][glyph_name]
    glyph.recalcBounds(result["glyf"])
    advance, _ = result["hmtx"][glyph_name]
    result["hmtx"][glyph_name] = (advance, getattr(glyph, "xMin", 0))
result["OS/2"].usWinAscent = result["head"].unitsPerEm
result["OS/2"].usWinDescent = 0
for record in result["name"].names:
    if record.nameID in (1, 4, 6, 16):
        name = "NetEaseNEXTIcons" if record.nameID == 6 else "NetEaseNEXT Icons"
        record.string = name.encode(record.getEncoding())
    elif record.nameID in (2, 17):
        record.string = "Regular".encode(record.getEncoding())
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
result.save(OUTPUT)
LICENSE.mkdir(parents=True, exist_ok=True)
urlretrieve(f"{BASE}/LICENSE", LICENSE / "LICENSE.txt")
(LICENSE / "source.json").write_text(json.dumps({"repository": "https://github.com/microsoft/fluentui-system-icons",
    "commit": COMMIT, "sources": sources, "fonttools": "4.67.0",
    "generated_sha256": hashlib.sha256(OUTPUT.read_bytes()).hexdigest()}, indent=2) + "\n", encoding="utf-8")
print(f"Generated {OUTPUT}: {len(result.getBestCmap())} glyph mappings, {OUTPUT.stat().st_size} bytes")
