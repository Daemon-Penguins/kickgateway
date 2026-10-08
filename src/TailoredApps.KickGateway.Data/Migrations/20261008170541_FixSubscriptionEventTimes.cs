using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <summary>
    /// Data-only fix: subscription / gifted-sub <see cref="ChatterEvent"/> rows were stamped with the payload's
    /// <c>created_at</c>, which is the subscription's start date (repeated on every renewal), not the event time.
    /// Re-stamp them with the inbox receive time — the projector does the same from now on.
    /// Kinds: 2 = SubscriptionNew, 3 = SubscriptionRenewal, 4 = SubscriptionGift. Sources: 1 = webhook, 2 = realtime.
    /// </summary>
    public partial class FixSubscriptionEventTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE e SET e.OccurredAt = w.ReceivedAt
                FROM ChatterEvents e
                JOIN ReceivedWebhooks w ON w.MessageId = e.SourceId
                WHERE e.Source = 1 AND e.Kind IN (2, 3, 4);

                UPDATE e SET e.OccurredAt = r.ReceivedAt
                FROM ChatterEvents e
                JOIN ReceivedRealtimeEvents r ON r.DedupeKey = e.SourceId
                WHERE e.Source = 2 AND e.Kind IN (2, 3, 4);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible (the wrong timestamps are not worth restoring); a replay of the inbox re-derives the rows.
        }
    }
}
