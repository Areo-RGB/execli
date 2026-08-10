namespace ExecMcp.SnippingCallback;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri)) return 2;
        return await new CallbackHandler().HandleAsync(uri).ConfigureAwait(false);
    }
}
