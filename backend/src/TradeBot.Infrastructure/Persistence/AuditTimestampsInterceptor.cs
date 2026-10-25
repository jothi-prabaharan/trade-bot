using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TradeBot.Domain.Common;

namespace TradeBot.Infrastructure.Persistence;

public sealed class AuditTimestampsInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null) return;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var entry in context.ChangeTracker.Entries<IHasUpdatedAt>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            entry.Entity.UpdatedAt = now;
            if (entry is { State: EntityState.Added, Entity: IAuditable auditable }) auditable.CreatedAt = now;
        }
    }
}
