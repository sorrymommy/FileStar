namespace FileStar.ViewModels;

/// <summary>One step of the address bar's path buttons (내 PC › Windows (C:) › Users …).</summary>
public sealed record Crumb(string Name, string FullPath);
