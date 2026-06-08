using AolToc.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AolToc.Client;

internal static class Program
{
  public static async Task<int> Main(string[] args)
  {
    var options = ClientOptions.Parse(args);
    var screenName = options.ScreenName ?? Prompt("Screen name");
    var password = options.Password ?? ReadPassword("Password");
    var joinedRooms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? activeRoom = null;

    await using var client = new TocChatClient();
    client.ChatRoomJoined += (_, room) =>
    {
      joinedRooms[room.RoomName] = room.RoomId;
      activeRoom ??= room.RoomName;
      Console.WriteLine($"[joined {room.RoomName} as room id {room.RoomId}]");
    };

    client.ChatMessageReceived += (_, message) =>
    {
      var whisper = message.IsWhisper ? " whisper" : string.Empty;
      var room = message.RoomName ?? message.RoomId;
      Console.WriteLine($"[{room}{whisper}] {message.Sender}: {message.Message}");
    };

    client.DirectMessageReceived += (_, message) =>
    {
      Console.WriteLine($"[im] {message.Sender}: {message.Message}");
    };

    client.RoomMemberChanged += (_, change) =>
    {
      var state = change.IsPresent ? "entered" : "left";
      Console.WriteLine($"[room {change.RoomId}] {change.ScreenName} {state}");
    };

    client.ErrorReceived += (_, error) =>
    {
      Console.WriteLine($"[error] {error}");
    };

    client.Disconnected += (_, _) =>
    {
      Console.WriteLine("[disconnected]");
    };

    try
    {
      await client.ConnectAsync(new TocClientOptions(options.Host, options.Port, screenName, password)
      {
        Flavor = options.UseToc2 ? TocProtocolFlavor.Toc2 : TocProtocolFlavor.Toc
      }).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Could not connect: {ex.Message}");
      return 1;
    }

    Console.WriteLine($"Signed on as {client.SignedOnScreenName} using {(options.UseToc2 ? "TOC2" : "TOC")}.");
    PrintHelp();

    while (true)
    {
      Console.Write(activeRoom is null ? "> " : $"{activeRoom}> ");
      var line = Console.ReadLine();
      if (line is null)
      {
        break;
      }

      if (string.IsNullOrWhiteSpace(line))
      {
        continue;
      }

      try
      {
        if (line.StartsWith('/'))
        {
          var keepGoing = await HandleCommandAsync(
                  client,
                  joinedRooms,
                  line,
                  () => activeRoom,
                  room => activeRoom = room)
              .ConfigureAwait(false);
          if (!keepGoing)
          {
            break;
          }

          continue;
        }

        if (activeRoom is null)
        {
          Console.WriteLine("Join a room first with /join room-name.");
          continue;
        }

        await client.SendChatAsync(activeRoom, line).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[local error] {ex.Message}");
      }
    }

    await client.SignOffAsync().ConfigureAwait(false);
    return 0;
  }

  private static async Task<bool> HandleCommandAsync(
      TocChatClient client,
      Dictionary<string, string> joinedRooms,
      string line,
      Func<string?> getActiveRoom,
      Action<string?> setActiveRoom)
  {
    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    var command = parts[0].ToLowerInvariant();

    switch (command)
    {
      case "/help":
        PrintHelp();
        return true;

      case "/join" when parts.Length >= 2:
        await client.JoinChatAsync(parts[1]).ConfigureAwait(false);
        return true;

      case "/rooms":
        if (joinedRooms.Count == 0)
        {
          Console.WriteLine("No joined rooms.");
        }
        else
        {
          foreach (var room in joinedRooms)
          {
            Console.WriteLine($"{room.Key} ({room.Value})");
          }
        }

        return true;

      case "/room" when parts.Length >= 2:
        if (!joinedRooms.ContainsKey(parts[1]) && !joinedRooms.ContainsValue(parts[1]))
        {
          Console.WriteLine("Unknown room. Use /rooms to see joined rooms.");
        }
        else
        {
          setActiveRoom(parts[1]);
        }

        return true;

      case "/leave":
        var roomToLeave = parts.Length >= 2 ? parts[1] : getActiveRoom();
        if (roomToLeave is null)
        {
          Console.WriteLine("No room to leave.");
          return true;
        }

        await client.LeaveChatAsync(roomToLeave).ConfigureAwait(false);
        RemoveRoom(joinedRooms, roomToLeave);
        setActiveRoom(joinedRooms.Keys.FirstOrDefault());
        return true;

      case "/w" when parts.Length >= 3:
      case "/whisper" when parts.Length >= 3:
        var activeRoom = getActiveRoom();
        if (activeRoom is null)
        {
          Console.WriteLine("Join a room before whispering.");
          return true;
        }

        await client.WhisperAsync(activeRoom, parts[1], parts[2]).ConfigureAwait(false);
        return true;

      case "/im" when parts.Length >= 3:
        await client.SendImAsync(parts[1], parts[2]).ConfigureAwait(false);
        return true;

      case "/quit":
      case "/exit":
        return false;

      default:
        Console.WriteLine("Unknown command or missing argument. Use /help.");
        return true;
    }
  }

