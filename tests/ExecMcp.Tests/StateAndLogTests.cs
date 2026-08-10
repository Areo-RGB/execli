using System.Text;
using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class StateAndLogTests
{
    [Fact]
    public async Task StateStore_ConcurrentWriters_DoNotLoseUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "execmcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        var stores = Enumerable.Range(0, 8).Select(_ => new StateStore(path, 2000)).ToArray();
        var tasks = Enumerable.Range(0, 80).Select(async index =>
        {
            var store = stores[index % stores.Length];
            await store.UpdateAsync(state => { state.Jobs.Add(new JobRecord { Id = $"j{index}", CreatedAt = DateTimeOffset.UtcNow }); return state; }, TestContext.Current.CancellationToken);
        });
        await Task.WhenAll(tasks);
        var final = await stores[0].ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(80, final.Jobs.Count);
    }

    [Fact]
    public async Task BoundedLog_PreservesAbsoluteByteMetadataAndUtf8()
    {
        var root = Path.Combine(Path.GetTempPath(), "execmcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "out.log");
        var log = new BoundedLog(path, 12);
        await log.AppendAsync(Encoding.UTF8.GetBytes("123456789雪abc"), TestContext.Current.CancellationToken);
        var meta = log.GetMetadata();
        Assert.True(meta.FullBytes > meta.TailBytes);
        var read = await log.ReadAsync(meta.TailStartOffset, 12, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\uFFFD', read.Text);
        Assert.Equal(meta.FullBytes, read.NextOffset);
        Assert.Equal(meta.FullBytes, read.FullBytes);
    }
}
