/*
  Platform in the analytics indexes - the hand-run half of migration 20261106002536_AddPlatformToAnalyticsIndexes.

  Run it on the kickgateway database BEFORE deploying the Api that contains that migration. The migration checks
  every index by name, so after this script it finds them all in place and only records itself in
  __EFMigrationsHistory. Safe to re-run (every step checks first) and to interrupt (a re-run carries on).
  Keep the list in sync with the migration's Added / Replaced arrays.

  ONLINE = ON where the edition supports it (EngineEdition 3 = Enterprise / Developer, 5 / 8 = Azure). Otherwise
  each CREATE holds a shared lock on its table for the duration of the build: reads keep working (analytics runs
  READ UNCOMMITTED), inserts into THAT table wait - the projector retries its batch, the transcript consumers retry
  the message. The ingest inboxes (ReceivedWebhooks / ReceivedRealtimeEvents / ReceivedTwitchEvents) are not touched.

  Disk: every new index needs room for itself, plus the sort in tempdb (SORT_IN_TEMPDB). Check first:
      EXEC sp_spaceused N'ChatMessages';  EXEC sp_spaceused N'ChatterEvents';  EXEC sp_spaceused N'LiveTranscripts';
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @online bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @with nvarchar(100) = CASE WHEN @online = 1 THEN N' WITH (ONLINE = ON, SORT_IN_TEMPDB = ON)' ELSE N' WITH (SORT_IN_TEMPDB = ON)' END;
DECLARE @msg nvarchar(400) = CONCAT(N'Edition: ', CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                                    N' -> ONLINE = ', CASE WHEN @online = 1 THEN N'ON' ELSE N'OFF' END);
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- New indexes first (smallest tables first), the ones they replace after: queries always have an index to use.
DECLARE @ix TABLE (Seq int IDENTITY PRIMARY KEY, Op varchar(6) NOT NULL, Tbl sysname NOT NULL, Name sysname NOT NULL, Cols nvarchar(400) NULL);
INSERT @ix (Op, Tbl, Name, Cols) VALUES
    ('create', N'LiveTranscripts', N'IX_LiveTranscripts_ChannelSlug_Platform_StartedAt',        N'([ChannelSlug], [Platform], [StartedAt]) INCLUDE ([AudioSeconds], [Language], [Confidence])'),
    ('create', N'LiveTranscripts', N'IX_LiveTranscripts_ChannelSlug_Platform_EndedAt',          N'([ChannelSlug], [Platform], [EndedAt])'),
    ('create', N'ChatterEvents',   N'IX_ChatterEvents_ChannelSlug_Platform_OccurredAt',         N'([ChannelSlug], [Platform], [OccurredAt]) INCLUDE ([Kind], [Amount], [RefId])'),
    ('create', N'ChatterEvents',   N'IX_ChatterEvents_UserId_Platform_OccurredAt',              N'([UserId], [Platform], [OccurredAt])'),
    ('create', N'ChatterEvents',   N'IX_ChatterEvents_CounterpartUserId_Platform_OccurredAt',   N'([CounterpartUserId], [Platform], [OccurredAt])'),
    ('create', N'ChatterEvents',   N'IX_ChatterEvents_RefId_Kind',                              N'([RefId], [Kind]) INCLUDE ([Platform])'),
    ('create', N'ChatMessages',    N'IX_ChatMessages_ChannelSlug_Platform_CreatedAt',           N'([ChannelSlug], [Platform], [CreatedAt]) INCLUDE ([SenderUserId], [ReplyToMessageId], [ReplyToUserId])'),
    ('create', N'ChatMessages',    N'IX_ChatMessages_ChannelSlug_Platform_SenderUserId_CreatedAt', N'([ChannelSlug], [Platform], [SenderUserId], [CreatedAt])'),
    ('create', N'ChatMessages',    N'IX_ChatMessages_SenderUsername_Platform_CreatedAt',        N'([SenderUsername], [Platform], [CreatedAt]) INCLUDE ([SenderUserId])'),
    ('create', N'ChatMessages',    N'IX_ChatMessages_SenderUserId_Platform_CreatedAt',          N'([SenderUserId], [Platform], [CreatedAt]) INCLUDE ([SenderUsername], [ChannelSlug])'),
    ('create', N'ChatMessages',    N'IX_ChatMessages_ReplyToUserId_Platform_CreatedAt',         N'([ReplyToUserId], [Platform], [CreatedAt])'),
    ('create', N'ChatMessages',    N'IX_ChatMessages_CreatedAt',                                N'([CreatedAt]) INCLUDE ([Platform], [ChannelSlug], [SenderUserId])'),
    ('drop',   N'LiveTranscripts', N'IX_LiveTranscripts_ChannelSlug_StartedAt',                 NULL),
    ('drop',   N'LiveTranscripts', N'IX_LiveTranscripts_ChannelSlug_EndedAt',                   NULL),
    ('drop',   N'ChatterEvents',   N'IX_ChatterEvents_ChannelSlug_OccurredAt',                  NULL),
    ('drop',   N'ChatterEvents',   N'IX_ChatterEvents_UserId_OccurredAt',                       NULL),
    ('drop',   N'ChatterEvents',   N'IX_ChatterEvents_CounterpartUserId_OccurredAt',            NULL),
    ('drop',   N'ChatterEvents',   N'IX_ChatterEvents_RefId',                                   NULL),
    ('drop',   N'ChatMessages',    N'IX_ChatMessages_ChannelSlug_CreatedAt',                    NULL),
    ('drop',   N'ChatMessages',    N'IX_ChatMessages_ChannelSlug_SenderUserId_CreatedAt',       NULL),
    ('drop',   N'ChatMessages',    N'IX_ChatMessages_SenderUsername_CreatedAt',                 NULL),
    ('drop',   N'ChatMessages',    N'IX_ChatMessages_SenderUserId_CreatedAt',                   NULL),
    ('drop',   N'ChatMessages',    N'IX_ChatMessages_ReplyToUserId_CreatedAt',                  NULL);

DECLARE @op varchar(6), @tbl sysname, @name sysname, @cols nvarchar(400), @sql nvarchar(max), @exists bit, @t0 datetime2;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Op, Tbl, Name, Cols FROM @ix ORDER BY Seq;
OPEN c;
FETCH NEXT FROM c INTO @op, @tbl, @name, @cols;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @exists = CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(QUOTENAME(@tbl)) AND name = @name) THEN 1 ELSE 0 END;
    IF (@op = 'create' AND @exists = 0) OR (@op = 'drop' AND @exists = 1)
    BEGIN
        SET @sql = CASE WHEN @op = 'create'
                        THEN CONCAT(N'CREATE INDEX ', QUOTENAME(@name), N' ON ', QUOTENAME(@tbl), N' ', @cols, @with, N';')
                        ELSE CONCAT(N'DROP INDEX ', QUOTENAME(@name), N' ON ', QUOTENAME(@tbl), N';') END;
        SET @msg = CONCAT(CONVERT(nvarchar(19), SYSDATETIME(), 120), N'  ', @op, N' ', @name, N' ...');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @t0 = SYSDATETIME();
        EXEC sys.sp_executesql @sql;
        SET @msg = CONCAT(CONVERT(nvarchar(19), SYSDATETIME(), 120), N'  done in ', DATEDIFF(second, @t0, SYSDATETIME()), N' s');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SET @msg = CONCAT(N'skip   ', @op, N' ', @name, N' (already done)');
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    FETCH NEXT FROM c INTO @op, @tbl, @name, @cols;
END
CLOSE c;
DEALLOCATE c;

-- Result: the nonclustered indexes of the three tables.
SELECT OBJECT_NAME(i.object_id) AS [Table], i.name AS [Index]
FROM sys.indexes i
WHERE i.object_id IN (OBJECT_ID(N'[ChatMessages]'), OBJECT_ID(N'[ChatterEvents]'), OBJECT_ID(N'[LiveTranscripts]'))
  AND i.type = 2
ORDER BY [Table], [Index];
