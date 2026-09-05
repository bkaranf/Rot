using System.Text.Json;
using System.Text.Json.Serialization;
using Rot.App.Models;

namespace Rot.App.Persistence;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _filePath;
    private readonly string _previousPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private bool _mainKnownCorrupt;

    public JsonSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rot",
            "settings.v1.json");
        _previousPath = _filePath + ".previous";
    }

    public string FilePath => _filePath;

    public async Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    useAsync: true);
            }
            catch (FileNotFoundException)
            {
                _mainKnownCorrupt = false;
                return RotSettings.CreateDefault();
            }
            catch (DirectoryNotFoundException)
            {
                _mainKnownCorrupt = false;
                return RotSettings.CreateDefault();
            }

            await using (stream)
            {
                var settings = await JsonSerializer.DeserializeAsync<RotSettings>(
                    stream,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                _mainKnownCorrupt = false;
                return (settings ?? RotSettings.CreateDefault()).Normalize();
            }
        }
        catch (JsonException exception)
        {
            var backupPath = PreserveCorruptSettingsFile();
            _mainKnownCorrupt = true;
            var recovered = await TryLoadPreviousAsync(cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                Console.Error.WriteLine(
                    $"[rot] WARN Settings file is invalid; preserved the original at {backupPath} " +
                    $"and recovered the previous valid settings from {_previousPath}: {exception.Message}");
                return recovered;
            }

            Console.Error.WriteLine(
                $"[rot] WARN Settings file is invalid; preserved the original at {backupPath}; " +
                $"no valid previous settings were available, using defaults: {exception.Message}");
            return RotSettings.CreateDefault();
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[rot] ERROR Settings could not be read; startup cannot safely continue without risking an overwrite: {exception.Message}");
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"[rot] ERROR Settings could not be read; startup cannot safely continue without risking an overwrite: {exception.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("The settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_filePath}.tmp-{Guid.NewGuid():N}";

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Flush(flushToDisk: true);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!_mainKnownCorrupt && File.Exists(_filePath))
                {
                    File.Replace(
                        temporaryPath,
                        _filePath,
                        _previousPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, _filePath, overwrite: true);
                }

                _mainKnownCorrupt = false;
            }
            finally
            {
                DeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default)
    {
        var settings = RotSettings.CreateDefault();
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    private string PreserveCorruptSettingsFile()
    {
        var backupPath = $"{_filePath}.corrupt-{Guid.NewGuid():N}";
        File.Copy(_filePath, backupPath, overwrite: false);
        return backupPath;
    }

    private async Task<RotSettings?> TryLoadPreviousAsync(CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _previousPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            await using (stream)
            {
                var settings = await JsonSerializer.DeserializeAsync<RotSettings>(
                    stream,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                return settings?.Normalize();
            }
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"[rot] WARN Previous settings snapshot is invalid; keeping it for inspection: {exception.Message}");
            return null;
        }
    }

    private static void DeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Could not remove temporary settings file '{temporaryPath}': {exception.Message}");
        }
    }
}
