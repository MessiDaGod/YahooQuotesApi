﻿using CsvHelper;
using Flurl;
using Flurl.Http;
using NodaTime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace YahooQuotesApi
{
    public sealed class YahooHistory
    {
        private readonly ILogger Logger;
        private readonly CancellationToken Ct;
        private Instant Start, End = Instant.MaxValue;
        private Frequency Frequency = Frequency.Daily;

        public YahooHistory(ILogger<YahooSnapshot> logger, CancellationToken ct = default) => (Logger, Ct) = (logger, ct);
        public YahooHistory(CancellationToken ct = default) : this(NullLogger<YahooSnapshot>.Instance, ct) { }

        public YahooHistory Period(Instant start, Instant end)
        {
            if (start > Utility.Clock.GetCurrentInstant())
                throw new ArgumentException("start > now");
            if (start > end)
                throw new ArgumentException("start > end");
            Start = start;
            End = end;
            return this;
        }
        public YahooHistory Period(Instant start) => Period(start, Instant.MaxValue);
        public YahooHistory Period(Duration duration) => Period(Utility.Clock.GetCurrentInstant().Minus(duration));
        public YahooHistory Period(DateTimeZone timeZone, LocalDate start, LocalDate end)
        {
            var startSeconds = start.At(new LocalTime(16, 0)).InZoneLeniently(timeZone).ToInstant();
            var endSeconds = (end == LocalDate.MaxIsoValue) ? Instant.MaxValue : end.At(new LocalTime(16, 0)).InZoneLeniently(timeZone).ToInstant();
            return Period(startSeconds, endSeconds);
        }
        public YahooHistory Period(DateTimeZone timeZone, LocalDate start) => Period(timeZone, start, LocalDate.MaxIsoValue);


        public Task<List<HistoryTick>?> GetHistoryAsync(string symbol, Frequency frequency = Frequency.Daily) =>
            GetTicksAsync<HistoryTick>(symbol, frequency);

        public Task<Dictionary<string, List<HistoryTick>?>> GetHistoryAsync(IEnumerable<string> symbols, Frequency frequency = Frequency.Daily) =>
            GetTicksAsync<HistoryTick>(symbols, frequency);

        public Task<List<DividendTick>?> GetDividendsAsync(string symbol) =>
            GetTicksAsync<DividendTick>(symbol);

        public Task<Dictionary<string, List<DividendTick>?>> GetDividendsAsync(IEnumerable<string> symbols) =>
            GetTicksAsync<DividendTick>(symbols);

        public Task<List<SplitTick>?> GetSplitsAsync(string symbol) =>
            GetTicksAsync<SplitTick>(symbol);

        public Task<Dictionary<string, List<SplitTick>?>> GetSplitsAsync(IEnumerable<string> symbols) =>
            GetTicksAsync<SplitTick>(symbols);

        private async Task<Dictionary<string, List<ITick>?>> GetTicksAsync<ITick>(IEnumerable<string> symbols, Frequency frequency = Frequency.Daily) where ITick : class
        {
            if (symbols == null)
                throw new ArgumentNullException(nameof(symbols));
            
            var symbolList = symbols.ToList();

            if (!symbolList.Any())
                throw new ArgumentException("Empty list.", nameof(symbolList));

            var duplicates = symbolList.CaseInsensitiveDuplicates();
            if (duplicates.Any())
            {
                var msg = "Duplicate symbol(s): " + duplicates.Select(s => "\"" + s + "\"").ToCommaDelimitedList() + ".";
                throw new ArgumentException(msg, nameof(symbolList));
            }

            // create a list of started tasks
            var tasks = symbolList.Select(symbol => GetTicksAsync<ITick>(symbol, frequency)).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var dictionary = tasks.Select((task, i) => (task, i))
                .ToDictionary(x => symbolList[x.i], x => x.task.Result, StringComparer.OrdinalIgnoreCase);

            return dictionary;
        }

        private async Task<List<ITick>?> GetTicksAsync<ITick>(string symbol, Frequency frequency = Frequency.Daily) where ITick : class
        {
            if (symbol == null)
                throw new ArgumentNullException(nameof(symbol));

            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Empty string.", nameof(symbol));

            Frequency = frequency;
            string tickParam = TickParser.GetParamFromType<ITick>();

            try
            {
                return await GetTickResponseAsync<ITick>(symbol, tickParam).ConfigureAwait(false);
            }
            catch (FlurlHttpException ex) when (ex.Call.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.LogInformation($"Symbol not found: \"{symbol}\".");
                return null;
            }
        }

        private async Task<List<ITick>> GetTickResponseAsync<ITick>(string symbol, string tickParam) where ITick:class
        {
            using var stream = await GetResponseStreamAsync(symbol, tickParam).ConfigureAwait(false);
            using var sr = new StreamReader(stream);
            using var csvReader = new CsvReader(sr);

            var ticks = new List<ITick>();

            csvReader.Read(); // skip header

            while (csvReader.Read())
            {
                var tick = TickParser.Parse<ITick>(csvReader.Context.Record);
                if (tick != null)
                    ticks.Add(tick);
            }
            return ticks;
        }

        private async Task<Stream> GetResponseStreamAsync(string symbol, string tickParam)
        {
            bool reset = false;
            while (true)
            {
                try
                {
                    var (client, crumb) = await ClientFactory.GetClientAndCrumbAsync(reset, Logger, Ct).ConfigureAwait(false);
                    return await _GetResponseStreamAsync(client, crumb).ConfigureAwait(false);
                }
                catch (FlurlHttpException ex) when (ex.Call.Response?.StatusCode == HttpStatusCode.Unauthorized && !reset)
                {
                    Logger.LogDebug("GetResponseStreamAsync: Unauthorized. Retrying.");
                    reset = true;
                }
                //catch (FlurlHttpException ex) when (ex.Call.Response?.StatusCode == HttpStatusCode.NotFound)
                //  throw new Exception($"Invalid symbol '{symbol}'.", ex);
            }

            #region Local Function

            Task<Stream> _GetResponseStreamAsync(IFlurlClient _client, string _crumb)
            {
                var url = "https://query1.finance.yahoo.com/v7/finance/download"
                    .AppendPathSegment(symbol)
                    .SetQueryParam("period1", Start.ToUnixTimeSeconds())
                    .SetQueryParam("period2", End.ToUnixTimeSeconds())
                    .SetQueryParam("interval", $"1{Frequency.Name()}")
                    .SetQueryParam("events", tickParam)
                    .SetQueryParam("crumb", _crumb);

                Logger.LogInformation(url);

                return url
                    .WithClient(_client)
                    .GetAsync(Ct)
                    .ReceiveStream();
            }

            #endregion
        }
    }
}
