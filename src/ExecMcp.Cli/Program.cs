namespace ExecMcp.Cli;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try { return await CliApp.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
