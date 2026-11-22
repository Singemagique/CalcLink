using System.Text.Json;
using System.Text.Json.Serialization;
using CalcLink.Core;

namespace CalcLink.App.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>User settings, persisted as JSON in %LOCALAPPDATA%\CalcLink\settings.json.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Backend used at startup; changes apply on restart.</summary>
    public CalculatorBackend Backend { get; set; } = CalculatorBackend.Auto;

    /// <summary>Folder used by "Receive to default folder"; null = not set.</summary>
    public string? DefaultReceiveFolder { get; set; }

    /// <summary>Root of the dated backups made by "Back up now"; null = Documents\CalcLink Backups.</summary>
    public string? BackupFolder { get; set; }

    /// <summary>Link timeout in seconds (tilibs default is 1.5 s).</summary>
    public double LinkTimeoutSeconds { get; set; } = 1.5;

    /// <summary>Calculator page: sort key, direction and grouping of the variable list.</summary>
    public VariableSortKey VariableSort { get; set; } = VariableSortKey.Name;

    public bool VariableSortDescending { get; set; }

    public VariableGrouping VariableGrouping { get; set; } = VariableGrouping.Folder;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsService
{
    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalcLink");

    public static string SettingsPath { get; } = Path.Combine(AppDataFolder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    /// <summary>The backend the running service was created with (settings changes apply on restart).</summary>
    public CalculatorBackend StartupBackend { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var stream = File.OpenRead(SettingsPath);
                Current = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: fall back to defaults rather than failing to start.
            Current = new AppSettings();
        }

        Current.LinkTimeoutSeconds = Math.Clamp(Current.LinkTimeoutSeconds, 0.5, 60);
        StartupBackend = Current.Backend;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: settings simply won't persist this time.
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }
}
