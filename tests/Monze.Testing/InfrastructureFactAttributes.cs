using Xunit;

namespace Monze.Testing;

/// <summary>
/// A fact that needs the campaign PostgreSQL. Without MONZE_TEST_POSTGRES it
/// is reported as skipped (never as a silent pass). In strict campaign mode it
/// is not skipped, so the missing infrastructure fails the test.
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (!TestPostgres.IsConfigured && !CampaignEnvironment.Strict)
        {
            Skip = $"Requires {TestPostgres.ConnectionVariable} (campaign PostgreSQL).";
        }
    }
}

/// <summary>A theory variant of <see cref="DbFactAttribute"/>.</summary>
public sealed class DbTheoryAttribute : TheoryAttribute
{
    public DbTheoryAttribute()
    {
        if (!TestPostgres.IsConfigured && !CampaignEnvironment.Strict)
        {
            Skip = $"Requires {TestPostgres.ConnectionVariable} (campaign PostgreSQL).";
        }
    }
}

/// <summary>A fact that needs the campaign Redis; same skip rules as <see cref="DbFactAttribute"/>.</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!TestRedis.IsConfigured && !CampaignEnvironment.Strict)
        {
            Skip = $"Requires {TestRedis.ConnectionVariable} (campaign Redis).";
        }
    }
}
