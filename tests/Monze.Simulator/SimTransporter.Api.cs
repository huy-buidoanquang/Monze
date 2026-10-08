using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Mezon.Net.Core;
using Mezon.Net.Internal.Api;
using Mezon.Net.Internal.Realtime;

namespace Monze.Simulator;

/// <summary>Socket API calls (<c>MezonMessageType.Api</c> frames).</summary>
public sealed partial class SimTransporter
{
    private const int DefaultMessagePage = 50;
    private const int MaxMessagePage = 100;

    private void HandleApiFrame(Connection connection, int cid, byte[] payload)
    {
        var envelope = Envelope.Parser.ParseFrom(payload);
        if (envelope.MessageCase != Envelope.MessageOneofCase.ApiRequestEvent)
        {
            _simulator.Recorder.RecordViolation(SessionId, "Api", $"Api frame carries {envelope.MessageCase} instead of an ApiRequestEvent.");
            _ = connection.Enqueue(ApiError(cid, MezonStatusCode.InvalidArgument, "expected api_request_event"));
            return;
        }

        var request = envelope.ApiRequestEvent;
        if (envelope.Cid != cid)
        {
            _simulator.Recorder.RecordViolation(SessionId, request.ApiName, $"Envelope cid {envelope.Cid} differs from frame cid {cid}.");
        }

        if (!MezonApiMap.TryGetIndex(request.ApiName, out var index) || index != request.ApiIndex)
        {
            _simulator.Recorder.RecordViolation(SessionId, request.ApiName, $"api_index {request.ApiIndex} does not match MezonApiMap for '{request.ApiName}'.");
            _ = connection.Enqueue(ApiError(cid, MezonStatusCode.InvalidArgument, "api index mismatch"));
            return;
        }

        Run(connection, request.ApiName, fault => AnswerApi(cid, request, fault));
    }

    private Reply AnswerApi(int cid, ApiRequestEvent request, SimFault? fault)
    {
        ApiCall? call;
        try
        {
            call = ParseApi(request.ApiName, request.Body);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _simulator.Recorder.RecordViolation(SessionId, request.ApiName, $"Malformed request body: {ex.Message}");
            return new Reply([ApiError(cid, MezonStatusCode.InvalidArgument, "malformed request body")]);
        }

        if (call is null)
        {
            _simulator.Recorder.RecordUnmodelled(SessionId, request.ApiName, $"Socket API or request variant not modelled (index {request.ApiIndex}, {request.Body.Length} body bytes).");
            return new Reply([ApiError(cid, MezonStatusCode.Unimplemented, "not modelled by the Monze simulator")]);
        }

        if (fault?.Kind == SimFaultKind.Error)
        {
            _simulator.Recorder.Record(call.Draft with { ResponseCode = (int)fault.Code, Fault = fault.Kind });
            return new Reply([ApiError(cid, fault.Code, fault.Detail ?? "simulated failure")]);
        }

        var outcome = call.Execute();
        _simulator.Recorder.Record(outcome.Draft with { ResponseCode = (int)outcome.Status, Fault = fault?.Kind });
        return outcome.Status == MezonStatusCode.Ok
            ? new Reply([new Frame(MezonMessageType.Api, cid, 0, outcome.Response?.ToByteArray() ?? [], Tracked: false)])
            : new Reply([ApiError(cid, outcome.Status, outcome.Detail ?? outcome.Status.ToString())]);
    }

    private ApiCall? ParseApi(string name, ByteString body) => name switch
    {
        SimOperations.ListClanDescs => ListClanDescs(ListClanDescRequest.Parser.ParseFrom(body)),
        SimOperations.ListChannelDescs => ListChannelDescs(ListChannelDescsRequest.Parser.ParseFrom(body)),
        SimOperations.ListChannelDetail => ListChannelDetail(ListChannelDetailRequest.Parser.ParseFrom(body)),
        SimOperations.ListClanUsers => ListClanUsers(ListClanUsersRequest.Parser.ParseFrom(body)),
        SimOperations.ListChannelMessages => ListChannelMessages(ListChannelMessagesRequest.Parser.ParseFrom(body)),
        SimOperations.ListChannelVoiceUsers => ListChannelVoiceUsers(ListChannelUsersRequest.Parser.ParseFrom(body)),
        SimOperations.ListRoles => ListRoles(RoleListEventRequest.Parser.ParseFrom(body)),
        SimOperations.UpdateRole => UpdateRole(UpdateRoleRequest.Parser.ParseFrom(body)),
        SimOperations.UpdateChannelMessage => UpdateChannelMessage(ChannelMessageUpdate.Parser.ParseFrom(body)),
        SimOperations.SessionRefresh => SessionRefresh(SessionRefreshRequest.Parser.ParseFrom(body)),
        _ => null
    };

