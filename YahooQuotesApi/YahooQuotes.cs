﻿using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YahooQuotesApi
{
    public sealed class YahooQuotes
    {
        private readonly ILogger Logger;
        private readonly IClock Clock;
        private readonly YahooSnapshot Snapshot;
        private readonly YahooHistory History;
        private readonly bool UseNonAdjustedClose;

        internal YahooQuotes(IClock clock, ILogger logger, Duration snapshotCacheDuration, Instant historyStartDate, Frequency frequency, Duration historyCacheDuration, bool nonAdjustedClose)
        {
            Logger = logger;
            Clock = clock;
            var httpFactory = new HttpClientFactoryConfigurator(logger).Produce();
            Snapshot = new YahooSnapshot(clock, logger, httpFactory, snapshotCacheDuration);
            History = new YahooHistory(clock, logger, httpFactory, historyStartDate, historyCacheDuration, frequency);
            UseNonAdjustedClose = nonAdjustedClose;
        }

        public async Task<Security?> GetAsync(string symbol, HistoryFlags historyFlags = HistoryFlags.None, string historyBase = "", CancellationToken ct = default) =>
            (await GetAsync(new[] { symbol }, historyFlags, historyBase, ct).ConfigureAwait(false)).Values.Single();

        public async Task<Dictionary<string, Security?>> GetAsync(IEnumerable<string> symbols, HistoryFlags historyFlags = HistoryFlags.None, string historyBase = "", CancellationToken ct = default)
        {
            var historyBaseSymbol = Symbol.TryCreate(historyBase, true) ?? throw new ArgumentException($"Invalid base symbol: {historyBase}.");
            var syms = symbols.ToSymbols().Distinct();
            var securities = await GetAsync(syms, historyFlags, historyBaseSymbol, ct).ConfigureAwait(false);
            return syms.ToDictionary(s => s.Name, s => securities[s], StringComparer.OrdinalIgnoreCase);
        }

        public async Task<Dictionary<Symbol, Security?>> GetAsync(IEnumerable<Symbol> symbols, HistoryFlags historyFlags, Symbol historyBase, CancellationToken ct = default)
        {
            if (symbols.Any(s => s.IsEmpty))
                throw new ArgumentException("Empty symbol.");
            if (historyBase.IsCurrencyRate)
                throw new ArgumentException($"Invalid base symbol: {historyBase}.");
            if (!historyBase.IsEmpty && symbols.Any(s => s.IsCurrencyRate))
                throw new ArgumentException($"Invalid symbol: {symbols.First(s => s.IsCurrencyRate)}.");
            if (historyBase.IsEmpty && symbols.Any(s => s.IsCurrency))
                throw new ArgumentException($"Invalid symbol: {symbols.First(s => s.IsCurrency)}.");
            if (!historyBase.IsEmpty && !historyFlags.HasFlag(HistoryFlags.PriceHistory))
                throw new ArgumentException("PriceHistory must be enabled when historyBase is specified.");
            try
            {
                var securities = await GetSecuritiesyAsync(symbols, historyFlags, historyBase, ct).ConfigureAwait(false);
                return symbols.ToDictionary(symbol => symbol, symbol => securities[symbol]);
            }
            catch (Exception e)
            {
                Logger.LogCritical(e, "YahooQuotes: GetAsync() error.");
                throw;
            }
        }

        private async Task<Dictionary<Symbol, Security?>> GetSecuritiesyAsync(IEnumerable<Symbol> symbols, HistoryFlags historyFlags, Symbol historyBase, CancellationToken ct)
        {
            var stockAndCurrencyRateSymbols = symbols.Where(s => s.IsStock || s.IsCurrencyRate).ToList();
            if (historyBase.IsStock && !stockAndCurrencyRateSymbols.Contains(historyBase))
                stockAndCurrencyRateSymbols.Add(historyBase);
            var securities = await Snapshot.GetAsync(stockAndCurrencyRateSymbols, ct).ConfigureAwait(false);

            if (historyFlags == HistoryFlags.None)
                return securities;

            if (!historyBase.IsEmpty)
                await AddCurrencies(symbols.Where(s => s.IsCurrency), historyBase, securities, ct).ConfigureAwait(false);

            await AddHistoryToSecurities(securities.Values.NotNull(), historyFlags, ct).ConfigureAwait(false);

            if (!historyBase.IsEmpty)
                HistoryBaseComposer.Compose(symbols.ToList(), historyBase, securities);

            return securities;
        }

        private async Task AddCurrencies(IEnumerable<Symbol> currencies, Symbol historyBase, Dictionary<Symbol, Security?> securities, CancellationToken ct)
        {
            // currency securities + historyBase currency + security currencies
            var currencySymbols = new HashSet<Symbol>(currencies);
            if (historyBase.IsCurrency)
                currencySymbols.Add(historyBase);
            foreach (var security in securities.Values.NotNull())
            {
                var currencySymbol = Symbol.TryCreate(security.Currency + "=X");
                if (currencySymbol is null)
                    security.PriceHistoryBase = Result<ValueTick[]>.Fail($"Invalid currency symbol: '{security.Currency}'.");
                else
                currencySymbols.Add(currencySymbol);
            }

            var rateSymbols = currencySymbols
                .Where(c => c.Currency != "USD")
                .Select(c => Symbol.TryCreate($"USD{c.Currency}=X"))
                .NotNull()
                .ToList();

            if (rateSymbols.Any())
            {
                var currencyRateSecurities = await Snapshot.GetAsync(rateSymbols, ct).ConfigureAwait(false);
                foreach (var security in currencyRateSecurities)
                    securities[security.Key] = security.Value; // long symbol
            }
        }

        private async Task AddHistoryToSecurities(IEnumerable<Security> securities, HistoryFlags historyFlags, CancellationToken ct)
        {
            var dividendTasks = new List<(Security, Task<Result<DividendTick[]>>)>();
            if (historyFlags.HasFlag(HistoryFlags.DividendHistory))
                dividendTasks = securities.Select(v => (v, History.GetDividendsAsync(v.Symbol, ct))).ToList();
            var splitTasks = new List<(Security, Task<Result<SplitTick[]>>)>();
            if (historyFlags.HasFlag(HistoryFlags.SplitHistory))
                splitTasks = securities.Select(v => (v, History.GetSplitsAsync(v.Symbol, ct))).ToList();
            var candleTasks = new List<(Security, Task<Result<CandleTick[]>>)>();
            if (historyFlags.HasFlag(HistoryFlags.PriceHistory))
                candleTasks = securities.Select(v => (v, History.GetCandlesAsync(v.Symbol, ct))).ToList();

            foreach (var (security, task) in dividendTasks)
                security.DividendHistory = await task.ConfigureAwait(false);

            foreach (var (security, task) in splitTasks)
                security.SplitHistory = await task.ConfigureAwait(false);
            foreach (var (security, task) in candleTasks)
            {
                var result = await task.ConfigureAwait(false);
                security.PriceHistory = result;
                security.PriceHistoryBase = GetPriceHistoryBaseAsync(result, security);
            }
            return;
        }

        private Result<ValueTick[]> GetPriceHistoryBaseAsync(Result<CandleTick[]> result, Security security)
        {
            if (result.HasError)
                return Result<ValueTick[]>.Fail(result.Error);
            if (security.ExchangeTimezone == null)
                return Result<ValueTick[]>.Fail("Exchange timezone not found: '{security.ExchangeTimezone}'.");
            if (security.ExchangeCloseTime == default)
                return Result<ValueTick[]>.Fail("ExchangeCloseTime not found.");

            var ticks = result.Value.Select(candleTick =>
                new ValueTick(candleTick, security.ExchangeCloseTime, security.ExchangeTimezone!, UseNonAdjustedClose)).ToList();
            if (!ticks.Any())
                return Result<ValueTick[]>.Fail("No history available.");

            var snapTime = security.RegularMarketTime;
            var snapPrice = security.RegularMarketPrice;
            if (snapTime == default || snapPrice is null)
            {
                if (snapTime == default)
                    Logger.LogDebug($"RegularMarketTime unavailable for symbol: {security.Symbol}.");
                if (snapPrice == null)
                    Logger.LogDebug($"RegularMarketTime unavailable for symbol: {security.Symbol}.");
                Result<ValueTick[]>.Ok(ticks.ToArray());
            }

            var now = Clock.GetCurrentInstant();
            var snapTimeInstant = snapTime.ToInstant();

            if (snapTimeInstant > now)
            {
                Logger.LogWarning($"Snapshot date: {snapTimeInstant} which follows current date: {now} adjusted for symbol: {security.Symbol}.");
                snapTimeInstant = now;
            }

            var latestHistory = ticks.Last();
            if (latestHistory.Date >= snapTimeInstant)
            {   // if history already includes snapshot, or exchange closes early
                Logger.LogTrace($"History tick with date: {latestHistory.Date} follows snapshot date: {snapTimeInstant} removed for symbol: {security.Symbol}.");
                ticks.Remove(latestHistory); 
                if (!ticks.Any() || ticks.Last().Date >= snapTimeInstant)
                    return Result<ValueTick[]>.Fail($"Invalid dates.");
            }

            var volume = security.RegularMarketVolume;
            if (volume is null)
            {
                Logger.LogTrace($"RegularMarketVolume unavailable for symbol: {security.Symbol}.");
                volume = 0;
            }

            ticks.Add(new ValueTick(snapTimeInstant, Convert.ToDouble(snapPrice), volume.Value));

            // hist < snap < now
            return Result<ValueTick[]>.Ok(ticks.ToArray());
        }
    }
}
