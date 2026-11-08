# TTrades Mechanical Bot

A cTrader cBot (C#) that trades a mechanical version of the **TTrades Fractal Model (TTFM)** on **FTMO's US100** (Nasdaq CFD), built to respect the FTMO 2-Step challenge rules.

> **Status: step 1 of 2.** The bot detects TTFM setups, enforces FTMO risk limits, sizes positions and reports base-rate statistics. **It does not enter trades yet** — the entry logic (`TryFindEntry`) is a stub until step 2. `Trading enabled` is off by default.

---

## Contents

- [Why this exists](#why-this-exists)
- [The model, as implemented](#the-model-as-implemented)
- [Instrument choice](#instrument-choice)
- [FTMO rules enforced](#ftmo-rules-enforced)
- [Repository layout](#repository-layout)
- [Getting started in cTrader](#getting-started-in-ctrader)
- [Parameters](#parameters)
- [Reading the backtest statistics](#reading-the-backtest-statistics)
- [Offline tests](#offline-tests)
- [Roadmap](#roadmap)
- [Known limitations](#known-limitations)
- [Disclaimer](#disclaimer)

---

## Why this exists

Discretionary ICT-style trading depends on judgment calls that are hard to repeat. TTFM is closer to mechanical: it is built on higher-timeframe (HTF) candle closures, sweeps of the previous candle's high/low, and a lower-timeframe (LTF) change in state of delivery (CISD) — all of which can be defined in code.

The project follows one rule: **test the premise before building the trade.** Step 1 measures whether a TTFM "C2" candle actually changes what the next candle does on US100. Only if it does is step 2 (entries) worth building.

## The model, as implemented

**HTF candles are built in code**, anchored to **18:00 ET** (the CME session open), not taken from the platform's H4 bars. With 4H candles the buckets are:

| 18–22 | 22–02 | 02–06 | 06–10 | **10–14** | 14–18 | (ET) |
|---|---|---|---|---|---|---|

So the 10:00–14:00 ET trading window is always exactly one candle. US and EU daylight-saving rules are hard-coded, so the bot does not depend on the operating system's time-zone database.

**Candle roles**

| Term | Definition in this bot |
|---|---|
| **C1** | The candle before C2. |
| **C2 (bull)** | Trades **below C1's low** and **closes back above it**. |
| **C2 (bear)** | Trades **above C1's high** and **closes back below it**. |
| Outside bar | Sweeps both sides of C1 → ambiguous, ignored. |
| **C3** | The candle after C2. Outcome vs C2: **failed** = traded through the C2 extreme; **expanded** = closed beyond the opposite C2 extreme; **consolidated** = neither. |
| **C4** | The candle after an *expanded* C3 (continuation). Evaluated the same way against C3. |

A fresh C2 takes priority over a C4 continuation when both point at the same next candle.

**Bias for the trading window:** if the 06–10 candle is a C2 (or an expanded C3), the 10–14 candle carries a bias (C3 or C4). Step 2 will look for an entry only inside such a candle.

**Chart markers** (visible in visual backtests): HTF candle boxes (window candles highlighted), `C2 bull` / `C2 bear` labels, the protected level (dotted) and the C2 midpoint/EQ (dashed), and `C3/C4 exp | cons | fail` outcome labels.

## Instrument choice

**FTMO US100 on cTrader**, with US500 and XAUUSD as robustness checks later.

- **Candles that match the model:** the 18:00 ET anchor makes 10:00–14:00 ET one real 4H candle. Crypto's 4H candles are UTC-aligned and don't line up with the NY session; crypto also carries a percentage commission on FTMO.
- **Costs:** FTMO charges no commission on indices; the cost is the spread (measured at roughly 1.7 points on average for US100, wider in the European session). Intraday edges are often about the size of trading costs, so the bot rejects any trade where **spread exceeds 10% of the stop distance** and trades only in NY hours.
- **Data:** signals are computed on the FTMO feed itself. The CFD tracks the cash index, so levels differ from NQ futures — a sweep on NQ is not necessarily a sweep on US100.

## FTMO rules enforced

FTMO 2-Step: 10% then 5% profit target, **5% max daily loss** (equity-based, includes floating P&L, recalculated at midnight CE(S)T from that moment's balance), **10% static max loss**, minimum 4 trading days, no time limit.

| Rule | FTMO limit | Bot behaviour (default) |
|---|---|---|
| Max daily loss | 5% of initial capital below the midnight CE(S)T balance | Closes the bot's positions and blocks new trades at **3.5%**, until the next CE(S)T day |
| Max loss | 10% below initial capital (static) | Closes everything and **stops the bot** at **8%** |
| Risk per trade | — | **0.5%** of balance, capped by the room left before either stop |
| Hyperactivity | > 2,000 server requests/day is forbidden | Never modifies orders per tick |
| News (funded account) | Restricted around high-impact releases | Manual blackout list, ±5 min by default |
| Overnight / weekend | — | Flattens at **15:50 ET** daily |

Sizing rule of thumb: keep `risk per trade ≤ 10% ÷ (worst backtested losing streak × 1.5)`. A worst streak of 12 → at most ~0.55% per trade.

## Repository layout

```
src/TtfmFtmoBot.cs                  the cBot (single file, paste into cTrader)
tests/CAlgoStub.cs                  minimal stand-in for cTrader's cAlgo.API (offline only)
tests/Program.cs                    unit + end-to-end tests
tests/TtfmFtmoBot.Tests.csproj      test project (links ../src/TtfmFtmoBot.cs)
tests/nuget.config                  no package sources: builds fully offline
```

## Getting started in cTrader

1. Open cTrader with your FTMO login (Free Trial first — it mirrors the account's conditions).
2. In the **Algo** section, create a new **cBot**, replace the template with `src/TtfmFtmoBot.cs`, and **Build**.
3. Open a **US100** chart on **15m** (TTrades' pairing for 4H; 5m also works).
4. Backtest with **tick data**, **visual mode** on to see the markers. Use at least two years.
5. When the backtest ends, copy the statistics table from the **Log** tab.

Leave **Trading enabled = false** until step 2 is implemented and validated.

> If the build reports an API mismatch, see [Known limitations](#known-limitations).

## Parameters

| Group | Parameter | Default | Notes |
|---|---|---|---|
| Risk | Risk per trade (% of balance) | 0.5 | |
| Risk | Initial capital | 100,000 | FTMO account size |
| Risk | FTMO max daily loss (%) | 5.0 | Reference only |
| Risk | Bot daily stop (%) | 3.5 | Must be below FTMO's |
| Risk | FTMO max loss (%) | 10.0 | Reference only |
| Risk | Bot max-loss stop (%) | 8.0 | Must be below FTMO's |
| Risk | Balance at last midnight CE(S)T | 0 | Set when restarting mid-day after losses; 0 = current balance |
| Risk | Max trades per day | 1 | |
| Model | HTF candle (hours) | 4 | 1, 2, 3, 4 or 6 |
| Model | Trade window start / end (ET hour) | 10 / 14 | Must sit on HTF boundaries |
| Model | Allow C4 continuation | true | |
| Filters | Max spread (price units) | 3.0 | |
| Filters | Max spread as % of stop | 10 | |
| Filters | Flatten at (ET) | 15:50 | |
| Filters | News times ET | empty | `yyyy-MM-dd HH:mm; ...`, e.g. `2026-12-09 14:00; 2026-12-11 08:30` |
| Filters | News blackout before / after (min) | 5 / 5 | |
| Execution | Trading enabled | false | Step 2 |
| Execution | Position label | TTFM | |
| Visuals | Draw HTF candles / labels, print stats | true | |

## Reading the backtest statistics

On stop the bot prints a table comparing every HTF candle with the candle before it:

| Column | Meaning (from the bias' point of view) |
|---|---|
| `N` | Number of candles |
| `close-dir` | Candle closed in the bias direction |
| `fav-1st` | Took the bias-side extreme of the previous candle first (e.g. previous high for a bull bias) |
| `adv-1st` | Took the opposite extreme first |
| `same-bar` | Both taken in the same LTF bar (order unknown) |
| `neither` | Inside candle |
| `expanded` | Closed beyond the target extreme |
| `failed` | Traded through the protected extreme |

Rows are split by context (`ALL candles`, `C3 after bull C2`, `C4 after bear C3`, …), direction view, and `all hours` vs `window` (10–14 ET).

**The go / no-go test:** compare `C3 after bull C2 | bull view | window` with `ALL candles | bull view | window` (and the bear equivalents). If `fav-1st`, `expanded` and `failed` are not meaningfully better than the baseline — with enough `N` to matter — the C2 condition adds no information on US100 and step 2 should not be built on it.

## Offline tests

The repo builds and tests without cTrader or internet access:

```bash
cd tests
dotnet run
```

55 checks cover: US/EU daylight-saving transitions, 4H bucket boundaries, C2/C3/C4 classification, first-touch ordering, an end-to-end run on synthetic 5-minute data, position sizing, the spread-to-stop filter, and both FTMO loss stops.

`tests/CAlgoStub.cs` is **not** the real cTrader API — it mirrors only the members the bot uses so the logic can be exercised offline.

## Roadmap

- [x] **Step 1** — HTF builder, C2/C3/C4 detection, FTMO risk guard, sizing, filters, base-rate statistics
- [ ] Run the step-1 backtest on US100 (≥ 2 years) and decide go / no-go
- [ ] **Step 2** — entry logic in `TryFindEntry`. Open decisions:
  - Trigger: LTF CISD in the bias direction; require price in C2 discount/premium first?
  - Stop: beyond the LTF swing that formed the CISD, or beyond the C2 extreme?
  - Target: C2 opposite extreme, fixed 2R, or hold to the HTF candle close?
  - Time exit: 14:00 ET (end of the window candle) or 15:50 ET?
- [ ] Validate against a **random-direction benchmark** (same bracket, random side) — only the difference is the signal
- [ ] Robustness on US500 and XAUUSD
- [ ] Forward test on the FTMO Free Trial
- [ ] FTMO challenge

## Known limitations

- **Compiled against a stub, not cTrader's library.** The cTrader API package could not be downloaded in the build environment. The first build inside cTrader may flag a small signature mismatch (e.g. a drawing overload) — fix or open an issue with the error.
- **News list is manual.** cTrader Automate has no built-in economic calendar; add FOMC, CPI, NFP etc. yourself. FOMC releases at 14:00 ET — the end of the trading window.
- **US DST rule** is the post-2007 one; backtests before 2007 would be misaligned.
- **Sizing assumes a USD account** (`Symbol.PipValue` is fixed when the bot starts).
- **Short stops** placed on bid-based bars should include a spread buffer — to be handled in step 2.
- **T-spot** is not implemented: TTrades' exact definition is proprietary, so any version here would be an approximation and is left as a step-2 decision.

## Disclaimer

Educational project, not financial advice. Backtests and simulated results do not guarantee future performance. Trading leveraged products carries a high risk of loss. TTrades and FTMO are not affiliated with this project.
