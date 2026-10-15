# Monitoring (metrics, alerts, faulted messages)

What the gateway measures, how it reaches Grafana, what to alert on, and what to do when
a consumer faults. Everything below is exported by every .NET container through
`AddServiceDefaults()` (OpenTelemetry) - nothing is scraped from the containers.

## Getting the data into Grafana

All services **push** over OTLP, driven by the standard `OTEL_*` environment variables.
Where those come from depends on the host.

### HQ VPS (hq-config) - one secret: `OTEL_EXPORTER_OTLP_ENDPOINT=http://alloy:4318`

hq-config's monitoring role (`ansible/roles/monitoring`, runbook `docs/monitoring.md` there)
drops a `docker-compose.override.yml` into `/opt/apps/kickgateway/` that gives every
compose service it saw in `docker compose config --services`:

```yaml
OTEL_EXPORTER_OTLP_ENDPOINT: "http://alloy:4317"
OTEL_EXPORTER_OTLP_PROTOCOL: "grpc"
OTEL_RESOURCE_ATTRIBUTES: "service.namespace=kickgateway,deployment.environment.name=production,deployment.environment=production"
```

Our deploy runs plain `docker compose up -d`, which merges that override. Alloy forwards
metrics to Prometheus' native OTLP receiver, logs to Loki and traces to Tempo. Grafana is at
`http://10.10.0.1:3000` (WireGuard only).

What has to hold for it to work:

- **The container sits on `traefik-public`** - that is where `alloy` resolves. Every .NET
  service in `deploy.yml` does (the translator was added for this; `clips-fetcher` and `mcp`
  have no OTel SDK and ignore the variables).
- **The container was recreated after the override appeared.** Any deploy does that;
  `docker inspect kickgateway-api-1 | grep OTEL` on the VPS shows the merged variables.
- **Precedence.** A compose override's `environment` beats this repo's `otel.env`
  (`env_file`), so where the override exists it decides (4317, gRPC). The repo secret
  `OTEL_EXPORTER_OTLP_ENDPOINT=http://alloy:4318` (Alloy's HTTP port, matching the default
  `http/protobuf`) is the belt and braces: the override lists only the services that were
  active when the sync ran (profiles come from `.env`, and the sync re-runs on a compose-file
  change or daily), so a transcriber/subtitles/translator container enabled in the same deploy
  would otherwise export nothing until the next day. Both paths land in the same Alloy.
  `OTEL_RESOURCE_ATTRIBUTES` in `otel.env` carries the same `service.namespace=kickgateway`
  as the override, so labels are identical either way. What else this repo contributes:
  `OTEL_SERVICE_NAME` per service, `OTEL_METRIC_EXPORT_INTERVAL` (15 s) and the trace sampler
  (`OTEL_TRACES_SAMPLER=parentbased_traceidratio`, ratio from the `OTEL_TRACES_SAMPLER_ARG`
  repo variable, default 0.1 - MassTransit emits a span per message).
- `env_file` is declared `required: false` on purpose: the override script runs
  `docker compose config --services` the moment our compose file lands, before the deploy has
  written `otel.env`.
- Nothing else: no `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, no `OTEL_EXPORTER_OTLP_HEADERS`, no
  protocol variable (Alloy's 4318 is HTTP, the default). Grafana/Prometheus credentials are
  hq-config's (`vault_grafana_admin_password`), not this repo's.

Labels you get in Prometheus (3.x, `promote_resource_attributes` in hq-config):
`job="kickgateway/kickgateway-api"` (namespace/name), `service_name`, `service_namespace`,
`service_instance_id` (a GUID per process start - group by `service_name`, not by instance),
`deployment_environment`.

### Any other host - `otel.env` from repo secrets

`deploy.yml` writes `/opt/apps/kickgateway/otel.env` from the repo secrets/vars and every .NET
container loads it. Two shapes:

| Backend | Set | Notes |
| --- | --- | --- |
| OpenTelemetry Collector / Grafana Alloy | `OTEL_EXPORTER_OTLP_ENDPOINT=http://<host>:4318` (`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, the default) or `http://<host>:4317` with `grpc` | One endpoint, all three signals. |
| Prometheus' own OTLP receiver (metrics only) | `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://<host>:9090/api/v1/otlp/v1/metrics` | Prometheus >= 2.47 with `--web.enable-otlp-receiver` (3.x: `otlp:` in the config). A signal-specific endpoint is used verbatim (full path). Traces and logs are then not exported at all. |

`OTEL_EXPORTER_OTLP_HEADERS` carries auth (`Authorization=Basic ...`, comma-separated);
`OTEL_METRIC_EXPORT_INTERVAL` (ms) defaults to `15000` so an alert fires within a minute of the
fault rather than after the SDK's 60 s default. Empty variables = export off (ServiceDefaults
and the SDK both treat empty as unset).

