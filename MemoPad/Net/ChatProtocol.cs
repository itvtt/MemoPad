using System.Buffers.Binary;
using System.IO;
using System.Text;
using MemoPad.Core;

namespace MemoPad.Net;

/// <summary>
/// 대화창 (정음 메모패드 SimpleChat) 형식, 포트 9712. 메시지 = [명령 4][길이 4][내용]
/// 글자 = [바이트 수 4][UTF-16 + NUL]. 들어가는 쪽(손님)이 연결하고, 연결받은 쪽(방장)이 모두에게 돌려줌
/// </summary>
public static class ChatProtocol
{
    public const int Join = 0x02, MyInfo = 0x0D, Member = 0x16, Say = 0x65, System = 0x66, Leave = 0x67, Broadcast = 0x69;
    public const int DefaultFace = 0x0B;

    public sealed record Message(int Command, byte[] Payload);

    public static byte[] Frame(int command, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, command);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    public static async Task<Message> ReadAsync(Stream stream, CancellationToken token)
    {
        int command = await JuProtocol.ReadIntAsync(stream, token);
        int length = await JuProtocol.ReadIntAsync(stream, token);
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException($"대화 메시지 길이가 이상합니다: {length}");
        var payload = length == 0 ? [] : await JuProtocol.ReadExactAsync(stream, length, token);
        return new Message(command, payload);
    }

    public static byte[] Text(string text) => JuProtocol.LengthPrefixedUnicode(text);

    /// <summary>내용에서 글자 하나 (at 이 다음 자리로)</summary>
    public static string ReadText(byte[] payload, ref int at)
    {
        if (at + 4 > payload.Length) return "";
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at));
        at += 4;
        if (length <= 0 || at + length > payload.Length) return "";
        string text = Encoding.Unicode.GetString(payload, at, length).TrimEnd('\0');
        at += length;
        return text;
    }

    public sealed record Participant(string ChatName, Person Person, int Face);

    /// <summary>손님이 보내는 내 정보 (명령 0x0D): [대화명][IP][이름][E-Mail][0][0]</summary>
    public static byte[] InfoPayload(string chatName, Person me) =>
        JuProtocol.Concat(Text(chatName), Text(me.Ip), Text(me.Name), Text(me.Email), JuProtocol.Int(0), JuProtocol.Int(0));

    public static Participant ParseInfo(byte[] payload)
    {
        int at = 0;
        string chat = ReadText(payload, ref at);
        var person = new Person { Ip = ReadText(payload, ref at), Name = ReadText(payload, ref at), Email = ReadText(payload, ref at) };
        return new Participant(chat, person, DefaultFace);
    }

    /// <summary>방장이 보내는 참여자 (명령 0x16): [얼굴][대화명][IP][이름][E-Mail][0]</summary>
    public static byte[] MemberPayload(Participant participant) =>
        JuProtocol.Concat(JuProtocol.Int(participant.Face), Text(participant.ChatName), Text(participant.Person.Ip), Text(participant.Person.Name), Text(participant.Person.Email), JuProtocol.Int(0));

    public static Participant ParseMember(byte[] payload)
    {
        int face = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : DefaultFace;
        int at = 4;
        string chat = ReadText(payload, ref at);
        var person = new Person { Ip = ReadText(payload, ref at), Name = ReadText(payload, ref at), Email = ReadText(payload, ref at) };
        return new Participant(chat, person, face);
    }

    /// <summary>방장이 모두에게 돌려주는 말 (명령 0x69): [보낸 IP][글]</summary>
    public static byte[] BroadcastPayload(string ip, string text) => JuProtocol.Concat(Text(ip), Text(text));
}
