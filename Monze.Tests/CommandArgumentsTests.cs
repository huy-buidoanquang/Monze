using Monze.Application.Commands;
using Xunit;

namespace Monze.Tests;

public sealed class CommandArgumentsTests
{
    [Fact]
    public void Prefix_and_slice_preserve_argument_order()
    {
        var source = new[] { "help", "welcome" };
        var arguments = new CommandArguments("monze", source);

        Assert.Equal(3, arguments.Count);
        Assert.Equal("monze", arguments[0]);
        Assert.Equal("help", arguments[1]);
        Assert.Equal("welcome", arguments[2]);

        var sliced = arguments.Slice(1);
        Assert.Equal(2, sliced.Count);
        Assert.Equal("help", sliced[0]);
        Assert.Equal("welcome", sliced[1]);
        Assert.Equal("help welcome", sliced.Join(' '));
    }

    [Fact]
    public void Slicing_and_single_argument_join_are_allocation_free_after_warmup()
    {
        var source = new[] { "points" };
        var arguments = new CommandArguments(source);
        _ = arguments.Slice(0)[0];
        _ = arguments.Slice(1).Join(' ');

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            var sliced = arguments.Slice(0);
            _ = sliced[0];
            _ = sliced.Slice(1).Join(' ');
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
