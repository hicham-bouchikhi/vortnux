"""Diagnose a collection using curl, without logging API keys or signed URLs."""
import argparse
import getpass
import json
import subprocess
import sys
import tempfile
from urllib.parse import urljoin, urlsplit


def quoted(value):
    if any(ord(c) < 32 for c in value):
        raise ValueError("Invalid control character in curl configuration")
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"') + '"'


def curl(url, key=None, payload=None, destination=None):
    config = "url = " + quoted(url) + "\n"
    if key:
        config += "header = " + quoted("apikey: " + key) + "\n"
    args = ["curl", "--silent", "--show-error", "--max-time", "60",
            "--proto", "=https", "--config", "-", "--write-out", "\n%{http_code}"]
    if payload is not None:
        config += "header = \"Content-Type: application/json\"\n"
        config += "data = " + quoted(json.dumps(payload)) + "\n"
    if destination:
        args += ["--output", destination, "--max-filesize", "8388608"]
    result = subprocess.run(args, input=config, text=True, capture_output=True, timeout=65)
    body, _, status = result.stdout.rpartition("\n")
    if result.returncode:
        raise RuntimeError(f"curl failed (exit {result.returncode}); check network/DNS and the 8 MiB archive limit")
    return int(status), body


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("slug", nargs="?", default="n0nymh")
    parser.add_argument("--revision", type=int)
    parser.add_argument("--key-stdin", action="store_true")
    args = parser.parse_args()
    key = sys.stdin.readline().strip() if args.key_stdin else getpass.getpass("Nexus API key (hidden): ").strip()
    if not key:
        raise ValueError("An API key is required")
    status, body = curl("https://api.nexusmods.com/v1/users/validate.json", key)
    print(f"Account validation: HTTP {status}", flush=True)
    if status != 200:
        return 1
    print("Premium:", json.loads(body).get("is_premium"), flush=True)
    query = "query($slug:String,$revision:Int){collectionRevision(slug:$slug,revision:$revision,domainName:\"cyberpunk2077\"){downloadLink revisionNumber revisionStatus modCount installationInfo externalResources{id} collection{name}}}"
    status, body = curl("https://api.nexusmods.com/v2/graphql", key,
                        {"query": query, "variables": {"slug": args.slug, "revision": args.revision}})
    print(f"Collection query: HTTP {status}", flush=True)
    if status != 200:
        return 1
    response = json.loads(body)
    if response.get("errors"):
        raise RuntimeError("Nexus GraphQL reported errors")
    revision = response["data"]["collectionRevision"]
    print("Collection:", revision["collection"]["name"], "revision:", revision["revisionNumber"],
          "mods:", revision["modCount"], flush=True)
    print("Extra instructions:", bool(revision["installationInfo"]),
          "external resources:", len(revision["externalResources"]), flush=True)
    resolver = urljoin("https://api.nexusmods.com/", revision["downloadLink"])
    parts = urlsplit(resolver)
    if parts.scheme != "https" or parts.netloc != "api.nexusmods.com" or parts.fragment:
        raise ValueError("Unexpected resolver; refusing to send credentials")
    status, body = curl(resolver, key)
    print(f"Archive resolver: HTTP {status}", flush=True)
    if status != 200:
        return 1
    response = json.loads(body)
    links = response.get("download_links", response.get("download_link"))
    if isinstance(links, list):
        links = links[0]
    address = links["URI"]
    parts = urlsplit(address)
    host = parts.hostname or ""
    print("Archive host:", host, flush=True)
    if (parts.scheme != "https" or parts.username or parts.password or parts.port not in (None, 443)
            or not any(host == d or host.endswith("." + d) for d in ("nexusmods.com", "nexus-cdn.com"))):
        raise ValueError("Unexpected archive host; refusing download")
    # The archive request uses only its signed URL, never the API-key header.
    with tempfile.TemporaryDirectory(prefix="vortnux-collection-check-") as directory:
        path = directory + "/collection.7z"
        status, _ = curl(address, destination=path)
        print(f"Archive download: HTTP {status}", flush=True)
        if status != 200:
            return 1
        with open(path, "rb") as archive:
            print("Archive signature:", archive.read(6).hex(), flush=True)
        result = subprocess.run(["7z", "x", "-so", path, "collection.json"], capture_output=True, timeout=30)
        if result.returncode:
            raise RuntimeError("Could not extract collection.json; install 7zip")
        manifest = json.loads(result.stdout)
        print("Manifest entries:", len(manifest.get("mods", [])), flush=True)
        # Report features the current app rejects without printing mod URLs or account data.
        features = set()
        for name, value in manifest.items():
            if name not in ("info", "mods", "modRules") and value:
                features.add(name)
        if manifest.get("modRules"):
            features.add("modRules")
        for name in ("installInstructions", "gameVersions"):
            if manifest.get("info", {}).get(name):
                features.add("info." + name)
        for mod in manifest.get("mods", []):
            for name in ("choices", "patches", "instructions", "fileOverrides", "phase"):
                if mod.get(name):
                    features.add("mods." + name)
            if mod.get("details", {}).get("type"):
                features.add("mods.details.type")
            source = mod.get("source", {})
            if source.get("type") != "nexus":
                features.add("source.type=" + str(source.get("type")))
            for name in ("instructions", "fileExpression"):
                if source.get(name):
                    features.add("source." + name)
            if source.get("updatePolicy") not in (None, "exact"):
                features.add("source.updatePolicy=" + str(source["updatePolicy"]))
        print("Unsupported manifest features:", ", ".join(sorted(features)) or "none detected", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, KeyError, IndexError, RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        print("Diagnostic failed:", error, file=sys.stderr)
        sys.exit(1)
