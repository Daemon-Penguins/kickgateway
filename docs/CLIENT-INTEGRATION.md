# Integrating a client app with the Kick Gateway

This document explains how to subscribe to Kick Gateway events from an external
.NET app using the shared contracts library and MassTransit on RabbitMQ.

It is written for **subscriber/consumer apps**. The Kick Gateway itself is the
publisher and you do not need to touch its code.

---

## TL;DR

1. Reference `TailoredApps.KickGateway.Contracts` (DLL or NuGet).
2. `AddMassTransit(...)` with `UsingRabbitMq` pointed at the same broker.
3. `AddConsumer<T>()` per event type you care about.
4. Call `KickEventTopology.ConfigurePublishTopology(cfg)` so your bus declares
   the same topic exchanges the gateway publishes to.
5. Declare a `ReceiveEndpoint` per consumer, with a queue name unique to your
   app, and bind it using `KickEventTopology.BindKickEvent<T>(endpoint, slugs)`.
   Pass one or more broadcaster slugs to filter, or no slugs at all for the
   firehose.

The unique queue name is your app's identity on the broker; **two apps with
two different names get every message (fanout per app)**, **two replicas
sharing one name compete (load balancing)**, and **the slugs you bind to are
the only messages the broker delivers to your queue**.

---

## What the gateway publishes

The gateway translates every verified Kick webhook into a strongly-typed
MassTransit message and publishes it via an EF-Core outbox. Each event type
is its own **topic exchange** named after the CLR type's full name (e.g.
`TailoredApps.KickGateway.Contracts.Events:ChatMessageSent`). The routing
key is the broadcaster slug (lowercase) — `xqc`, `adin`, etc.

The contracts you will consume live in `TailoredApps.KickGateway.Contracts.Events`:

| Contract                          | When it fires                                     |
|-----------------------------------|---------------------------------------------------|
| `ChatMessageSent`                 | Viewer posted a chat message                       |
| `ChannelFollowed`                 | Someone followed the channel                       |
| `ChannelSubscriptionNew`          | New paid subscription                              |
| `ChannelSubscriptionGifts`        | Gifted subs                                        |
| `ChannelSubscriptionRenewal`      | Subscription renewal                               |
| `LivestreamStatusUpdated`         | Stream went live / offline                         |
| `LivestreamMetadataUpdated`       | Title / category / language changed                |
| `ModerationBanned`                | Ban / timeout                                      |
| `KicksGifted`                     | Viewer sent Kicks                                  |
| `ChannelRewardRedemptionUpdated`  | Channel-point reward redeemed / state changed      |
| `KickEventUnknown`                | Fallback for event types not yet mapped            |

Every contract inherits `KickEventBase` which exposes the envelope:

