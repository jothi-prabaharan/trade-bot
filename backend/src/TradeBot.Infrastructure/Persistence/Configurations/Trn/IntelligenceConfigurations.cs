using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeBot.Domain.Entities.Trn;

namespace TradeBot.Infrastructure.Persistence.Configurations.Trn;

internal sealed class CompanyDailyNewsConfiguration : IEntityTypeConfiguration<CompanyDailyNews>
{
    public void Configure(EntityTypeBuilder<CompanyDailyNews> b)
    {
        b.ToTable("company_daily_news", Schemas.Trn, t =>
            t.HasCheckConstraint("ck_company_daily_news_sentiment_score", "sentiment_score BETWEEN -100 AND 100"));

        b.HasKey(x => x.NewsId);
        b.Property(x => x.Headline).HasMaxLength(500);
        b.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.SourceUrls).Jsonb();
        b.Property(x => x.GeminiStructuredOutput).Jsonb();
        b.Property(x => x.NiftyWeightedImpact).HasPrecision(9, 4);
        b.Property(x => x.BankniftyWeightedImpact).HasPrecision(9, 4);

        b.HasIndex(x => new { x.TradingDate, x.ContentHash }).IsUnique();
        b.HasIndex(x => new { x.TradingDate, x.CompanyId });
        b.HasIndex(x => new { x.TradingDate, x.InstrumentId });
        b.HasIndex(x => new { x.TradingDate, x.ImpactLevel });
        b.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
    }
}

internal sealed class DailyMarketSentimentConfiguration : IEntityTypeConfiguration<DailyMarketSentiment>
{
    public void Configure(EntityTypeBuilder<DailyMarketSentiment> b)
    {
        b.ToTable("daily_market_sentiment", Schemas.Trn, t =>
            t.HasCheckConstraint("ck_daily_market_sentiment_score", "net_sentiment_score BETWEEN -100 AND 100"));

        b.HasKey(x => x.SentimentId);
        b.Property(x => x.NetSentimentScore).HasPrecision(7, 2);
        b.Property(x => x.ActiveCatalysts).Jsonb();
        b.Property(x => x.HaltReason).HasMaxLength(500);

        b.HasIndex(x => new { x.TradingDate, x.InstrumentId }).IsUnique();
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
    }
}

internal sealed class OhlcvCandleConfiguration : IEntityTypeConfiguration<OhlcvCandle>
{
    public void Configure(EntityTypeBuilder<OhlcvCandle> b)
    {
        b.ToTable("ohlcv_candles", Schemas.Trn, t =>
        {
            t.HasCheckConstraint("ck_ohlcv_candles_range", "low >= 0 AND high >= low AND high >= GREATEST(open, close) AND low <= LEAST(open, close)");
            t.HasCheckConstraint("ck_ohlcv_candles_volume", "volume >= 0 AND (open_interest IS NULL OR open_interest >= 0)");
        });

        b.HasKey(x => x.CandleId);
        b.HasIndex(x => new { x.InstrumentId, x.SecurityId, x.Timeframe, x.CandleTime })
            .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_ohlcv_candles_series_time");
        b.HasIndex(x => new { x.SecurityId, x.Timeframe, x.CandleTime })
            .HasFilter("security_id IS NOT NULL").HasDatabaseName("ix_ohlcv_candles_option_series");
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
        b.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId);
    }
}

