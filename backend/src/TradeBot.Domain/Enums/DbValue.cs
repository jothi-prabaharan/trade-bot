using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace TradeBot.Domain.Enums;

/// <summary>Overrides the stored text for an enum member (default is UPPER_SNAKE of the member name).</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class DbValueAttribute(string value) : Attribute
{
    public string Value { get; } = value;
}

public static class DbValue
{
    private static readonly ConcurrentDictionary<Type, (Dictionary<object, string> ToDb, Dictionary<string, object> FromDb)> Maps = new();

    public static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum => Map(typeof(TEnum)).ToDb[value];

    public static TEnum FromDb<TEnum>(string value) where TEnum : struct, Enum =>
        Map(typeof(TEnum)).FromDb.TryGetValue(value, out var e)
            ? (TEnum)e
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Unknown {typeof(TEnum).Name} value");

    public static IReadOnlyCollection<string> AllValues(Type enumType) => Map(enumType).ToDb.Values;

    private static (Dictionary<object, string> ToDb, Dictionary<string, object> FromDb) Map(Type enumType) =>
        Maps.GetOrAdd(enumType, t =>
        {
            var toDb = new Dictionary<object, string>();
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                toDb[field.GetValue(null)!] = field.GetCustomAttribute<DbValueAttribute>()?.Value ?? ToUpperSnake(field.Name);
            return (toDb, toDb.ToDictionary(kv => kv.Value, kv => kv.Key));
        });

    private static string ToUpperSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }
}
