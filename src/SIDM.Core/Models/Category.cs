namespace SIDM.Core.Models;

public class Category
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? DefaultPath { get; set; }
    /// <summary>Comma-separated extensions (e.g. "mp4,mkv,webm").</summary>
    public string? Extensions { get; set; }
    /// <summary>Hex color string (e.g. "#FF5733") for UI badges.</summary>
    public string? Color { get; set; }
}
