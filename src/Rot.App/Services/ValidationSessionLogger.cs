using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Rot.App.Services;

internal sealed class ValidationSessionLogger : IDisposable
{
    public const string EnableArgument = "--validation-session";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<ValidationRecord> _records = Channel.CreateUnbounded<ValidationRecord>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly long _sessionStartedAt = Stopwatch.GetTimestamp();
    private readonly Task _writerTask;
    private Exception? _writerFailure;
    private int _accepting = 1;

    private ValidationSessionLogger(string path)
    {
        Path = path;

        // Open the file before reporting that validation is armed. This makes a
        // bad path or sharing failure a startup failure instead of silently
        // collecting evidence that can never be written.
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            useAsync: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        // The WPF dispatcher must never be the continuation target for the log
        // drain because Dispose runs synchronously on that dispatcher.
        _writerTask = Task.Run(() => WriteRecordsAsync(writer));
        Record("session.started", new
        {
            processId = Environment.ProcessId,
            commandLine = Environment.CommandLine
        });
    }

    public string Path { get; }

    public static ValidationSessionLogger? CreateIfRequested(IReadOnlyList<string> arguments)
    {
        if (!arguments.Any(argument =>
                string.Equals(argument, EnableArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rot",
            "Validation");
        Directory.CreateDirectory(directory);
        var fileName = $"rot-session-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.jsonl";
        return new ValidationSessionLogger(System.IO.Path.Combine(directory, fileName));
    }

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public void Record(string kind, object? data = null, long? triggerTimestamp = null)
    {
        if (Volatile.Read(ref _accepting) == 0 || Volatile.Read(ref _writerFailure) is not null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var record = new ValidationRecord(
            DateTimeOffset.UtcNow,
            ElapsedMilliseconds(_sessionStartedAt, now),
            triggerTimestamp.HasValue
                ? ElapsedMilliseconds(triggerTimestamp.Value, now)
                : null,
            kind,
            FreezeData(data));

        if (!_records.Writer.TryWrite(record))
        {
            Console.Error.WriteLine("[rot] ERROR Validation log stopped accepting evidence.");
            Interlocked.Exchange(ref _accepting, 0);
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            ObserveWriterCompletion();
            return;
        }

        Record("session.stopped");
        Interlocked.Exchange(ref _accepting, 0);
        _records.Writer.TryComplete();
        ObserveWriterCompletion();
    }

    private static object? FreezeData(object? data) => data is JsonElement element
        ? element.Clone()
        : data;

    private static double ElapsedMilliseconds(long startedAt, long endedAt) =>
        Math.Round((endedAt - startedAt) * 1000d / Stopwatch.Frequency, 3);

    private void ObserveWriterCompletion()
    {
        try
        {
            _writerTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _writerFailure ??= exception;
        }

        if (_writerFailure is not null)
        {
            Console.Error.WriteLine($"[rot] ERROR Validation log did not flush cleanly: {_writerFailure}");
        }
    }

    private async Task WriteRecordsAsync(StreamWriter writer)
    {
        try
        {
            await using (writer.ConfigureAwait(false))
            {
                await foreach (var record in _records.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    var line = JsonSerializer.Serialize(new
                    {
                        timestamp = record.Timestamp.ToString("O"),
                        record.SessionElapsedMs,
                        record.DeltaMs,
                        record.Kind,
                        record.Data
                    }, JsonOptions);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            _writerFailure = exception;
            Interlocked.Exchange(ref _accepting, 0);
            _records.Writer.TryComplete(exception);
            throw;
        }
    }

    private sealed record ValidationRecord(
        DateTimeOffset Timestamp,
        double SessionElapsedMs,
        double? DeltaMs,
        string Kind,
        object? Data);
}
