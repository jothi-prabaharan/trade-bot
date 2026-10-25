using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TradeBot.Infrastructure.Persistence.Configurations;

internal static class PrecisionExtensions
{
    public static PropertyBuilder<decimal> Pct(this PropertyBuilder<decimal> p) => p.HasPrecision(7, 4);
    public static PropertyBuilder<decimal> Score(this PropertyBuilder<decimal> p) => p.HasPrecision(5, 2);
    public static PropertyBuilder<T> Jsonb<T>(this PropertyBuilder<T> p) => p.HasColumnType("jsonb");
}
