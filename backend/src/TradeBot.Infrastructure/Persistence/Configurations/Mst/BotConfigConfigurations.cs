using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Infrastructure.Persistence.Configurations.Mst;

internal sealed class GeminiNewsConfigConfiguration : IEntityTypeConfiguration<GeminiNewsConfig>
{
    public void Configure(EntityTypeBuilder<GeminiNewsConfig> b)
    {
        b.ToTable("gemini_news_config", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_gemini_news_config_weights_sum", "pattern_weight_pct + news_weight_pct + oi_greeks_weight_pct = 100");
            t.HasCheckConstraint("ck_gemini_news_config_weights_range",
                "pattern_weight_pct BETWEEN 0 AND 100 AND news_weight_pct BETWEEN 0 AND 100 AND oi_greeks_weight_pct BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_gemini_news_config_polling", "intraday_poll_seconds >= 10 AND volume_spike_trigger_multiplier > 1 AND max_calls_per_minute >= 1");
            t.HasCheckConstraint("ck_gemini_news_config_premarket_time", "premarket_scan_time < TIME '09:15'");
        });

        b.HasKey(x => x.GeminiConfigId);
        b.Property(x => x.ModelName).HasMaxLength(100);
        b.Property(x => x.PatternWeightPct).Score();
        b.Property(x => x.NewsWeightPct).Score();
        b.Property(x => x.OiGreeksWeightPct).Score();
        b.Property(x => x.VolumeSpikeTriggerMultiplier).HasPrecision(6, 2);
        b.HasIndex(x => x.IsActive).IsUnique().HasFilter("is_active").HasDatabaseName("ux_gemini_news_config_single_active");
    }
}

internal sealed class StrategyConfigConfiguration : IEntityTypeConfiguration<StrategyConfig>
{
    public void Configure(EntityTypeBuilder<StrategyConfig> b)
    {
        b.ToTable("strategy_config", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_strategy_config_session_times",
                "entry_start_time >= TIME '09:15' AND entry_cutoff_time > entry_start_time AND square_off_time > entry_cutoff_time AND square_off_time <= TIME '15:29'");
            t.HasCheckConstraint("ck_strategy_config_auth", "auth_login_time < entry_start_time AND auth_max_retries BETWEEN 0 AND 10");
            t.HasCheckConstraint("ck_strategy_config_confidence", "min_combined_confidence BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_strategy_config_execution",
                "sl_limit_buffer_pct > 0 AND sl_limit_buffer_pct <= 10 AND sl_gap_fallback_seconds BETWEEN 1 AND 30 AND entry_fill_timeout_seconds BETWEEN 2 AND 60");
            t.HasCheckConstraint("ck_strategy_config_universe", "stock_watchlist_size BETWEEN 0 AND 50 AND history_backfill_days BETWEEN 1 AND 30");
            t.HasCheckConstraint("ck_strategy_config_timeframes",
                "array_position(ARRAY['1m','3m','5m','15m','1h','1d'], trend_timeframe::text) > array_position(ARRAY['1m','3m','5m','15m','1h','1d'], trigger_timeframe::text)");
        });

        b.HasKey(x => x.StrategyConfigId);
        b.Property(x => x.MinCombinedConfidence).Score();
        b.Property(x => x.SlLimitBufferPct).Pct();
        b.HasIndex(x => x.IsActive).IsUnique().HasFilter("is_active").HasDatabaseName("ux_strategy_config_single_active");
    }
}

internal sealed class AlertChannelConfiguration : IEntityTypeConfiguration<AlertChannel>
{
    public void Configure(EntityTypeBuilder<AlertChannel> b)
    {
        b.ToTable("alert_channels", Schemas.Mst, t =>
            t.HasCheckConstraint("ck_alert_channels_telegram_complete",
                "NOT is_active OR channel_type <> 'TELEGRAM' OR (bot_token IS NOT NULL AND chat_id IS NOT NULL)"));

        b.HasKey(x => x.AlertChannelId);
        b.Property(x => x.ChannelName).HasMaxLength(100);
        b.Property(x => x.ChatId).HasMaxLength(100);
        b.HasIndex(x => x.ChannelName).IsUnique();
    }
}

internal sealed class WebPushKeysConfiguration : IEntityTypeConfiguration<WebPushKeys>
{
    public void Configure(EntityTypeBuilder<WebPushKeys> b)
    {
        b.ToTable("web_push_keys", Schemas.Mst, t => t.HasCheckConstraint("ck_web_push_keys_single_row", $"web_push_keys_id = {WebPushKeys.SingletonId}"));
        b.HasKey(x => x.WebPushKeysId);
        b.Property(x => x.WebPushKeysId).ValueGeneratedNever();
        b.Property(x => x.PublicKey).HasMaxLength(100);
    }
}

internal sealed class WebPushSubscriptionConfiguration : IEntityTypeConfiguration<WebPushSubscription>
{
    public void Configure(EntityTypeBuilder<WebPushSubscription> b)
    {
        b.ToTable("web_push_subscriptions", Schemas.Mst, t =>
            t.HasCheckConstraint("ck_web_push_subscriptions_https", "endpoint LIKE 'https://%'"));
        b.HasKey(x => x.WebPushSubscriptionId);
        b.Property(x => x.Endpoint).HasMaxLength(1000);
        b.Property(x => x.P256dh).HasMaxLength(200);
        b.Property(x => x.DeviceLabel).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.HasIndex(x => x.Endpoint).IsUnique();
    }
}
