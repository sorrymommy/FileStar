namespace FileStar.Services;

/// <summary>Light or dark colors for every window.</summary>
public interface IThemeService
{
    bool IsDark { get; }

    void Apply(bool dark);
}
