-- Meeting invitations and their pending replies belong to the text channel
-- that started the meeting. Existing sent messages are left untouched.
UPDATE outbox_delivery AS item
SET channel_id = session.text_channel_id
FROM meeting_session AS session
WHERE item.meeting_session_id = session.id
  AND item.kind = 'Announcement'
  AND item.status IN ('pending', 'sending')
  AND item.external_message_id IS NULL
  AND session.text_channel_id IS NOT NULL
  AND item.channel_id <> session.text_channel_id;

-- A previously queued voice notification must not be reused as a reply target
-- after the delivery channel is corrected.
UPDATE meeting_session AS session
SET notification_channel_id = session.text_channel_id,
    notification_message_id = NULL
WHERE session.status IN ('requested', 'suggested')
  AND session.text_channel_id IS NOT NULL
  AND session.voice_channel_id IS NOT NULL
  AND session.notification_channel_id = session.voice_channel_id
  AND session.notification_channel_id <> session.text_channel_id;
