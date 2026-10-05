using System.Threading.Channels;
using Xunit;

namespace Monze.Tests;

public sealed class MeetingIngressItemTests
{
    [Fact]
    public void Voice_profile_preserves_the_sdk_event_fields()
    {
        var item = MeetingIngressItem.VoiceProfile(7, 8, 9, "Clan Nick");

        Assert.Equal(MeetingIngressKind.VoiceProfile, item.Kind);
        Assert.Equal(7, item.ClanId);
        Assert.Equal(8, item.VoiceChannelId);
        Assert.Equal(9, item.UserId);
        Assert.Equal("Clan Nick", item.ParticipantLabel);
    }

    [Fact]
    public void Voice_profile_creation_and_bounded_enqueue_allocate_zero_bytes_after_warmup()
    {
        const string participantLabel = "Clan Nick";
        var queue = Channel.CreateBounded<MeetingIngressItem>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(queue.Writer.TryWrite(MeetingIngressItem.VoiceProfile(7, 8, 9, participantLabel)));
            Assert.True(queue.Reader.TryRead(out _));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            if (!queue.Writer.TryWrite(MeetingIngressItem.VoiceProfile(7, 8, 9, participantLabel))
                || !queue.Reader.TryRead(out _))
            {
                throw new InvalidOperationException("The pre-sized meeting ingress queue rejected a roundtrip.");
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
