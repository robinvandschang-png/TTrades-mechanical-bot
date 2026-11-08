// Minimal stand-in for the cAlgo.API surface used by the bot, so the bot compiles and
// its logic can be exercised offline. NOT the real API — signatures mirror the real ones
// as used in the bot.
using System;
using System.Collections.Generic;
using System.Linq;

namespace cAlgo.API.Internals { public interface IServer { DateTime Time { get; } } }

namespace cAlgo.API
{
    public enum AccessRights { None, FileSystem, Internet, Registry, FullAccess }
    public static class TimeZones { public const string UTC = "UTC"; }

    [AttributeUsage(AttributeTargets.Class)]
    public class RobotAttribute : Attribute
    {
        public string TimeZone { get; set; }
        public AccessRights AccessRights { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ParameterAttribute : Attribute
    {
        public ParameterAttribute(string name) { }
        public string Group { get; set; }
        public object DefaultValue { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public double Step { get; set; }
    }

    public enum RoundingMode { ToNearest, Down, Up }
    public enum TradeType { Buy, Sell }
    public enum ErrorCode { TechnicalError, BadVolume, NoMoney }
    public enum PositionCloseReason { Closed, StopLoss, TakeProfit, StopOut }
    public enum LineStyle { Solid, Dots, DotsRare, DotsVeryRare, Lines, LinesDots }
    public enum VerticalAlignment { Top, Center, Bottom }

    public sealed class TimeFrame
    {
        private readonly string _n; private TimeFrame(string n) { _n = n; }
        public static readonly TimeFrame Minute = new TimeFrame("m1"), Minute2 = new TimeFrame("m2"), Minute3 = new TimeFrame("m3"),
            Minute5 = new TimeFrame("m5"), Minute10 = new TimeFrame("m10"), Minute15 = new TimeFrame("m15"),
            Minute20 = new TimeFrame("m20"), Minute30 = new TimeFrame("m30"), Hour = new TimeFrame("h1");
        public override string ToString() => _n;
    }

    public struct Color
    {
        public int A, R, G, B;
        public static Color FromArgb(int a, int r, int g, int b) => new Color { A = a, R = r, G = g, B = b };
        public static Color LimeGreen => FromArgb(255, 50, 205, 50);
        public static Color Red => FromArgb(255, 255, 0, 0);
        public static Color Orange => FromArgb(255, 255, 165, 0);
    }

    public class ChartRectangle { public bool IsFilled { get; set; } }
    public class ChartText { public string Text; public VerticalAlignment VerticalAlignment { get; set; } }
    public class ChartTrendLine { }

    public class Chart
    {
        public readonly List<string> Texts = new List<string>();
        public ChartRectangle DrawRectangle(string name, DateTime t1, double y1, DateTime t2, double y2, Color c, int thickness, LineStyle ls) => new ChartRectangle();
        public ChartText DrawText(string name, string text, DateTime t, double y, Color c) { Texts.Add($"{t:yyyy-MM-dd HH:mm}Z {text} @ {y}"); return new ChartText { Text = text }; }
        public ChartTrendLine DrawTrendLine(string name, DateTime t1, double y1, DateTime t2, double y2, Color c, int thickness, LineStyle ls) => new ChartTrendLine();
    }

    public class TimeSeries { public readonly List<DateTime> L = new List<DateTime>(); public DateTime this[int i] => L[i]; }
    public class DataSeries { public readonly List<double> L = new List<double>(); public double this[int i] => L[i]; }

    public class Bars
    {
        public TimeSeries OpenTimes { get; } = new TimeSeries();
        public DataSeries OpenPrices { get; } = new DataSeries();
        public DataSeries HighPrices { get; } = new DataSeries();
        public DataSeries LowPrices { get; } = new DataSeries();
        public DataSeries ClosePrices { get; } = new DataSeries();
        public int Count => OpenTimes.L.Count;
        public void Add(DateTime t, double o, double h, double l, double c) { OpenTimes.L.Add(t); OpenPrices.L.Add(o); HighPrices.L.Add(h); LowPrices.L.Add(l); ClosePrices.L.Add(c); }
    }

    public class Position
    {
        public string Label { get; set; } public string SymbolName { get; set; } public TradeType TradeType { get; set; }
        public double VolumeInUnits { get; set; } public double NetProfit { get; set; } public double EntryPrice { get; set; }
    }

    public class PositionClosedEventArgs { public Position Position { get; set; } public PositionCloseReason Reason { get; set; } }

    public class Positions
    {
        public readonly List<Position> Open = new List<Position>();
        public event Action<PositionClosedEventArgs> Closed;
        public Position[] FindAll(string label, string symbolName) => Open.Where(p => p.Label == label && p.SymbolName == symbolName).ToArray();
        public void RaiseClosed(Position p) { Open.Remove(p); Closed?.Invoke(new PositionClosedEventArgs { Position = p }); }
    }

    public class TradeResult { public bool IsSuccessful { get; set; } public Position Position { get; set; } public ErrorCode? Error { get; set; } }

    public class Symbol
    {
        public double Ask { get; set; } public double Bid { get; set; } public double Spread => Ask - Bid;
        public double PipSize { get; set; } = 1.0; public double PipValue { get; set; } = 1.0;
        public double VolumeInUnitsMin { get; set; } = 0.01; public double VolumeInUnitsMax { get; set; } = 1000; public double VolumeInUnitsStep { get; set; } = 0.01;
        public double NormalizeVolumeInUnits(double v, RoundingMode m) => Math.Floor(v / VolumeInUnitsStep) * VolumeInUnitsStep;
    }

    public interface IAccount { double Balance { get; } double Equity { get; } }
    public class Account : IAccount { public double Balance { get; set; } public double Equity { get; set; } }
    public class Server : Internals.IServer { public DateTime Time { get; set; } }

    public abstract class Robot
    {
        public Bars Bars { get; } = new Bars();
        public Server Server { get; } = new Server();
        public Account Account { get; } = new Account { Balance = 100000, Equity = 100000 };
        public Positions Positions { get; } = new Positions();
        public Symbol Symbol { get; } = new Symbol { Ask = 20001, Bid = 20000 };
        public string SymbolName => "US100.cash";
        public TimeFrame TimeFrame { get; set; } = TimeFrame.Minute5;
        public Chart Chart { get; } = new Chart();
        public bool IsBacktesting { get; set; } = true;
        public readonly List<string> Log = new List<string>();
        public bool Stopped;

        protected virtual void OnStart() { } protected virtual void OnBar() { } protected virtual void OnTick() { } protected virtual void OnStop() { }
        public void Print(string s) { Log.Add(s); Console.WriteLine(s); }
        public void Stop() { Stopped = true; }
        public TradeResult ExecuteMarketOrder(TradeType tt, string symbol, double volume, string label, double? slPips, double? tpPips)
        {
            var p = new Position { Label = label, SymbolName = symbol, TradeType = tt, VolumeInUnits = volume, EntryPrice = tt == TradeType.Buy ? Symbol.Ask : Symbol.Bid };
            Positions.Open.Add(p);
            return new TradeResult { IsSuccessful = true, Position = p };
        }
        public TradeResult ClosePosition(Position p) { Positions.RaiseClosed(p); return new TradeResult { IsSuccessful = true }; }

        // test drivers
        public void RunStart() => OnStart();
        public void RunBar() => OnBar();
        public void RunTick() => OnTick();
        public void RunStop() => OnStop();
    }
}