- `KickMessageId` — unique per delivery (use it for idempotent processing).
- `KickSubscriptionId` — which Kick subscription delivered it.
- `BroadcasterAccountId`, `BroadcasterUserId`, `BroadcasterSlug` — fan out per channel.
- `ReceivedAt` (gateway clock, UTC) and `KickTimestamp` (Kick's clock, UTC, nullable).
- `RawPayload` — the original JSON for fields the typed contract didn't surface.

The base interface `IKickEvent` is implemented by every event — useful if you
want one consumer class to handle "all events" (see the
`Subscribers.Analytics` sample in this repo).

---

## Step 1 — Get the contracts assembly

You have two options. Both produce the same CLR types, which is all that
matters: MassTransit maps the type's full name to a RabbitMQ exchange.

### Option A: NuGet (preferred once published)

```xml
<PackageReference Include="TailoredApps.KickGateway.Contracts" Version="0.2.*" />
```

### Option B: project / DLL reference

If you have the source checkout next to your client app:

```xml
<ProjectReference Include="..\kickgateway\src\TailoredApps.KickGateway.Contracts\TailoredApps.KickGateway.Contracts.csproj" />
```

Or, if you only have the binary, drop the DLL into a `libs/` folder and:

```xml
<Reference Include="TailoredApps.KickGateway.Contracts">
  <HintPath>libs\TailoredApps.KickGateway.Contracts.dll</HintPath>
  <Private>true</Private>
</Reference>
```

> The **assembly name and message type's full name must match** what the
> gateway publishes. Don't fork-and-rename the contracts project locally —
> MassTransit derives the RabbitMQ exchange from the type's full name.

You also need the MassTransit RabbitMQ packages:

```xml
<PackageReference Include="MassTransit" Version="8.*" />
<PackageReference Include="MassTransit.RabbitMQ" Version="8.*" />
```

(The Contracts package transitively brings `MassTransit.RabbitMQ` because the
topology helper needs it.)

---

## Step 2 — Wire MassTransit in your client app

`Program.cs` of a `Microsoft.NET.Sdk.Worker` project (the simplest shape — works
identically in an ASP.NET app):

```csharp
using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;

var builder = Host.CreateApplicationBuilder(args);

// Read the channels this app cares about from config so it's hot-swappable.
var channels = builder.Configuration
    .GetSection("Subscribers:Channels")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<MyChatConsumer>();
    x.AddConsumer<MySubscriptionConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"]!, ushort.Parse(rmq["Port"]!), rmq["VirtualHost"]!, h =>
        {
            h.Username(rmq["Username"]!);
            h.Password(rmq["Password"]!);
        });

        // Match the gateway's topology (topic exchanges per event type).
        KickEventTopology.ConfigurePublishTopology(cfg);

        // One durable queue per consumer, each filtered by `channels`.
        cfg.ReceiveEndpoint("myapp-chat", e =>
        {
            KickEventTopology.BindKickEvent<ChatMessageSent>(e, channels);
            e.ConfigureConsumer<MyChatConsumer>(ctx);
        });

        cfg.ReceiveEndpoint("myapp-subs", e =>
        {
            KickEventTopology.BindKickEvent<ChannelSubscriptionNew>(e, channels);
            e.ConfigureConsumer<MySubscriptionConsumer>(ctx);
        });
    });
});

await builder.Build().RunAsync();

public sealed class MyChatConsumer(ILogger<MyChatConsumer> log) : IConsumer<ChatMessageSent>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx)
    {
        log.LogInformation("{User} on {Channel}: {Text}",
            ctx.Message.SenderUsername, ctx.Message.BroadcasterSlug, ctx.Message.Content);
        return Task.CompletedTask;
    }
}
```

`appsettings.json`:

```json
{
  "RabbitMq": {
    "Host": "rabbitmq.your-infra",
    "Port": 5672,
    "VirtualHost": "/",
    "Username": "kick-subscriber-myapp",
    "Password": "..."
  },
  "Subscribers": {
    "Channels": ["xqc", "adin"]
  }
}
```

That's the entire integration.

---

## Filtering by channel

`KickEventTopology.BindKickEvent<T>(endpoint, slugs)` creates one AMQP binding
per slug. The slug becomes the routing key the broker matches against.

```csharp
// Receive ChatMessageSent ONLY for xqc and adin.
KickEventTopology.BindKickEvent<ChatMessageSent>(e, "xqc", "adin");

// Receive every channel (firehose). Equivalent to passing no slugs at all.
KickEventTopology.BindKickEvent<ChatMessageSent>(e, KickEventTopology.AllChannels);
KickEventTopology.BindKickEvent<ChatMessageSent>(e);
```

The broker does the filtering. Messages for channels you didn't bind never
hit your consumer, never count against your prefetch, never sit in your queue.

Slugs are lowercased automatically; pass them however you have them.

You can also use RabbitMQ topic-exchange wildcards:

- `xqc.*` would match a single extra segment if the gateway ever extended the
  routing key (it doesn't today — the gateway publishes the slug only).
- `#` matches everything (and is exposed as `KickEventTopology.AllChannels`).

### Changing the channel list later

Channel bindings are AMQP bindings on a durable queue. If you redeploy your
app with a different slug list, MassTransit declares the new bindings on
startup, but **old bindings remain** — your queue will keep receiving old
slugs too until you unbind manually (RabbitMQ Management UI, or `rabbitmqctl
clear_queue_policy` / `delete_binding`). Two common approaches:

- **Rotate the queue name** when you change the filter (e.g. include a version
  number in the queue name). Old queues drain and can be deleted.
- **Manage bindings out of band** with `rabbitmqadmin` if you need to keep
  the queue identity stable.

Most apps never change the filter, so this is a future-you problem rather
than a today-you problem.

---

## How the topology guarantees what you asked for

> *"When my app is running, I want to receive messages. When my app is offline,
> nothing should disappear. And I want to choose which channels."*

Here is how MassTransit + RabbitMQ deliver all three.

### 1 — Publisher side (gateway, already built)

When the gateway calls `IPublishEndpoint.Publish(new ChatMessageSent {...})`:

1. It writes the message into a **transactional outbox** in the gateway's
   SQL Server (EF Core integration). The webhook HTTP response succeeds the
   moment the DB transaction commits. If RabbitMQ is down at this exact
   moment, the message stays in the outbox and is retried.
2. The outbox dispatcher forwards to RabbitMQ. The message is routed to a
   **topic exchange named after the message type**, with **routing key =
   broadcaster slug**.
3. The exchange is **durable** and messages are **persistent**, so the broker
   writes them to disk.

The gateway never loses a message between "webhook accepted" and "in RabbitMQ."

### 2 — Subscriber side (your client app)

When your app starts and connects, MassTransit:

1. Declares one **durable, non-auto-delete queue** per `ReceiveEndpoint` you
   defined (e.g. `myapp-chat`). Durable means the queue survives broker
   restarts; non-auto-delete means it survives your app disconnecting.
2. For each `BindKickEvent<T>(slug)` call, creates an AMQP binding from the
   `T` exchange to the queue with that slug as the routing key.
3. Starts consuming, with **manual ack** — messages only leave the queue
   once your `Consume()` returns successfully.

Because the queue is durable + bindings are durable + messages are persistent:

- **Multiple apps each get every message they bound to.** Different
  ReceiveEndpoint names = different queues = the topic exchange copies to
  every binding that matches the routing key.
- **Nothing disappears while your app is offline.** Your queue accumulates
  matching messages on disk. When your app reconnects, it drains from the head.
- **Channel filter is enforced by the broker.** If your binding key is `xqc`
  and the gateway publishes a message with routing key `adin`, RabbitMQ
  drops it at the exchange — it never enters your queue.
- **Failed processing is retried.** A thrown exception in `Consume()` ⇒ no
  ack ⇒ redelivery. After the retry policy is exhausted the message moves to
  the per-queue error queue (`<queue>_error`) for inspection — it isn't dropped.

### 3 — Scaling out one app

If you run two replicas with the **same** ReceiveEndpoint names, they share
the same queue and RabbitMQ load-balances messages between them (competing
consumer pattern). This is the right shape for almost every use case.

If you want each replica to actually get every message (rare — usually only
for in-memory UI taps), give each replica a distinct ReceiveEndpoint name
(e.g. include the hostname). That is what the gateway's own admin live-feed
does.

---

## Operational checklist

Verify in RabbitMQ Management UI (`http://<rabbit-host>:15672`):

- **Exchanges tab** — one topic exchange per event type (full message-type
  name, e.g. `TailoredApps.KickGateway.Contracts.Events:ChatMessageSent`).
  Type = `topic`, durable = ✓.
- **Queues tab** — one queue per `ReceiveEndpoint` you declared. Durable = ✓,
  Auto-delete = ✗.
- **Bindings** — open any exchange; you should see one binding per
  `(queue, slug)` pair. Routing key column shows the slug (or `#`).

Smoke test: stop your subscriber, fire a chat message in the Kick channel,
watch the queue depth climb in the Management UI, restart your subscriber,
watch it drain to 0. That's the "nothing disappears" guarantee in action.

To verify filtering: bind to `xqc` only, then fire a message on `adin`. The
exchange tab will show the published message but your queue depth stays at 0.

---

## Idempotency note

RabbitMQ is at-least-once. Retries, connection blips, or scaling events can
cause the same message to be delivered twice. The gateway gives you a
deterministic dedupe key — `ctx.Message.KickMessageId` — that is unique per
Kick delivery. If your handler has external side effects (DB writes, third-
party API calls, sending email), key your idempotency on `KickMessageId`.

```csharp
public async Task Consume(ConsumeContext<ChatMessageSent> ctx)
{
    if (await _seen.AlreadyProcessedAsync(ctx.Message.KickMessageId)) return;
    // ... your work ...
    await _seen.MarkProcessedAsync(ctx.Message.KickMessageId);
}
```

---

## Picking a queue name

Your `ReceiveEndpoint` name is your app's identity on the broker. Use
something stable and descriptive:

- ✅ `loyalty-chat`, `alerts-stream-status`, `chat-overlay-xqc`
- ❌ machine names, build numbers, anything that changes between deploys —
  you'd lose your queue (and its backlog) on every restart.

If you do need per-replica isolation, append a stable replica id, not the
hostname.

---

## Migrating contract versions

Adding a property to an existing event = backwards-compatible. Subscribers
built against the old contract simply ignore the new field.

Removing or renaming a property = breaking. The gateway bumps the
`Contracts` package minor version and the README will call it out. Until you
upgrade, your handler will see the field at its default (`""` for string,
`null` for nullable types).

If Kick ships a brand new event type before the gateway maps it, you will see
it arrive as `KickEventUnknown` with `EventType` set to the raw Kick type and
the full body in `RawPayload`. That's your bridge until the next gateway
release.
