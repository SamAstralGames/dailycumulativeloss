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
- `Enable historical recovery`: restore a conservative realized peak from closed positions when no cache exists.
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

## Validation Checklist

1. Attach the indicator to a chart and select the target account.
2. Confirm the panel shows `DLL rem`, `DCL`, `Peak`, and `Equity`.
3. Enable `Show diagnostics` and verify the session date and cache status.
4. Open a demo position and confirm the line moves with floating PnL.
5. Close the position and verify a CSV row is written.
6. Remove and re-add the indicator; the daily peak should restore from cache.
7. Test below 50% and 25% remaining only on a demo/sim account to verify alerts.

## Known Limits

- Full intratrade historical replay from bars/ticks is not implemented yet.
- The current fallback without cache reconstructs only a conservative realized peak from closed positions.
- Multi-currency account conversion depends on the values exposed by Quantower and the connected broker/datafeed.
