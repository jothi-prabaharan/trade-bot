using System.Buffers.Binary;
using TradeBot.Application.Market;

namespace TradeBot.Infrastructure.External.Paytm;

/// <summary>
/// Decodes Paytm Money broadcast frames: concatenated packets of [type byte][little-endian body].
/// Layouts mirror the official pyPMClient WebSocketClient. Ticks are stamped with the receive time.
/// </summary>
public static class PaytmTickParser
{
    public const byte Ltp = 61, Quote = 62, Full = 63, IndexLtp = 64, IndexQuote = 65, IndexFull = 66;

    public static int BodySize(byte type) => type switch
    {
        Ltp => 22, Quote => 66, Full => 174, IndexLtp => 22, IndexQuote => 42, IndexFull => 38, _ => -1,
    };

    public static List<Tick> Parse(ReadOnlySpan<byte> frame, DateTime receivedUtc)
    {
        var ticks = new List<Tick>();
        var pos = 0;
        while (pos < frame.Length)
        {
            var type = frame[pos];
            var size = BodySize(type);
            if (size < 0 || pos + 1 + size > frame.Length) break; // unknown packet: its length is unknowable
            var b = frame.Slice(pos + 1, size);
            pos += 1 + size;

            var tick = type switch
            {
                Ltp or IndexLtp => new Tick(U32(b, 8), F(b, 0), receivedUtc),
                Quote => new Tick(U32(b, 8), F(b, 0), receivedUtc, U32(b, 22), null, F(b, 18)),
                Full => new Tick(U32(b, 108), F(b, 100), receivedUtc, U32(b, 122), U32(b, 166), F(b, 118)),
                _ => new Tick(U32(b, 4), F(b, 0), receivedUtc), // index quote/full
            };
            if (tick.Ltp > 0 && tick.SecurityId > 0) ticks.Add(tick);
        }
        return ticks;
    }

    private static decimal F(ReadOnlySpan<byte> b, int at)
    {
        var f = BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
        return float.IsFinite(f) && Math.Abs(f) < 1e9f ? Math.Round((decimal)f, 2) : 0; // corrupt floats must not throw
    }
    private static long U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
}
