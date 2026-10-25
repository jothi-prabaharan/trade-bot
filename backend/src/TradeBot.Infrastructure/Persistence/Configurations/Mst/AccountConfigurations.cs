using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Infrastructure.Persistence.Configurations.Mst;

internal sealed class TradingAccountConfiguration : IEntityTypeConfiguration<TradingAccount>
{
    public void Configure(EntityTypeBuilder<TradingAccount> b)
    {
        b.ToTable("trading_accounts", Schemas.Mst, t =>
            t.HasCheckConstraint("ck_trading_accounts_real_mode_confirmed", "trade_mode <> 'REAL' OR real_mode_enabled_at IS NOT NULL"));

        b.HasKey(x => x.AccountId);
        b.Property(x => x.AccountName).HasMaxLength(100);
        b.Property(x => x.ClientId).HasMaxLength(50);
        b.Property(x => x.UserName).HasMaxLength(150);
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.BrokerBranch).HasMaxLength(100);
        b.Property(x => x.LoginId).HasMaxLength(100);
        b.Property(x => x.RealModeEnabledBy).HasMaxLength(100);
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => x.ClientId).IsUnique();
        // At most one active market-data provider
        b.HasIndex(x => x.IsDataProvider).IsUnique().HasFilter("is_data_provider AND is_active")
            .HasDatabaseName("ux_trading_accounts_single_data_provider");

        b.HasOne(x => x.RiskConfig).WithOne(x => x.Account).HasForeignKey<AccountRiskConfig>(x => x.AccountId);
        b.HasMany(x => x.AuthTokens).WithOne(x => x.Account).HasForeignKey(x => x.AccountId);
    }
}

internal sealed class AccountAuthTokenConfiguration : IEntityTypeConfiguration<AccountAuthToken>
{
    public void Configure(EntityTypeBuilder<AccountAuthToken> b)
    {
        b.ToTable("account_auth_tokens", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_account_auth_tokens_success_has_token",
                "login_status <> 'SUCCESS' OR (x_jwt_token IS NOT NULL AND token_expiry IS NOT NULL)");
            t.HasCheckConstraint("ck_account_auth_tokens_attempt_count", "attempt_count >= 0");
        });

        b.HasKey(x => x.TokenId);
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.HasIndex(x => new { x.AccountId, x.CreatedAt }).IsDescending(false, true);
    }
}

internal sealed class AccountRiskConfigConfiguration : IEntityTypeConfiguration<AccountRiskConfig>
{
    public void Configure(EntityTypeBuilder<AccountRiskConfig> b)
    {
        b.ToTable("account_risk_config", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_account_risk_config_capital", "allocated_capital > 0 AND opening_balance >= 0 AND available_margin >= 0 AND utilised_margin >= 0");
            t.HasCheckConstraint("ck_account_risk_config_risk_pct", "risk_per_trade_pct > 0 AND risk_per_trade_pct <= 10");
            t.HasCheckConstraint("ck_account_risk_config_premium_band", "min_single_trade_price > 0 AND max_single_trade_price > min_single_trade_price");
            t.HasCheckConstraint("ck_account_risk_config_sl_target", "default_sl_pct > 0 AND default_sl_pct < 100 AND default_target_pct > 0 AND max_sl_pct >= default_sl_pct AND max_sl_pct < 100");
            // Fixed % fallback must itself satisfy min RR
            t.HasCheckConstraint("ck_account_risk_config_fallback_rr", "min_risk_reward_ratio >= 1 AND default_target_pct >= default_sl_pct * min_risk_reward_ratio");
            t.HasCheckConstraint("ck_account_risk_config_trailing", "breakeven_trigger_pct > 0 AND breakeven_trigger_pct <= 100 AND trailing_sl_step_pct > 0 AND trailing_sl_step_pct < 100");
            t.HasCheckConstraint("ck_account_risk_config_daily_limits", "max_daily_loss > 0 AND max_daily_loss <= allocated_capital AND max_daily_profit > 0");
            t.HasCheckConstraint("ck_account_risk_config_sizing", "max_open_trades >= 1 AND max_lots_per_trade >= 1 AND lot_multiplier > 0 AND lot_multiplier <= 1");
            t.HasCheckConstraint("ck_account_risk_config_confidence", "min_pattern_confidence BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_account_risk_config_trade_pacing", "max_trades_per_day BETWEEN 1 AND 100 AND reentry_cooldown_minutes BETWEEN 0 AND 240");
            t.HasCheckConstraint("ck_account_risk_config_costs", "slippage_buffer_pct BETWEEN 0 AND 5 AND simulated_slippage_pct BETWEEN 0 AND 5 AND simulated_brokerage_per_order >= 0");
        });

        b.HasKey(x => x.ConfigId);
        b.HasIndex(x => x.AccountId).IsUnique();
        b.Property(x => x.RiskPerTradePct).Pct();
        b.Property(x => x.DefaultSlPct).Pct();
        b.Property(x => x.DefaultTargetPct).Pct();
        b.Property(x => x.MaxSlPct).Pct();
        b.Property(x => x.BreakevenTriggerPct).Pct();
        b.Property(x => x.TrailingSlStepPct).Pct();
        b.Property(x => x.SlippageBufferPct).Pct();
        b.Property(x => x.SimulatedSlippagePct).Pct();
        b.Property(x => x.MinRiskRewardRatio).HasPrecision(6, 2);
        b.Property(x => x.LotMultiplier).HasPrecision(6, 2);
        b.Property(x => x.MinPatternConfidence).Score();
        b.Property(x => x.Version).IsRowVersion();
    }
}
