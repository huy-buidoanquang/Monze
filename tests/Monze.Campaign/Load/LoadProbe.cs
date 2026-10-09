using System.Diagnostics;
using Monze.Simulator;

namespace Monze.Campaign.Load;

/// <summary>Checks that Monze still answers: one `*monze help` on a free command channel, answered within the timeout.</summary>
public static class LoadProbe
{
    public static async Task<bool> AnswersAsync(SimulatedMonzeHost host, LoadWorld world, TimeSpan timeout)
    {
        var tracker = new LoadTracker(world);
        host.Recorder.Recorded += tracker.OnRecorded;
        try
        {
            if (!tracker.TryTakeCommandChannel(out var slot, measured: false))
            {
                return false;
            }

            var clan = world.Clans.First(candidate => candidate.Id == slot.Clan);
            var answer = tracker.CommandSentAsync(slot.Clan, slot.Channel, clan.Members[0], "*monze help", Stopwatch.GetTimestamp(), measured: false);
            await host.Inbound.SayAsync(slot.Clan, slot.Channel, clan.Members[0], "*monze help");
            return await Task.WhenAny(answer, Task.Delay(timeout)) == answer;
        }
        finally
        {
            host.Recorder.Recorded -= tracker.OnRecorded;
        }
    }
}
