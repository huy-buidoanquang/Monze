namespace Monze.Infrastructure.Persistence;

internal static class PostgresMeetingQueries
{
    internal const string BindMeetingRoom = """
        WITH target AS (
          SELECT id
          FROM meeting_session
          WHERE voice_channel_id = @voice
            AND clan_id = @clan
            AND status IN ('suggested', 'live')
            AND (room_id IS NULL OR room_id = @room)
          ORDER BY id DESC
          LIMIT 1
        ), updated AS (
          UPDATE meeting_session AS item
          SET status = 'live', room_id = @room
          FROM target
          WHERE item.id = target.id
          RETURNING item.id, item.clan_id, item.voice_channel_id
        ), claimed AS (
          UPDATE voice_claim AS claim
          SET expires_at = 'infinity'::timestamptz
          FROM updated
          WHERE claim.clan_id = updated.clan_id
            AND claim.voice_channel_id = updated.voice_channel_id
            AND claim.session_id = updated.id
          RETURNING claim.session_id
        )
        SELECT id FROM updated;
        """;

    internal const string BindMeetingRoomByVoiceChannel = """
        WITH target AS (
          SELECT id
          FROM meeting_session
          WHERE voice_channel_id = @voice
            AND status IN ('suggested', 'live')
            AND (room_id IS NULL OR room_id = @room)
          ORDER BY id DESC
          LIMIT 1
        ), updated AS (
          UPDATE meeting_session AS item
          SET status = 'live', room_id = @room
          FROM target
          WHERE item.id = target.id
          RETURNING item.id, item.clan_id, item.voice_channel_id
        ), claimed AS (
          UPDATE voice_claim AS claim
          SET expires_at = 'infinity'::timestamptz
          FROM updated
          WHERE claim.clan_id = updated.clan_id
            AND claim.voice_channel_id = updated.voice_channel_id
            AND claim.session_id = updated.id
          RETURNING claim.session_id
        )
        SELECT id FROM updated;
        """;
}
