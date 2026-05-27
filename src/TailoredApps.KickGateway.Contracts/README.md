# TailoredApps.KickGateway.Contracts

Strongly-typed [MassTransit](https://masstransit.io/) message contracts +
RabbitMQ topology helper for subscribers of the
[Kick Gateway](https://github.com/Daemon-Penguins/kickgateway).

Reference this package from any .NET app and you can `IConsumer<T>` on the
same CLR types the gateway publishes — every Kick webhook ends up as a typed
event on a topic exchange routed by the broadcaster slug.

## Install

```bash
dotnet add package TailoredApps.KickGateway.Contracts
```

## Quick start

```csharp
using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;

services.AddMassTransit(x =>
{
    x.AddConsumer<MyChatConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host("rabbitmq.your-infra", "/", h =>
        {
            h.Username("..."); h.Password("...");
        });

        // Match the gateway's topology (topic exchanges, slug = routing key).
        KickEventTopology.ConfigurePublishTopology(cfg);

        // One durable queue, bound only to messages where BroadcasterSlug == "xqc".
        cfg.ReceiveEndpoint("myapp-chat", e =>
        {
            KickEventTopology.BindKickEvent<ChatMessageSent>(e, "xqc");
            e.ConfigureConsumer<MyChatConsumer>(ctx);
        });
    });
});

public class MyChatConsumer : IConsumer<ChatMessageSent>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx)
    {
        // ctx.Message.SenderUsername, ctx.Message.Content, ctx.Message.RawPayload, ...
        return Task.CompletedTask;
    }
}
```

Pass no slugs (or `KickEventTopology.AllChannels`) to get the firehose
across every broadcaster.

## What you get

- 10 typed event records under `TailoredApps.KickGateway.Contracts.Events.*`
  (chat, follow, sub-new / -gifts / -renewal, livestream status / metadata,
  ban, kicks, reward redemption) plus `KickEventUnknown` for forward-compat.
- `KickEventBase` / `IKickEvent` — common envelope (KickMessageId for dedupe,
  BroadcasterSlug, ReceivedAt, RawPayload).
- `KickEventTopology.ConfigurePublishTopology(cfg)` — switches the bus to
  topic exchanges keyed on the broadcaster slug.
- `KickEventTopology.BindKickEvent<TEvent>(endpoint, slugs...)` — one binding
  per slug; broker filters at delivery time.

## Full integration guide

See [docs/CLIENT-INTEGRATION.md](https://github.com/Daemon-Penguins/kickgateway/blob/main/docs/CLIENT-INTEGRATION.md)
in the repository for guarantees (durability, no-loss, competing consumers),
operational tips, idempotency, and contract-versioning policy.

## License

MIT.
