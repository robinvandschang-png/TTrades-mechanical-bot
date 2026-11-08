using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using cAlgo.API;
using cAlgo.Robots;

static class Program
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static DateTime D(int y, int mo, int d, int h, int mi = 0) => new DateTime(y, mo, d, h, mi, 0);

    static int Main()
    {
        Console.WriteLine("---- time conversions");
        Check(TtfmTime.UtcToNewYork(D(2026, 11, 9, 15)) == D(2026, 11, 9, 10), "EST: 15:00Z -> 10:00 ET");
        Check(TtfmTime.UtcToNewYork(D(2026, 7, 15, 14)) == D(2026, 7, 15, 10), "EDT: 14:00Z -> 10:00 ET");
        Check(TtfmTime.UtcToNewYork(D(2026, 3, 8, 6, 59)) == D(2026, 3, 8, 1, 59), "US DST start: 06:59Z -> 01:59 EST");
        Check(TtfmTime.UtcToNewYork(D(2026, 3, 8, 7, 0)) == D(2026, 3, 8, 3, 0), "US DST start: 07:00Z -> 03:00 EDT");
        Check(TtfmTime.UtcToNewYork(D(2026, 11, 1, 5, 59)) == D(2026, 11, 1, 1, 59), "US DST end: 05:59Z -> 01:59 EDT");
        Check(TtfmTime.UtcToNewYork(D(2026, 11, 1, 6, 0)) == D(2026, 11, 1, 1, 0), "US DST end: 06:00Z -> 01:00 EST");
        Check(TtfmTime.UtcToCentralEurope(D(2026, 3, 29, 0, 59)) == D(2026, 3, 29, 1, 59), "EU DST start: 00:59Z -> 01:59 CET");
        Check(TtfmTime.UtcToCentralEurope(D(2026, 3, 29, 1, 0)) == D(2026, 3, 29, 3, 0), "EU DST start: 01:00Z -> 03:00 CEST");
        Check(TtfmTime.UtcToCentralEurope(D(2026, 10, 25, 0, 59)) == D(2026, 10, 25, 2, 59), "EU DST end: 00:59Z -> 02:59 CEST");
        Check(TtfmTime.UtcToCentralEurope(D(2026, 10, 25, 1, 0)) == D(2026, 10, 25, 2, 0), "EU DST end: 01:00Z -> 02:00 CET");
        Check(TtfmTime.NewYorkToUtc(D(2026, 11, 12, 8, 30)) == D(2026, 11, 12, 13, 30), "news 08:30 ET (EST) -> 13:30Z");
        Check(TtfmTime.NewYorkToUtc(D(2026, 7, 29, 14, 0)) == D(2026, 7, 29, 18, 0), "news 14:00 ET (EDT) -> 18:00Z");

        Console.WriteLine("---- 4H buckets anchored 18:00 ET");
        var day = D(2026, 11, 9, 0);
        (int h, int m, DateTime exp)[] cases =
        {
            (10, 0, day.AddHours(10)), (13, 55, day.AddHours(10)), (14, 0, day.AddHours(14)), (17, 30, day.AddHours(14)),
            (18, 0, day.AddHours(18)), (21, 59, day.AddHours(18)), (22, 0, day.AddHours(22)), (1, 0, day.AddHours(-2)),
            (2, 0, day.AddHours(2)), (9, 55, day.AddHours(6)),
        };
        foreach (var c in cases)
            Check(TtfmTime.BucketStartEt(day.AddHours(c.h).AddMinutes(c.m), 4) == c.exp, $"{c.h:00}:{c.m:00} ET -> bucket {c.exp:MM-dd HH:mm}");
        Check(TtfmTime.IsBucketAligned(10, 4) && TtfmTime.IsBucketAligned(14, 4) && TtfmTime.IsBucketAligned(2, 4) && !TtfmTime.IsBucketAligned(9, 4), "window alignment check");

        Console.WriteLine("---- C2 / outcome / first touch");
        Check(TtfmLogic.ClassifyC2(110, 100, 108, 98, 105) == TtfmDir.Bull, "sweep low + close inside = bull C2");
        Check(TtfmLogic.ClassifyC2(110, 100, 112, 101, 109) == TtfmDir.Bear, "sweep high + close inside = bear C2");
        Check(TtfmLogic.ClassifyC2(110, 100, 112, 98, 105) == TtfmDir.None, "outside bar = none");
        Check(TtfmLogic.ClassifyC2(110, 100, 105, 98, 99.5) == TtfmDir.None, "sweep low but close below = none");
        Check(TtfmLogic.EvaluateNext(TtfmDir.Bull, 100, 90, 105, 91, 101) == TtfmOutcome.Expanded, "bull expanded");
        Check(TtfmLogic.EvaluateNext(TtfmDir.Bull, 100, 90, 105, 89, 101) == TtfmOutcome.Failed, "bull failed beats expanded");
        Check(TtfmLogic.EvaluateNext(TtfmDir.Bull, 100, 90, 99, 91, 95) == TtfmOutcome.Consolidated, "bull consolidated");
        Check(TtfmLogic.EvaluateNext(TtfmDir.Bear, 100, 90, 99, 85, 89) == TtfmOutcome.Expanded, "bear expanded");
        var t1 = D(2026, 1, 1, 10); var t2 = t1.AddMinutes(5);
        Check(TtfmLogic.FirstTouch(TtfmDir.Bull, t1, t2) == TtfmTouch.FavorableFirst, "bull: high first");
        Check(TtfmLogic.FirstTouch(TtfmDir.Bull, t2, t1) == TtfmTouch.AdverseFirst, "bull: low first");
        Check(TtfmLogic.FirstTouch(TtfmDir.Bear, t2, t1) == TtfmTouch.FavorableFirst, "bear: low first");
        Check(TtfmLogic.FirstTouch(TtfmDir.Bull, t1, t1) == TtfmTouch.SameBar, "same bar");
        Check(TtfmLogic.FirstTouch(TtfmDir.Bull, null, null) == TtfmTouch.Neither, "inside candle");

        Console.WriteLine("---- end-to-end: synthetic 5m bars through the bot");
        var bot = NewBot();
        bot.RunStart();
        // (ET start of bucket, open, high, low, close, lowFirst)
        var start = D(2026, 11, 8, 18);
        var buckets = new (double o, double h, double l, double c, bool lowFirst)[]
        {
            (100, 105, 95, 102, true),   // Sun 18-22
            (102, 106, 99, 104, true),   // Sun 22-02
            (104, 110, 100, 106, true),  // Mon 02-06  C1
            (106, 108, 98, 105, true),   // Mon 06-10  bull C2
            (105, 112, 101, 111, true),  // Mon 10-14  C3 (window) expanded
            (111, 115, 109, 114, true),  // Mon 14-18  C4 expanded
            (114, 116, 110, 112, false), // Mon 18-22  bear C2
            (112, 113, 105, 106, false), // Mon 22-02  C3 expanded
            (106, 108, 104, 107, true),  // Tue 02-06  C4 consolidated + new bull C2
            (107, 109, 106, 108, true),  // Tue 06-10  C3 consolidated
            (108, 110, 103, 104, false), // Tue 10-14  bear C2 (window, no bias)
            (104, 105, 100, 101, false), // Tue 14-18  C3 expanded
        };
        for (int b = 0; b < buckets.Length; b++)
            foreach (var bar in BucketBars(start.AddHours(4 * b), buckets[b]))
                Feed(bot, bar);
        Feed(bot, (TtfmTime.NewYorkToUtc(start.AddHours(48)), 101, 101, 101, 101)); // opens Tue 18:00 -> flush
        bot.RunStop();

        var texts = bot.Chart.Texts;
        foreach (var t in texts) Console.WriteLine("   label " + t);
        string[] expected =
        {
            "2026-11-09 03:00Z C2 bear", "2026-11-09 07:00Z C3 fail",   // Sun 22-02 swept Sun 18-22 high and closed back inside
            "2026-11-09 11:00Z C2 bull", "2026-11-09 15:00Z C3 exp", "2026-11-09 19:00Z C4 exp", "2026-11-09 23:00Z C2 bear",
            "2026-11-10 03:00Z C3 exp", "2026-11-10 07:00Z C4 cons", "2026-11-10 07:00Z C2 bull", "2026-11-10 11:00Z C3 cons",
            "2026-11-10 15:00Z C2 bear", "2026-11-10 19:00Z C3 exp",
        };
        foreach (var e in expected)
            Check(texts.Any(t => t.StartsWith(e)), "label " + e);
        Check(texts.Count == expected.Length, $"label count {texts.Count} == {expected.Length}");
        Check(bot.Log.Any(l => l.StartsWith("C3 after bull C2 | bull view | window") && l.Contains(" 1 ")), "stats: one window C3 after bull C2");

        Console.WriteLine("---- position sizing via PlaceTrade (reflection)");
        var bot2 = NewBot();
        bot2.RunStart();
        bot2.Server.Time = D(2026, 11, 9, 15, 30);
        bot2.RunTick();
        InvokePlaceTrade(bot2, TtfmDir.Bull, stop: 20001 - 30, target: 20001 + 60);
        var pos = bot2.Positions.Open.SingleOrDefault();
        Check(pos != null && Math.Abs(pos.VolumeInUnits - 16.66) < 1e-9, $"0.5% of 100k over a 30-pt stop = 16.66 units (got {pos?.VolumeInUnits})");
        bot2.Positions.Open.Clear();
        InvokePlaceTrade(bot2, TtfmDir.Bull, stop: 20001 - 5, target: null);
        Check(bot2.Positions.Open.Count == 0 && bot2.Log.Last().Contains("% of the"), "rejects stop where spread > 10% of risk");

        Console.WriteLine("---- FTMO risk guard");
        var bot3 = NewBot();
        bot3.RunStart();
        bot3.Server.Time = D(2026, 11, 9, 15, 30);
        bot3.RunTick();
        bot3.Positions.Open.Add(new Position { Label = "TTFM", SymbolName = "US100.cash" });
        bot3.Account.Equity = 96400;    // below 100k - 3.5% (= 96,500)
        bot3.RunTick();
        Check(bot3.Positions.Open.Count == 0 && bot3.Log.Any(l => l.Contains("bot daily stop")), "daily stop closes positions at -3.5%");
        Check(!bot3.Stopped, "daily stop does not stop the bot");
        bot3.Server.Time = D(2026, 11, 10, 0, 30); // 01:30 CET next day
        bot3.Account.Balance = 96400;
        bot3.RunTick();
        bot3.Account.Equity = 91900;    // below 100k - 8%
        bot3.RunTick();
        Check(bot3.Stopped && bot3.Log.Any(l => l.Contains("max-loss stop")), "max-loss stop stops the bot at -8%");

        Console.WriteLine(_fail == 0 ? "ALL TESTS PASSED" : $"{_fail} TEST(S) FAILED");
        return _fail == 0 ? 0 : 1;
    }

    static TtfmFtmoBot NewBot() => new TtfmFtmoBot
    {
        RiskPercent = 0.5, InitialCapital = 100000, FtmoDailyLossPct = 5, BotDailyStopPct = 3.5, FtmoMaxLossPct = 10, BotMaxLossStopPct = 8,
        DayStartBalanceOverride = 0, MaxTradesPerDay = 1, HtfHours = 4, WindowStartHourEt = 10, WindowEndHourEt = 14, AllowC4 = true,
        MaxSpread = 3, MaxSpreadToStopPct = 10, FlattenHourEt = 15, FlattenMinuteEt = 50, NewsTimesEt = "2026-11-12 08:30; bad", NewsBeforeMin = 5, NewsAfterMin = 5,
        TradingEnabled = false, PositionLabel = "TTFM", DrawHtfBoxes = true, DrawLabels = true, PrintStats = true,
    };

    static void Feed(TtfmFtmoBot bot, (DateTime t, double o, double h, double l, double c) bar)
    {
        bot.Bars.Add(bar.t, bar.o, bar.h, bar.l, bar.c);
        bot.Server.Time = bar.t;
        bot.RunBar();
        bot.RunTick();
    }

    static IEnumerable<(DateTime, double, double, double, double)> BucketBars(DateTime startEt, (double o, double h, double l, double c, bool lowFirst) k)
    {
        double e1 = k.lowFirst ? k.l : k.h, e2 = k.lowFirst ? k.h : k.l;
        var pts = new (int bar, double px)[] { (0, k.o), (12, e1), (32, e2), (47, k.c) };
        double Path(int i)
        {
            for (int s = 0; s < pts.Length - 1; s++)
                if (i >= pts[s].bar && i <= pts[s + 1].bar)
                    return pts[s].px + (pts[s + 1].px - pts[s].px) * (i - pts[s].bar) / (pts[s + 1].bar - pts[s].bar);
            return k.c;
        }
        double prev = k.o;
        for (int i = 0; i < 48; i++)
        {
            double c = Path(i);
            double o = i == 0 ? k.o : prev;
            yield return (TtfmTime.NewYorkToUtc(startEt.AddMinutes(5 * i)), o, Math.Max(o, c), Math.Min(o, c), c);
            prev = c;
        }
    }

    static void InvokePlaceTrade(TtfmFtmoBot bot, TtfmDir dir, double stop, double? target)
    {
        var t = typeof(TtfmFtmoBot);
        var sigType = t.GetNestedType("EntrySignal", BindingFlags.NonPublic);
        var sig = Activator.CreateInstance(sigType, true);
        sigType.GetField("Dir").SetValue(sig, dir);
        sigType.GetField("StopPrice").SetValue(sig, stop);
        sigType.GetField("TargetPrice").SetValue(sig, target);
        sigType.GetField("Reason").SetValue(sig, "test");
        t.GetMethod("PlaceTrade", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(bot, new[] { sig });
    }
}