  private static void RemoveRoom(Dictionary<string, string> joinedRooms, string roomNameOrId)
  {
    if (joinedRooms.Remove(roomNameOrId))
    {
      return;
    }

    var match = joinedRooms.FirstOrDefault(room =>
        room.Value.Equals(roomNameOrId, StringComparison.OrdinalIgnoreCase));

    if (!string.IsNullOrEmpty(match.Key))
    {
      joinedRooms.Remove(match.Key);
    }
  }

  private static string Prompt(string label)
  {
    Console.Write($"{label}: ");
    return Console.ReadLine() ?? string.Empty;
  }

  private static string ReadPassword(string label)
  {
    Console.Write($"{label}: ");

    if (Console.IsInputRedirected)
    {
      return Console.ReadLine() ?? string.Empty;
    }

    var chars = new List<char>();
    while (true)
    {
      var key = Console.ReadKey(intercept: true);
      if (key.Key == ConsoleKey.Enter)
      {
        Console.WriteLine();
        return new string(chars.ToArray());
      }

      if (key.Key == ConsoleKey.Backspace)
      {
        if (chars.Count > 0)
        {
          chars.RemoveAt(chars.Count - 1);
          Console.Write("\b \b");
        }

        continue;
      }

      chars.Add(key.KeyChar);
      Console.Write('*');
    }
  }

  private static void PrintHelp()
  {
    Console.WriteLine("Commands:");
    Console.WriteLine("  /join room        Join or create a chat room");
    Console.WriteLine("  /rooms            List rooms joined by this client");
    Console.WriteLine("  /room room        Set the active room");
    Console.WriteLine("  /leave [room]     Leave a room");
    Console.WriteLine("  /w user message   Whisper in the active room");
    Console.WriteLine("  /im user message  Send a direct IM");
    Console.WriteLine("  /quit             Sign off");
    Console.WriteLine("Type a plain line to send it to the active room.");
  }
}

internal sealed record ClientOptions(
    string Host,
    int Port,
    string? ScreenName,
    string? Password,
    bool UseToc2)
{
  public static ClientOptions Parse(string[] args)
  {
    var host = "127.0.0.1";
    var port = 5190;
    string? screenName = null;
    string? password = null;
    var useToc2 = false;

    for (var i = 0; i < args.Length; i++)
    {
      switch (args[i])
      {
        case "--host" when i + 1 < args.Length:
          host = args[++i];
          break;

        case "--port" when i + 1 < args.Length:
          port = int.Parse(args[++i]);
          break;

        case "--user" when i + 1 < args.Length:
          screenName = args[++i];
          break;

        case "--password" when i + 1 < args.Length:
          password = args[++i];
          break;

        case "--toc2":
          useToc2 = true;
          break;

        case "--help":
        case "-h":
          PrintUsage();
          Environment.Exit(0);
          break;

        default:
          throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
      }
    }

    return new ClientOptions(host, port, screenName, password, useToc2);
  }

  private static void PrintUsage()
  {
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/AolToc.Client -- --user alice --password password");
    Console.WriteLine("  dotnet run --project src/AolToc.Client -- --user bob --password password --toc2");
  }
}