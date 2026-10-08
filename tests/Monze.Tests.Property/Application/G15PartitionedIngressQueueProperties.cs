using CsCheck;
using Monze.Application.Ingress;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Application;

/// <summary>
/// G15: PartitionedIngressQueue. Items of one key come out of one lane in
/// write order, a lane rejects writes exactly when it is full, nothing is
/// lost or duplicated, concurrent writers keep per-key order, and keys spread
/// evenly over lanes (chi-square, 15 degrees of freedom, alpha 0.001).
/// </summary>
public sealed class G15PartitionedIngressQueueProperties
{
    private static readonly int[] PartitionCounts = [1, 2, 4, 16];

    [Fact]
    [Req("REQ-ING-001")]
    [Covers("port:IEventIngressQueue.TryWrite")]
    public void Writes_keep_per_key_order_and_respect_lane_capacity()
    {
        var cases =
            from partitions in Gen.OneOfConst(PartitionCounts)
            from capacityPerLane in Gen.Int[1, 6]
            from keyPool in Gen.OneOfConst("few", "many", "snowflake")
            from keys in (keyPool switch
            {
                "few" => Gen.Long[1, 3],
                "many" => Gen.Long[-1_000, 1_000_000],
                _ => Gen.Long[1_600_000_000_000L, 1_900_000_000_000L].Select(static millis => (millis << 22) | 7)
            }).Array[1, 64]
            from reads in Gen.Int[0, 3].Array[keys.Length, keys.Length]
            select (partitions, capacity: partitions * capacityPerLane, keyPool, keys, reads);
        PropertyRun.Run(
            "G15",
            cases,
            static value =>
            {
                var (partitions, capacity, keyPool, keys, reads) = value;
                var tags = new Dictionary<string, string> { ["partitions"] = partitions.ToString(System.Globalization.CultureInfo.InvariantCulture), ["keys"] = keyPool };
                var queue = new PartitionedIngressQueue<Item>(partitions, capacity);
                var lanes = Enumerable.Range(0, partitions).Select(static _ => new Queue<Item>()).ToArray();
                var laneCapacity = capacity / partitions;
                var input = $"partitions={partitions} capacity={capacity} writes={keys.Length}";
                for (var i = 0; i < keys.Length; i++)
                {
                    var item = new Item(keys[i], i);
                    var lane = queue.GetPartition(keys[i]);
                    var expectedAccepted = lanes[lane].Count < laneCapacity;
                    if (queue.TryWrite(keys[i], item) != expectedAccepted)
                    {
                        return PropertyResult.Fail(input, tags, $"write {i} to lane {lane}: expected accepted={expectedAccepted}");
                    }

                    if (expectedAccepted)
                    {
                        lanes[lane].Enqueue(item);
                    }

                    for (var r = 0; r < reads[i]; r++)
                    {
                        var readLane = (i + r) % partitions;
                        var gotItem = queue.GetReader(readLane).TryRead(out var read);
                        var expected = lanes[readLane].Count > 0;
                        if (gotItem != expected || (expected && read != lanes[readLane].Dequeue()))
                        {
                            return PropertyResult.Fail(input, tags, $"read from lane {readLane} after write {i}");
                        }
                    }
                }

                for (var lane = 0; lane < partitions; lane++)
                {
                    while (queue.GetReader(lane).TryRead(out var remaining))
                    {
                        if (lanes[lane].Count == 0 || remaining != lanes[lane].Dequeue())
                        {
                            return PropertyResult.Fail(input, tags, $"drain of lane {lane}");
                        }
                    }

                    if (lanes[lane].Count != 0)
                    {
                        return PropertyResult.Fail(input, tags, $"lane {lane} lost {lanes[lane].Count} items");
                    }
                }

                return PropertyResult.Pass(input, tags);
            },
            iterations: 30_000,
            declare: static ledger => ledger.Dimension("partitions", "1", "2", "4", "16").Dimension("keys", "few", "many", "snowflake"));
    }

    [Fact]
    [Req("REQ-ING-001")]
    public async Task Concurrent_writers_keep_per_key_order()
    {
        var rounds = 200 * CampaignEnvironment.PbtScale;
        using var ledger = CaseLedger.Open("property", "G15b");
        for (var round = 0; round < rounds; round++)
        {
            const int writers = 8;
            const int perWriter = 500;

            // Every lane can hold every item, so several keys sharing a lane never block.
            var queue = new PartitionedIngressQueue<Item>(16, 16 * writers * perWriter);
            await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
            {
                for (var sequence = 0; sequence < perWriter; sequence++)
                {
                    Assert.True(queue.TryWrite(writer + (round * writers), new Item(writer + (round * writers), sequence)));
                }
            })));
            queue.Complete();
            var next = new Dictionary<long, int>();
            for (var lane = 0; lane < queue.PartitionCount; lane++)
            {
                await foreach (var item in queue.GetReader(lane).ReadAllAsync())
                {
                    var expected = next.TryGetValue(item.Key, out var value) ? value : 0;
                    Assert.Equal(expected, item.Sequence);
                    next[item.Key] = expected + 1;
                }
            }

            Assert.All(next.Values, static count => Assert.Equal(perWriter, count));
            ledger.Pass($"round {round}: {writers} writers x {perWriter}");
        }
    }

    [Theory]
    [Req("REQ-ING-001", "REQ-PERF-002")]
    [InlineData("random")]
    [InlineData("sequential")]
    [InlineData("snowflake")]
    public void Keys_spread_evenly_over_lanes(string keys)
    {
        const int partitions = 16;
        const int samples = 64_000;
        const double critical = 37.70; // chi-square, df = 15, alpha = 0.001
        var queue = new PartitionedIngressQueue<Item>(partitions, partitions);
        var random = RunSeed.RandomFor("G15c-" + keys);
        var counts = new int[partitions];
        for (var i = 0; i < samples; i++)
        {
            var key = keys switch
            {
                "random" => random.NextInt64(),
                "sequential" => 2_104_288_434_238_525_440L + i,
                _ => (1_700_000_000_000L + (i * 37L)) << 22 | ((long)random.Next(32) << 12) | (uint)random.Next(4096)
            };
            counts[queue.GetPartition(key)]++;
        }

        const double expected = samples / (double)partitions;
        var chiSquare = counts.Sum(static count => (count - expected) * (count - expected) / expected);
        using var ledger = CaseLedger.Open("property", "G15c-" + keys);
        ledger.Record(chiSquare < critical ? "pass" : "fail", $"{keys}: chi-square {chiSquare:0.00} over {samples} keys");
        Assert.True(chiSquare < critical, $"{keys} keys: chi-square {chiSquare:0.00} >= {critical} ({string.Join(",", counts)})");
    }

    private readonly record struct Item(long Key, int Sequence);
}
