# TailoredApps.TwitchGateway.Contracts

MassTransit message contracts published by the **Twitch** side of the gateway. A separate package from
`TailoredApps.KickGateway.Contracts`: reference only the platform you consume, and the two version
independently.

Every contract type has its own RabbitMQ **topic exchange**; the routing key is the broadcaster's
Twitch **login** (lowercase). Bind `#` for every channel or a login for one.

| Family | Contracts | Source |
| --- | --- | --- |
| `Chat` | `TwitchChatMessage`, `TwitchChatNotification` (subs, gifts, raids, announcements...), `TwitchChatMessageDeleted`, `TwitchChatCleared`, `TwitchChatUserMessagesCleared`, `TwitchChatSettingsUpdated` | EventSub WebSocket (`channel.chat.*`) |
| `Channel` | `TwitchStreamOnline`, `TwitchStreamOffline`, `TwitchChannelUpdated` | Helix `GET /streams` polling |
| `Channel` | `TwitchStreamStatsRequested` → `TwitchStreamStats` (request/response: live state, viewers, title/category, the running broadcast's VOD) | Helix on demand, answered by the gateway's Api |
| root | `TwitchEventUnknown` | any EventSub type without a typed contract |
| `Media` | `TwitchAudioSegment` (ephemeral, short TTL), `TwitchLiveTranscript` | live HLS capture + Whisper |

```csharp
cfg.ReceiveEndpoint("my-app-twitch-chat", e =>
{
    TwitchEventTopology.BindTwitchEvent<TwitchChatMessage>(e, "somestreamer");   // or no logins = "#"
    e.ConfigureConsumer<MyChatConsumer>(context);
});
```

All events carry the `ITwitchEvent` envelope: broadcaster login/id/name, EventSub type + version,
`MessageId`, `DedupeKey`, `ReceivedAt`, `TwitchTimestamp` and the raw `payload.event` JSON.
