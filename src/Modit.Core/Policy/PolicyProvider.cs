using Microsoft.Win32;

namespace Modit.Core.Policy;

/// <summary>Names of administrator policies (docs/plan/08, "Group Policy / MDM").</summary>
public static class PolicyNames
{
    public const string UpdateCheck = "UpdateCheck";
    public const string UpdateChannel = "UpdateChannel";
    public const string UpdateUrl = "UpdateUrl";
    public const string UpdateDeferDays = "UpdateDeferDays";
    public const string TelemetryEnabled = "TelemetryEnabled";
}

public interface IPolicyProvider
{
    /// <summary>Null when the policy is not configured, so the user setting applies.</summary>
    bool? GetBoolean(string name);

    int? GetInt32(string name);

    string? GetString(string name);

    /// <summary>True when an administrator has configured this value; the UI then shows it as locked.</summary>
    bool IsManaged(string name);
}

/// <summary>
/// Reads administrator policy from <c>HKLM\SOFTWARE\Policies\Modit</c>, then <c>HKCU\SOFTWARE\Policies\Modit</c>.
/// Machine policy wins. Values are read on demand so a Group Policy refresh applies without a restart.
/// </summary>
public sealed class RegistryPolicyProvider : IPolicyProvider
{
    public const string DefaultKeyPath = @"SOFTWARE\Policies\Modit";

    private readonly string _keyPath;

    public RegistryPolicyProvider(string keyPath = DefaultKeyPath)
    {
        _keyPath = keyPath;
    }

    public bool? GetBoolean(string name) => GetInt32(name) is { } value ? value != 0 : null;

    public int? GetInt32(string name) => Read(name) switch
    {
        int number => number,
        string text when int.TryParse(text, out int parsed) => parsed,
        _ => null,
    };

    public string? GetString(string name) => Read(name) switch
    {
        string text when text.Length > 0 => text,
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    public bool IsManaged(string name) => Read(name) is not null;

    private object? Read(string name)
    {
        return ReadFrom(Registry.LocalMachine) ?? ReadFrom(Registry.CurrentUser);

        object? ReadFrom(RegistryKey hive)
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(_keyPath, writable: false);
                return key?.GetValue(name);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }
}
