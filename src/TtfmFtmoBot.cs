// =====================================================================================
//  TTFM FTMO Bot  —  STEP 1: infrastructure + HTF C2/C3/C4 detection (no entries yet)
// -------------------------------------------------------------------------------------
//  Instrument : FTMO US100 (Nasdaq CFD) on cTrader. Chart timeframe = LTF (5m or 15m).
//  Model      : TTrades Fractal Model. HTF candles are built in code, anchored to
//               18:00 ET (CME session open), so with 4H the candles are
//               18-22, 22-02, 02-06, 06-10, 10-14, 14-18 ET. 10:00-14:00 ET is one candle.
//  Step 1     : - FTMO 2-Step risk guard (daily loss on equity, static max loss)
//               - risk-based position sizing (ready for step 2)
//               - trade window, flatten time, spread filter, manual news blackout
//               - C2 detection (sweep of previous HTF candle + close back inside),
//                 C3/C4 outcome tracking, chart markers
//               - base-rate statistics printed on stop: does a C2 actually make the
//                 next candle more likely to move in its direction than ANY candle?
//  Step 2     : TryFindEntry() — LTF CISD entry, stop, target. Currently returns null.
//
//  Notes:
//  - Robot runs in UTC; ET and CE(S)T are computed with the US/EU DST rules in code,
//    so the bot does not depend on the OS time-zone database.
//  - FTMO daily loss resets at midnight CE(S)T from the balance at that moment;
//    the guard mirrors that and closes this bot's positions before FTMO's limit.
//  - FTMO forbids "hyperactivity" (>2,000 server requests/day): this bot never
//    modifies orders per tick.
//  - Sizing uses Symbol.PipValue (per unit, account currency) — assumes a USD account.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class TtfmFtmoBot : Robot
    {
        // ------------------------------------------------------------------ Risk (FTMO 2-Step)
        [Parameter("Risk per trade (% of balance)", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 3, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Initial capital (FTMO account size)", Group = "Risk", DefaultValue = 100000, MinValue = 1000)]
        public double InitialCapital { get; set; }

        [Parameter("FTMO max daily loss (%)", Group = "Risk", DefaultValue = 5.0, MinValue = 1, MaxValue = 10, Step = 0.5)]
        public double FtmoDailyLossPct { get; set; }

        [Parameter("Bot daily stop (%) — must be below FTMO's", Group = "Risk", DefaultValue = 3.5, MinValue = 0.5, MaxValue = 10, Step = 0.25)]
        public double BotDailyStopPct { get; set; }

        [Parameter("FTMO max loss (%)", Group = "Risk", DefaultValue = 10.0, MinValue = 1, MaxValue = 20, Step = 0.5)]
        public double FtmoMaxLossPct { get; set; }

        [Parameter("Bot max-loss stop (%) — must be below FTMO's", Group = "Risk", DefaultValue = 8.0, MinValue = 0.5, MaxValue = 20, Step = 0.5)]
        public double BotMaxLossStopPct { get; set; }

        [Parameter("Balance at last midnight CE(S)T (0 = current)", Group = "Risk", DefaultValue = 0, MinValue = 0)]
        public double DayStartBalanceOverride { get; set; }

        [Parameter("Max trades per day", Group = "Risk", DefaultValue = 1, MinValue = 1, MaxValue = 5)]
        public int MaxTradesPerDay { get; set; }

        // ------------------------------------------------------------------ Model
        [Parameter("HTF candle (hours: 1, 2, 3, 4 or 6)", Group = "Model", DefaultValue = 4, MinValue = 1, MaxValue = 6)]
        public int HtfHours { get; set; }

        [Parameter("Trade window start (ET hour)", Group = "Model", DefaultValue = 10, MinValue = 0, MaxValue = 23)]
        public int WindowStartHourEt { get; set; }

        [Parameter("Trade window end (ET hour)", Group = "Model", DefaultValue = 14, MinValue = 1, MaxValue = 24)]
        public int WindowEndHourEt { get; set; }

        [Parameter("Allow C4 continuation", Group = "Model", DefaultValue = true)]
        public bool AllowC4 { get; set; }

        // ------------------------------------------------------------------ Filters
        [Parameter("Max spread (price units)", Group = "Filters", DefaultValue = 3.0, MinValue = 0.1, Step = 0.1)]
        public double MaxSpread { get; set; }

        [Parameter("Max spread as % of stop distance", Group = "Filters", DefaultValue = 10, MinValue = 1, MaxValue = 50)]
        public double MaxSpreadToStopPct { get; set; }

        [Parameter("Flatten at (ET hour)", Group = "Filters", DefaultValue = 15, MinValue = 0, MaxValue = 23)]
        public int FlattenHourEt { get; set; }

        [Parameter("Flatten at (ET minute)", Group = "Filters", DefaultValue = 50, MinValue = 0, MaxValue = 59)]
        public int FlattenMinuteEt { get; set; }

        [Parameter("News times ET (yyyy-MM-dd HH:mm; ...)", Group = "Filters", DefaultValue = "")]
        public string NewsTimesEt { get; set; }

        [Parameter("News blackout before (min)", Group = "Filters", DefaultValue = 5, MinValue = 0, MaxValue = 120)]
        public int NewsBeforeMin { get; set; }

        [Parameter("News blackout after (min)", Group = "Filters", DefaultValue = 5, MinValue = 0, MaxValue = 120)]
        public int NewsAfterMin { get; set; }

        // ------------------------------------------------------------------ Execution
        [Parameter("Trading enabled (step 2)", Group = "Execution", DefaultValue = false)]
        public bool TradingEnabled { get; set; }

        [Parameter("Position label", Group = "Execution", DefaultValue = "TTFM")]
        public string PositionLabel { get; set; }

        // ------------------------------------------------------------------ Visuals / output
        [Parameter("Draw HTF candles", Group = "Visuals", DefaultValue = true)]
        public bool DrawHtfBoxes { get; set; }

        [Parameter("Draw setup labels and levels", Group = "Visuals", DefaultValue = true)]
        public bool DrawLabels { get; set; }

        [Parameter("Print statistics on stop", Group = "Visuals", DefaultValue = true)]
        public bool PrintStats { get; set; }

        // ================================================================== State
        private TimeSpan _ltf;
        private readonly List<HtfCandle> _htf = new List<HtfCandle>();
        private HtfCandle _forming;
        private int _lastProcessedIndex = -1;
        private bool _warmup;

        private readonly List<Setup> _openSetups = new List<Setup>();
        private Bias _nextBias;    // bias prepared for the candle that starts at _nextBias.ForStartEt
        private Bias _activeBias;  // bias of the currently forming candle (null = no setup)

        private readonly List<DateTime> _newsUtc = new List<DateTime>();

        private DateTime _cetDay;
        private double _dayStartBalance;
        private bool _haltedToday;
        private int _tradesToday;

        private readonly TtfmStatsBook _stats = new TtfmStatsBook();

        // ================================================================== Lifecycle
        protected override void OnStart()
        {
            ValidateParameters();
            _ltf = TimeFrameToSpan(TimeFrame);
            if (TimeSpan.FromHours(HtfHours).Ticks % _ltf.Ticks != 0 || _ltf >= TimeSpan.FromHours(HtfHours))
                throw new ArgumentException($"Chart timeframe {TimeFrame} must divide the {HtfHours}H HTF candle.");

            ParseNews();

            var cetNow = TtfmTime.UtcToCentralEurope(Server.Time);
            _cetDay = cetNow.Date;
            _dayStartBalance = DayStartBalanceOverride > 0 ? DayStartBalanceOverride : Account.Balance;
            if (DayStartBalanceOverride <= 0)
                Print("[risk] Day-start balance taken from current balance. If you restarted mid-day after losses, set 'Balance at last midnight CE(S)T'.");

            Positions.Closed += OnPositionClosed;

            // Warm-up: build HTF history from bars already loaded (no trading, no stats).
            _warmup = true;
            for (int i = 0; i <= Bars.Count - 2; i++)
                ProcessClosedBar(i);
            _warmup = false;

            Print($"[start] {SymbolName} LTF={TimeFrame} HTF={HtfHours}H anchored 18:00 ET, window {WindowStartHourEt:00}-{WindowEndHourEt:00} ET, " +
                  $"trading={(TradingEnabled ? "ON" : "OFF (detection only)")}, HTF candles warmed up: {_htf.Count}");
            Print($"[risk] daily stop {BotDailyStopPct}% (FTMO {FtmoDailyLossPct}%), max-loss stop {BotMaxLossStopPct}% (FTMO {FtmoMaxLossPct}%) of {InitialCapital:N0}; day-start balance {_dayStartBalance:N2}");
        }

        protected override void OnBar()
        {
            int lastClosed = Bars.Count - 2;
            for (int i = _lastProcessedIndex + 1; i <= lastClosed; i++)
                ProcessClosedBar(i);
        }

        protected override void OnTick()
        {
            RiskGuard();
            FlattenIfLate();
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
            if (PrintStats)
                foreach (var line in _stats.Report(WindowStartHourEt, WindowEndHourEt))
                    Print(line);
        }

        // ================================================================== HTF candle builder
        private void ProcessClosedBar(int i)
        {
            if (i <= _lastProcessedIndex) return;

            DateTime openUtc = Bars.OpenTimes[i];
            double o = Bars.OpenPrices[i], h = Bars.HighPrices[i], l = Bars.LowPrices[i], c = Bars.ClosePrices[i];

            DateTime et = TtfmTime.UtcToNewYork(openUtc);
            DateTime bucketStartEt = TtfmTime.BucketStartEt(et, HtfHours);

            if (_forming != null && bucketStartEt != _forming.StartEt)
                FinalizeForming();               // data gap / session break: previous candle is done

            if (_forming == null)
                StartCandle(bucketStartEt, openUtc, i, o);

            _forming.AddBar(i, openUtc, h, l, c);

            if (!_warmup)
                OnLtfBarClosed(i);

            DateTime closeEt = TtfmTime.UtcToNewYork(openUtc + _ltf);
            if (closeEt >= _forming.EndEt)
                FinalizeForming();               // last LTF bar of the bucket has closed

            _lastProcessedIndex = i;
        }

        private void StartCandle(DateTime startEt, DateTime openUtc, int index, double open)
        {
            var prev = _htf.Count > 0 ? _htf[_htf.Count - 1] : null;
            _forming = new HtfCandle(startEt, startEt.AddHours(HtfHours), openUtc, index, open)
            {
                RefHigh = prev?.High,
                RefLow = prev?.Low,
                IsWindowCandle = startEt.Hour >= WindowStartHourEt && startEt.Hour < WindowEndHourEt
            };

            _activeBias = (_nextBias != null && _nextBias.ForStartEt == startEt) ? _nextBias : null;
            _nextBias = null;
            _forming.Context = _activeBias == null ? null : $"{_activeBias.Stage} after {(_activeBias.Dir == TtfmDir.Bull ? "bull" : "bear")} {(_activeBias.Stage == "C3" ? "C2" : "C3")}";
            _forming.ContextDir = _activeBias?.Dir ?? TtfmDir.None;
        }

        private void FinalizeForming()
        {
            var c = _forming;
            _forming = null;
            _activeBias = null;

            var prev = _htf.Count > 0 ? _htf[_htf.Count - 1] : null;
            _htf.Add(c);
            if (_htf.Count > 600) _htf.RemoveAt(0);

            if (!_warmup && prev != null)
                RecordStats(c);

            DrawHtfCandle(c);

            // 1) advance existing setups (this candle is their C3 or C4)
            Bias continuation = UpdateSetups(c);

            // 2) is this candle a new C2?
            Bias fresh = null;
            if (prev != null)
            {
                var dir = TtfmLogic.ClassifyC2(prev.High, prev.Low, c.High, c.Low, c.Close);
                if (dir != TtfmDir.None)
                {
                    _openSetups.Add(new Setup { Dir = dir, C1 = prev, C2 = c, Stage = SetupStage.AwaitC3 });
                    DrawC2(c, dir);
                    fresh = new Bias
                    {
                        Dir = dir,
                        Stage = "C3",
                        ForStartEt = c.EndEt,
                        ProtectedLevel = dir == TtfmDir.Bull ? c.Low : c.High,   // C2 extreme = swing to protect
                        Target = dir == TtfmDir.Bull ? c.High : c.Low,           // C2 opposite extreme
                        Eq = c.Eq,
                        Reference = c
                    };
                }
            }

            // A fresh C2 takes priority over a C4 continuation for the next candle.
            _nextBias = fresh ?? continuation;
        }

        private Bias UpdateSetups(HtfCandle c)
        {
            Bias continuation = null;
            foreach (var s in _openSetups)
            {
                if (s.Stage == SetupStage.AwaitC3)
                {
                    s.C3 = c;
                    var outcome = TtfmLogic.EvaluateNext(s.Dir, s.C2.High, s.C2.Low, c.High, c.Low, c.Close);
                    DrawOutcome(c, s.Dir, "C3", outcome);
                    if (outcome == TtfmOutcome.Expanded && AllowC4)
                    {
                        s.Stage = SetupStage.AwaitC4;
                        continuation = new Bias
                        {
                            Dir = s.Dir,
                            Stage = "C4",
                            ForStartEt = c.EndEt,
                            ProtectedLevel = s.Dir == TtfmDir.Bull ? c.Low : c.High,  // C3 extreme (step-2 decision)
                            Target = s.Dir == TtfmDir.Bull ? c.High : c.Low,
                            Eq = c.Eq,
                            Reference = c
                        };
                    }
                    else s.Stage = SetupStage.Done;
                }
                else if (s.Stage == SetupStage.AwaitC4)
                {
                    var outcome = TtfmLogic.EvaluateNext(s.Dir, s.C3.High, s.C3.Low, c.High, c.Low, c.Close);
                    DrawOutcome(c, s.Dir, "C4", outcome);
                    s.Stage = SetupStage.Done;
                }
            }
            _openSetups.RemoveAll(s => s.Stage == SetupStage.Done);
            return continuation;
        }

        // ================================================================== Entry pipeline
        private void OnLtfBarClosed(int index)
        {
            if (_forming == null || !_forming.IsWindowCandle || _activeBias == null) return;

            var signal = TryFindEntry(index, _activeBias, _forming);
            if (signal == null) return;

            if (!TradingEnabled)
            {
                Print($"[signal-only] {signal.Dir} stop {signal.StopPrice} target {signal.TargetPrice} — {signal.Reason}");
                return;
            }

            if (!CanOpenNewTrade(out string reason))
            {
                Print($"[skip] {signal.Dir} signal blocked: {reason}");
                return;
            }

            PlaceTrade(signal);
        }

        /// <summary>
        /// STEP 2 — not implemented yet. Called on every closed LTF bar inside the trade-window
        /// candle when that candle has a bias (C3 after a C2, or C4 after an expanded C3).
        /// Planned logic (rules to be agreed first):
        ///   1. wait for price to trade toward the protected side (into C2 discount for longs),
        ///   2. detect an LTF CISD in the bias direction (body close beyond the open of the
        ///      opposing candle series that made the intra-candle extreme),
        ///   3. stop beyond the new LTF swing (plus spread buffer for shorts),
        ///   4. target = bias.Target (C2 opposite extreme) or a fixed R multiple.
        /// Return null when there is no entry on this bar.
        /// </summary>
        private EntrySignal TryFindEntry(int index, Bias bias, HtfCandle candle)
        {
            return null;
        }

        private bool CanOpenNewTrade(out string reason)
        {
            DateTime now = Server.Time;
            DateTime et = TtfmTime.UtcToNewYork(now);

            if (_haltedToday) { reason = "daily stop hit"; return false; }
            if (_tradesToday >= MaxTradesPerDay) { reason = "max trades per day reached"; return false; }
            if (Positions.FindAll(PositionLabel, SymbolName).Length > 0) { reason = "position already open"; return false; }
            if (et.TimeOfDay >= new TimeSpan(FlattenHourEt, FlattenMinuteEt, 0)) { reason = "past flatten time"; return false; }
            if (InNewsBlackout(now)) { reason = "news blackout"; return false; }
            if (Symbol.Spread > MaxSpread) { reason = $"spread {Symbol.Spread} > {MaxSpread}"; return false; }

            reason = null;
            return true;
        }

        private void PlaceTrade(EntrySignal signal)
        {
            bool isLong = signal.Dir == TtfmDir.Bull;
            double entry = isLong ? Symbol.Ask : Symbol.Bid;
            double stopDistance = isLong ? entry - signal.StopPrice : signal.StopPrice - entry;
            if (stopDistance <= 0) { Print($"[skip] stop {signal.StopPrice} is on the wrong side of entry {entry}"); return; }

            double spread = Symbol.Ask - Symbol.Bid;
            if (spread / stopDistance * 100.0 > MaxSpreadToStopPct)
            {
                Print($"[skip] spread {spread:F2} is {spread / stopDistance * 100.0:F1}% of the {stopDistance:F2} stop (max {MaxSpreadToStopPct}%)");
                return;
            }

            // Risk money: % of balance, capped by the room left before the bot's own stops.
            double riskMoney = Account.Balance * RiskPercent / 100.0;
            double room = Math.Min(Account.Equity - DailyBotFloor(), Account.Equity - MaxBotFloor());
            riskMoney = Math.Min(riskMoney, room * 0.9);
            if (riskMoney <= 0) { Print("[skip] no risk budget left before the bot's loss stops"); return; }

            double slPips = stopDistance / Symbol.PipSize;
            double units = riskMoney / (slPips * Symbol.PipValue);
            units = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                Print($"[skip] size {units} below minimum {Symbol.VolumeInUnitsMin} for risk {riskMoney:F2}");
                return;
            }
            units = Math.Min(units, Symbol.VolumeInUnitsMax);

            double? tpPips = signal.TargetPrice.HasValue ? Math.Abs(signal.TargetPrice.Value - entry) / Symbol.PipSize : (double?)null;

            var result = ExecuteMarketOrder(isLong ? TradeType.Buy : TradeType.Sell, SymbolName, units, PositionLabel, slPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradesToday++;
                Print($"[trade] {(isLong ? "BUY" : "SELL")} {units} @ {result.Position.EntryPrice} SL {signal.StopPrice} TP {signal.TargetPrice} risk≈{riskMoney:F2} — {signal.Reason}");
            }
            else
            {
                Print($"[error] order failed: {result.Error}");
            }
        }

        // ================================================================== Risk guard (FTMO)
        private double DailyBotFloor() => _dayStartBalance - InitialCapital * BotDailyStopPct / 100.0;
        private double MaxBotFloor() => InitialCapital * (1.0 - BotMaxLossStopPct / 100.0);

        private void RiskGuard()
        {
            DateTime cetNow = TtfmTime.UtcToCentralEurope(Server.Time);
            if (cetNow.Date != _cetDay)
            {
                _cetDay = cetNow.Date;
                _dayStartBalance = Account.Balance;
                _haltedToday = false;
                _tradesToday = 0;
                if (!IsBacktesting)
                    Print($"[risk] new FTMO day {_cetDay:yyyy-MM-dd}: day-start balance {_dayStartBalance:N2}, bot floor {DailyBotFloor():N2}, FTMO floor {_dayStartBalance - InitialCapital * FtmoDailyLossPct / 100.0:N2}");
            }

            double equity = Account.Equity;

            if (equity <= MaxBotFloor())
            {
                CloseAll("max-loss stop");
                Print($"[risk] equity {equity:N2} hit the bot max-loss stop {MaxBotFloor():N2}. Bot stopped.");
                Stop();
                return;
            }

            if (!_haltedToday && equity <= DailyBotFloor())
            {
                CloseAll("daily stop");
                _haltedToday = true;
                Print($"[risk] equity {equity:N2} hit the bot daily stop {DailyBotFloor():N2}. No new trades until midnight CE(S)T.");
            }
        }

        private void FlattenIfLate()
        {
            DateTime et = TtfmTime.UtcToNewYork(Server.Time);
            if (et.TimeOfDay >= new TimeSpan(FlattenHourEt, FlattenMinuteEt, 0)
                && Positions.FindAll(PositionLabel, SymbolName).Length > 0)
                CloseAll("flatten time");
        }

        private void CloseAll(string why)
        {
            foreach (var p in Positions.FindAll(PositionLabel, SymbolName))
            {
                var r = ClosePosition(p);
                if (!r.IsSuccessful) Print($"[error] close failed ({why}): {r.Error}");
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != PositionLabel || p.SymbolName != SymbolName) return;
            Print($"[closed] {p.TradeType} {p.VolumeInUnits} net {p.NetProfit:F2} reason {args.Reason}");
        }

        // ================================================================== News blackout
        private void ParseNews()
        {
            _newsUtc.Clear();
            if (string.IsNullOrWhiteSpace(NewsTimesEt)) return;

            foreach (var raw in NewsTimesEt.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = raw.Trim();
                if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var et))
                    _newsUtc.Add(TtfmTime.NewYorkToUtc(et));
                else
                    Print($"[news] could not parse '{s}' — expected yyyy-MM-dd HH:mm (ET)");
            }
            Print($"[news] {_newsUtc.Count} blackout times loaded");
        }

        private bool InNewsBlackout(DateTime utcNow)
        {
            foreach (var n in _newsUtc)
                if (utcNow >= n.AddMinutes(-NewsBeforeMin) && utcNow <= n.AddMinutes(NewsAfterMin))
                    return true;
            return false;
        }

        // ================================================================== Statistics
        private void RecordStats(HtfCandle c)
        {
            if (!c.RefHigh.HasValue || !c.RefLow.HasValue) return;

            foreach (var dir in new[] { TtfmDir.Bull, TtfmDir.Bear })
                _stats.Add("ALL candles", dir, c.IsWindowCandle, Measure(c, dir));

            if (c.Context != null)
                _stats.Add(c.Context, c.ContextDir, c.IsWindowCandle, Measure(c, c.ContextDir));
        }

        private static TtfmMeasure Measure(HtfCandle c, TtfmDir dir)
        {
            double refHigh = c.RefHigh.Value, refLow = c.RefLow.Value;
            bool bull = dir == TtfmDir.Bull;
            return new TtfmMeasure
            {
                CloseInDir = bull ? c.Close > c.Open : c.Close < c.Open,
                Touch = TtfmLogic.FirstTouch(dir, c.FirstAboveUtc, c.FirstBelowUtc),
                Outcome = TtfmLogic.EvaluateNext(dir, refHigh, refLow, c.High, c.Low, c.Close)
            };
        }

        // ================================================================== Drawing
        private void DrawHtfCandle(HtfCandle c)
        {
            if (!DrawHtfBoxes) return;
            var color = c.IsWindowCandle ? Color.FromArgb(110, 30, 144, 255) : Color.FromArgb(70, 128, 128, 128);
            var rect = Chart.DrawRectangle($"{PositionLabel}_htf_{c.StartUtc.Ticks}", c.StartUtc, c.High, c.LastBarUtc + _ltf, c.Low, color, 1, LineStyle.Solid);
            rect.IsFilled = false;
        }

        private void DrawC2(HtfCandle c, TtfmDir dir)
        {
            if (!DrawLabels) return;
            bool bull = dir == TtfmDir.Bull;
            var color = bull ? Color.FromArgb(255, 38, 166, 154) : Color.FromArgb(255, 239, 83, 80);
            DateTime t1 = c.StartUtc, t2 = c.LastBarUtc + _ltf + TimeSpan.FromHours(HtfHours);
            string id = c.StartUtc.Ticks.ToString(CultureInfo.InvariantCulture);

            var text = Chart.DrawText($"{PositionLabel}_c2_{id}", bull ? "C2 bull" : "C2 bear", c.StartUtc, bull ? c.Low : c.High, color);
            text.VerticalAlignment = bull ? VerticalAlignment.Top : VerticalAlignment.Bottom;

            Chart.DrawTrendLine($"{PositionLabel}_prot_{id}", t1, bull ? c.Low : c.High, t2, bull ? c.Low : c.High, color, 1, LineStyle.Dots);
            Chart.DrawTrendLine($"{PositionLabel}_eq_{id}", t1, c.Eq, t2, c.Eq, Color.FromArgb(160, 128, 128, 128), 1, LineStyle.Lines);
        }

        private void DrawOutcome(HtfCandle c, TtfmDir dir, string stage, TtfmOutcome outcome)
        {
            if (!DrawLabels) return;
            bool bull = dir == TtfmDir.Bull;
            Color color;
            string tag;
            switch (outcome)
            {
                case TtfmOutcome.Expanded: color = Color.LimeGreen; tag = "exp"; break;
                case TtfmOutcome.Failed: color = Color.Red; tag = "fail"; break;
                default: color = Color.Orange; tag = "cons"; break;
            }
            var text = Chart.DrawText($"{PositionLabel}_{stage}_{c.StartUtc.Ticks}_{(int)dir}", $"{stage} {tag}", c.StartUtc, bull ? c.Low : c.High, color);
            text.VerticalAlignment = bull ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        }

        // ================================================================== Helpers
        private void ValidateParameters()
        {
            if (24 % HtfHours != 0)
                throw new ArgumentException("HTF hours must be 1, 2, 3, 4 or 6.");
            if (!TtfmTime.IsBucketAligned(WindowStartHourEt, HtfHours) || !TtfmTime.IsBucketAligned(WindowEndHourEt % 24, HtfHours) || WindowEndHourEt <= WindowStartHourEt)
                throw new ArgumentException($"Trade window {WindowStartHourEt}-{WindowEndHourEt} ET must start and end on {HtfHours}H candle boundaries (anchored 18:00 ET).");
            if (BotDailyStopPct >= FtmoDailyLossPct)
                throw new ArgumentException("Bot daily stop must be below FTMO's max daily loss.");
            if (BotMaxLossStopPct >= FtmoMaxLossPct)
                throw new ArgumentException("Bot max-loss stop must be below FTMO's max loss.");
        }

        private static TimeSpan TimeFrameToSpan(TimeFrame tf)
        {
            if (tf == TimeFrame.Minute) return TimeSpan.FromMinutes(1);
            if (tf == TimeFrame.Minute2) return TimeSpan.FromMinutes(2);
            if (tf == TimeFrame.Minute3) return TimeSpan.FromMinutes(3);
            if (tf == TimeFrame.Minute5) return TimeSpan.FromMinutes(5);
            if (tf == TimeFrame.Minute10) return TimeSpan.FromMinutes(10);
            if (tf == TimeFrame.Minute15) return TimeSpan.FromMinutes(15);
            if (tf == TimeFrame.Minute20) return TimeSpan.FromMinutes(20);
            if (tf == TimeFrame.Minute30) return TimeSpan.FromMinutes(30);
            if (tf == TimeFrame.Hour) return TimeSpan.FromHours(1);
            throw new ArgumentException($"Unsupported chart timeframe {tf}. Use 1m-30m for a 4H model.");
        }

        // ================================================================== Inner types
        private enum SetupStage { AwaitC3, AwaitC4, Done }

        private sealed class Setup
        {
            public TtfmDir Dir;
            public HtfCandle C1, C2, C3;
            public SetupStage Stage;
        }

        private sealed class Bias
        {
            public TtfmDir Dir;
            public string Stage;            // "C3" or "C4"
            public DateTime ForStartEt;     // start (ET) of the candle this bias applies to
            public double ProtectedLevel;   // swing that must hold (C2 extreme for C3, C3 extreme for C4)
            public double Target;           // first draw on liquidity (opposite extreme of the reference candle)
            public double Eq;               // reference candle midpoint
            public HtfCandle Reference;
        }

        private sealed class EntrySignal
        {
            public TtfmDir Dir;
            public double StopPrice;
            public double? TargetPrice;
            public string Reason;
        }

        private sealed class HtfCandle
        {
            public readonly DateTime StartEt, EndEt, StartUtc;
            public DateTime LastBarUtc;
            public readonly double Open;
            public double High = double.MinValue, Low = double.MaxValue, Close;
            public DateTime HighUtc, LowUtc;
            public readonly int FirstIndex;
            public int LastIndex;

            public double? RefHigh, RefLow;                 // previous HTF candle's range
            public DateTime? FirstAboveUtc, FirstBelowUtc;  // first LTF bar trading beyond it
            public bool IsWindowCandle;
            public string Context;                          // e.g. "C3 after bull C2"
            public TtfmDir ContextDir;

            public double Eq => (High + Low) / 2.0;

            public HtfCandle(DateTime startEt, DateTime endEt, DateTime startUtc, int firstIndex, double open)
            {
                StartEt = startEt; EndEt = endEt; StartUtc = startUtc;
                FirstIndex = firstIndex; LastIndex = firstIndex; Open = open; Close = open;
                LastBarUtc = startUtc;
            }

            public void AddBar(int index, DateTime openUtc, double high, double low, double close)
            {
                if (high > High) { High = high; HighUtc = openUtc; }
                if (low < Low) { Low = low; LowUtc = openUtc; }
                Close = close;
                LastIndex = index;
                LastBarUtc = openUtc;
                if (RefHigh.HasValue && FirstAboveUtc == null && high > RefHigh.Value) FirstAboveUtc = openUtc;
                if (RefLow.HasValue && FirstBelowUtc == null && low < RefLow.Value) FirstBelowUtc = openUtc;
            }
        }
    }

    // =====================================================================================
    //  Pure logic (no cTrader dependencies) — unit-testable outside cTrader
    // =====================================================================================
    public enum TtfmDir { None = 0, Bull = 1, Bear = -1 }
    public enum TtfmOutcome { Expanded, Consolidated, Failed }
    public enum TtfmTouch { FavorableFirst, AdverseFirst, SameBar, Neither }

    public static class TtfmLogic
    {
        /// <summary>C2 = sweep of the previous candle's extreme and a close back inside its range.
        /// Outside bars that sweep both sides are ambiguous and return None.</summary>
        public static TtfmDir ClassifyC2(double prevHigh, double prevLow, double high, double low, double close)
        {
            bool bull = low < prevLow && close > prevLow;
            bool bear = high > prevHigh && close < prevHigh;
            if (bull && bear) return TtfmDir.None;
            if (bull) return TtfmDir.Bull;
            if (bear) return TtfmDir.Bear;
            return TtfmDir.None;
        }

        /// <summary>How the next candle behaved relative to a reference candle (C2 for C3, C3 for C4).
        /// Failed = traded through the protected extreme; Expanded = closed beyond the opposite extreme.</summary>
        public static TtfmOutcome EvaluateNext(TtfmDir dir, double refHigh, double refLow, double high, double low, double close)
        {
            if (dir == TtfmDir.Bull)
            {
                if (low < refLow) return TtfmOutcome.Failed;
                return close > refHigh ? TtfmOutcome.Expanded : TtfmOutcome.Consolidated;
            }
            if (dir == TtfmDir.Bear)
            {
                if (high > refHigh) return TtfmOutcome.Failed;
                return close < refLow ? TtfmOutcome.Expanded : TtfmOutcome.Consolidated;
            }
            throw new ArgumentException("Direction required");
        }

        /// <summary>Which side of the previous candle's range was taken first, from the bias' point of view.</summary>
        public static TtfmTouch FirstTouch(TtfmDir dir, DateTime? firstAbove, DateTime? firstBelow)
        {
            DateTime? fav = dir == TtfmDir.Bull ? firstAbove : firstBelow;
            DateTime? adv = dir == TtfmDir.Bull ? firstBelow : firstAbove;
            if (fav == null && adv == null) return TtfmTouch.Neither;
            if (adv == null) return TtfmTouch.FavorableFirst;
            if (fav == null) return TtfmTouch.AdverseFirst;
            if (fav.Value == adv.Value) return TtfmTouch.SameBar;
            return fav.Value < adv.Value ? TtfmTouch.FavorableFirst : TtfmTouch.AdverseFirst;
        }
    }

    public static class TtfmTime
    {
        public static DateTime NthSunday(int year, int month, int n)
        {
            var first = new DateTime(year, month, 1);
            int offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + 7 * (n - 1));
        }

        public static DateTime LastSunday(int year, int month)
        {
            var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(int)last.DayOfWeek);
        }

        /// <summary>US rule since 2007: EDT from 2nd Sunday of March 02:00 EST to 1st Sunday of November 02:00 EDT.</summary>
        public static DateTime UtcToNewYork(DateTime utc)
        {
            DateTime dstStartUtc = NthSunday(utc.Year, 3, 2).AddHours(7);
            DateTime dstEndUtc = NthSunday(utc.Year, 11, 1).AddHours(6);
            bool dst = utc >= dstStartUtc && utc < dstEndUtc;
            return DateTime.SpecifyKind(utc.AddHours(dst ? -4 : -5), DateTimeKind.Unspecified);
        }

        public static DateTime NewYorkToUtc(DateTime et)
        {
            DateTime asEst = DateTime.SpecifyKind(et.AddHours(5), DateTimeKind.Utc);
            if (UtcToNewYork(asEst) == DateTime.SpecifyKind(et, DateTimeKind.Unspecified)) return asEst;
            return DateTime.SpecifyKind(et.AddHours(4), DateTimeKind.Utc);
        }

        /// <summary>EU rule: CEST from last Sunday of March 01:00 UTC to last Sunday of October 01:00 UTC.</summary>
        public static DateTime UtcToCentralEurope(DateTime utc)
        {
            DateTime dstStartUtc = LastSunday(utc.Year, 3).AddHours(1);
            DateTime dstEndUtc = LastSunday(utc.Year, 10).AddHours(1);
            bool dst = utc >= dstStartUtc && utc < dstEndUtc;
            return DateTime.SpecifyKind(utc.AddHours(dst ? 2 : 1), DateTimeKind.Unspecified);
        }

        /// <summary>Start (ET) of the HTF bucket containing 'et'. Buckets are anchored to 18:00 ET.</summary>
        public static DateTime BucketStartEt(DateTime et, int htfHours)
        {
            DateTime shifted = et.AddHours(6);                       // 18:00 ET -> 00:00
            int bucket = (int)Math.Floor(shifted.TimeOfDay.TotalHours / htfHours) * htfHours;
            return shifted.Date.AddHours(bucket).AddHours(-6);
        }

        public static bool IsBucketAligned(int hourEt, int htfHours) => ((hourEt + 6) % 24) % htfHours == 0;
    }

    public sealed class TtfmMeasure
    {
        public bool CloseInDir;
        public TtfmTouch Touch;
        public TtfmOutcome Outcome;
    }

    public sealed class TtfmStatsBook
    {
        private sealed class Line
        {
            public int N, CloseInDir, FavFirst, AdvFirst, SameBar, Neither, Expanded, Failed;
        }

        private readonly Dictionary<string, Line> _lines = new Dictionary<string, Line>();
        private readonly List<string> _order = new List<string>();

        public void Add(string context, TtfmDir dir, bool windowCandle, TtfmMeasure m)
        {
            Bump($"{context} | {(dir == TtfmDir.Bull ? "bull" : "bear")} view | all hours", m);
            if (windowCandle)
                Bump($"{context} | {(dir == TtfmDir.Bull ? "bull" : "bear")} view | window", m);
        }

        private void Bump(string key, TtfmMeasure m)
        {
            if (!_lines.TryGetValue(key, out var l)) { l = new Line(); _lines[key] = l; _order.Add(key); }
            l.N++;
            if (m.CloseInDir) l.CloseInDir++;
            switch (m.Touch)
            {
                case TtfmTouch.FavorableFirst: l.FavFirst++; break;
                case TtfmTouch.AdverseFirst: l.AdvFirst++; break;
                case TtfmTouch.SameBar: l.SameBar++; break;
                default: l.Neither++; break;
            }
            if (m.Outcome == TtfmOutcome.Expanded) l.Expanded++;
            if (m.Outcome == TtfmOutcome.Failed) l.Failed++;
        }

        public IEnumerable<string> Report(int windowStart, int windowEnd)
        {
            yield return "================ TTFM base rates (HTF candle vs previous candle) ================";
            yield return "fav-first = took the bias-side extreme of the previous candle before the other side";
            yield return "failed    = traded through the protected extreme; expanded = closed beyond the target extreme";
            yield return $"window    = candles starting {windowStart:00}:00-{windowEnd:00}:00 ET. Compare each setup line with its 'ALL candles' line.";
            yield return string.Format(CultureInfo.InvariantCulture, "{0,-48} {1,6} {2,9} {3,9} {4,9} {5,9} {6,9} {7,9} {8,9}",
                "context", "N", "close-dir", "fav-1st", "adv-1st", "same-bar", "neither", "expanded", "failed");
            foreach (var key in _order.OrderBy(k => k.StartsWith("ALL") ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal))
            {
                var l = _lines[key];
                Func<int, string> pct = x => l.N == 0 ? "-" : (100.0 * x / l.N).ToString("F1", CultureInfo.InvariantCulture) + "%";
                yield return string.Format(CultureInfo.InvariantCulture, "{0,-48} {1,6} {2,9} {3,9} {4,9} {5,9} {6,9} {7,9} {8,9}",
                    key, l.N, pct(l.CloseInDir), pct(l.FavFirst), pct(l.AdvFirst), pct(l.SameBar), pct(l.Neither), pct(l.Expanded), pct(l.Failed));
            }
            yield return "=================================================================================";
        }
    }
}
