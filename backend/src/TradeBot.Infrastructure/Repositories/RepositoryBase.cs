using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TradeBot.Infrastructure.Persistence;

namespace TradeBot.Infrastructure.Repositories;

public abstract class RepositoryBase(IDbContextFactory<TradeBotDbContext> factory)
{
    protected Task<TradeBotDbContext> Db(CancellationToken ct) => factory.CreateDbContextAsync(ct);

    /// <summary>
    /// Copies scalar values of a detached entity onto its tracked row (navigations are never attached,
    /// so related rows can't be re-inserted). xmin is checked when the entity has a Version token.
    /// Store-generated values (xmin, computed PnL) are copied back.
    /// </summary>
    protected static async Task UpdateScalarsAsync<T>(TradeBotDbContext db, T detached, CancellationToken ct, params object[] key) where T : class
    {
        var tracked = await db.Set<T>().FindAsync(key, ct) ?? throw new KeyNotFoundException($"{typeof(T).Name} {string.Join(",", key)} not found.");
        var entry = db.Entry(tracked);
        entry.CurrentValues.SetValues(detached);

        if (entry.Metadata.FindProperty("Version") is { } version)
            entry.Property(version.Name).OriginalValue = typeof(T).GetProperty(version.Name)!.GetValue(detached);
        foreach (var p in entry.Properties.Where(p => p.Metadata.ValueGenerated == ValueGenerated.OnAddOrUpdate && !p.Metadata.IsConcurrencyToken))
            p.IsModified = false;

        await db.SaveChangesAsync(ct);

        foreach (var p in entry.Properties.Where(p => p.Metadata.ValueGenerated != ValueGenerated.Never && p.Metadata.PropertyInfo is not null))
            p.Metadata.PropertyInfo!.SetValue(detached, p.CurrentValue);
    }
}
