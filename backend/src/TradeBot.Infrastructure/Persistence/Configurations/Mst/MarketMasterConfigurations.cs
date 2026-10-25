using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeBot.Domain.Entities.Mst;

namespace TradeBot.Infrastructure.Persistence.Configurations.Mst;

internal sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> b)
    {
        b.ToTable("instruments", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_instruments_lot_tick", "lot_size > 0 AND tick_size > 0");
            t.HasCheckConstraint("ck_instruments_strike_step", "strike_step IS NULL OR strike_step > 0");
        });

        b.HasKey(x => x.InstrumentId);
        b.Property(x => x.Symbol).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(150);
        b.HasIndex(x => x.Symbol).IsUnique();
        b.HasIndex(x => x.IsTradable).HasFilter("is_tradable AND is_active");
    }
}

internal sealed class StockCompanyConfiguration : IEntityTypeConfiguration<StockCompany>
{
    public void Configure(EntityTypeBuilder<StockCompany> b)
    {
        b.ToTable("stock_companies", Schemas.Mst, t =>
            t.HasCheckConstraint("ck_stock_companies_weights",
                "nifty50_weight_pct BETWEEN 0 AND 100 AND banknifty_weight_pct BETWEEN 0 AND 100 AND finnifty_weight_pct BETWEEN 0 AND 100"));

        b.HasKey(x => x.CompanyId);
        b.Property(x => x.NseSymbol).HasMaxLength(50);
        b.Property(x => x.CompanyName).HasMaxLength(200);
        b.Property(x => x.Sector).HasMaxLength(100);
        b.Property(x => x.Industry).HasMaxLength(150);
        b.Property(x => x.Nifty50WeightPct).HasColumnName("nifty50_weight_pct").Pct();
        b.Property(x => x.BankniftyWeightPct).Pct();
        b.Property(x => x.FinniftyWeightPct).Pct();

        b.HasIndex(x => x.NseSymbol).IsUnique();
        b.HasIndex(x => x.InstrumentId);
        b.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId);
    }
}

internal sealed class SecurityMasterConfiguration : IEntityTypeConfiguration<SecurityMaster>
{
    public void Configure(EntityTypeBuilder<SecurityMaster> b)
    {
        b.ToTable("security_master", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_security_master_option_fields",
                "(security_type IN ('OPTIDX', 'OPTSTK')) = (opt_type IS NOT NULL AND strike_price IS NOT NULL AND expiry_date IS NOT NULL)");
            t.HasCheckConstraint("ck_security_master_future_expiry", "security_type NOT IN ('FUTIDX', 'FUTSTK') OR expiry_date IS NOT NULL");
            t.HasCheckConstraint("ck_security_master_lot_tick", "lot_size > 0 AND tick_size > 0");
        });

        b.HasKey(x => x.SecurityId);
        b.Property(x => x.SecurityId).ValueGeneratedNever();
        b.Property(x => x.PmlSymbol).HasMaxLength(100);

        b.HasIndex(x => new { x.InstrumentId, x.ExpiryDate, x.StrikePrice, x.OptType }).HasDatabaseName("ix_security_master_option_chain");
        b.HasIndex(x => x.PmlSymbol);
        b.HasOne(x => x.Instrument).WithMany(x => x.Securities).HasForeignKey(x => x.InstrumentId);
    }
}

internal sealed class ChartPatternConfiguration : IEntityTypeConfiguration<ChartPattern>
{
    public void Configure(EntityTypeBuilder<ChartPattern> b)
    {
        b.ToTable("chart_patterns", Schemas.Mst, t =>
        {
            t.HasCheckConstraint("ck_chart_patterns_min_candles", "min_candles_required >= 1");
            t.HasCheckConstraint("ck_chart_patterns_default_weight", "default_weight BETWEEN 0 AND 100");
        });

        b.HasKey(x => x.PatternId);
        b.Property(x => x.PatternCode).HasMaxLength(60);
        b.Property(x => x.PatternName).HasMaxLength(120);
        b.Property(x => x.DefaultWeight).Score();
        b.Property(x => x.PatternParameters).Jsonb().HasDefaultValueSql("'{}'::jsonb");

        b.HasIndex(x => x.PatternCode).IsUnique();
        b.HasIndex(x => new { x.Category, x.IsActive });
    }
}

internal sealed class ChargeRateConfiguration : IEntityTypeConfiguration<ChargeRate>
{
    public void Configure(EntityTypeBuilder<ChargeRate> b)
    {
        b.ToTable("charge_rates", Schemas.Mst, t =>
            t.HasCheckConstraint("ck_charge_rates_non_negative",
                "brokerage_per_order >= 0 AND stt_sell_pct >= 0 AND exchange_txn_pct >= 0 AND sebi_fee_pct >= 0 AND stamp_duty_buy_pct >= 0 AND gst_pct >= 0"));

        b.HasKey(x => x.ChargeRateId);
        b.Property(x => x.SttSellPct).HasPrecision(9, 6);
        b.Property(x => x.ExchangeTxnPct).HasPrecision(9, 6);
        b.Property(x => x.SebiFeePct).HasPrecision(9, 6);
        b.Property(x => x.StampDutyBuyPct).HasPrecision(9, 6);
        b.Property(x => x.GstPct).HasPrecision(9, 6);
        b.HasIndex(x => new { x.SecurityType, x.EffectiveFrom }).IsUnique();
    }
}
