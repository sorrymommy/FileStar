namespace FileStar.ViewModels;

/// <summary>One step of the breadcrumb bar.</summary>
public sealed record Crumb(string Name, string FullPath, bool ShowSeparator);
