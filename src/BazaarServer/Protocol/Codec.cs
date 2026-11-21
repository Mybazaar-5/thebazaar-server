using BazaarGameShared;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using MessagePack;

namespace BazaarServer.Protocol;

/// <summary>
/// 与客户端逐字节对齐的 MessagePack 编解码器（含反序列化安全防护）。
/// - 序列化必须使用客户端自带的 MessagePackConfig.Options（Lz4BlockArray + 1024 阈值）；
/// - 反序列化启用包体长度上限、UntrustedData 深度与哈希冲突防护，防范畸形包与 LZ4 膨胀攻击。
/// </summary>
public static class Codec
{
    /// <summary>客户端单条命令请求体最大字节数（256 KB）。</summary>
    public const int MaxCommandPayloadBytes = 256 * 1024;

    /// <summary>单条服务端消息/回放反序列化最大字节数（4 MB）。</summary>
    public const int MaxMessagePayloadBytes = 4 * 1024 * 1024;

    /// <summary>与客户端对齐的编码选项。</summary>
    public static readonly MessagePackSerializerOptions Options = MessagePackConfig.Options;

    /// <summary>用于不可信网络输入的加固反序列化选项（含深度上限与抗碰撞哈希）。</summary>
    public static readonly MessagePackSerializerOptions UntrustedReadOptions =
        MessagePackConfig.Options.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>反序列化客户端指令（强制检查长度上限与反序列化安全边界）。</summary>
    public static INetCommand ReadCommand(byte[] body)
    {
        if (body == null || body.Length == 0)
            throw new InvalidDataException("指令数据包为空，无法解析。");

        if (body.Length > MaxCommandPayloadBytes)
            throw new InvalidDataException($"指令数据包体积超出限制（最大允许 {MaxCommandPayloadBytes / 1024} KB）。");

        try
        {
            var cmd = MessagePackSerializer.Deserialize<INetCommand>(body, UntrustedReadOptions);
            if (cmd == null)
                throw new InvalidDataException("指令数据包内容为空或结构不受支持。");
            return cmd;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidDataException("指令数据包格式非法或超出安全嵌套深度限制。", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or NotSupportedException)
        {
            throw new InvalidDataException("指令数据包已损坏或无法反序列化。", ex);
        }
    }

    /// <summary>安全尝试解析客户端指令，失败时返回可读中文错误信息。</summary>
    public static bool TryReadCommand(byte[]? body, out INetCommand? command, out string? errorZh)
    {
        command = null;
        errorZh = null;
        if (body == null || body.Length == 0)
        {
            errorZh = "指令数据包为空。";
            return false;
        }

        try
        {
            command = ReadCommand(body);
            return true;
        }
        catch (Exception ex)
        {
            errorZh = ex.Message;
            return false;
        }
    }

    /// <summary>反序列化服务端消息（用于观战帧合并与回放校验，受最大长度与 UntrustedData 保护）。</summary>
    public static INetMessage ReadMessage(byte[] body)
    {
        if (body == null || body.Length == 0)
            throw new InvalidDataException("消息数据包为空，无法解析。");

        if (body.Length > MaxMessagePayloadBytes)
            throw new InvalidDataException($"消息数据包体积超出限制（最大允许 {MaxMessagePayloadBytes / (1024 * 1024)} MB）。");

        try
        {
            var msg = MessagePackSerializer.Deserialize<INetMessage>(body, UntrustedReadOptions);
            if (msg == null)
                throw new InvalidDataException("消息数据包内容为空。");
            return msg;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("消息数据包格式异常或超出安全限制。", ex);
        }
    }

    public static byte[] WriteMessage(INetMessage message)
        => MessagePackSerializer.Serialize(message, Options);

    public static string ToJson(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "<空数据包>";
        if (bytes.Length > MaxMessagePayloadBytes) return "<数据包过大，已跳过调试打印>";
        try { return MessagePackSerializer.ConvertToJson(bytes, UntrustedReadOptions); }
        catch (Exception ex) { return "<无法格式化消息: " + ex.Message + ">"; }
    }

    public static string ToJson(INetMessage message)
    {
        try { return MessagePackSerializer.ConvertToJson(WriteMessage(message), UntrustedReadOptions); }
        catch (Exception ex) { return "<无法格式化消息: " + ex.Message + ">"; }
    }
}
