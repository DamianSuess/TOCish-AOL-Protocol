using System.Buffers.Binary;
using System.Text;

namespace AolToc.Protocol;

public sealed record FlapFrame(byte Channel, ushort Sequence, byte[] Payload)
{
  public const byte StartMarker = 0x2A;
  public const byte SignOnChannel = 1;
  public const byte DataChannel = 2;
  public const byte ErrorChannel = 3;
  public const byte KeepAliveChannel = 5;
  public const int HeaderLength = 6;

  public string GetText()
  {
    return Encoding.UTF8.GetString(Payload).TrimEnd('\0', '\r', '\n');
  }

  public byte[] ToWire()
  {
    if (Payload.Length > ushort.MaxValue)
    {
      throw new InvalidOperationException("FLAP payload is too large.");
    }

    var buffer = new byte[HeaderLength + Payload.Length];
    buffer[0] = StartMarker;
    buffer[1] = Channel;
    BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), Sequence);
    BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), (ushort)Payload.Length);
    Payload.CopyTo(buffer.AsSpan(HeaderLength));
    return buffer;
  }

  public static FlapFrame FromText(byte channel, ushort sequence, string text, bool nullTerminate = true)
  {
    var payload = Encoding.UTF8.GetBytes(nullTerminate ? text + '\0' : text);
    return new FlapFrame(channel, sequence, payload);
  }

  public static bool TryRead(ReadOnlySpan<byte> wire, out FlapFrame? frame, out int bytesRead)
  {
    frame = null;
    bytesRead = 0;

    if (wire.Length < HeaderLength)
    {
      return false;
    }

    if (wire[0] != StartMarker)
    {
      throw new FormatException("FLAP frame does not start with '*'.");
    }

    var length = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(4, 2));
    if (wire.Length < HeaderLength + length)
    {
      return false;
    }

    var payload = wire.Slice(HeaderLength, length).ToArray();
    frame = new FlapFrame(wire[1], BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(2, 2)), payload);
    bytesRead = HeaderLength + length;
    return true;
  }
}
