using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TradeBot.Domain.Common;
using TradeBot.Domain.Enums;
using TradeBot.Infrastructure.Security;

namespace TradeBot.Infrastructure.Persistence.Conventions;

public sealed class DbValueEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(v => DbValue.ToDb(v), v => DbValue.FromDb<TEnum>(v))
    where TEnum : struct, Enum;

public sealed class EncryptedStringConverter(ISecretProtector protector)
    : ValueConverter<string, string>(v => protector.Protect(v), v => protector.Unprotect(v));

internal static class TradeBotModelConventions
{
    public const string IstToday = "(now() AT TIME ZONE 'Asia/Kolkata')::date";

    /// <summary>Runs after entity configurations: enum text + CHECK, encryption, timestamps, restrict deletes.</summary>
    public static void ApplyTradeBotConventions(this ModelBuilder modelBuilder, ISecretProtector protector)
    {
        var encrypted = new EncryptedStringConverter(protector);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;

            foreach (var property in entity.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                var column = property.GetColumnName();

                if (clrType.IsEnum)
                {
                    var values = DbValue.AllValues(clrType);
                    property.SetValueConverter((ValueConverter)Activator.CreateInstance(typeof(DbValueEnumConverter<>).MakeGenericType(clrType))!);
                    property.SetMaxLength(values.Max(v => v.Length));
                    entity.AddCheckConstraint($"ck_{table}_{column}", $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})");
                }

                if (property.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is not null)
                {
                    property.SetValueConverter(encrypted);
                    property.SetColumnType("text");
                }

                switch (property.Name)
                {
                    case "CreatedAt" or "UpdatedAt" or "AnalyzedAt" or "DetectedAt":
                        property.SetDefaultValueSql("now()");
                        break;
                    case "TradingDate" when !property.IsPrimaryKey():
                        property.SetDefaultValueSql(IstToday);
                        break;
                }
            }

            foreach (var fk in entity.GetForeignKeys())
                fk.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }
}
