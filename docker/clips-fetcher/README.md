# clips-fetcher

A tiny **browser-TLS fetch proxy** that lets the gateway read Kick's clip listing.

## Why it exists

Kick clips are only available on the **website host** `https://kick.com/api/v2/channels/{slug}/clips`.
That host is behind **Cloudflare TLS-fingerprint (JA3) blocking**: a .NET `HttpClient`
gets `403`, while a client presenting a real browser fingerprint gets `200`. No JS /
managed challenge is involved, so a headless browser is overkill — we only need a real
browser **TLS fingerprint**, which [`curl_cffi`](https://github.com/lexiforest/curl_cffi)
provides.

The official `api.kick.com/public/v1` API (used elsewhere in the gateway) has **no clips
endpoint**, and the clip **video** files on `clips.kick.com` are reachable by plain .NET —
so this proxy is needed **only** for the clip *listing* JSON.

## API

| Route | Auth | Purpose |
|-------|------|---------|
| `GET /healthz` | none | container healthcheck → `200 ok` |
| `GET /fetch?url=<urlencoded>` | `X-Fetch-Secret` header | fetch the URL with a browser fingerprint, return the upstream status/body verbatim |

It is intentionally **dumb** — no Kick knowledge lives here. The .NET backend
(`TailoredApps.Integrations.Kick.KickClipsClient`) builds the Kick URL and parses the JSON.

## Safety

- **host allowlist** (`ALLOWED_HOSTS`, default `kick.com,api.kick.com,clips.kick.com`),
- **shared secret** (`FETCH_SECRET`; `/fetch` refuses everything until it is set),
- **https-only**, and
- **internal network only** — never published to the host or Traefik.

## Config (env)

| Var | Default | Notes |
|-----|---------|-------|
| `PORT` | `8080` | listen port |
| `FETCH_SECRET` | _(empty)_ | required; backend must send it as `X-Fetch-Secret` |
| `IMPERSONATE` | `chrome` | curl_cffi target (alias = newest supported Chrome) |
| `FETCH_TIMEOUT` | `25` | seconds |
| `ALLOWED_HOSTS` | `kick.com,api.kick.com,clips.kick.com` | comma-separated |

## Run locally

```pwsh
docker build -t clips-fetcher docker/clips-fetcher
docker run --rm -p 8099:8080 -e FETCH_SECRET=devsecret clips-fetcher
# then:
curl -H "X-Fetch-Secret: devsecret" "http://localhost:8099/fetch?url=https%3A%2F%2Fkick.com%2Fapi%2Fv2%2Fchannels%2Fxqc%2Fclips%3Fsort%3Ddate"
```

In dev the Aspire AppHost builds and runs this automatically and injects `FETCH_SECRET`.
