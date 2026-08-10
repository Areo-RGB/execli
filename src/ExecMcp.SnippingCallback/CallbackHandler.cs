using ExecMcp.Core;
using Windows.ApplicationModel.DataTransfer;

namespace ExecMcp.SnippingCallback;

public sealed class CallbackHandler
{
    private readonly SnippingCorrelationStore _correlations;
    public CallbackHandler(SnippingCorrelationStore? correlations = null) => _correlations = correlations ?? new SnippingCorrelationStore();

    public async Task<int> HandleAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        SnippingCallbackData data;
        try { data = SnippingCallbackValidator.Parse(uri); }
        catch { return 2; }

        if (!await _correlations.ConsumeAsync(data.CorrelationId, cancellationToken).ConfigureAwait(false)) return 3;
        if (data.Code != 200) return 0;
        try
        {
            var file = await SharedStorageAccessManager.RedeemTokenForFileAsync(data.FileAccessToken!);
            GC.KeepAlive(file);
            return 0;
        }
        catch { return 4; }
    }
}
