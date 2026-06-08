using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AolToc.Protocol;

public sealed class FlapConnection : IAsyncDisposable
{
  public const string Flapon = "FLAPON\r\n\r\n";

  private readonly TcpClient _client;
  private readonly NetworkStream _stream;
  private readonly SemaphoreSlim _sendLock = new(1, 1);
  private ushort _nextSequence = 1;

  private FlapConnection(TcpClient client)
  {
    _client = client;
    _stream = client.GetStream();
  }

  public static async Task<FlapConnection> ConnectAsClientAsync(
    TcpClient client,
    CancellationToken cancellationToken = default)
  {
    var connection = new FlapConnection(client);
    await connection.WriteFlaponAsync(cancellationToken).ConfigureAwait(false);
    await connection.ReadFlaponAsync(cancellationToken).ConfigureAwait(false);
    return connection;
  }

  public static async Task<FlapConnection> AcceptAsServerAsync(
    TcpClient client,
    CancellationToken cancellationToken = default)
  {
    var connection = new FlapConnection(client);
    await connection.ReadFlaponAsync(cancellationToken).ConfigureAwait(false);
    await connection.WriteFlaponAsync(cancellationToken).ConfigureAwait(false);
    return connection;
  }

  public async Task SendTextAsync(
    byte channel,
    string text,
    bool nullTerminate = true,
    CancellationToken cancellationToken = default)
  {
    var payload = Encoding.UTF8.GetBytes(nullTerminate ? text + '\0' : text);
    await SendAsync(channel, payload, cancellationToken).ConfigureAwait(false);
  }

  public async Task SendAsync(byte channel, byte[] payload, CancellationToken cancellationToken = default)
  {
    await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var frame = new FlapFrame(channel, _nextSequence++, payload);
      await _stream.WriteAsync(frame.ToWire(), cancellationToken).ConfigureAwait(false);
      await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _sendLock.Release();
    }
  }

  public async Task<FlapFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
  {
    var header = new byte[FlapFrame.HeaderLength];
    if (!await ReadExactlyOrEofAsync(header, cancellationToken).ConfigureAwait(false))
      return null;

    if (header[0] != FlapFrame.StartMarker)
      throw new FormatException("FLAP frame does not start with '*'.");

    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
    var payload = new byte[length];
    if (length > 0)
      await ReadExactlyOrThrowAsync(payload, cancellationToken).ConfigureAwait(false);

    return new FlapFrame(header[1], BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)), payload);
  }

  public ValueTask DisposeAsync()
  {
    _sendLock.Dispose();
    _stream.Dispose();
    _client.Dispose();
    return ValueTask.CompletedTask;
  }

  private async Task WriteFlaponAsync(CancellationToken cancellationToken)
  {
    var bytes = Encoding.ASCII.GetBytes(Flapon);
    await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
  }

  private async Task ReadFlaponAsync(CancellationToken cancellationToken)
  {
    var expected = Encoding.ASCII.GetBytes(Flapon);
    var actual = new byte[expected.Length];
    await ReadExactlyOrThrowAsync(actual, cancellationToken).ConfigureAwait(false);

    if (!actual.AsSpan().SequenceEqual(expected))
    {
      throw new FormatException("Expected FLAPON handshake.");
    }
  }

  private async Task<bool> ReadExactlyOrEofAsync(byte[] buffer, CancellationToken cancellationToken)
  {
    var offset = 0;
    while (offset < buffer.Length)
    {
      var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
      if (read == 0)
      {
        if (offset == 0)
          return false;

        throw new EndOfStreamException("Connection closed in the middle of a FLAP frame.");
      }

      offset += read;
    }

    return true;
  }

  private async Task ReadExactlyOrThrowAsync(byte[] buffer, CancellationToken cancellationToken)
  {
    if (!await ReadExactlyOrEofAsync(buffer, cancellationToken).ConfigureAwait(false))
      throw new EndOfStreamException("Connection closed while reading from the stream.");
  }
}
