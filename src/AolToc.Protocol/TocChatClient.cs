using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AolToc.Protocol;

public sealed class TocChatClient : IAsyncDisposable
{
  private readonly Dictionary<string, string> _roomIdToName = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, string> _roomNameToId = new(StringComparer.OrdinalIgnoreCase);
  private readonly SemaphoreSlim _sendLock = new(1, 1);
  private CancellationTokenSource? _readerCancellation;
  private FlapConnection? _connection;
  private Task? _readerTask;
  private TocProtocolFlavor _flavor;

  public string? SignedOnScreenName { get; private set; }

  public event EventHandler<TocEvent>? EventReceived;

  public event EventHandler<ChatRoomJoined>? ChatRoomJoined;

  public event EventHandler<ChatMessage>? ChatMessageReceived;

  public event EventHandler<DirectMessage>? DirectMessageReceived;

  public event EventHandler<RoomMemberChanged>? RoomMemberChanged;

  public event EventHandler<string>? ErrorReceived;

  public event EventHandler? Disconnected;

  public async Task ConnectAsync(TocClientOptions options, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(options);

    if (_connection is not null)
    {
      throw new InvalidOperationException("Client is already connected.");
    }

    _flavor = options.Flavor;
    var tcpClient = new TcpClient();
    await tcpClient.ConnectAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
    _connection = await FlapConnection.ConnectAsClientAsync(tcpClient, cancellationToken).ConfigureAwait(false);

    await _connection.SendTextAsync(
        FlapFrame.SignOnChannel,
        options.Flavor == TocProtocolFlavor.Toc2 ? "TOC2" : "TOC",
        nullTerminate: false,
        cancellationToken).ConfigureAwait(false);

    var signOnCommand = TocCommand.Format(
        options.Flavor == TocProtocolFlavor.Toc2 ? "toc2_signon" : "toc_signon",
        options.AuthorizerHost,
        options.AuthorizerPort.ToString(),
        options.ScreenName,
        TocPassword.Roast(options.Password),
        options.Language,
        options.ClientId);

    await SendRawCommandAsync(signOnCommand, cancellationToken).ConfigureAwait(false);
    await WaitForSignOnAsync(cancellationToken).ConfigureAwait(false);

    _readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _readerTask = Task.Run(() => ReadLoopAsync(_readerCancellation.Token), CancellationToken.None);
  }

  public Task JoinChatAsync(string roomName, CancellationToken cancellationToken = default)
  {
    var command = TocCommand.Format($"{_flavor.CommandPrefix()}_chat_join", "4", roomName);
    return SendRawCommandAsync(command, cancellationToken);
  }

  public async Task SendChatAsync(string roomNameOrId, string message, CancellationToken cancellationToken = default)
  {
    var roomId = ResolveRoomId(roomNameOrId);
    var command = TocCommand.Format($"{_flavor.CommandPrefix()}_chat_send", roomId, message);
    await SendRawCommandAsync(command, cancellationToken).ConfigureAwait(false);
  }

  public async Task WhisperAsync(
      string roomNameOrId,
      string recipient,
      string message,
      CancellationToken cancellationToken = default)
  {
    var roomId = ResolveRoomId(roomNameOrId);
    var command = TocCommand.Format($"{_flavor.CommandPrefix()}_chat_whisper", roomId, recipient, message);
    await SendRawCommandAsync(command, cancellationToken).ConfigureAwait(false);
  }

  public async Task LeaveChatAsync(string roomNameOrId, CancellationToken cancellationToken = default)
  {
    var roomId = ResolveRoomId(roomNameOrId);
    var command = TocCommand.Format($"{_flavor.CommandPrefix()}_chat_leave", roomId);
    await SendRawCommandAsync(command, cancellationToken).ConfigureAwait(false);
  }

  public Task SendImAsync(string recipient, string message, CancellationToken cancellationToken = default)
  {
    var command = TocCommand.Format($"{_flavor.CommandPrefix()}_send_im", recipient, message);
    return SendRawCommandAsync(command, cancellationToken);
  }

  public Task SignOffAsync(CancellationToken cancellationToken = default)
  {
    return SendRawCommandAsync(TocCommand.Format($"{_flavor.CommandPrefix()}_signoff"), cancellationToken);
  }

  public async ValueTask DisposeAsync()
  {
    if (_readerCancellation is not null)
    {
      await _readerCancellation.CancelAsync().ConfigureAwait(false);
      _readerCancellation.Dispose();
    }

    if (_connection is not null)
    {
      await _connection.DisposeAsync().ConfigureAwait(false);
    }

    if (_readerTask is not null)
    {
      try
      {
        await _readerTask.ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
      }
      catch (ObjectDisposedException)
      {
      }
    }

    _sendLock.Dispose();
  }

