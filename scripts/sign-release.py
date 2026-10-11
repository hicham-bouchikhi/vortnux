"""Sign the final AppImage bytes. Requires an explicitly configured, matching publisher key."""
import hashlib
import json
import os
import subprocess
import tempfile
from pathlib import Path

version = os.environ["VORTNUX_VERSION"]
repository = os.environ["GITHUB_REPOSITORY"]
artifact = Path("dist") / f"Vortnux-{version}-x86_64.AppImage"
payload = {"Version": version, "Url": f"https://github.com/{repository}/releases/download/v{version}/{artifact.name}",
           "Sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(), "Size": artifact.stat().st_size}
manifest = Path("dist/update-linux-x64.json")
manifest.write_bytes(json.dumps(payload, separators=(",", ":")).encode())
with tempfile.TemporaryDirectory(prefix="vortnux-sign-") as directory:
    private = Path(directory) / "release.pem"
    descriptor = os.open(private, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "w") as stream:
        stream.write(os.environ["VORTNUX_SIGNING_KEY"])
    public = subprocess.check_output(["openssl", "pkey", "-in", str(private), "-pubout"], text=True)
    embedded = json.loads(Path("release/trust.json").read_text())["PublicKey"]
    if public.strip() != embedded.strip():
        raise SystemExit("Signing key does not match the embedded publisher key.")
    subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(private), "-sigopt", "rsa_padding_mode:pss",
                    "-sigopt", "rsa_pss_saltlen:digest", "-out", "dist/update-linux-x64.sig", str(manifest)], check=True)
print("Signed final AppImage manifest with the configured publisher key.")
