namespace SIDM.Core.Models;

/// <summary>
/// Single key-value row in the Settings table. Values are JSON-encoded so any
/// serializable type can be stored under any key.
/// </summary>
public class SettingEntry
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}
