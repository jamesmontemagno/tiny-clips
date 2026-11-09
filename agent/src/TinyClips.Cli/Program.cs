using TinyClips.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await Cli.RunAsync(args, new CliEnvironment(), cancellation.Token);
