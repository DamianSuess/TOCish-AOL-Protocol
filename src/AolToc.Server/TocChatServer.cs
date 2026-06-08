using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AolToc.Protocol;

namespace AolToc.Server;

public sealed class TocChatServer(IPAddress address, int port, InMemoryUserStore users)
{
  private readonly ConcurrentDictionary<string, ClientSession> _online = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<string, ChatRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
  private readonly TcpListener _listener = new(address, port);
  private int _nextRoomId = 1000;

  public async Task RunAsync(CancellationToken cancellationToken)
  {
    _listener.Start();

    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var session = new ClientSession(this, tcpClient);
        _ = Task.Run(() => session.RunAsync(cancellationToken), CancellationToken.None);
      }
    }
    finally
    {
      _listener.Stop();
    }
  }

  internal async Task SignOnAsync(
      ClientSession session,
      string screenName,
      string password,
      TocProtocolFlavor flavor,
      CancellationToken cancellationToken)
  {
    if (!ScreenName.IsValid(screenName))
    {
      await session.SendErrorAsync("980", "Invalid screen name.", cancellationToken).ConfigureAwait(false);
      return;
    }

    string plainPassword;
    try
    {
      plainPassword = TocPassword.Unroast(password);
    }
    catch (FormatException)
    {
      await session.SendErrorAsync("980", "Invalid roasted password.", cancellationToken).ConfigureAwait(false);
      return;
    }

    if (!users.TryAuthenticate(screenName, plainPassword, out var displayName))
    {
      await session.SendErrorAsync("980", "Authentication failed.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var normalized = ScreenName.Normalize(displayName);
    session.MarkSignedOn(displayName, normalized, flavor);

    if (!_online.TryAdd(normalized, session))
    {
      session.ClearSignedOn();
      await session.SendErrorAsync("901", "Screen name is already signed on.", cancellationToken)
          .ConfigureAwait(false);
      return;
    }

    await session.SendEventAsync("SIGN_ON", cancellationToken, displayName).ConfigureAwait(false);
  }

  internal async Task JoinRoomAsync(ClientSession session, string roomName, CancellationToken cancellationToken)
  {
    if (!session.IsSignedOn)
    {
      await session.SendErrorAsync("901", "Sign on before joining chat.", cancellationToken).ConfigureAwait(false);
      return;
    }

    if (string.IsNullOrWhiteSpace(roomName))
    {
      await session.SendErrorAsync("901", "Room name is required.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var key = ScreenName.Normalize(roomName);
    var room = _rooms.GetOrAdd(key, _ => new ChatRoom(Interlocked.Increment(ref _nextRoomId).ToString(), roomName));
    var existingMembers = room.Snapshot();
    var added = room.Add(session);

    await session.SendEventAsync("CHAT_JOIN", cancellationToken, room.Id, room.Name).ConfigureAwait(false);

    if (!added)
    {
      return;
    }

    foreach (var member in existingMembers)
    {
      await session.SendEventAsync(
        "CHAT_UPDATE_BUDDY",
        cancellationToken,
        room.Id,
        member.ScreenNameOrUnknown,
        "T").ConfigureAwait(false);
    }

    foreach (var member in room.Snapshot())
    {
      await member.SendEventAsync(
        "CHAT_UPDATE_BUDDY",
        cancellationToken,
        room.Id,
        session.ScreenNameOrUnknown,
        "T").ConfigureAwait(false);
    }
  }

  internal async Task SendRoomMessageAsync(
      ClientSession session,
      string roomId,
      string message,
      CancellationToken cancellationToken)
  {
    var room = FindRoomById(roomId);
    if (room is null || !room.Contains(session))
    {
      await session.SendErrorAsync("901", "You are not in that room.", cancellationToken).ConfigureAwait(false);
      return;
    }

    foreach (var member in room.Snapshot())
    {
      await member.SendEventAsync(
        "CHAT_IN",
        cancellationToken,
        room.Id,
        session.ScreenNameOrUnknown,
        "F",
        message).ConfigureAwait(false);
    }
  }

  internal async Task SendWhisperAsync(
    ClientSession session,
    string roomId,
    string recipient,
    string message,
    CancellationToken cancellationToken)
  {
    var room = FindRoomById(roomId);
    if (room is null || !room.Contains(session))
    {
      await session.SendErrorAsync("901", "You are not in that room.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var recipientSession = room.FindMember(recipient);
    if (recipientSession is null)
    {
      await session.SendErrorAsync("901", "Recipient is not in that room.", cancellationToken).ConfigureAwait(false);
      return;
    }

    await recipientSession.SendEventAsync(
      "CHAT_IN",
      cancellationToken,
      room.Id,
      session.ScreenNameOrUnknown,
      "T",
      message).ConfigureAwait(false);

    if (!ReferenceEquals(recipientSession, session))
    {
      await session.SendEventAsync(
        "CHAT_IN",
        cancellationToken,
        room.Id,
        session.ScreenNameOrUnknown,
        "T",
        message).ConfigureAwait(false);
    }
  }

  internal async Task LeaveRoomAsync(ClientSession session, string roomId, CancellationToken cancellationToken)
  {
    var room = FindRoomById(roomId);
    if (room is null)
    {
      return;
    }

    if (!room.Remove(session))
    {
      return;
    }

    foreach (var member in room.Snapshot())
    {
      await member.SendEventAsync(
        "CHAT_UPDATE_BUDDY",
        cancellationToken,
        room.Id,
        session.ScreenNameOrUnknown,
        "F").ConfigureAwait(false);
    }
  }

  internal async Task SendImAsync(
      ClientSession session,
      string recipient,
      string message,
      CancellationToken cancellationToken)
  {
    if (!_online.TryGetValue(ScreenName.Normalize(recipient), out var recipientSession))
    {
      await session.SendErrorAsync("901", "Recipient is not online.", cancellationToken).ConfigureAwait(false);
      return;
    }

    await recipientSession.SendEventAsync(
      "IM_IN",
      cancellationToken,
      session.ScreenNameOrUnknown,
      "F",
      message).ConfigureAwait(false);

    await session.SendEventAsync("IM_OUT", cancellationToken, recipientSession.ScreenNameOrUnknown, message)
      .ConfigureAwait(false);
  }

  internal async Task RemoveSessionAsync(ClientSession session, CancellationToken cancellationToken)
  {
    if (session.NormalizedScreenName is not null)
    {
      _online.TryRemove(session.NormalizedScreenName, out _);
    }

    foreach (var room in _rooms.Values)
    {
      if (!room.Remove(session))
        continue;

      foreach (var member in room.Snapshot())
      {
        await member.SendEventAsync(
          "CHAT_UPDATE_BUDDY",
          cancellationToken,
          room.Id,
          session.ScreenNameOrUnknown,
          "F").ConfigureAwait(false);
      }
    }
  }

  private ChatRoom? FindRoomById(string roomId)
  {
    return _rooms.Values.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.OrdinalIgnoreCase));
  }
}