### Naming after translation

OTLP names become Prometheus names: dots to underscores, the unit appended, `_total` on
counters. `kickgateway.webhook.duration` (unit `s`) is
`kickgateway_webhook_duration_seconds_bucket` / `_sum` / `_count`;
`kickgateway.realtime.ingest.dropped` is `kickgateway_realtime_ingest_dropped_total`. Tags
become labels the same way (`kick.event_type` -> `kick_event_type`,
`messaging.masstransit.endpoint` -> `messaging_masstransit_endpoint`).

Dev: the Aspire dashboard shows the same meters (resource -> Metrics), no configuration.

## What is measured

### Ingest edges - meter `TailoredApps.KickGateway` (`ServiceDefaults/KickGatewayMetrics.cs`)

These cover the part MassTransit cannot see: from "a message arrived from the outside
world" to "it is committed in the inbox and enlisted in the outbox", and the whole path to
a subscriber. All durations are seconds; histogram buckets are tuned for ms-level ingest
(`0.005 ... 30`) and for cross-system lag (`0.1 ... 300`).

| Metric | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `kickgateway.webhook.duration` | histogram (s) | `kick.event_type`, `outcome` | One Kick webhook delivery in the Api: read body, verify signature, dedupe, inbox row, outbox enlist, commit. `outcome`: `published`, `duplicate` (Kick redelivered), `unknown_broadcaster` (recorded, dropped), `rejected` (400, headers), `unauthorized` (401, signature), `error` (handler threw -> Kick will retry). |
| `kickgateway.webhook.kick_delay` | histogram (s) | `kick.event_type` | Kick's own delivery delay: its `Kick-Event-Message-Timestamp` to our receipt (published deliveries only). Not ours to fix; it explains "late" events that are not the gateway's. |
| `kickgateway.realtime.ingest.duration` | histogram (s) | `kick.event`, `outcome` | Pusher frame taken off the socket -> inbox row + outbox publish committed, **including** the wait in the ingest queue. `kick.event` is the short Kick event name (`ChatMessageEvent`). `outcome`: `published`, `duplicate`, `error` (dropped after the retries). |
| `kickgateway.realtime.ingest.queue_depth` | gauge | | Frames waiting for an ingest worker (all workers). Sustained growth = SQL cannot keep up with chat volume. |
| `kickgateway.realtime.ingest.dropped` | counter | `reason` | Frames thrown away because their worker's queue was full (`Kick:Realtime:IngestQueueCapacity`, 1000 per worker). Must stay at 0; each one is lost chat. |
| `kickgateway.realtime.connection.errors` | counter | `reason`, `pusher.code` | Pusher sessions that ended abnormally. `refused` = `pusher:error` 4000-4099, i.e. the app key / cluster is wrong because Kick rotated it (2026-10-15: 4001 "App key ... not in this cluster", every reconnect, zero frames for hours) - fix `KICK_REALTIME_APP_KEY`; `pusher_error` = other codes; `dropped` = socket error; `closed` = by the server. A healthy listener shows a handful of `closed`/`dropped` a day. |
| `kickgateway.event.delivery_lag` | histogram (s) | `kick.event_type` | **End to end**: the webhook reached the gateway -> the published contract reached a subscriber. Measured on the Api's own live-feed consumer, so it is exactly what a downstream app sees: outbox delivery + RabbitMQ + consume. This is the "does the system react to new messages" number. |

### Twitch side - meter `TailoredApps.TwitchGateway` (`ServiceDefaults/TwitchGatewayMetrics.cs`)

Same idea, own meter name, so `twitchgateway_*` in Prometheus isolates the platform.

| Metric | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `twitchgateway.ingest.duration` | histogram (s) | `twitch.event`, `outcome` | EventSub notification (or polled Helix state) received -> inbox row + outbox publish committed, including the queue wait. `twitch.event` = the EventSub type (`channel.chat.message`) or `helix.stream.online` etc. `outcome`: `published`, `duplicate`, `error`. |
| `twitchgateway.ingest.queue_depth` | gauge | | Events waiting for an ingest worker. |
| `twitchgateway.ingest.dropped` | counter | `reason` | Events thrown away because a worker's queue was full. Must stay 0. |
| `twitchgateway.eventsub.session.events` | counter | `reason` | EventSub session lifecycle: `connected`, `reconnect` (Twitch-requested move), `closed`, `keepalive_timeout`, `error`, `revoked`, `subscribe_failed`, `unauthorized` (bot token refused - refreshed automatically; if it keeps coming, reconnect the bot at `/admin/twitch`), `no_token` (no bot connected). |
| `twitchgateway.eventsub.subscriptions` | gauge | | Enabled chat subscriptions across sessions = channels x enabled types. 0 while the bot token is missing. |
| `twitchgateway.audio.segments` | counter | `outcome` | Capture: `published`, `ad_skipped` (stitched ad break), `fetch_failed`, `too_large`. |
| `twitchgateway.audio.captures` | gauge | | Channels whose live audio is being captured right now. |
| `twitchgateway.event.delivery_lag` | histogram (s) | `twitch.event` | End to end, measured on the Api's Twitch live-feed tap. |

