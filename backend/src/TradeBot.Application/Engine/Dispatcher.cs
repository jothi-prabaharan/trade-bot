using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradeBot.Application.Abstractions;
using TradeBot.Application.Market;
using TradeBot.Application.Trading;
using TradeBot.Application.Validation;
using TradeBot.Domain.Entities.Mst;
using TradeBot.Domain.Entities.Trn;
using TradeBot.Domain.Enums;

namespace TradeBot.Application.Engine;

public sealed record DispatchOutcome(long AccountId, bool Placed, IReadOnlyList<RuleViolation> Violations, string? Message, SizingResult? Sizing);

public sealed record SignalContext(Signal Signal, DetectedPattern Detection, ChartPattern Pattern, Instrument Instrument, SecurityMaster Option, string? NewsHeadline);

/// <summary>
/// Fans a signal out to every active account in parallel. Each account gets its own bracket (per its risk policy),
/// the full Phase 2 validation chain and auto-downsized sizing; validation + submission is serialized per account.
/// </summary>
public sealed class Dispatcher(
    ConfigCache config, TokenStore tokens, MarketDataHub hub, TradeManager trades,
    ITradingRepository trading, IIntelligenceRepository intel, IAccountRepository accounts,
    TimeProvider clock, ILogger<Dispatcher> log)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _accountLocks = new();

    public async Task<IReadOnlyList<DispatchOutcome>> DispatchAsync(SignalContext s, CancellationToken ct)
    {
        var today = Ist.Today(clock);
        var sentiment = await intel.SentimentAsync(today, s.Instrument.InstrumentId, ct);
        var news = await intel.NewsImpactsAsync(today, s.Instrument, ct);
        var bot = await trading.GetOrCreateBotStateAsync(today, ct);

        var outcomes = await Task.WhenAll(config.Accounts.Select(a => DispatchOneAsync(a.AccountId, s, sentiment, news, bot, ct)));

        var placed = outcomes.Count(o => o.Placed);
        var reason = placed > 0 ? null : string.Join("; ", outcomes.Select(o => $"#{o.AccountId}: {o.Message ?? string.Join(",", o.Violations.Select(v => v.Code))}"));
        await intel.UpdateSignalStatusAsync(s.Signal.SignalId, placed > 0 ? SignalStatus.Dispatched : SignalStatus.Rejected, reason, ct);
        return outcomes;
    }

    private async Task<DispatchOutcome> DispatchOneAsync(long accountId, SignalContext s, DailyMarketSentiment? sentiment,
        List<NewsImpact> news, DailyBotState bot, CancellationToken ct)
    {
        var gate = _accountLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var account = await accounts.GetAsync(accountId, ct);
            if (account?.RiskConfig is not { } risk) return new DispatchOutcome(accountId, false, [], "Account or risk config missing", null);
            if (hub.Last(s.Option.SecurityId) is not { } quote) return new DispatchOutcome(accountId, false, [], "No live quote for the option", null);

            var today = Ist.Today(clock);
            var day = await trading.GetOrCreateDayPnlAsync(accountId, today, account.TradeMode, risk.OpeningBalance, risk.AvailableMargin, ct);
            var bracket = SignalBuilder.ForAccount(s.Signal, quote.Ltp, BracketPolicy.From(risk), s.Option.TickSize);
            var rate = ChargesCalculator.Resolve(config.ChargeRates, s.Option.SecurityType, today);

            var ctx = new EntryContext
            {
                NowUtc = clock.GetUtcNow().UtcDateTime,
                Calendar = config.Calendar,
                Account = account,
                Risk = risk,
                LatestToken = tokens.Latest(accountId),
                DayPnl = day,
                BotState = bot,
                OpenTradeCount = await trading.OpenTradeCountAsync(accountId, ct),
                HasOrderForSignal = await trading.HasOrderForSignalAsync(accountId, s.Signal.SignalId, ct),
                TradesToday = await trading.TradesTodayAsync(accountId, today, ct),
                LastStopOutOnInstrumentUtc = await trading.LastStopOutAsync(accountId, s.Instrument.InstrumentId, today, ct),
                Strategy = config.Strategy,
                Gemini = config.Gemini,
                Instrument = s.Instrument,
                Security = s.Option,
                Pattern = s.Pattern,
                Detection = s.Detection,
                Signal = s.Signal,
                Sentiment = sentiment,
                TodaysNews = news,
                Quote = new LiveQuote(quote.Ltp, quote.TimeUtc),
                StopLossPrice = bracket.StopLoss,
                TargetPrice = bracket.Target,
                BrokeragePerOrder = account.TradeMode == TradeMode.Real ? rate.BrokeragePerOrder : risk.SimulatedBrokeragePerOrder,
            };

            var decision = EntryValidator.Validate(ctx);
            if (!decision.IsApproved)
            {
                var codes = string.Join(", ", decision.Violations.Select(v => v.Code));
                log.LogInformation("Signal {Signal} rejected for account {Account}: {Codes}", s.Signal.SignalId, accountId, codes);
                await trading.AddEventAsync(new SystemEvent
                {
                    AccountId = accountId, EventType = "SIGNAL_REJECTED", Severity = EventSeverity.Info,
                    Message = $"Signal {s.Signal.SignalId} rejected: {codes}" is var m && m.Length > 1000 ? m[..1000] : m,
                    Payload = System.Text.Json.JsonSerializer.Serialize(decision.Violations),
                }, ct);
                return new DispatchOutcome(accountId, false, decision.Violations, null, null);
            }

            var ack = await trades.SubmitEntryAsync(new EntryRequest(account, s.Signal, decision.Sizing!, quote.Ltp, s.Instrument.InstrumentId,
                s.Option.PmlSymbol, s.Pattern.PatternName, s.NewsHeadline));
            return new DispatchOutcome(accountId, ack.Success, [], ack.Message, decision.Sizing);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Dispatch to account {Account} failed", accountId);
            return new DispatchOutcome(accountId, false, [], e.Message, null);
        }
        finally { gate.Release(); }
    }
}
