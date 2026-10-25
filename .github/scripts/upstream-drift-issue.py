#!/usr/bin/env python3
"""Open, update or close the single Upstream drift issue from the workflow outputs."""
import os
import subprocess

title = "Upstream drift"
verify_ok = os.environ.get("VERIFY_OK") == "true"
newer_ok = os.environ.get("NEWER_OK") == "true"
newer = os.environ.get("NEWER", "").strip()
problems: list[str] = []
if not newer_ok:
    problems.append("Could not compare the pinned tags with the latest releases.")
if not verify_ok:
    problems.append("`setup --verify-manifest` failed: a pinned URL or SHA-256 no longer matches.")
if newer:
    problems.append("A pinned release is not the latest:\n\n" + newer)

existing = subprocess.check_output(
    ["gh", "issue", "list", "--search", f"{title} in:title", "--state", "open", "--json", "number", "--jq", ".[0].number"],
    text=True,
).strip()

if not problems:
    if existing:
        subprocess.check_call(["gh", "issue", "close", existing, "--comment", "The pins match again."])
        print(f"closed #{existing}")
    else:
        print("no drift")
    raise SystemExit(0)

body = "\n\n".join(problems) + "\n\nSee docs/setup.md (Bumping a pin)."
if existing:
    subprocess.check_call(["gh", "issue", "edit", existing, "--body", body])
    print(f"updated #{existing}")
else:
    subprocess.check_call(["gh", "issue", "create", "--title", title, "--body", body])
    print("opened")

raise SystemExit(1)
