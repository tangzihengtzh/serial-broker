"""Regenerate offline HTML help. Requires Python-Markdown only at development time."""
from pathlib import Path
import markdown

root = Path(__file__).resolve().parents[1]
body = markdown.markdown((root / "docs/V2-USER-GUIDE.md").read_text(encoding="utf-8"), extensions=["fenced_code", "tables"])
css = """body{font:16px/1.7 'Segoe UI','Microsoft YaHei UI',sans-serif;max-width:960px;margin:40px auto;padding:0 24px;color:#253447;background:#f5f7fb}h1,h2{line-height:1.3;color:#226b80}h2{margin-top:40px}pre,table{background:#fff;border:1px solid #dce4ec;border-radius:10px}pre{padding:16px;overflow:auto}code{font-family:Consolas,monospace;font-size:.92em}table{border-collapse:collapse;width:100%}th,td{padding:10px;border:1px solid #dce4ec;text-align:left}a{color:#226b80}@media(prefers-color-scheme:dark){body{background:#171b24;color:#e7ecf5}pre,table{background:#212735;border-color:#364156}td,th{border-color:#364156}h1,h2,a{color:#8dd1e0}}"""
(root / "docs/V2-USER-GUIDE.html").write_text(f'<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Serial Broker V2.0.0 使用指南</title><style>{css}</style><body>{body}</body></html>\n', encoding="utf-8")
print("Generated offline HTML help.")
