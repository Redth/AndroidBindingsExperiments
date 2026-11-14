import hashlib
import json
from html.parser import HTMLParser
from pathlib import Path
import urllib.request

ROOT = Path(__file__).parent
PAGES = {
    "lifecycle": "https://developer.android.com/jetpack/androidx/releases/lifecycle",
    "collection": "https://developer.android.com/jetpack/androidx/releases/collection",
    "activity": "https://developer.android.com/jetpack/androidx/releases/activity",
    "navigation": "https://developer.android.com/jetpack/androidx/releases/navigation",
    "savedstate": "https://developer.android.com/jetpack/androidx/releases/savedstate",
    "gms-releases": "https://developers.google.com/android/guides/releases",
    "gms-versioning": "https://developers.google.com/android/guides/versioning",
    "androidx-versioning": "https://developer.android.com/jetpack/androidx/versions",
}


class Text(HTMLParser):
    def __init__(self):
        super().__init__()
        self.parts = []
        self.hidden = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style"):
            self.hidden += 1
        if tag in ("h1", "h2", "h3", "h4", "p", "li", "tr", "div", "pre"):
            self.parts.append("\n")
        if tag in ("h1", "h2", "h3", "h4"):
            identifier = dict(attrs).get("id", "")
            self.parts.append(f"\n[{identifier}] ")

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.hidden -= 1
        if tag in ("p", "li", "h1", "h2", "h3", "h4"):
            self.parts.append("\n")

    def handle_data(self, value):
        if not self.hidden:
            self.parts.append(value)


manifest = {}
for name, url in PAGES.items():
    raw = ROOT / (name + ".html")
    if not raw.exists():
        with urllib.request.urlopen(url, timeout=120) as response:
            raw.write_bytes(response.read())
    parser = Text()
    parser.feed(raw.read_text())
    text = "\n".join(line.strip() for line in "".join(parser.parts).splitlines() if line.strip())
    (ROOT / (name + ".txt")).write_text(text)
    manifest[name] = {"url": url, "sha256": hashlib.sha256(raw.read_bytes()).hexdigest()}
    print(name, len(text), "characters", flush=True)
(ROOT / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