  private async Task WaitForSignOnAsync(CancellationToken cancellationToken)
  {
    while (true)
    {
      var frame = await RequireConnection().ReadFrameAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new EndOfStreamException("Server disconnected before sign-on completed.");

      if (frame.Channel != FlapFrame.DataChannel)
      {
        continue;
      }

      var tocEvent = TocEvent.Parse(frame.GetText());
      ProcessEvent(tocEvent);

      if (tocEvent.Name.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException(tocEvent.JoinArgumentsFrom(0));
      }

      if (tocEvent.Name.Equals("SIGN_ON", StringComparison.OrdinalIgnoreCase)
          || tocEvent.Name.Equals("SIGN_ON2", StringComparison.OrdinalIgnoreCase))
      {
        SignedOnScreenName = tocEvent.Arguments.Count > 0 ? tocEvent.Arguments[0] : string.Empty;
        return;
      }
    }
  }

  private async Task ReadLoopAsync(CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var frame = await RequireConnection().ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
          break;
        }

        if (frame.Channel == FlapFrame.DataChannel)
        {
          ProcessEvent(TocEvent.Parse(frame.GetText()));
        }
      }
    }
    catch (OperationCanceledException)
    {
    }
    catch (ObjectDisposedException)
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
      Disconnected?.Invoke(this, EventArgs.Empty);
    }
  }

  private void ProcessEvent(TocEvent tocEvent)
  {
    EventReceived?.Invoke(this, tocEvent);

    switch (tocEvent.Name.ToUpperInvariant())
    {
      case "CHAT_JOIN":
      case "CHAT_JOIN2":
        if (tocEvent.Arguments.Count >= 2)
        {
          _roomIdToName[tocEvent.Arguments[0]] = tocEvent.Arguments[1];
          _roomNameToId[tocEvent.Arguments[1]] = tocEvent.Arguments[0];
          ChatRoomJoined?.Invoke(this, new ChatRoomJoined(tocEvent.Arguments[0], tocEvent.Arguments[1]));
        }

        break;

      case "CHAT_IN":
      case "CHAT_IN2":
        if (tocEvent.Arguments.Count >= 4)
        {
          var roomId = tocEvent.Arguments[0];
          _roomIdToName.TryGetValue(roomId, out var roomName);
          ChatMessageReceived?.Invoke(
              this,
              new ChatMessage(
                  roomId,
                  roomName,
                  tocEvent.Arguments[1],
                  tocEvent.JoinArgumentsFrom(3),
                  tocEvent.Arguments[2].Equals("T", StringComparison.OrdinalIgnoreCase)));
        }

        break;

      case "IM_IN":
      case "IM_IN2":
        if (tocEvent.Arguments.Count >= 3)
        {
          DirectMessageReceived?.Invoke(
              this,
              new DirectMessage(tocEvent.Arguments[0], tocEvent.JoinArgumentsFrom(2)));
        }

        break;

      case "CHAT_UPDATE_BUDDY":
      case "CHAT_UPDATE_BUDDY2":
        if (tocEvent.Arguments.Count >= 3)
        {
          RoomMemberChanged?.Invoke(
              this,
              new RoomMemberChanged(
                  tocEvent.Arguments[0],
                  tocEvent.Arguments[1],
                  tocEvent.Arguments[2].Equals("T", StringComparison.OrdinalIgnoreCase)));
        }

        break;

      case "ERROR":
        ErrorReceived?.Invoke(this, tocEvent.JoinArgumentsFrom(0));
        break;
    }
  }

  private async Task SendRawCommandAsync(string command, CancellationToken cancellationToken)
  {
    await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await RequireConnection().SendTextAsync(FlapFrame.DataChannel, command, cancellationToken: cancellationToken)
          .ConfigureAwait(false);
    }
    finally
    {
      _sendLock.Release();
    }
  }

  private string ResolveRoomId(string roomNameOrId)
  {
    if (_roomIdToName.ContainsKey(roomNameOrId))
    {
      return roomNameOrId;
    }

    if (_roomNameToId.TryGetValue(roomNameOrId, out var id))
    {
      return id;
    }

    throw new InvalidOperationException($"Unknown room '{roomNameOrId}'. Join it first.");
  }

  private FlapConnection RequireConnection()
  {
    return _connection ?? throw new InvalidOperationException("Client is not connected.");
  }
}