using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Default <see cref="ISettingsService"/>. Atomic writes via <c>.tmp</c> +
/// <c>File.Move</c> so an interrupted save cannot leave a half-written
/// settings.json behind.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _filePath;
    private readonly string _tempPath;
    private readonly object _lock = new();
    private AppSettings _current = new();

    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, "settings.json"))
    {
    }

    /// <summary>Test-friendly constructor allowing the JSON path to be redirected.</summary>
    public SettingsService(ILogger<SettingsService> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
        _tempPath = filePath + ".tmp";
    }

    public AppSettings Current
    {
        get
        {
            lock (_lock) { return _current; }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogDebug("No settings.json at {Path}; using defaults", _filePath);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                _logger.LogWarning("settings.json deserialized as null; keeping defaults");
                return;
            }
            lock (_lock) { _current = loaded; }
            _logger.LogDebug("Loaded settings from {Path}", _filePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to load settings.json; keeping defaults");
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        lock (_lock) { _current = settings; }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            await using (var stream = File.Create(_tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(_tempPath, _filePath, overwrite: true);
            _logger.LogDebug("Saved settings to {Path}", _filePath);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to save settings.json; latest state is retained at {TempPath}", _tempPath);
        }
    }
}
