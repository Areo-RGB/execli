using ExecMcp.Core;
using ExecMcp.SnippingCallback;
namespace ExecMcp.Tests;

public sealed class SnippingCallbackTests
{
    [Fact]
    public async Task Cancellation_ConsumesCorrelationWithoutTouchingAFileToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "execmcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SnippingCorrelationStore(Path.Combine(root, "correlations.json"));
        var id = Guid.NewGuid();
        await store.AddAsync(id);
        var handler = new CallbackHandler(store);

        var code = await handler.HandleAsync(new Uri($"execmcp-snip://complete?code=499&reason=Cancelled&x-request-correlation-id={id}"));

        Assert.Equal(0, code);
        Assert.False(await store.ConsumeAsync(id));
    }
}
