# Daily Cumulative Loss

Quantower indicator that tracks the Apex-style intraday Daily Loss Limit from account equity.

The core calculation is intentionally simple:

```text
CurrentEquity = Account.Balance + Account.OpenProfitLoss
DailyPeakBalance = max(CurrentEquity since session reset)
DCL = DailyPeakBalance - CurrentEquity
DLL remaining = MaxDailyLoss - DCL
```

The indicator does not accumulate ticks. Quantower already computes floating PnL; this indicator samples account state and tracks the session peak.

## Install

Build the project in Visual Studio or run:

```powershell
dotnet build
```

The project currently outputs the DLL to the configured Quantower scripts folder:

```text
C:\Quantower\Settings\Scripts\Indicators\DailyCumulativeLoss
```

Restart Quantower or refresh scripts if the indicator does not appear immediately.

## Settings

- `Account`: account to monitor.
- `Max daily loss`: Apex DLL amount.
- `Reset hour Paris`: session reset hour, default `23`.
- `Cache directory`: optional custom CSV cache folder.
- `HUD enabled`: show/hide the top-right HUD.
- `Flashing alert`: flash the HUD in critical zone.
- `Show diagnostics`: show cache/session/debug lines in the HUD.
- `Enable historical recovery`: when no cache exists, try to restore the session peak from closed positions and intratrade historical replay.
- `Enable platform alerts`: send Quantower alerts at 50% and 25% remaining.
- `Show level labels`: show 100% / 50% / 25% / 0 guide labels.

## Cache

Default cache folder:

```text
%LOCALAPPDATA%\DailyCumulativeLoss
```

File format:

```text
DailyCumulativeLoss_Cache_{AccountName}_{YYYYMMDD}.csv
```

Rows are appended when the session peak increases and when a position closes. If the last row is corrupted, the reader scans backward until it finds the last valid row.

## Historical Recovery

When no cache exists for the current session, the indicator now uses a best-effort recovery engine:

1. Reads closed positions for the selected account since the Paris reset.
2. Rebuilds the realized balance path from closed PnL.
3. For each closed position, tries to request Quantower historical data for the trade interval, first `TICK1`, then `MIN1`.
4. Uses the most favorable intratrade price to estimate the maximum floating equity reached during the trade.
5. Falls back to the realized-only peak if symbol history, trade fields, or tick/minute data are unavailable.

The replay runs in the background and is capped internally, so recovery never runs inside `OnUpdate()` and does not grow without bound. Enable `Show diagnostics` to see the recovery mode and replay counts in the HUD.

## Validation Checklist

1. Attach the indicator to a chart and select the target account.
2. Confirm the panel shows `DLL rem`, `DCL`, `Peak`, and `Equity`.
3. Enable `Show diagnostics` and verify the session date and cache status.
4. Open a demo position and confirm the line moves with floating PnL.
5. Close the position and verify a CSV row is written.
6. Remove and re-add the indicator; the daily peak should restore from cache.
7. Remove the CSV cache on a demo account with closed trades, reload the indicator, and check `Recovery` / `Replay` diagnostics.
8. Test below 50% and 25% remaining only on a demo/sim account to verify alerts.

## Known Limits

- Historical replay depends on Quantower exposing closed-position fields, symbol history, tick value, and tick size for the connected broker/datafeed.
- If tick history is unavailable, the engine attempts 1-minute history; if both fail, it falls back to the conservative realized peak from closed positions.
- Multi-currency account conversion depends on the values exposed by Quantower and the connected broker/datafeed.
