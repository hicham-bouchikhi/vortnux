"""Parse website and manager links without persisting authorization tokens."""

from dataclasses import dataclass, field
import re
from urllib.parse import parse_qs, urlencode, urlsplit


def positive(value: str) -> int:
    if not re.fullmatch(r"[0-9]+", value) or int(value) < 1:
        raise ValueError("IDs and revision numbers must be positive integers.")
    return int(value)


@dataclass(frozen=True)
class ModLink:
    game: str
    mod_id: int
    file_id: int | None = None
    key: str | None = field(default=None, repr=False)
    expires: int | None = None
    user_id: int | None = None

    @property
    def website(self) -> str:
        base = f"https://www.nexusmods.com/{self.game}/mods/{self.mod_id}"
        if self.file_id:
            return base + "?" + urlencode({"tab": "files", "file_id": self.file_id, "nmm": 1})
        return base + "?tab=files"


@dataclass(frozen=True)
class CollectionLink:
    game: str
    slug: str
    revision: int | None = None

    @property
    def website(self) -> str:
        base = f"https://www.nexusmods.com/games/{self.game}/collections/{self.slug}"
        return base + (f"/revisions/{self.revision}" if self.revision else "")


def parse_link(value: str) -> ModLink | CollectionLink:
    if len(value) > 16384 or any(ord(c) < 32 for c in value.strip()):
        raise ValueError("Invalid Nexus link.")
    try:
        url = urlsplit(value.strip())
        port = url.port
    except ValueError:
        raise ValueError("Invalid Nexus link.") from None
    if url.username or url.password or port:
        raise ValueError("Invalid Nexus link.")
    query = parse_qs(url.query, keep_blank_values=True)
    if any(len(query.get(k, [])) > 1 for k in ("key", "expires", "user_id", "file_id", "revision")):
        raise ValueError("Duplicate parameters in Nexus link.")
    nxm = url.scheme.lower() == "nxm"
    if nxm:
        game = url.hostname or ""
        path = url.path.strip("/").split("/")
    elif url.scheme == "https" and url.hostname in {"www.nexusmods.com", "nexusmods.com", "next.nexusmods.com"}:
        path = url.path.strip("/").split("/")
        if path[0] == "games":
            path = path[1:]
        game, *path = path
    else:
        raise ValueError("Paste a Nexus Mods website URL or an nxm:// manager link.")
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,79}", game):
        raise ValueError("Invalid Nexus game domain.")
    if len(path) == 2 and path[0] == "mods" and not nxm:
        file_id = positive(query["file_id"][0]) if "file_id" in query else None
        return ModLink(game, positive(path[1]), file_id)
    if len(path) == 4 and path[0] == "mods" and path[2] == "files" and nxm:
        key = query.get("key", [None])[0]
        expiry = positive(query["expires"][0]) if "expires" in query else None
        if bool(key) != bool(expiry):
            raise ValueError("A manager download key must include its expiry time.")
        user_id = positive(query["user_id"][0]) if "user_id" in query else None
        return ModLink(game, positive(path[1]), positive(path[3]), key, expiry, user_id)
    if len(path) in {2, 4} and path[0] == "collections":
        if not re.fullmatch(r"[a-zA-Z0-9_-]{1,80}", path[1]):
            raise ValueError("Invalid collection slug.")
        revision = query.get("revision", ["latest"])[0]
        if len(path) == 4:
            if path[2] != "revisions":
                raise ValueError("Invalid collection revision URL.")
            revision = path[3]
        return CollectionLink(game, path[1], None if revision == "latest" else positive(revision))
    raise ValueError("This link is not a mod file or a collection.")
