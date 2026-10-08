using Google.Protobuf;
using Mezon.Net.Core;
using Mezon.Net.Internal.Realtime;
using RealtimeError = Mezon.Net.Internal.Realtime.Error;

namespace Monze.Simulator;

/// <summary>Realtime envelopes (<c>MezonMessageType.Realtime</c> frames).</summary>
public sealed partial class SimTransporter
{
    // Mezon.Net.Client/Messaging/EphemeralMessageCodes.cs (v1.6.2, internal).
    private const int EphemeralSend = 12;
    private const int EphemeralUpdate = 14;
    private const int EphemeralDelete = 15;

    // mezon-api rejects stream modes below StreamModeChannel (2).
    private const int MinimumStreamMode = 2;

    // realtime.proto Error.Code values.
    private const int RuntimeException = 0;
    private const int BadInput = 3;

    private void HandleRealtimeFrame(Connection connection, int cid, byte[] payload)
    {
        var envelope = Envelope.Parser.ParseFrom(payload);
        var operation = envelope.MessageCase == Envelope.MessageOneofCase.Ping
            ? SimOperations.Heartbeat
            : envelope.MessageCase.ToString();
        if (envelope.Cid != cid)
        {
            _simulator.Recorder.RecordViolation(SessionId, operation, $"Envelope cid {envelope.Cid} differs from frame cid {cid}.");
        }

        if (!SimOperations.Realtime.Contains(operation))
        {
            // No answer: an awaited acknowledgement times out in the SDK.
            _simulator.Recorder.RecordUnmodelled(SessionId, operation, "Realtime envelope.");
            return;
        }

        Run(connection, operation, fault => AnswerRealtime(connection, envelope, fault));
    }

    private Reply AnswerRealtime(Connection connection, Envelope envelope, SimFault? fault)
    {
        switch (envelope.MessageCase)
        {
            case Envelope.MessageOneofCase.Ping:
                RecordHeartbeat(fault);
                return fault?.Kind == SimFaultKind.Error
                    ? Reply.None
                    : Ack(new Envelope { Cid = envelope.Cid, Pong = new Pong() });
            case Envelope.MessageOneofCase.ClanJoin:
                return ClanJoin(connection, envelope.ClanJoin, fault);
            case Envelope.MessageOneofCase.ChannelJoin:
                return ChannelPresence(SimActionKind.ChannelJoin, envelope.ChannelJoin.ClanId, envelope.ChannelJoin.ChannelId, envelope.ChannelJoin, fault);
            case Envelope.MessageOneofCase.ChannelLeave:
                return ChannelPresence(SimActionKind.ChannelLeave, envelope.ChannelLeave.ClanId, envelope.ChannelLeave.ChannelId, envelope.ChannelLeave, fault);
            case Envelope.MessageOneofCase.ChannelMessageSend:
                return ChannelMessageSend(envelope.Cid, envelope.ChannelMessageSend, fault);
            case Envelope.MessageOneofCase.EphemeralMessageSend:
                return EphemeralMessageSend(envelope.Cid, envelope.EphemeralMessageSend, fault);
            case Envelope.MessageOneofCase.ChannelMessageRemove:
                return ChannelMessageRemove(envelope.Cid, envelope.ChannelMessageRemove, fault);
            default:
                _simulator.Recorder.RecordUnmodelled(SessionId, envelope.MessageCase.ToString(), "Realtime envelope.");
                return Reply.None;
        }
    }