internal sealed class DetectedPatternConfiguration : IEntityTypeConfiguration<DetectedPattern>
{
    public void Configure(EntityTypeBuilder<DetectedPattern> b)
    {
        b.ToTable("detected_patterns", Schemas.Trn, t =>
        {
            t.HasCheckConstraint("ck_detected_patterns_directional", "direction IN ('BULLISH', 'BEARISH')");
            t.HasCheckConstraint("ck_detected_patterns_structural_levels",
                "structural_sl_price > 0 AND structural_target_price > 0 AND neck_or_breakout_price > 0 AND (" +
                "(direction = 'BULLISH' AND structural_sl_price < neck_or_breakout_price AND neck_or_breakout_price < structural_target_price) OR " +
                "(direction = 'BEARISH' AND structural_target_price < neck_or_breakout_price AND neck_or_breakout_price < structural_sl_price))");
            t.HasCheckConstraint("ck_detected_patterns_confidence", "confidence_score BETWEEN 0 AND 100");
        });

        b.HasKey(x => x.DetectionId);
        b.Property(x => x.ConfidenceScore).Score();
        b.Property(x => x.PivotCoordinates).Jsonb();

        b.HasIndex(x => new { x.InstrumentId, x.DetectedAt }).IsDescending(false, true);
        b.HasIndex(x => x.PatternId);
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
        b.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId);
        b.HasOne(x => x.Pattern).WithMany().HasForeignKey(x => x.PatternId);
    }
}

internal sealed class SignalConfiguration : IEntityTypeConfiguration<Signal>
{
    public void Configure(EntityTypeBuilder<Signal> b)
    {
        b.ToTable("signals", Schemas.Trn, t =>
        {
            t.HasCheckConstraint("ck_signals_mandatory_sl_target",
                "entry_option_ltp > 0 AND mandatory_sl_price > 0 AND mandatory_target_price > 0 AND (" +
                "(signal_type = 'BUY' AND mandatory_sl_price < entry_option_ltp AND entry_option_ltp < mandatory_target_price) OR " +
                "(signal_type = 'SELL' AND mandatory_target_price < entry_option_ltp AND entry_option_ltp < mandatory_sl_price))");
            t.HasCheckConstraint("ck_signals_scores",
                "pattern_score BETWEEN 0 AND 100 AND news_score BETWEEN 0 AND 100 AND oi_greeks_score BETWEEN 0 AND 100 AND combined_ai_confidence BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_signals_risk_reward_positive", "risk_reward_ratio > 0");
            t.HasCheckConstraint("ck_signals_rejection_reason", "status <> 'REJECTED' OR rejection_reason IS NOT NULL");
        });

        b.HasKey(x => x.SignalId);
        b.Property(x => x.OptionDelta).HasPrecision(6, 4);
        // Derived so it can never disagree with the stored prices; 0 on a degenerate bracket so the SL/Target CHECK reports it
        b.Property(x => x.RiskRewardRatio).HasPrecision(9, 4).HasComputedColumnSql(
            "COALESCE(ROUND(CASE WHEN signal_type = 'BUY' " +
            "THEN (mandatory_target_price - entry_option_ltp) / NULLIF(entry_option_ltp - mandatory_sl_price, 0) " +
            "ELSE (entry_option_ltp - mandatory_target_price) / NULLIF(mandatory_sl_price - entry_option_ltp, 0) END, 4), 0)", stored: true);
        b.Property(x => x.PatternScore).Score();
        b.Property(x => x.NewsScore).Score();
        b.Property(x => x.OiGreeksScore).Score();
        b.Property(x => x.CombinedAiConfidence).Score();
        b.Property(x => x.GreeksAndOi).Jsonb();
        b.Property(x => x.RejectionReason).HasMaxLength(500);

        b.HasIndex(x => new { x.TradingDate, x.Status });
        b.HasIndex(x => x.DetectionId);
        b.HasIndex(x => x.SecurityId);
        b.HasOne(x => x.Detection).WithMany().HasForeignKey(x => x.DetectionId);
        b.HasOne(x => x.News).WithMany().HasForeignKey(x => x.NewsId);
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
        b.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId);
    }
}

internal sealed class DailyWatchlistConfiguration : IEntityTypeConfiguration<DailyWatchlist>
{
    public void Configure(EntityTypeBuilder<DailyWatchlist> b)
    {
        b.ToTable("daily_watchlist", Schemas.Trn);
        b.HasKey(x => x.WatchlistId);
        b.Property(x => x.Score).HasPrecision(7, 2);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.AddedAt).HasDefaultValueSql("now()");
        b.HasIndex(x => new { x.TradingDate, x.InstrumentId }).IsUnique();
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
    }
}
