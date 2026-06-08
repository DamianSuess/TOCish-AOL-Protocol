namespace AolToc.Protocol;

public sealed record TocClientOptions(
    string Host,
    int Port,
    string ScreenName,
    string Password)
{
  public TocProtocolFlavor Flavor { get; init; } = TocProtocolFlavor.Toc;

  public string AuthorizerHost { get; init; } = "localhost";

  public int AuthorizerPort { get; init; } = 5190;

  public string Language { get; init; } = "english";

  public string ClientId { get; init; } = "TIC:Codex TOC Chat";
}
