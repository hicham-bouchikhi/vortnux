"""Embed the explicitly configured publisher public key before Native AOT compilation."""
import json
import os
import re
from pathlib import Path

repository = os.environ["GITHUB_REPOSITORY"]
key = os.environ["VORTNUX_RELEASE_PUBLIC_KEY"]
if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository) or "BEGIN PUBLIC KEY" not in key:
    raise SystemExit("Configure the release repository and RSA public key before building a signed release.")
Path("release/trust.json").write_text(json.dumps({"Repository": repository, "PublicKey": key}) + "\n")