    private ApiCall ListClanDescs(ListClanDescRequest request)
    {
        var draft = Read(SimOperations.ListClanDescs, request);
        return new ApiCall(draft, () =>
        {
            var list = new ClanDescList();
            foreach (var clan in World.ClansOf(BotId).Take(_simulator.Options.ClanDiscoveryLimit ?? int.MaxValue))
            {
                list.Clandesc.Add(new ClanDesc
                {
                    ClanId = clan.Id,
                    ClanName = clan.Name,
                    CreatorId = clan.OwnerId,
                    WelcomeChannelId = clan.WelcomeChannelId
                });
            }

            return ApiOutcome.Ok(draft, list);
        });
    }

    private ApiCall ListChannelDescs(ListChannelDescsRequest request)
    {
        var draft = Read(SimOperations.ListChannelDescs, request) with { ClanId = request.ClanId, Code = request.ChannelType };
        return new ApiCall(draft, () =>
        {
            var list = new ChannelDescList();
            if (request.ClanId == 0)
            {
                // Clan 0 lists the bot's direct-message channels (DmChannelManager
                // asks for channel type Dm); group channels are not modelled.
                if (request.ChannelType is 0 or (int)ChannelType.Dm)
                {
                    foreach (var direct in World.DirectChannels)
                    {
                        list.Channeldesc.Add(DirectDescription(direct));
                    }
                }

                return ApiOutcome.Ok(draft, list);
            }

            if (ClanAccess(request.ClanId) is { } denied)
            {
                return ApiOutcome.Fail(draft, denied);
            }

            foreach (var channel in World.ChannelsOf(request.ClanId))
            {
                // Monze observed that the Channel list mode (1) also returns voice
                // rooms (Features/Meeting/MonzeBot.VoiceRooms.cs); 0 means "all".
                if (request.ChannelType is 0 or (int)ChannelType.Channel || channel.Type == request.ChannelType)
                {
                    list.Channeldesc.Add(_simulator.ToDescription(channel));
                }
            }

            return ApiOutcome.Ok(draft, list);
        });
    }