### MassTransit - meter `MassTransit` (every service)

Built-in instrumentation, enabled in `ServiceDefaults`. The useful ones:

| Metric | Meaning |
| --- | --- |
| `messaging.masstransit.consume.duration`, `.consume` | Consumer time and count, tagged `messaging.masstransit.endpoint` (queue), `.message_type`, `.consumer_type`. |
| `messaging.masstransit.consume.errors` | Faulted consumes, after retries: the message went to `<queue>_error` (or was discarded on the throwaway queues). Tagged `messaging.masstransit.exception_type`. |
| `messaging.masstransit.consume.retries` | In-process retries (`UseMessageRetry`). Rising without errors = transient trouble being absorbed. |
| `messaging.masstransit.delivery.duration` | Sent -> consume start, as the consumer sees it (broker + outbox latency). |
| `messaging.masstransit.receive.duration`, `.receive.errors` | Transport-level receive per queue. |
| `messaging.masstransit.outbox.delivery`, `.outbox.delivery.errors` | Outbox -> RabbitMQ deliveries by the Api and the realtime listener. Errors = broker unreachable; the messages wait in `OutboxMessage` and nothing is lost, but every subscriber falls behind. |

Plus ASP.NET Core (`http.server.request.duration` - hq-config provisions the "ASP.NET Core"
dashboards 19924/19925 for it), `HttpClient` and .NET runtime metrics.

### Traces

`ServiceDefaults` enables ASP.NET Core, `HttpClient` and MassTransit activity sources, so a
webhook shows up in Tempo as one trace: HTTP request -> publish -> every consumer that took
it. Sampling is parent-based at the webhook / frame root (10 % by default), so a kept trace is
always complete. A Loki line with `trace_id` links to its trace.

### Logs

- MassTransit logs every fault at **Error** as `R-FAULT <queue> <message id> <message
  type> <consumer>(<duration>)` with the exception, and retries as `R-RETRY ...`
  (category `MassTransit`).
- The realtime listener warns `Failed to ingest realtime frame ... - frame lost` when a
  frame is dropped after its retries, and `Realtime ingest queue full ...` (at most once per
  10 s) when frames are dropped for lack of room.
- The Api logs `Duplicate webhook ...` at Information when Kick redelivers (a burst of
  these right after slow `kickgateway.webhook.duration` samples = Kick timed out on us).

On HQ every line now reaches Loki twice: once through Alloy's Docker tail (labels
`container`, `compose_project`, `compose_service`) and once over OTLP (with `trace_id` and the
structured attributes). hq-config offers two opt-outs for the Docker copy:
`monitoring_docker_logs_drop_regex: "kickgateway-.*"` in its `servers.yml`, or the compose
label `monitoring.logs: "otlp"` per service. Keep the Docker copy until OTLP logs are proven
in Loki: a container that dies during startup never gets to export anything.

## Alert rules (PromQL, Grafana-managed)

Expressions assume the Prometheus names above and HQ's labels. `for` is a suggestion. On HQ the
provisioned rules live in hq-config
`ansible/roles/monitoring/templates/grafana/provisioning/alerting/rules.yml.j2` (folder
`hq-config`); the RabbitMQ one there is `hq-rabbitmq-queue-no-consumer`.

