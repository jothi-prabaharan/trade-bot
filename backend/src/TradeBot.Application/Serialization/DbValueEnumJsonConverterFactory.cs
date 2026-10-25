using System.Text.Json;
using System.Text.Json.Serialization;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Serialization;

/// <summary>Serializes enums with their DB text ('PAPER', 'SL_HIT', '15m') so API, UI and DB share one vocabulary.</summary>
public sealed class DbValueEnumJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException($"Null is not a valid {typeof(TEnum).Name}.");
            try { return DbValue.FromDb<TEnum>(text); }
            catch (ArgumentOutOfRangeException e) { throw new JsonException(e.Message, e); }
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DbValue.ToDb(value));
    }
}
