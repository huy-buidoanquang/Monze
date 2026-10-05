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
        if (command.Equals(MonzeCommandNames.Ai, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Ai;
        if (command.Equals(MonzeCommandNames.Translate, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Translate;
        if (command.Equals(MonzeCommandNames.Composer, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Composer;
        if (command.Equals(MonzeCommandNames.Simplify, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Simplify;
        if (command.Equals(MonzeCommandNames.Meeting, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Meeting;
        if (command.Equals(MonzeCommandNames.Summary, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Summary;
        if (command.Equals(MonzeCommandNames.Setup, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Setup;
        if (command.Equals(MonzeCommandNames.Welcome, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Welcome;
        if (command.Equals(MonzeCommandNames.Role, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Role;
        if (command.Equals(MonzeCommandNames.Avatar, StringComparison.OrdinalIgnoreCase)
            || command.Equals(MonzeCommandNames.AvatarAliasAva, StringComparison.OrdinalIgnoreCase)
            || command.Equals(MonzeCommandNames.AvatarAliasAvt, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Avatar;
        if (command.Equals(MonzeCommandNames.Monze, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Monze;
        if (command.Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase)) return MonzeCommandNames.Help;
        return MonzeCommandNames.Unknown;
    }

    private MonzeRateLimitRule GetRule(string command)
    {
        if (command is MonzeCommandNames.Ai
            or MonzeCommandNames.Translate
            or MonzeCommandNames.Composer
            or MonzeCommandNames.Simplify)
        {
            return new MonzeRateLimitRule(MonzeRateLimitBucket.Ai, _options.AiLimit, _options.AiWindow);
        }

        if (command is MonzeCommandNames.Meeting or MonzeCommandNames.Summary)
        {
            return new MonzeRateLimitRule(MonzeRateLimitBucket.Meeting, _options.MeetingLimit, _options.MeetingWindow);
        }

        if (command is MonzeCommandNames.Setup
            or MonzeCommandNames.Welcome
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
