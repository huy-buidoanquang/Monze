using Monze.Infrastructure.Caching;
using Xunit;

namespace Monze.Tests;

public sealed class MonzeCacheKeyTests
{
    [Fact]
    public void Equal_parts_have_equal_hash_codes()
    {
        var left = new MonzeCacheKey(2104288434238525440, "clan-settings", "current");
        var right = new MonzeCacheKey(2104288434238525440, "clan-settings", "current");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Dictionary_lookup_uses_typed_key()
    {
        var key = new MonzeCacheKey(42, "channel-policy", "7");
        var values = new Dictionary<MonzeCacheKey, int> { [key] = 1 };

        Assert.True(values.TryGetValue(new MonzeCacheKey(42, "channel-policy", "7"), out var value));
        Assert.Equal(1, value);
    }
}
