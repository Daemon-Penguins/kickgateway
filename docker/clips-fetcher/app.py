"""
clips-fetcher — a tiny browser-TLS fetch proxy.

Kick's clip listing lives on the website host (https://kick.com/api/v2/...), which
is behind Cloudflare TLS-fingerprint (JA3) blocking: a .NET HttpClient gets 403,
while a request that presents a real browser fingerprint gets 200. This sidecar is
the bypass — it forwards a single GET through curl_cffi (which impersonates a real
Chrome TLS/HTTP fingerprint) and returns the upstream response verbatim.

It is intentionally DUMB: no Kick knowledge lives here. The .NET backend builds the
Kick URL, calls GET /fetch?url=..., and parses the JSON. That keeps all domain logic
in one place (TailoredApps.Integrations.Kick) and makes this swappable.

Safety (it must never become an open SSRF relay):
  * host allowlist (kick.com / api.kick.com / clips.kick.com only),
  * a shared-secret header (X-Fetch-Secret),
  * https-only,
  * and it is only ever exposed on the internal Aspire/compose network — never
    published to the host or Traefik.
"""

import hmac
import logging
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from curl_cffi import requests as cffi_requests

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("clips-fetcher")

PORT = int(os.environ.get("PORT", "8080"))
SECRET = os.environ.get("FETCH_SECRET", "")
# curl_cffi impersonation target. "chrome" is an alias for the newest Chrome
# profile the installed curl_cffi supports — robust across version bumps.
IMPERSONATE = os.environ.get("IMPERSONATE", "chrome")
TIMEOUT = float(os.environ.get("FETCH_TIMEOUT", "25"))
ALLOWED_HOSTS = {
    h.strip().lower()
    for h in os.environ.get("ALLOWED_HOSTS", "kick.com,api.kick.com,clips.kick.com").split(",")
    if h.strip()
}


def host_allowed(host: str | None) -> bool:
    host = (host or "").lower()
    return any(host == h or host.endswith("." + h) for h in ALLOWED_HOSTS)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "clips-fetcher/1.0"

    def _send(self, status: int, body, content_type: str = "text/plain; charset=utf-8") -> None:
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    # Quieter, structured-ish logging (default BaseHTTPRequestHandler is noisy).
    def log_message(self, fmt: str, *args) -> None:
        log.info("%s %s", self.address_string(), fmt % args)

    def do_GET(self) -> None:
        parsed = urlparse(self.path)

        if parsed.path == "/healthz":
            self._send(200, "ok")
            return
        if parsed.path != "/fetch":
            self._send(404, "not found")
            return

        # --- shared-secret gate ---
        if not SECRET:
            self._send(503, "fetcher not configured (FETCH_SECRET missing)")
            return
        if not hmac.compare_digest(self.headers.get("X-Fetch-Secret", ""), SECRET):
            self._send(403, "forbidden")
            return

        # --- validate target ---
        target = (parse_qs(parsed.query).get("url") or [""])[0]
        if not target:
            self._send(400, "missing url")
            return
        tp = urlparse(target)
        if tp.scheme != "https" or not host_allowed(tp.hostname):
            self._send(400, "host not allowed")
            return

        # --- fetch with a real browser fingerprint ---
        try:
            resp = cffi_requests.get(
                target,
                impersonate=IMPERSONATE,
                timeout=TIMEOUT,
                headers={"Accept": "application/json, text/plain, */*"},
            )
        except Exception as ex:  # curl_cffi raises its own error types
            log.warning("fetch failed for %s: %s", target, ex)
            self._send(502, f"upstream fetch error: {ex}")
            return

        self._send(
            resp.status_code,
            resp.content,
            content_type=resp.headers.get("Content-Type", "application/octet-stream"),
        )

    def do_HEAD(self) -> None:
        self.do_GET()


def main() -> None:
    if not SECRET:
        log.warning("FETCH_SECRET is not set — /fetch will refuse all requests until configured")
    log.info(
        "clips-fetcher listening on :%d (impersonate=%s, allowed=%s)",
        PORT,
        IMPERSONATE,
        ",".join(sorted(ALLOWED_HOSTS)),
    )
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
