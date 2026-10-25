using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeBot.Domain.Entities.Trn;

namespace TradeBot.Infrastructure.Persistence.Configurations.Trn;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("orders", Schemas.Trn, t =>
        {
            // Every leg carries the bracket; long-option bracket means SL < Target
            t.HasCheckConstraint("ck_orders_mandatory_sl_target", "stop_loss_price > 0 AND target_sell_price > stop_loss_price");
            t.HasCheckConstraint("ck_orders_entry_bracket",
                "order_leg_type <> 'ENTRY' OR (stop_loss_price < order_ltp AND order_ltp < target_sell_price AND stop_loss_price < price AND price < target_sell_price)");
            t.HasCheckConstraint("ck_orders_parent_link", "(order_leg_type = 'ENTRY') = (parent_order_id IS NULL)");
            t.HasCheckConstraint("ck_orders_long_only", "(order_leg_type = 'ENTRY') = (txn_type = 'B')");
            t.HasCheckConstraint("ck_orders_leg_order_type",
                "(order_leg_type <> 'STOP_LOSS' OR order_type = 'SL') AND (order_leg_type <> 'TARGET_SELL' OR order_type = 'LMT')");
            t.HasCheckConstraint("ck_orders_sl_trigger",
                "order_type <> 'SL' OR (trigger_price IS NOT NULL AND trigger_price > 0 AND (txn_type <> 'S' OR price <= trigger_price) AND (txn_type <> 'B' OR price >= trigger_price))");
            t.HasCheckConstraint("ck_orders_quantity", "quantity > 0 AND filled_quantity >= 0 AND filled_quantity <= quantity");
            t.HasCheckConstraint("ck_orders_prices", "price > 0 AND order_ltp > 0 AND (avg_fill_price IS NULL OR avg_fill_price > 0)");
            t.HasCheckConstraint("ck_orders_derivatives_only", "segment = 'D'");
            t.HasCheckConstraint("ck_orders_rejection_reason", "status NOT IN ('REJECTED', 'FAILED') OR rejection_reason IS NOT NULL");
        });

        b.HasKey(x => x.OrderId);
        b.Property(x => x.BrokerOrderNo).HasMaxLength(50);
        b.Property(x => x.ExchangeOrderId).HasMaxLength(50);
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.RawResponse).Jsonb();
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.BrokerOrderNo }).IsUnique().HasFilter("broker_order_no IS NOT NULL");
        b.HasIndex(x => new { x.TradingDate, x.AccountId });
        b.HasIndex(x => x.ParentOrderId);
        b.HasIndex(x => x.SignalId);
        b.HasIndex(x => new { x.AccountId, x.Status })
            .HasFilter("status IN ('PENDING', 'PLACED', 'OPEN', 'TRIGGER_PENDING', 'PARTIALLY_FILLED')")
            .HasDatabaseName("ix_orders_working");

        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne(x => x.Signal).WithMany().HasForeignKey(x => x.SignalId);
        b.HasOne(x => x.ParentOrder).WithMany(x => x.ChildOrders).HasForeignKey(x => x.ParentOrderId);
        b.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId);
    }
}

