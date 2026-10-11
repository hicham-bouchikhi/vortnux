"""Nexus REST v1 and GraphQL v2, using the user's own credentials."""

from dataclasses import dataclass
import threading
import time
from urllib.parse import urlsplit

import requests

from . import __version__
from .links import CollectionLink, ModLink


class NexusError(Exception):
    pass


class BrowserRequired(NexusError):
    pass


@dataclass(frozen=True)
class Account:
    user_id: int
    name: str
    premium: bool


COLLECTION_QUERY = """
query VortnuxCollection($slug: String, $revision: Int, $domain: String) {
  collectionRevision(slug: $slug, revision: $revision, domainName: $domain) {
    revisionNumber downloadLink installationInfo totalSize
    collectionSchema { version }
    collection { name slug game { domainName name } }
    externalResources { name resourceUrl }
    modFiles {
      fileId optional
      file { fileId modId name sizeInBytes version game { domainName } }
    }
  }
}
"""


class NexusClient:
    def __init__(self, api_key: str, *, rest_url="https://api.nexusmods.com/v1",
                 graphql_url="https://api.nexusmods.com/v2/graphql", transport=None,
                 interval=1.0):
        self._key = api_key.strip()
        self.rest_url = rest_url
        self.graphql_url = graphql_url
        self._transport = transport or requests.request
        self._lock = threading.Lock()
        self._last = 0.0
        self.interval = interval
        self.account: Account | None = None

    def _request(self, method: str, url: str, **kwargs):
        if not self._key:
            raise NexusError("Connect your Nexus account first.")
        with self._lock:
            time.sleep(max(0, self.interval - (time.monotonic() - self._last)))
            self._last = time.monotonic()
        headers = {"apikey": self._key, "Application-Name": "Vortnux",
                   "Application-Version": __version__, "Accept": "application/json"}
        try:
            response = self._transport(method, url, headers=headers, timeout=(10, 30),
                                       allow_redirects=False, **kwargs)
        except requests.RequestException:
            raise NexusError("Could not reach Nexus Mods. Check your connection and retry.") from None
        with response:
            if response.status_code == 429:
                raise NexusError("Nexus API rate limit reached. Wait before retrying.")
            if response.status_code == 401:
                raise NexusError("Nexus rejected the API key. Reconnect with a valid key.")
            if response.status_code == 403:
                raise NexusError("Nexus denied access. Check account permissions or open the file on Nexus.")
            if response.status_code == 404:
                raise NexusError("This mod, file, or collection was not found on Nexus.")
            if not 200 <= response.status_code < 300:
                raise NexusError(f"Nexus returned HTTP {response.status_code}. Try again later.")
            try:
                return response.json()
            except ValueError:
                raise NexusError("Nexus returned an unreadable response.") from None

    def validate(self) -> Account:
        data = self._request("GET", self.rest_url + "/users/validate.json")
        self.account = Account(int(data["user_id"]), str(data["name"]), bool(data["is_premium"]))
        return self.account

    def files(self, link: ModLink) -> list[dict]:
        data = self._request("GET", f"{self.rest_url}/games/{link.game}/mods/{link.mod_id}/files.json")
        return data["files"]

    def file_info(self, link: ModLink) -> dict:
        return self._request("GET", f"{self.rest_url}/games/{link.game}/mods/{link.mod_id}/files/{link.file_id}.json")

    def download_url(self, link: ModLink) -> str:
        if not self.account:
            self.validate()
        if link.user_id and link.user_id != self.account.user_id:
            raise BrowserRequired("This download link belongs to another Nexus account. Open the file again.")
        if not self.account.premium and not link.key:
            raise BrowserRequired("Open this file on Nexus and choose Download with manager.")
        if link.key and link.expires and link.expires <= time.time():
            raise BrowserRequired("The download link expired. Open the file on Nexus again.")
        params = {"key": link.key, "expires": link.expires} if link.key else {}
        links = self._request("GET", f"{self.rest_url}/games/{link.game}/mods/{link.mod_id}/files/{link.file_id}/download_link.json", params=params)
        return self._first_uri(links)

    def collection(self, link: CollectionLink) -> dict:
        data = self._request("POST", self.graphql_url,
                             json={"query": COLLECTION_QUERY, "variables": {
                                 "slug": link.slug, "revision": link.revision, "domain": link.game}})
        if data.get("errors"):
            # Server messages may contain signed URLs or user data; do not echo them.
            raise NexusError("Nexus could not load this collection. Check the link, visibility, and account access.")
        revision = (data.get("data") or {}).get("collectionRevision")
        if not revision:
            raise NexusError("No published collection revision was found.")
        return revision

    def collection_archive_url(self, download_link: str) -> str:
        # GraphQL returns a REST resolver path, not necessarily a CDN URL.
        parsed = urlsplit(download_link)
        if parsed.scheme or parsed.netloc:
            if parsed.scheme != "https" or parsed.hostname != "api.nexusmods.com" or parsed.username or parsed.password or parsed.port:
                raise NexusError("Nexus returned an unexpected collection resolver.")
            url = download_link
        else:
            if not download_link.startswith("/v1/collections/"):
                raise NexusError("Nexus returned an unexpected collection resolver.")
            url = "https://api.nexusmods.com" + download_link
        data = self._request("GET", url)
        links = data.get("download_links") or [data.get("download_link")]
        return self._first_uri(links)

    @staticmethod
    def _first_uri(links) -> str:
        if not isinstance(links, list) or not links or not isinstance(links[0], dict) or not links[0].get("URI"):
            raise NexusError("Nexus did not return a download mirror.")
        return str(links[0]["URI"])