    private Reply ClanJoin(Connection connection, ClanJoin join, SimFault? fault)
    {
        var status = fault?.Kind == SimFaultKind.Error
            ? fault.Code
            : World.FindClan(join.ClanId) is null
                ? MezonStatusCode.NotFound
                : World.IsMember(join.ClanId, BotId) ? MezonStatusCode.Ok : MezonStatusCode.PermissionDenied;
        if (status == MezonStatusCode.Ok)
        {
            connection.JoinedClans.TryAdd(join.ClanId, 0);
        }

        _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.ClanJoin,
            Operation = SimOperations.ClanJoin,
            SessionId = SessionId,
            ClanId = join.ClanId,
            ResponseCode = (int)status,
            Fault = fault?.Kind,
            Request = join
        });
        return Reply.None;
    }

    private Reply ChannelPresence(SimActionKind kind, long clanId, long channelId, IMessage request, SimFault? fault)
    {
        var channel = World.FindChannel(channelId);
        var status = fault?.Kind == SimFaultKind.Error
            ? fault.Code
            : channel is null || channel.ClanId != clanId ? MezonStatusCode.NotFound : MezonStatusCode.Ok;
        _simulator.Recorder.Record(new SimAction
        {
            Kind = kind,
            Operation = kind.ToString(),
            SessionId = SessionId,
            ClanId = clanId,
            ChannelId = channelId,
            ResponseCode = (int)status,
            Fault = fault?.Kind,
            Request = request
        });
        return Reply.None;
    }

    private Reply ChannelMessageSend(int cid, ChannelMessageSend send, SimFault? fault)
    {
        var references = send.References.Where(static reference => reference.RefType == 0 && reference.MessageRefId > 0).ToList();
        var draft = new SimAction
        {
            Kind = SimActionKind.SendMessage,
            Operation = SimOperations.ChannelMessageSend,
            SessionId = SessionId,
            ClanId = send.ClanId,
            ChannelId = send.ChannelId,
            ContentJson = send.Content,
            ReplyToMessageId = references.Count > 0 ? references[0].MessageRefId : null,
            Mentions = send.Mentions.ToList(),
            MentionEveryone = send.MentionEveryone,
            Code = send.Code,
            Request = send
        };
        if (fault?.Kind == SimFaultKind.Error)
        {
            return RejectRealtime(cid, draft, fault.Code, fault, RuntimeException, fault.Detail);
        }

        if (send.Code != 0)
        {
            _simulator.Recorder.RecordUnmodelled(SessionId, SimOperations.ChannelMessageSend, $"Message code {send.Code}.");
            return RejectRealtime(cid, draft, MezonStatusCode.Unimplemented, fault, BadInput, "message code not modelled");
        }

        var channel = World.FindChannel(send.ChannelId);
        if (TargetError(channel, send.ClanId, send.Mode) is { } error)
        {
            return RejectRealtime(cid, draft, error, fault, BadInput, error.ToString());
        }

        CheckStream(SimOperations.ChannelMessageSend, channel, send.Mode, send.IsPublic);
        var message = World.StoreMessage(new SimMessage(
            World.NextId(),
            channel!.ClanId,
            channel.Id,
            BotId,
            send.Content,
            0,
            World.Time.GetUtcNow(),
            draft.ReplyToMessageId,
            draft.MentionUserIds,
            send.MentionEveryone,
            null,
            0,
            false));
        _simulator.Recorder.Record(draft with { MessageId = message.Id, Fault = fault?.Kind });
        var after = _simulator.Options.EchoBotMessages
            ? () => _simulator.Echo(message)
            : (Action?)null;
        return Ack(AckEnvelope(cid, message.Id, channel.Id), after);
    }

    private Reply EphemeralMessageSend(int cid, EphemeralMessageSend ephemeral, SimFault? fault)
    {
        var send = ephemeral.Message ?? new ChannelMessageSend();
        var receivers = ephemeral.ReceiverIds.ToList();
        var kind = send.Code switch
        {
            0 or EphemeralSend => SimActionKind.SendEphemeral,
            EphemeralUpdate => SimActionKind.UpdateEphemeral,
            EphemeralDelete => SimActionKind.DeleteEphemeral,
            _ => (SimActionKind?)null
        };
        var draft = new SimAction
        {
            Kind = kind ?? SimActionKind.SendEphemeral,
            Operation = SimOperations.EphemeralMessageSend,
            SessionId = SessionId,
            ClanId = send.ClanId,
            ChannelId = send.ChannelId,
            MessageId = send.Id,
            TargetUserId = receivers.FirstOrDefault(),
            ReceiverIds = receivers,
            ContentJson = send.Content,
            Mentions = send.Mentions.ToList(),
            Code = send.Code,
            Request = ephemeral
        };
        if (fault?.Kind == SimFaultKind.Error)
        {
            return RejectRealtime(cid, draft, fault.Code, fault, RuntimeException, fault.Detail);
        }

        if (kind is null)
        {
            _simulator.Recorder.RecordUnmodelled(SessionId, SimOperations.EphemeralMessageSend, $"Ephemeral code {send.Code}.");
            return RejectRealtime(cid, draft, MezonStatusCode.Unimplemented, fault, BadInput, "ephemeral code not modelled");
        }

        if (ephemeral.Message is null || receivers.Count == 0)
        {
            return RejectRealtime(cid, draft, MezonStatusCode.InvalidArgument, fault, BadInput, "message and receivers are required");
        }

        var channel = World.FindChannel(send.ChannelId);
        if (TargetError(channel, send.ClanId, send.Mode) is { } error)
        {
            return RejectRealtime(cid, draft, error, fault, BadInput, error.ToString());
        }

        if (receivers.Any(receiver => !World.IsMember(channel!.ClanId, receiver)))
        {
            return RejectRealtime(cid, draft, MezonStatusCode.InvalidArgument, fault, BadInput, "every receiver must be a clan member");
        }

        CheckStream(SimOperations.EphemeralMessageSend, channel, send.Mode, send.IsPublic);
        long messageId;
        if (kind == SimActionKind.SendEphemeral)
        {
            messageId = send.Id != 0 ? send.Id : World.NextId();
            World.StoreMessage(new SimMessage(
                messageId,
                channel!.ClanId,
                channel.Id,
                BotId,
                send.Content,
                EphemeralSend,
                World.Time.GetUtcNow(),
                null,
                draft.MentionUserIds,
                send.MentionEveryone,
                receivers[0],
                0,
                false));
        }
        else
        {
            var existing = World.FindMessage(send.Id);
            if (existing is null
                || existing.Deleted
                || existing.ChannelId != channel!.Id
                || existing.SenderId != BotId
                || existing.EphemeralReceiverId is not long receiver
                || !receivers.Contains(receiver))
            {
                return RejectRealtime(cid, draft, MezonStatusCode.NotFound, fault, BadInput, "no such ephemeral message for this receiver");
            }

            messageId = existing.Id;
            if (kind == SimActionKind.UpdateEphemeral)
            {
                World.ReviseMessage(messageId, send.Content);
            }
            else
            {
                World.DeleteMessage(messageId);
            }
        }

        _simulator.Recorder.Record(draft with { MessageId = messageId, Fault = fault?.Kind });
        return Ack(AckEnvelope(cid, messageId, channel.Id));
    }

    private Reply ChannelMessageRemove(int cid, ChannelMessageRemove remove, SimFault? fault)
    {
        var draft = new SimAction
        {
            Kind = SimActionKind.DeleteMessage,
            Operation = SimOperations.ChannelMessageRemove,
            SessionId = SessionId,
            ClanId = remove.ClanId,
            ChannelId = remove.ChannelId,
            MessageId = remove.MessageId,
            Request = remove
        };
        if (fault?.Kind == SimFaultKind.Error)
        {
            return RejectRealtime(cid, draft, fault.Code, fault, RuntimeException, fault.Detail);
        }

        var channel = World.FindChannel(remove.ChannelId);
        if (TargetError(channel, remove.ClanId, remove.Mode) is { } error)
        {
            return RejectRealtime(cid, draft, error, fault, BadInput, error.ToString());
        }

        var message = World.FindMessage(remove.MessageId);
        if (message is null || message.Deleted || message.ChannelId != channel!.Id || message.IsEphemeral)
        {
            return RejectRealtime(cid, draft, MezonStatusCode.NotFound, fault, BadInput, "no such message");
        }

        if (message.SenderId != BotId)
        {
            // Moderating other users' messages is not modelled.
            return RejectRealtime(cid, draft, MezonStatusCode.PermissionDenied, fault, BadInput, "only the sender can delete a message");
        }

        CheckStream(SimOperations.ChannelMessageRemove, channel, remove.Mode, remove.IsPublic);
        World.DeleteMessage(message.Id);
        _simulator.Recorder.Record(draft with { Fault = fault?.Kind });
        return Ack(AckEnvelope(cid, message.Id, channel.Id));
    }

    private MezonStatusCode? TargetError(SimChannel? channel, long clanId, int mode)
    {
        if (mode < MinimumStreamMode)
        {
            return MezonStatusCode.InvalidArgument;
        }

        if (channel is null || channel.ClanId != clanId)
        {
            return MezonStatusCode.NotFound;
        }

        // The bot is a participant of every direct-message channel it has.
        if (World.DirectPeer(channel.Id) is not null)
        {
            return null;
        }

        return World.IsMember(channel.ClanId, BotId) ? null : MezonStatusCode.PermissionDenied;
    }

    /// <summary>
    /// The server routes by mode and is_public; a value that disagrees with
    /// the channel would reach the wrong stream, so it is a contract violation.
    /// </summary>
    private void CheckStream(string operation, SimChannel? channel, int mode, bool isPublic)
    {
        if (channel is null)
        {
            return;
        }

        if (mode != channel.StreamMode)
        {
            _simulator.Recorder.RecordViolation(SessionId, operation, $"Stream mode {mode} does not match channel type {channel.Type} (expected {channel.StreamMode}).");
        }

        if (isPublic == channel.IsPrivate)
        {
            _simulator.Recorder.RecordViolation(SessionId, operation, $"is_public={isPublic} does not match channel privacy (private={channel.IsPrivate}).");
        }
    }

    private Reply RejectRealtime(int cid, SimAction draft, MezonStatusCode status, SimFault? fault, int errorCode, string? detail)
    {
        _simulator.Recorder.Record(draft with { ResponseCode = (int)status, Fault = fault?.Kind });
        var error = new RealtimeError { Code = errorCode, Message = detail ?? status.ToString() };
        error.Context.Add("status", ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ack(new Envelope { Cid = cid, Error = error });
    }

    private static Envelope AckEnvelope(int cid, long messageId, long channelId)
        => new()
        {
            Cid = cid,
            // mezon-api channelMessageSend / sendEphemeralMessageToBot fill only these two fields.
            ChannelMessageAck = new ChannelMessageAck { MessageId = messageId, ChannelId = channelId }
        };

    private static Reply Ack(Envelope envelope, Action? after = null)
        => new([new Frame(MezonMessageType.Realtime, 0, 0, envelope.ToByteArray(), Tracked: false)], after);
}