internal sealed class DailyTradeConfiguration : IEntityTypeConfiguration<DailyTrade>
{
    public void Configure(EntityTypeBuilder<DailyTrade> b)
    {
        b.ToTable("daily_trades", Schemas.Trn, t =>
        {
            t.HasCheckConstraint("ck_daily_trades_mandatory_sl_target",
                "entry_buy_price > 0 AND stop_loss_price > 0 AND stop_loss_price < entry_buy_price AND target_sell_price > entry_buy_price");
            t.HasCheckConstraint("ck_daily_trades_trailing_sl", "trailing_sl_price >= stop_loss_price AND trailing_sl_price < target_sell_price");
            t.HasCheckConstraint("ck_daily_trades_quantity", "quantity > 0 AND lots > 0");
            t.HasCheckConstraint("ck_daily_trades_exit_state", "(trade_status = 'OPEN') = (exit_time IS NULL AND exit_sell_price IS NULL)");
            t.HasCheckConstraint("ck_daily_trades_charges", "brokerage_and_charges >= 0");
        });

        b.HasKey(x => x.TradeId);
        b.Property(x => x.NetPnl).HasComputedColumnSql("gross_pnl - brokerage_and_charges", stored: true);
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => x.EntryOrderId).IsUnique();
        b.HasIndex(x => x.SlOrderId).IsUnique();
        b.HasIndex(x => x.TargetOrderId).IsUnique();
        b.HasIndex(x => x.ExitOrderId).IsUnique();
        b.HasIndex(x => new { x.TradingDate, x.AccountId });
        b.HasIndex(x => x.AccountId).HasFilter("trade_status = 'OPEN'").HasDatabaseName("ix_daily_trades_open");

        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne(x => x.Signal).WithMany().HasForeignKey(x => x.SignalId);
        b.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId);
        b.HasOne(x => x.Detection).WithMany().HasForeignKey(x => x.DetectionId);
        b.HasOne(x => x.News).WithMany().HasForeignKey(x => x.NewsId);
        b.HasOne(x => x.EntryOrder).WithOne().HasForeignKey<DailyTrade>(x => x.EntryOrderId);
        b.HasOne(x => x.SlOrder).WithOne().HasForeignKey<DailyTrade>(x => x.SlOrderId);
        b.HasOne(x => x.TargetOrder).WithOne().HasForeignKey<DailyTrade>(x => x.TargetOrderId);
        b.HasOne(x => x.ExitOrder).WithOne().HasForeignKey<DailyTrade>(x => x.ExitOrderId);
    }
}

internal sealed class DailyAccountPnlConfiguration : IEntityTypeConfiguration<DailyAccountPnl>
{
    public void Configure(EntityTypeBuilder<DailyAccountPnl> b)
    {
        b.ToTable("daily_account_pnl", Schemas.Trn, t =>
        {
            t.HasCheckConstraint("ck_daily_account_pnl_counts",
                "total_trades >= 0 AND winning_trades >= 0 AND losing_trades >= 0 AND winning_trades + losing_trades <= total_trades");
            t.HasCheckConstraint("ck_daily_account_pnl_non_negative",
                "total_charges >= 0 AND daily_loss_limit_used_pct >= 0 AND opening_balance >= 0 AND available_margin >= 0 AND utilised_margin >= 0");
            t.HasCheckConstraint("ck_daily_account_pnl_kill_switch", "NOT kill_switch_triggered OR kill_switch_at IS NOT NULL");
        });

        b.HasKey(x => x.SummaryId);
        b.Property(x => x.NetPnl).HasComputedColumnSql("realized_gross_pnl + unrealized_mtm_pnl - total_charges", stored: true);
        b.Property(x => x.DailyLossLimitUsedPct).HasPrecision(7, 2);
        b.Property(x => x.HaltReason).HasMaxLength(500);
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => new { x.TradingDate, x.AccountId }).IsUnique();
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
    }
}

internal sealed class DailyBotStateConfiguration : IEntityTypeConfiguration<DailyBotState>
{
    public void Configure(EntityTypeBuilder<DailyBotState> b)
    {
        b.ToTable("daily_bot_state", Schemas.Trn, t =>
            t.HasCheckConstraint("ck_daily_bot_state_kill_switch", "NOT global_kill_switch_triggered OR triggered_at IS NOT NULL"));

        b.HasKey(x => x.TradingDate);
        b.Property(x => x.TriggeredBy).HasMaxLength(100);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Version).IsRowVersion();
    }
}

internal sealed class SystemEventConfiguration : IEntityTypeConfiguration<SystemEvent>
{
    public void Configure(EntityTypeBuilder<SystemEvent> b)
    {
        b.ToTable("system_events", Schemas.Trn);

        b.HasKey(x => x.EventId);
        b.Property(x => x.EventType).HasMaxLength(60);
        b.Property(x => x.Message).HasMaxLength(1000);
        b.Property(x => x.Payload).Jsonb();

        b.HasIndex(x => x.CreatedAt).IsDescending();
        b.HasIndex(x => new { x.AccountId, x.CreatedAt }).IsDescending(false, true);
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
    }
}