    private ApiCall ListChannelDetail(ListChannelDetailRequest request)
    {
        var draft = Read(SimOperations.ListChannelDetail, request) with { ChannelId = request.ChannelId };
        return new ApiCall(draft, () =>
        {
            var channel = World.FindChannel(request.ChannelId);
            if (channel is null)
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.NotFound);
            }

            var withClan = draft with { ClanId = channel.ClanId };
            if (World.DirectPeer(channel.Id) is not null)
            {
                return ApiOutcome.Ok(withClan, DirectDescription(channel));
            }

            return World.IsMember(channel.ClanId, BotId)
                ? ApiOutcome.Ok(withClan, _simulator.ToDescription(channel))
                : ApiOutcome.Fail(withClan, MezonStatusCode.PermissionDenied);
        });
    }

    private ApiCall ListClanUsers(ListClanUsersRequest request)
    {
        var draft = Read(SimOperations.ListClanUsers, request) with { ClanId = request.ClanId };
        return new ApiCall(draft, () =>
        {
            if (ClanAccess(request.ClanId) is { } denied)
            {
                return ApiOutcome.Fail(draft, denied);
            }

            var list = new ClanUserList { ClanId = request.ClanId };
            foreach (var member in World.MembersOf(request.ClanId))
            {
                var user = World.FindUser(member.UserId);
                if (user is null)
                {
                    continue;
                }

                var clanUser = new ClanUserList.Types.ClanUser
                {
                    ClanId = member.ClanId,
                    ClanNick = member.ClanNick ?? string.Empty,
                    User = new User
                    {
                        Id = user.Id,
                        Username = user.Username,
                        DisplayName = user.DisplayName,
                        AvatarUrl = user.Avatar,
                        JoinTimeSeconds = (uint)member.JoinedAt.ToUnixTimeSeconds()
                    }
                };
                clanUser.RoleId.Add(member.RoleIds.Order());
                list.ClanUsers.Add(clanUser);
            }

            return ApiOutcome.Ok(draft, list);
        });
    }

    private ApiCall ListChannelMessages(ListChannelMessagesRequest request)
    {
        var draft = Read(SimOperations.ListChannelMessages, request) with
        {
            ClanId = request.ClanId,
            ChannelId = request.ChannelId,
            MessageId = request.MessageId,
            Code = request.Direction
        };
        return new ApiCall(draft, () =>
        {
            var channel = World.FindChannel(request.ChannelId);
            if (channel is null || channel.ClanId != request.ClanId)
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.NotFound);
            }

            if (!World.IsMember(channel.ClanId, BotId))
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.PermissionDenied);
            }

            var limit = request.Limit <= 0 ? DefaultMessagePage : Math.Min(request.Limit, MaxMessagePage);
            var visible = World.MessagesIn(channel.Id).Where(static message => !message.Deleted && !message.IsEphemeral);

            // Direction 1 lists the anchor and newer messages (Monze relies on
            // this, Hosting/MonzeBot.Commands.cs); any other value lists the
            // anchor and older messages, newest first.
            var page = request.Direction == 1
                ? visible.Where(message => message.Id >= request.MessageId).OrderBy(static message => message.Id).Take(limit)
                : visible.Where(message => request.MessageId <= 0 || message.Id <= request.MessageId).OrderByDescending(static message => message.Id).Take(limit);
            var list = new ChannelMessageList();
            foreach (var message in page)
            {
                list.Messages.Add(_simulator.ToChannelMessage(message));
            }

            return ApiOutcome.Ok(draft, list);
        });
    }

    private ApiCall ListChannelVoiceUsers(ListChannelUsersRequest request)
    {
        var draft = Read(SimOperations.ListChannelVoiceUsers, request) with
        {
            ClanId = request.ClanId,
            ChannelId = request.ChannelId,
            Code = request.ChannelType
        };
        return new ApiCall(draft, () =>
        {
            if (ClanAccess(request.ClanId) is { } denied)
            {
                return ApiOutcome.Fail(draft, denied);
            }

            var rooms = World.ChannelsOf(request.ClanId).Where(static channel => channel.IsVoice).ToList();
            if (request.ChannelId != 0)
            {
                rooms = rooms.Where(channel => channel.Id == request.ChannelId).ToList();
                if (rooms.Count == 0)
                {
                    return ApiOutcome.Fail(draft, MezonStatusCode.NotFound);
                }
            }

            // Only occupied rooms are listed; an absent room is empty.
            var list = new VoiceChannelUserList();
            foreach (var room in rooms)
            {
                var occupants = World.VoiceOccupants(room.Id);
                if (occupants.Count == 0)
                {
                    continue;
                }

                var entry = new VoiceChannelUser { ChannelId = room.Id, RoomName = MezonSimulator.MeetingCode(room) };
                entry.UserIds.Add(occupants.Select(static id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                list.VoiceChannelUsers.Add(entry);
            }

            return ApiOutcome.Ok(draft, list);
        });
    }

    private ApiCall ListRoles(RoleListEventRequest request)
    {
        var draft = Read(SimOperations.ListRoles, request) with { ClanId = request.ClanId };
        return new ApiCall(draft, () =>
        {
            if (ClanAccess(request.ClanId) is { } denied)
            {
                return ApiOutcome.Fail(draft, denied);
            }

            var owner = World.FindClan(request.ClanId)!.OwnerId;
            var roles = new RoleList();
            foreach (var role in World.RolesOf(request.ClanId))
            {
                roles.Roles.Add(new Role
                {
                    Id = role.Id,
                    Title = role.Title,
                    Slug = role.Title.ToLowerInvariant(),
                    ClanId = role.ClanId,
                    CreatorId = owner,
                    Active = role.Active ? 1 : 0
                });
            }

            return ApiOutcome.Ok(draft, new RoleListEventResponse
            {
                ClanId = request.ClanId,
                Limit = request.Limit,
                State = request.State,
                Cursor = request.Cursor,
                Roles = roles
            });
        });
    }

    private ApiCall? UpdateRole(UpdateRoleRequest request)
    {
        if (request.Title is not null
            || request.Color is not null
            || request.RoleIcon is not null
            || request.Description is not null
            || request.ActivePermissionIds.Count > 0
            || request.RemovePermissionIds.Count > 0
            || request.MaxPermissionId != 0)
        {
            // Only role-holder changes are modelled; metadata and permission
            // edits fail closed (Monze never sends them).
            return null;
        }

        var added = request.AddUserIds.ToList();
        var removed = request.RemoveUserIds.ToList();
        var draft = new SimAction
        {
            Kind = SimActionKind.RoleAssignment,
            Operation = SimOperations.UpdateRole,
            SessionId = SessionId,
            ClanId = request.ClanId,
            RoleId = request.RoleId,
            TargetUserId = added.Concat(removed).FirstOrDefault(),
            AddedUserIds = added,
            RemovedUserIds = removed,
            Request = request
        };
        return new ApiCall(draft, () =>
        {
            var role = World.FindRole(request.RoleId);
            if (role is null || (request.ClanId != 0 && role.ClanId != request.ClanId))
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.NotFound);
            }

            var withClan = draft with { ClanId = role.ClanId };
            if (!World.IsMember(role.ClanId, BotId))
            {
                return ApiOutcome.Fail(withClan, MezonStatusCode.PermissionDenied);
            }

            return World.TryChangeRoleHolders(role.ClanId, role.Id, added, removed)
                ? ApiOutcome.Ok(withClan, new Empty())
                : ApiOutcome.Fail(withClan, MezonStatusCode.InvalidArgument, "every role holder must be a clan member");
        });
    }

    private ApiCall UpdateChannelMessage(ChannelMessageUpdate request)
    {
        var draft = new SimAction
        {
            Kind = SimActionKind.UpdateMessage,
            Operation = SimOperations.UpdateChannelMessage,
            SessionId = SessionId,
            ClanId = request.ClanId,
            ChannelId = request.ChannelId,
            MessageId = request.MessageId,
            ContentJson = request.Content,
            Mentions = request.Mentions.ToList(),
            Request = request
        };
        return new ApiCall(draft, () =>
        {
            var message = World.FindMessage(request.MessageId);
            if (message is null || message.Deleted || message.ChannelId != request.ChannelId || message.ClanId != request.ClanId)
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.NotFound);
            }

            if (message.SenderId != BotId)
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.PermissionDenied, "only the sender can edit a message");
            }

            if (message.IsEphemeral)
            {
                return ApiOutcome.Fail(draft, MezonStatusCode.InvalidArgument, "ephemeral messages are edited with EphemeralMessageSend code 14");
            }

            CheckStream(SimOperations.UpdateChannelMessage, World.FindChannel(message.ChannelId), request.Mode, request.IsPublic);
            World.ReviseMessage(message.Id, request.Content);
            return ApiOutcome.Ok(draft, new Empty());
        });
    }

    private ApiCall SessionRefresh(SessionRefreshRequest request)
    {
        var draft = new SimAction
        {
            Kind = SimActionKind.SessionRefresh,
            Operation = SimOperations.SessionRefresh,
            SessionId = SessionId
        };
        return new ApiCall(draft, () => _simulator.RefreshSession(request.Token) is { } session
            ? ApiOutcome.Ok(draft, session)
            : ApiOutcome.Fail(draft, MezonStatusCode.Unauthenticated));
    }

    /// <summary>A direct-message channel lists its peer in user_ids, as DmChannelManager expects.</summary>
    private Mezon.Net.Internal.Api.ChannelDescription DirectDescription(SimChannel channel)
    {
        var description = _simulator.ToDescription(channel);
        if (World.DirectPeer(channel.Id) is long peer && World.FindUser(peer) is { } user)
        {
            description.UserIds.Add(peer);
            description.Usernames.Add(user.Username);
            description.DisplayNames.Add(user.DisplayName);
        }

        return description;
    }

    private SimAction Read(string operation, IMessage request)
        => new()
        {
            Kind = SimActionKind.ApiRead,
            Operation = operation,
            SessionId = SessionId,
            Request = request
        };

    private MezonStatusCode? ClanAccess(long clanId)
    {
        if (World.FindClan(clanId) is null)
        {
            return MezonStatusCode.NotFound;
        }

        return World.IsMember(clanId, BotId) ? null : MezonStatusCode.PermissionDenied;
    }

    private static Frame ApiError(int cid, MezonStatusCode code, string detail)
        => new(MezonMessageType.Api, cid, (int)code, Encoding.UTF8.GetBytes(detail), Tracked: false);

    private SimWorld World => _simulator.World;

    private long BotId => _simulator.World.Bot.Id;

    private sealed record ApiCall(SimAction Draft, Func<ApiOutcome> Execute);

    private sealed record ApiOutcome(MezonStatusCode Status, IMessage? Response, string? Detail, SimAction Draft)
    {
        public static ApiOutcome Ok(SimAction draft, IMessage response) => new(MezonStatusCode.Ok, response, null, draft);

        public static ApiOutcome Fail(SimAction draft, MezonStatusCode status, string? detail = null) => new(status, null, detail, draft);
    }
}
