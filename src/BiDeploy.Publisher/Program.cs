namespace BiDeploy.Publisher;

internal static class PublisherProgram
{
    private static Task<int> Main(string[] args) => PublisherCli.RunAsync(args, Console.Out, Console.Error);
}