| Alert | Expression | `for` |
| --- | --- | --- |
| **Consumer faults** (a message landed in `_error` / was discarded) | `sum by (service_name, messaging_masstransit_endpoint, messaging_masstransit_message_type, messaging_masstransit_exception_type) (increase(messaging_masstransit_consume_errors_total[10m])) > 0` | 0m |
| **Faulted messages parked** (broker side; works without any app export) | `sum by (vhost, queue) (rabbitmq_queue_messages{queue=~".+_(error|skipped)"}) > 0` | 5m |
| Work queue without a consumer (the existing `hq-rabbitmq-queue-no-consumer`, minus `_error`/`_skipped`, which never have one) | `(rabbitmq_queue_messages_ready{queue!~".+_(error|skipped)"} > 0) and on (vhost, queue) (rabbitmq_queue_consumers == 0)` | 15m |
| Webhook handler errors (Kick is retrying against us) | `sum(increase(kickgateway_webhook_duration_seconds_count{outcome="error"}[10m])) > 0` | 0m |
| Webhook handler slow (p95 over 1 s) | `histogram_quantile(0.95, sum by (le) (rate(kickgateway_webhook_duration_seconds_bucket{outcome="published"}[5m]))) > 1` | 5m |
| Event delivery lag (p95 over 5 s end to end) | `histogram_quantile(0.95, sum by (le) (rate(kickgateway_event_delivery_lag_seconds_bucket[5m]))) > 5` | 5m |
| Realtime frames dropped or lost | `sum(increase(kickgateway_realtime_ingest_dropped_total[5m])) > 0 or sum(increase(kickgateway_realtime_ingest_duration_seconds_count{outcome="error"}[5m])) > 0` | 0m |
| Realtime ingest backing up | `max(kickgateway_realtime_ingest_queue_depth) > 500` | 2m |
| **Realtime listener refused by Pusher** (Kick rotated the app key - set `KICK_REALTIME_APP_KEY`, see README) | `sum(increase(kickgateway_realtime_connection_errors_total{reason="refused"}[10m])) > 0` | 0m |
| Realtime ingest silent (listener up, nothing committed for 15 min - Pusher refusing, roster empty or the sidecar failing) | `(sum(increase(kickgateway_realtime_ingest_duration_seconds_count[15m])) or vector(0)) == 0 and on () (count(target_info{service_name="kickgateway-realtime"}) > 0)` | 10m |
| **Twitch bot token refused** (reconnect the bot at `/admin/twitch`) | `sum(increase(twitchgateway_eventsub_session_events_total{reason=~"unauthorized|no_token"}[15m])) > 2` | 0m |
| Twitch EventSub flapping (sessions dropping or failing to subscribe) | `sum(increase(twitchgateway_eventsub_session_events_total{reason=~"error|keepalive_timeout|subscribe_failed"}[15m])) > 10` | 0m |
| Twitch events dropped or lost | `sum(increase(twitchgateway_ingest_dropped_total[5m])) > 0 or sum(increase(twitchgateway_ingest_duration_seconds_count{outcome="error"}[5m])) > 0` | 0m |
| Twitch ingest silent (listener up with subscriptions, nothing committed for 15 min) | `(sum(increase(twitchgateway_ingest_duration_seconds_count[15m])) or vector(0)) == 0 and on () (max(twitchgateway_eventsub_subscriptions) > 0)` | 10m |
| Outbox cannot reach the broker | `sum by (service_name) (increase(messaging_masstransit_outbox_delivery_errors_total[5m])) > 0` | 2m |
| Api stopped reporting | `absent_over_time(kickgateway_webhook_duration_seconds_count[15m])` (series go stale ~5 min after the last push) | 0m |

Kick's own delay is informational: graph `histogram_quantile(0.95, sum by (le) (rate(kickgateway_webhook_kick_delay_seconds_bucket[5m])))` next to the handler duration to tell "Kick is late" from "we are slow".

## Runbook: a `<queue>_error` queue has messages

MassTransit moves a message to `<queue>_error` after its consumer threw (after the
endpoint's retries). Nothing consumes `_error` queues **by design**, so "queue has messages
but no consumer" is the wrong signal for them - alert on the count (rule above) and exclude
`_error`/`_skipped` from the no-consumer rule.

1. **Read why.** RabbitMQ management UI (`http://10.10.0.1:15672` on HQ) -> the `_error`
   queue -> *Get messages* (Ack mode: *Nack message requeue true*) -> headers
   `MT-Fault-ExceptionType`, `MT-Fault-Message`, `MT-Fault-StackTrace`, `MT-Fault-Timestamp`,
   `MT-Host-*` (which process). The consumer's container log has the same line as `R-FAULT`.
2. **Fix the cause** and deploy.
3. **Put the messages back**: Shovel plugin from `<queue>_error` to `<queue>` (the UI's
   *Move messages* button needs `rabbitmq_shovel_management`), or purge them if they are
   worthless. `LiveTranscript` slices are the durable record of what was said - prefer moving.

Retry policy today: the Api's SQL-backed consumers (`kickgateway-live-transcripts`,
`kickgateway-live-transcript-translations`) retry 5 times (1, 3, 5, 7, 9 s) before faulting;
the realtime listener retries a frame's commit twice (250, 500 ms) and then drops it with a
warning; the transcriber, translator and subtitles queues discard faults (throwaway,
time-sensitive data). Faults that survive the retries are real and want a human.
