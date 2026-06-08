using System;

namespace AolToc.Protocol;

public enum TocProtocolFlavor
{
  Toc,
  Toc2,
}

public static class TocProtocolFlavorExtensions
{
  public static string CommandPrefix(this TocProtocolFlavor flavor)
  {
    return flavor == TocProtocolFlavor.Toc2 ? "toc2" : "toc";
  }

  public static bool IsSignOnCommand(this string commandName)
  {
    return
      string.Equals(commandName, "toc_signon", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(commandName, "toc2_signon", StringComparison.OrdinalIgnoreCase);
  }

  public static bool IsToc2Command(this string commandName)
  {
    return commandName.StartsWith("toc2_", StringComparison.OrdinalIgnoreCase);
  }
}
