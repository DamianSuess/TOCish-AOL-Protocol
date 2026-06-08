using System.Net.Sockets;
using AolToc.Protocol;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AolToc.Server;

internal sealed class ClientSession(TocChatServer server, TcpClient tcpClient)
{
  private FlapConnection? _connection;
  private TocProtocolFlavor _flavor = TocProtocolFlavor.Toc;

  public string? ScreenName { get; private set; }

  public string? NormalizedScreenName { get; private set; }

  public bool IsSignedOn => ScreenName is not null;

  public string ScreenNameOrUnknown => ScreenName ?? "unknown";

  public async Task RunAsync(CancellationToken cancellationToken)
  {
    try
    {
      _connection = await FlapConnection.AcceptAsServerAsync(tcpClient, cancellationToken).ConfigureAwait(false);

      while (!cancellationToken.IsCancellationRequested)
      {
        var frame = await _connection.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
          break;

        if (frame.Channel == FlapFrame.SignOnChannel)
        {
          _flavor = frame.GetText().Contains('2', StringComparison.OrdinalIgnoreCase)
            ? TocProtocolFlavor.Toc2
            : TocProtocolFlavor.Toc;
          continue;
        }

        if (frame.Channel != FlapFrame.DataChannel)
          continue;

        await HandleCommandAsync(TocCommand.Parse(frame.GetText()), cancellationToken).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
    }
    catch (EndOfStreamException)
    {
    }
    catch (IOException)
    {
    }
    catch (SocketException)
    {
    }
    finally
    {
      await server.RemoveSessionAsync(this, CancellationToken.None).ConfigureAwait(false);

      if (_connection is not null)
      {
        await _connection.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  public void MarkSignedOn(string screenName, string normalizedScreenName, TocProtocolFlavor flavor)
  {
    ScreenName = screenName;
    NormalizedScreenName = normalizedScreenName;
    _flavor = flavor;
  }

  public void ClearSignedOn()
  {
    ScreenName = null;
    NormalizedScreenName = null;
  }

  public Task SendErrorAsync(string code, string message, CancellationToken cancellationToken)
  {
    return SendEventAsync("ERROR", cancellationToken, code, message);
  }

  public Task SendEventAsync(string eventName, CancellationToken cancellationToken, params string[] arguments)
  {
    var resolvedName = _flavor == TocProtocolFlavor.Toc2
        && !eventName.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
        && !eventName.EndsWith('2')
          ? $"{eventName}2"
          : eventName;

    var payload = arguments.Length == 0
      ? resolvedName
      : $"{resolvedName}:{string.Join(':', arguments)}";

    return RequireConnection().SendTextAsync(FlapFrame.DataChannel, payload, cancellationToken: cancellationToken);
  }

  private async Task HandleCommandAsync(TocCommand command, CancellationToken cancellationToken)
  {
    if (command.Name.IsToc2Command())
    {
      _flavor = TocProtocolFlavor.Toc2;
    }

    if (command.Name.IsSignOnCommand())
    {
      var credentials = ExtractCredentials(command);
      if (credentials is null)
      {
        await SendErrorAsync("980", "Sign-on command is missing credentials.", cancellationToken)
          .ConfigureAwait(false);

        return;
      }

      var flavor = command.Name.IsToc2Command() ? TocProtocolFlavor.Toc2 : _flavor;
      await server.SignOnAsync(this, credentials.Value.ScreenName, credentials.Value.Password, flavor, cancellationToken)
        .ConfigureAwait(false);

      return;
    }

    if (!IsSignedOn)
    {
      await SendErrorAsync("901", "Sign on first.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var shortName = StripPrefix(command.Name);
    switch (shortName)
    {
      case "init":
      case "set_config":
      case "add_buddy":
      case "remove_buddy":
        return;

      case "chat_join":
        if (command.Arguments.Count >= 2)
        {
          await server.JoinRoomAsync(this, command.Arguments[1], cancellationToken).ConfigureAwait(false);
          return;
        }

        break;

      case "chat_send":
        if (command.Arguments.Count >= 2)
        {
          await server.SendRoomMessageAsync(
            this,
            command.Arguments[0],
            string.Join(' ', command.Arguments.Skip(1)),
            cancellationToken).ConfigureAwait(false);

          return;
        }

        break;

      case "chat_whisper":
        if (command.Arguments.Count >= 3)
        {
          await server.SendWhisperAsync(
            this,
            command.Arguments[0],
            command.Arguments[1],
            string.Join(' ', command.Arguments.Skip(2)),
            cancellationToken).ConfigureAwait(false);
          return;
        }

        break;

      case "chat_leave":
        if (command.Arguments.Count >= 1)
        {
          await server.LeaveRoomAsync(this, command.Arguments[0], cancellationToken).ConfigureAwait(false);
          return;
        }

        break;

      case "send_im":
        if (command.Arguments.Count >= 2)
        {
          await server.SendImAsync(
            this,
            command.Arguments[0],
            string.Join(' ', command.Arguments.Skip(1)),
            cancellationToken).ConfigureAwait(false);
          return;
        }

        break;

      case "signoff":
        tcpClient.Close();
        return;
    }

    await SendErrorAsync("901", $"Unsupported or malformed command '{command.Name}'.", cancellationToken)
      .ConfigureAwait(false);
  }

  private FlapConnection RequireConnection()
  {
    return _connection ?? throw new InvalidOperationException("Session is not connected.");
  }

  private static (string ScreenName, string Password)? ExtractCredentials(TocCommand command)
  {
    if (command.Arguments.Count >= 4 && int.TryParse(command.Arguments[1], out _))
      return (command.Arguments[2], command.Arguments[3]);

    if (command.Arguments.Count >= 2)
      return (command.Arguments[0], command.Arguments[1]);

    return null;
  }

  private static string StripPrefix(string name)
  {
    if (name.StartsWith("toc2_", StringComparison.OrdinalIgnoreCase))
      return name[5..].ToLowerInvariant();

    if (name.StartsWith("toc_", StringComparison.OrdinalIgnoreCase))
      return name[4..].ToLowerInvariant();

    return name.ToLowerInvariant();
  }
}