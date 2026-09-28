using System.Collections.Concurrent;
using System.Security.Cryptography;
using Monze.Application;

namespace Monze.Infrastructure.Caching;

public sealed class MemoryWelcomeDraftStore : IWelcomeDraftStore
{
    private const int MaxEntries = 4_096;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, WelcomeDraftTicket> _tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _claimed = new(StringComparer.Ordinal);

    public WelcomeDraftTicket Create(
        long clanId,
        long channelId,
        bool enabled,
        WelcomeEmbedSettings draft,
        DateTimeOffset now)
    {
        CleanupExpired(now);
        if (_tickets.Count >= MaxEntries)
        {
            throw new InvalidOperationException("Welcome draft capacity is temporarily full.");
        }

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToHexString(bytes);
        var ticket = new WelcomeDraftTicket(
            token,
            clanId,
            channelId,
            enabled,
            draft,
            now.Add(Lifetime));

        if (!_tickets.TryAdd(token, ticket))
        {
            throw new InvalidOperationException("Could not create a unique welcome draft token.");
        }

        return ticket;
    }

    public bool TryClaim(
        string token,
        long clanId,
        long channelId,
        DateTimeOffset now,
        out WelcomeDraftTicket ticket)
    {
        ticket = default!;
        if (!TryGet(token, clanId, channelId, now, out var candidate))
        {
            return false;
        }

        if (!_claimed.TryAdd(token, 0))
        {
            return false;
        }

        ticket = candidate;
        return true;
    }

    public bool TryGet(
        string token,
        long clanId,
        long channelId,
        DateTimeOffset now,
        out WelcomeDraftTicket ticket)
    {
        ticket = default!;
        if (string.IsNullOrWhiteSpace(token)
            || !_tickets.TryGetValue(token, out var candidate))
        {
            return false;
        }

        if (candidate.ClanId != clanId || candidate.ChannelId != channelId)
        {
            return false;
        }

        if (candidate.ExpiresAt <= now)
        {
            Remove(candidate);
            return false;
        }

        ticket = candidate;
        return true;
    }

    public void Complete(WelcomeDraftTicket ticket)
        => Remove(ticket);

    public void Release(WelcomeDraftTicket ticket)
        => _claimed.TryRemove(ticket.Token, out _);

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var pair in _tickets)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                Remove(pair.Value);
            }
        }
    }

    private void Remove(WelcomeDraftTicket ticket)
    {
        ((ICollection<KeyValuePair<string, WelcomeDraftTicket>>)_tickets)
            .Remove(new KeyValuePair<string, WelcomeDraftTicket>(ticket.Token, ticket));
        _claimed.TryRemove(ticket.Token, out _);
    }
}
