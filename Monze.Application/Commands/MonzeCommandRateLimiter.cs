namespace Monze.Application.Commands;

public sealed class MonzeCommandRateLimiter
{
    private readonly object _gate = new();
    private readonly MonzeRateLimitOptions _options;
    private readonly Dictionary<MonzeRateLimitKey, MonzeRateLimitWindow> _windows = new();

    public MonzeCommandRateLimiter(MonzeRateLimitOptions? options = null)
        => _options = options ?? MonzeRateLimitOptions.Default;

    public bool TryAcquire(
        long clanId,
        long userId,
        string command,
        DateTimeOffset now,
        out TimeSpan retryAfter)
    {
        var commandKey = NormalizeCommand(command.AsSpan());
        var rule = GetRule(commandKey);
        var key = new MonzeRateLimitKey(clanId, userId, rule.Bucket, commandKey);
        lock (_gate)
        {
            if (_windows.Count >= _options.MaxEntries)
            {
                TrimOneExpired(now);
            }
            if (_windows.Count >= _options.MaxEntries && !_windows.ContainsKey(key))
            {
                retryAfter = rule.Window;
                return false;
            }

            if (!_windows.TryGetValue(key, out var window) || now >= window.ExpiresAt)
            {
                _windows[key] = new MonzeRateLimitWindow(now.Add(rule.Window), 1);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (window.Count >= rule.Limit)
            {
                retryAfter = window.ExpiresAt - now;
                return false;
            }

            _windows[key] = new MonzeRateLimitWindow(window.ExpiresAt, window.Count + 1);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private static string NormalizeCommand(ReadOnlySpan<char> command)
    {
        command = command.Trim();
        if (command.Equals(MonzeCommandNames.Summarize, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Summarize;
        if (command.Equals(MonzeCommandNames.Translate, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Translate;
        if (command.Equals(MonzeCommandNames.Rewrite, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Rewrite;
        if (command.Equals(MonzeCommandNames.Shorten, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Shorten;
        if (command.Equals(MonzeCommandNames.Meeting, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Meeting;
        if (command.Equals(MonzeCommandNames.Summary, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Summary;
        if (command.Equals(MonzeCommandNames.Setup, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Setup;
        if (command.Equals(MonzeCommandNames.Welcome, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Welcome;
        if (command.Equals(MonzeCommandNames.Announce, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Announce;
        if (command.Equals(MonzeCommandNames.Outbox, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Outbox;
        if (command.Equals(MonzeCommandNames.Faq, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Faq;
        if (command.Equals(MonzeCommandNames.Role, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Role;
        if (command.Equals(MonzeCommandNames.Monze, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Monze;
        if (command.Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Help;
        if (command.Equals(MonzeCommandNames.Event, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Event;
        if (command.Equals(MonzeCommandNames.Info, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Info;
        if (command.Equals(MonzeCommandNames.Points, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Points;
        if (command.Equals(MonzeCommandNames.Leaderboard, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Leaderboard;
        if (command.Equals(MonzeCommandNames.Spin, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Spin;
        if (command.Equals(MonzeCommandNames.Topic, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Topic;
        return MonzeCommandNames.Unknown;
    }

    private MonzeRateLimitRule GetRule(string command)
    {
        if (command is MonzeCommandNames.Summarize
            or MonzeCommandNames.Translate
            or MonzeCommandNames.Rewrite
            or MonzeCommandNames.Shorten)
        {
            return new MonzeRateLimitRule(MonzeRateLimitBucket.Ai, _options.AiLimit, _options.AiWindow);
        }

        if (command is MonzeCommandNames.Meeting or MonzeCommandNames.Summary)
        {
            return new MonzeRateLimitRule(MonzeRateLimitBucket.Meeting, _options.MeetingLimit, _options.MeetingWindow);
        }

        if (command is MonzeCommandNames.Setup
            or MonzeCommandNames.Welcome
            or MonzeCommandNames.Announce
            or MonzeCommandNames.Outbox
            or MonzeCommandNames.Faq
            or MonzeCommandNames.Role)
        {
            return new MonzeRateLimitRule(MonzeRateLimitBucket.Admin, _options.AdminLimit, _options.AdminWindow);
        }

        return new MonzeRateLimitRule(MonzeRateLimitBucket.User, _options.UserLimit, _options.UserWindow);
    }

    private void TrimOneExpired(DateTimeOffset now)
    {
        if (_windows.Count == 0)
        {
            return;
        }

        MonzeRateLimitKey expiredKey = default;
        var found = false;
        foreach (var pair in _windows)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                expiredKey = pair.Key;
                found = true;
                break;
            }
        }

        if (found)
        {
            _windows.Remove(expiredKey);
        }
    }
}

