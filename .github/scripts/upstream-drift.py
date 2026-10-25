#!/usr/bin/env python3
"""Compare each release tag pinned in the setup manifest with that repo's latest release."""
import json
import os
import re
import subprocess

manifest = json.load(open("src/PS5LM.Companion.Core/Setup/setup-manifest.json", encoding="utf-8"))
pins: dict[tuple[str, str], set[str]] = {}


def consider(url: str) -> None:
    match = re.match(r"https://github.com/([^/]+)/([^/]+)/releases/download/([^/]+)/", url)
    if match:
        pins.setdefault((match.group(1), match.group(2)), set()).add(match.group(3))


consider(manifest["payloadManager"]["url"])
for payload in manifest["payloads"]:
    consider(payload["url"])
    for extra in payload.get("files") or []:
        consider(extra["url"])
for app in manifest["apps"]:
    consider(app["url"])

notes: list[str] = []
for (owner, repo), tags in sorted(pins.items()):
    try:
        latest = subprocess.check_output(
            ["gh", "api", f"repos/{owner}/{repo}/releases/latest", "--jq", ".tag_name"],
            text=True,
        ).strip()
    except subprocess.CalledProcessError:
        notes.append(f"- {owner}/{repo}: could not read the latest release (pinned {', '.join(sorted(tags))})")
        continue
    stale = sorted(tag for tag in tags if tag != latest)
    if stale:
        notes.append(f"- {owner}/{repo}: pinned {', '.join(stale)}, latest {latest}")

text = "\n".join(notes)
path = os.environ.get("GITHUB_OUTPUT")
if path:
    with open(path, "a", encoding="utf-8") as handle:
        handle.write("newer<<EOF\n")
        handle.write(text)
        handle.write("\nEOF\n")
else:
    print(text or "pins match the latest releases")
