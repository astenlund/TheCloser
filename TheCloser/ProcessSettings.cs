namespace TheCloser;

internal record ProcessSettings
{
    public string? Method { get; init; }
    public TitleBarClickPosition? ClickPosition { get; init; }
}
