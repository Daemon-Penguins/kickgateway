# Kick Gateway

> Pełna dokumentacja projektowa, ADR-y i runbooki są w Obsidian Vaulcie
> użytkownika: `D:\Obsidian\nieprzecietny\10_Tematy\kickgateway\` +
> `D:\Obsidian\nieprzecietny\20_Repozytoria\kickgateway.md`. Patrz też
> `CLAUDE.md` w roocie tego repo.

Webhook gateway that ingests Kick.com events for many broadcasters under many
Kick developer apps and republishes them to RabbitMQ via MassTransit.
Downstream services subscribe to typed contracts in
`TailoredApps.KickGateway.Contracts` without ever touching the Kick API.

**Prod:** https://kickgateway.madagascar.net.pl (deployed on hq-config —
shared SQL Server + RabbitMQ on WireGuard VPN, Traefik public TLS).

## What's in the box

```
src/
  TailoredApps.Integrations.Kick/        # Kick REST + OAuth (PKCE) + sig-verify client.
  TailoredApps.KickGateway.Contracts/    # MassTransit message contracts (shared lib).
  TailoredApps.KickGateway.Api/          # WebAPI + Blazor admin + webhook receiver. Dockerfile here.
  TailoredApps.KickGateway.Worker/       # Sample subscriber (logs every contract). Dockerfile here.
  TailoredApps.KickGateway.AppHost/      # Aspire orchestrator (F5 from VS).
  TailoredApps.KickGateway.ServiceDefaults/  # OTel + health + resilience.
docker/docker-compose.yml                # RabbitMQ + SQL Server (dev fallback).
.github/workflows/deploy.yml             # Build → push Docker images → deploy to hq-config.
```

## Architecture

- **Multi-client.** Each row in `KickClientApp` holds one Kick developer app
  (ClientId / Secret / RedirectUri / WebhookUrl). The gateway can host any
  number of them. Each Kick app must have its webhook URL pointed at this
  gateway's `/api/webhooks/kick`.
- **Multi-broadcaster.** Each row in `KickBroadcasterAccount` is one channel
  authenticated under one client app. Same channel under two client apps =
  two rows. Tokens are refreshed on demand and on a 2-minute background pass.
- **Inbox.** Every webhook delivery is inserted into `ReceivedWebhook` keyed
  on Kick's `Kick-Event-Message-Id`. Duplicates are short-circuited.
- **Outbox.** The publish to RabbitMQ goes through MassTransit's
  `AddEntityFrameworkOutbox<KickGatewayDbContext>` — the message only
  leaves the DB once the inbox row commits. No publish-without-record drift
  and vice-versa.
- **Topology.** MassTransit auto-creates one topic exchange per published
  contract and lets each consumer queue bind to it. Workers can scale
  horizontally; consumers on the same queue compete; new contracts only need
  a new consumer class.

## Quick start

### Option A — .NET Aspire (recommended, F5 from Visual Studio)

Open `TailoredApps.KickGateway.slnx`, set **TailoredApps.KickGateway.AppHost**
as the startup project, and F5. The AppHost:

- starts SQL Server + RabbitMQ in containers (with the management UI exposed)
- injects `ConnectionStrings:KickGateway` into the Api and `RabbitMq:*` into
  both Api and Worker
- runs the Api + Worker side-by-side
- opens the Aspire dashboard (resources, logs, traces, metrics)

From the CLI:

```pwsh
dotnet run --project src/TailoredApps.KickGateway.AppHost
```

### Option B — manual

```pwsh
# 1. Boot infra
docker compose -f docker/docker-compose.yml up -d

# 2. Apply schema (runs on first Api start automatically), or manually:
dotnet ef database update --project src/TailoredApps.KickGateway.Api

# 3. Run gateway + worker
dotnet run --project src/TailoredApps.KickGateway.Api
dotnet run --project src/TailoredApps.KickGateway.Worker
```

Open `https://localhost:5001/admin`:

1. **Clients** — paste ClientId / Secret / RedirectUri (the one set in the
   Kick dev portal) plus the webhook URL the portal will POST to.
2. **Broadcasters** — pick a client app, click **Start OAuth**. The Kick
   consent screen appears; on approval you're redirected back and a row
   appears here.
3. Click **Enroll all events** to subscribe the broadcaster to every event
   in `KickEventTypes.All` via `/public/v1/events/subscriptions`.

Webhook deliveries hit `/api/webhooks/kick`, get signature-verified, deduped,
mapped to a typed contract, and published. The sample worker logs everything;
your real subscribers can be in any process that references the contracts
project.

## Deploy (hq-config)

`.github/workflows/deploy.yml` builds two Docker images
(`nieprzecietnykowalski/kickgateway-api`, `nieprzecietnykowalski/kickgateway-worker`),
pushes to Docker Hub, and deploys to the hq-config VPS via SSH +
`docker compose`. The compose stack joins two shared networks managed by
[hq-config](https://github.com/Daemon-Penguins/hq-config):

- `traefik-public` — Traefik handles TLS for `kickgateway.madagascar.net.pl`
- `db-internal`   — reaches the shared SQL Server (`sqlserver:1433`)

### Required GitHub repo secrets

| Secret | What |
| --- | --- |
| `DOCKERHUB_USER` / `DOCKERHUB_TOKEN` | push to Docker Hub |
| `DEPLOY_HOST` / `DEPLOY_USER` / `DEPLOY_SSH_KEY` | SSH into the VPS |
| `DB_CONNECTION_STRING` | `Server=sqlserver,1433;Database=kickgateway;User Id=…;Password=…;TrustServerCertificate=true;Encrypt=false` |
| `RABBITMQ_HOST` / `RABBITMQ_PORT` / `RABBITMQ_VHOST` / `RABBITMQ_USERNAME` / `RABBITMQ_PASSWORD` | broker on hq-config (private vhost recommended) |

### Pre-deploy operator checklist

1. `kickgateway.madagascar.net.pl` DNS A record → VPS public IP.
2. `kickgateway` database created on the shared SQL Server + a user with `db_owner` on it. EF migrations apply on first Api start.
3. Kick dev portal: each `KickClientApp` row's RedirectUri set to `https://kickgateway.madagascar.net.pl/api/auth/kick/callback`, WebhookUrl set to `https://kickgateway.madagascar.net.pl/api/webhooks/kick`.
4. RabbitMQ broker on hq-config is up (`ansible-playbook deploy-rabbitmq.yml` if not) and credentials are available.

## Wiring a downstream subscriber

```csharp
services.AddMassTransit(x =>
{
    x.AddConsumer<MyChatHandler>();
    x.SetKebabCaseEndpointNameFormatter();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host("localhost", "/", h => { h.Username("guest"); h.Password("guest"); });
        cfg.ConfigureEndpoints(ctx);
    });
});

public class MyChatHandler : IConsumer<ChatMessageSent>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx) => ...;
}
```

## Notes

- Kick's `events[].version` MUST be a JSON number on subscription create.
  `KickClient.CreateSubscriptionAsync` already does that.
- `broadcaster_user_id` is omitted when subscribing under a user token —
  Kick infers from the token. Required only for app tokens.
- The webhook URL Kick POSTs to is the value in the dev portal's app
  config, NOT what you send in the subscription body.
- Public key rotates; `KickSignatureVerifier` retries once with a forced
  refresh on verification failure.
