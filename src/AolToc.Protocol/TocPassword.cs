using System;
using System.Globalization;
using System.Text;

namespace AolToc.Protocol;

public static class TocPassword
{
  private const string RoastKey = "Tic/Toc";

  public static string Roast(string password)
  {
    ArgumentNullException.ThrowIfNull(password);

    var builder = new StringBuilder(2 + (password.Length * 2));
    builder.Append("0x");

    for (var i = 0; i < password.Length; i++)
    {
      var roasted = password[i] ^ RoastKey[i % RoastKey.Length];
      builder.Append(roasted.ToString("x2", CultureInfo.InvariantCulture));
    }

    return builder.ToString();
  }

  public static string Unroast(string passwordOrRoasted)
  {
    ArgumentNullException.ThrowIfNull(passwordOrRoasted);

    if (!passwordOrRoasted.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
      return passwordOrRoasted;
    }

    var hex = passwordOrRoasted[2..];
    if (hex.Length % 2 != 0)
    {
      throw new FormatException("Roasted password hex must contain an even number of digits.");
    }

    var builder = new StringBuilder(hex.Length / 2);
    for (var i = 0; i < hex.Length; i += 2)
    {
      var value = byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      var unroasted = value ^ RoastKey[(i / 2) % RoastKey.Length];
      builder.Append((char)unroasted);
    }

    return builder.ToString();
  }
}
