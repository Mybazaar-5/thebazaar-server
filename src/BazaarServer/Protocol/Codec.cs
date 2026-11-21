using System.Text;
using BazaarGameShared;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using MessagePack;

namespace BazaarServer.Protocol;

/// <summary>
/// 与客户端逐字节对齐的 MessagePack 编解码。
/// 唯一要点：必须使用客户端自带的 MessagePackConfig.Options（Lz4BlockArray + 1024 阈值）。
/// </summary>
public static class Codec
{
    public static readonly MessagePackSerializerOptions Options = MessagePackConfig.Options;

    public static INetCommand ReadCommand(byte[] body)
        => MessagePackSerializer.Deserialize<INetCommand>(body, Options);

    public static byte[] WriteMessage(INetMessage message)
        => MessagePackSerializer.Serialize(message, Options);

    public static string ToJson(byte[] bytes)
    {
        try { return MessagePackSerializer.ConvertToJson(bytes, Options); }
        catch (Exception ex) { return "<unprintable: " + ex.Message + ">"; }
    }

    public static string ToJson(INetMessage message)
    {
        try { return MessagePackSerializer.ConvertToJson(WriteMessage(message), Options); }
        catch (Exception ex) { return "<unprintable: " + ex.Message + ">"; }
    }
}
