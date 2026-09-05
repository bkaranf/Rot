using Rot.App.Updates;
using System.Text;

namespace Rot.Updater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var installDirectory = Required(options, "--install");
            var stagingDirectory = Required(options, "--staging");
            var readyPipe = Required(options, "--ready-pipe");
            if (!int.TryParse(Required(options, "--pid"), out var processId) || processId <= 0 ||
                !long.TryParse(Required(options, "--start-ticks"), out var startTicks) || startTicks <= 0 ||
                !long.TryParse(Required(options, "--wait-ms"), out var waitMilliseconds) ||
                waitMilliseconds is <= 0 or > 600_000)
            {
                throw new UpdateException("The updater process identity or timeout is invalid.");
            }

            var request = new UpdateInstallRequest(
                installDirectory,
                processId,
                startTicks,
                TimeSpan.FromMilliseconds(waitMilliseconds),
                readyPipe);
            var installer = new PortableUpdateInstaller(new WindowsUpdateProcessRuntime());
            var result = await installer.InstallAsync(stagingDirectory, request,
                candidatePrepared: options.TryGetValue("--prepared-pipe", out var preparedPipe)
                    ? token => UpdateReadiness.NotifyPreparedAsync(preparedPipe, token)
                    : null).ConfigureAwait(false);
            Console.WriteLine($"Rot update installed at {result.InstallDirectory}.");
            return 0;
        }
        catch (UpdateException exception)
        {
            WriteErrorLog(TryGetStagingArgument(args), exception);
            Console.Error.WriteLine($"Rot update failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            WriteErrorLog(TryGetStagingArgument(args), exception);
            Console.Error.WriteLine($"Rot update failed: {exception.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[index + 1]) || !options.TryAdd(args[index], args[index + 1]))
            {
                throw new UpdateException("The updater arguments are invalid.");
            }
        }

        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value)
            ? value
            : throw new UpdateException($"The updater argument {name} is required.");

    private static string? TryGetStagingArgument(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--staging", StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteErrorLog(string? stagingArgument, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(stagingArgument))
        {
            return;
        }

        try
        {
            var current = Path.GetFullPath(stagingArgument);
            for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (UpdatePaths.IsOwnedStagingDirectory(current))
                {
                    var logPath = Path.Combine(current, "update-error.log");
                    if (File.Exists(logPath) && File.GetAttributes(logPath).HasFlag(FileAttributes.ReparsePoint))
                    {
                        return;
                    }

                    var content = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O}\n{exception}\n");
                    if (content.Length > 16 * 1024)
                    {
                        content = content[..(16 * 1024)];
                    }

                    File.WriteAllBytes(logPath, content);
                    return;
                }

                current = Directory.GetParent(current)?.FullName ?? string.Empty;
            }
        }
        catch
        {
            // The updater must keep its original failure result if diagnostics cannot be written.
        }
    }
}
