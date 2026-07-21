namespace Sharpbill.Domain.ValueObjects;

public sealed record UiPreferences
{
    public string? BaseTone { get; init; }
    public string? BackgroundDepth { get; init; }
    public string? BorderGlow { get; init; }
    public string? GlowIntensity { get; init; }
    public string? Scanlines { get; init; }
    public string? CornerRadius { get; init; }
    public string? Motion { get; init; }
    public double? RainDensity { get; init; }
    public string? RainSpeed { get; init; }
    public string? RainGlyphs { get; init; }
    public string? FontFamily { get; init; }
    public string? TextScale { get; init; }
    public string? Density { get; init; }
    public bool? HighContrastText { get; init; }
    public bool? ReduceTransparency { get; init; }
    public string? FocusRing { get; init; }
    public bool? ZebraRows { get; init; }
    public bool? LinkUnderlines { get; init; }
    public int? Version { get; init; }
}
