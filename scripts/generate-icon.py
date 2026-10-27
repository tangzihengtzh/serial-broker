"""Render Serial Broker's source-controlled connector icon; Pillow is only needed to regenerate assets."""
from pathlib import Path
from PIL import Image, ImageDraw

target = Path(__file__).resolve().parents[1] / "ui/SerialBroker.Gui/Assets"
target.mkdir(parents=True, exist_ok=True)
pins = [(x, 103) for x in (80, 104, 128, 152, 176)] + [(x, 128) for x in (92, 116, 140, 164)]
circles = "".join(f'<circle cx="{x}" cy="{y}" r="6" fill="#A5ECF5"/>' for x, y in pins)
svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<rect x="8" y="8" width="240" height="240" rx="56" fill="#226B80"/>
<path d="M60 72H196L182 156H74Z" fill="#174E60" stroke="white" stroke-width="12" stroke-linejoin="round"/>
{circles}
<path d="M128 162V185M86 207V185H170V207" fill="none" stroke="#A5ECF5" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/>
<circle cx="86" cy="207" r="9" fill="white"/><circle cx="170" cy="207" r="9" fill="white"/>
</svg>'''
(target / "SerialBroker.svg").write_text(svg, encoding="utf-8")
s = 4
im = Image.new("RGBA", (256*s, 256*s))
d = ImageDraw.Draw(im)
d.rounded_rectangle((8*s, 8*s, 248*s, 248*s), radius=56*s, fill="#226B80")
outer = [(60,72), (196,72), (182,156), (74,156)]
d.polygon([(x*s,y*s) for x,y in outer], fill="#174E60")
d.line([(x*s,y*s) for x,y in outer + [outer[0]]], fill="white", width=12*s, joint="curve")
for x,y in outer:
    d.ellipse(((x-6)*s,(y-6)*s,(x+6)*s,(y+6)*s),fill="white")
for x,y in pins:
    d.ellipse(((x-6)*s,(y-6)*s,(x+6)*s,(y+6)*s),fill="#A5ECF5")
for points in [[(128,162),(128,185)], [(86,207),(86,185),(170,185),(170,207)]]:
    d.line([(x*s,y*s) for x,y in points],fill="#A5ECF5",width=10*s,joint="curve")
for x in (86,170):
    d.ellipse(((x-9)*s,198*s,(x+9)*s,216*s),fill="white")
im = im.resize((256,256),Image.Resampling.LANCZOS)
im.save(target / "SerialBroker.png")
im.save(target / "SerialBroker.ico",sizes=[(n,n) for n in (16,20,24,32,40,48,64,128,256)])
print("Generated connector SVG, PNG and multiresolution ICO.")
