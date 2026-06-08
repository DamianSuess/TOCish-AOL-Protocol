namespace AolToc.Protocol;

public sealed record ChatRoomJoined(string RoomId, string RoomName);

public sealed record ChatMessage(string RoomId, string? RoomName, string Sender, string Message, bool IsWhisper);

public sealed record DirectMessage(string Sender, string Message);

public sealed record RoomMemberChanged(string RoomId, string ScreenName, bool IsPresent);
