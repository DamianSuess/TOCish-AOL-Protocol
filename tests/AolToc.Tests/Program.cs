using AolToc.Protocol;
using AolToc.Server;
using System.Net;
using System.Net.Sockets;

namespace AolToc.Tests;

internal static class Program
{
  public static async Task<int> Main()
  {
    var tests = new (string Name, Func<Task> Body)[]
    {
      ("password roast round-trips", () => RunSync(PasswordRoastRoundTrips)),
      ("screen names normalize like AIM names", () => RunSync(ScreenNamesNormalize)),
      ("commands parse quoted arguments", () => RunSync(CommandsParseQuotedArguments)),
      ("commands preserve empty quoted arguments", () => RunSync(CommandsPreserveEmptyQuotedArguments)),
      ("commands format reusable payloads", () => RunSync(CommandsFormatReusablePayloads)),
      ("events preserve colon-rich message tails", () => RunSync(EventsPreserveMessageTails)),
      ("FLAP frames round-trip to wire bytes", () => RunSync(FlapFramesRoundTrip)),
      ("server relays chat between real clients", ServerRelaysChatBetweenRealClients)
    };

    var failed = 0;
    foreach (var test in tests)
    {
      try
      {
        await test.Body().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
      }
      catch (Exception ex)
      {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
      }
    }

    Console.WriteLine(failed == 0 ? "All tests passed." : $"{failed} test(s) failed.");
    return failed == 0 ? 0 : 1;
  }

  private static Task RunSync(Action action)
  {
    action();
    return Task.CompletedTask;
  }

  private static void PasswordRoastRoundTrips()
  {
    var roasted = TocPassword.Roast("password");
    Assert(roasted.StartsWith("0x", StringComparison.OrdinalIgnoreCase), "Password should be roasted as hex.");
    AssertEqual("password", TocPassword.Unroast(roasted));
  }

  private static void ScreenNamesNormalize()
  {
    AssertEqual("alicecooper", ScreenName.Normalize("Alice Cooper"));
    Assert(ScreenName.IsValid("Alice_Cooper-1"), "Expected valid screen name.");
    Assert(!ScreenName.IsValid("ab"), "Expected short screen name to be invalid.");
  }

  private static void CommandsParseQuotedArguments()
  {
    var command = TocCommand.Parse("toc_chat_send 1001 \"hello there\" bare\\ value\0");
    AssertEqual("toc_chat_send", command.Name);
    AssertEqual("1001", command.Arguments[0]);
    AssertEqual("hello there", command.Arguments[1]);
    AssertEqual("bare value", command.Arguments[2]);
  }

  private static void CommandsPreserveEmptyQuotedArguments()
  {
    var command = TocCommand.Parse("toc_send_im bob \"\"");
    AssertEqual("toc_send_im", command.Name);
    AssertEqual("bob", command.Arguments[0]);
    AssertEqual(string.Empty, command.Arguments[1]);
  }

  private static void CommandsFormatReusablePayloads()
  {
    var formatted = TocCommand.Format("toc_chat_send", "1001", "hello \"quoted\" friend");
    var parsed = TocCommand.Parse(formatted);
    AssertEqual("toc_chat_send", parsed.Name);
    AssertEqual("1001", parsed.Arguments[0]);
    AssertEqual("hello \"quoted\" friend", parsed.Arguments[1]);
  }

  private static void EventsPreserveMessageTails()
  {
    var tocEvent = TocEvent.Parse("CHAT_IN:1001:alice:F:hello:with:colons\0");
    AssertEqual("CHAT_IN", tocEvent.Name);
    AssertEqual("hello:with:colons", tocEvent.JoinArgumentsFrom(3));
  }

  private static void FlapFramesRoundTrip()
  {
    var frame = FlapFrame.FromText(FlapFrame.DataChannel, 42, "SIGN_ON:alice");
    var wire = frame.ToWire();

    Assert(FlapFrame.TryRead(wire, out var decoded, out var bytesRead), "Expected complete frame.");
    AssertEqual(wire.Length, bytesRead);
    AssertEqual(FlapFrame.DataChannel, decoded!.Channel);
    AssertEqual((ushort)42, decoded.Sequence);
    AssertEqual("SIGN_ON:alice", decoded.GetText());
  }

  private static async Task ServerRelaysChatBetweenRealClients()
  {
    var port = GetFreePort();
    var users = new InMemoryUserStore(allowRegistration: false);
    users.AddOrUpdate("alice", "password");
    users.AddOrUpdate("bob", "password");

    using var serverCancellation = new CancellationTokenSource();
    var server = new TocChatServer(IPAddress.Loopback, port, users);
    var serverTask = Task.Run(() => server.RunAsync(serverCancellation.Token));
    await Task.Delay(250).ConfigureAwait(false);

    await using var alice = new TocChatClient();
    await using var bob = new TocChatClient();

    var bobJoined = new TaskCompletionSource<ChatRoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
    var bobReceived = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

    bob.ChatRoomJoined += (_, room) =>
    {
      if (room.RoomName.Equals("lobby", StringComparison.OrdinalIgnoreCase))
      {
        bobJoined.TrySetResult(room);
      }
    };

    bob.ChatMessageReceived += (_, message) =>
    {
      if (message.Sender.Equals("alice", StringComparison.OrdinalIgnoreCase)
              && message.Message == "hello from smoke")
      {
        bobReceived.TrySetResult(message);
      }
    };

    try
    {
      await alice.ConnectAsync(new TocClientOptions("127.0.0.1", port, "alice", "password"))
          .ConfigureAwait(false);
      await bob.ConnectAsync(new TocClientOptions("127.0.0.1", port, "bob", "password")
      {
        Flavor = TocProtocolFlavor.Toc2
      }).ConfigureAwait(false);

      await alice.JoinChatAsync("lobby").ConfigureAwait(false);
      await bob.JoinChatAsync("lobby").ConfigureAwait(false);
      await bobJoined.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

      await alice.SendChatAsync("lobby", "hello from smoke").ConfigureAwait(false);
      var received = await bobReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
      AssertEqual("hello from smoke", received.Message);
    }
    finally
    {
      await SafeSignOffAsync(alice).ConfigureAwait(false);
      await SafeSignOffAsync(bob).ConfigureAwait(false);
      serverCancellation.Cancel();

      try
      {
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
      }
      catch (TimeoutException)
      {
        throw new InvalidOperationException("Server did not stop after cancellation.");
      }
    }
  }

  private static async Task SafeSignOffAsync(TocChatClient client)
  {
    try
    {
      await client.SignOffAsync().ConfigureAwait(false);
    }
    catch (Exception)
    {
    }
  }

  private static int GetFreePort()
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  private static void Assert(bool value, string message)
  {
    if (!value)
    {
      throw new InvalidOperationException(message);
    }
  }

  private static void AssertEqual<T>(T expected, T actual)
  {
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
      throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
  }
}