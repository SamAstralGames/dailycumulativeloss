# Technical Architecture Specification: Daily Cumulative Loss Indicator
**Target Platform:** Quantower (C# Algo API)
**Indicator Name:** Daily Cumulative Loss (DCL)
**Indicator Type:** Separate Window Indicator (`SeparateWindow = true`)
**Objective:** Track, persist, and visualize the Apex Trader Funding Intraday Daily Loss Limit (DLL) with full fault tolerance and disaster recovery.

---

## 1. Core Mathematical Logic & Business Rules
The indicator tracks the cumulative loss/drawdown between the highest valuation reached by the account during the session (including open/unrealized PnL) and the current valuation.

* **Current Equity ($E_t$):** `Account.Balance + Account.OpenProfitLoss`
* **Daily Peak Balance ($P_{day}$):** $\max(E_t)$ recorded since the session reset (23:00 Paris Time / 17:00 EST).
* **Daily Cumulative Loss ($DCL_t$):** $P_{day} - E_t$
* **Remaining Daily Limit ($DLL_{rem}$):** `MaxDailyLoss` - $DCL_t$
* **Liquidation Threshold Line:** $P_{day}$ - `MaxDailyLoss`

---

## 2. System Architecture & Fault Tolerance (Disaster Recovery)

To handle application crashes, network disconnections, or restarts mid-session, the indicator implements a hybrid persistence layer.

[ Indicator Initialization (OnInit) ]
                    │
                    ▼
     Does local CSV cache exist for Today?
      ├── YES ──► Read last row ──► Restore [Daily Peak Balance]
      │
      └── NO  ──► Execute Historical Fallback Engine
                    │
                    ├─► Fetch closed trades since 23:00
                    ├─► Fetch historical 1-min/tick bars for trade intervals
                    ├─► Simulate Max Unrealized Equity ($P_{day}$)
                    └─► Initialize [Daily Peak Balance]

### A. Local Cache Engine (CSV)
* **File Naming:** `DailyCumulativeLoss_Cache_{AccountName}_{YYYYMMDD}.csv`
* **Trigger:** Append a new row asynchronously whenever $P_{day}$ increments or a trade closes.
* **Schema:** `Timestamp_UTC;Timestamp_Local;Balance;OpenPnL;CurrentEquity;DailyPeakBalance;DailyCumulativeLoss`

### B. Historical Fallback Engine (No Cache Recovery)
If no CSV cache is found upon `OnInit()`:
1.  Query `Core.Instance.HistoricalData` to fetch all closed trades since 23:00 of the previous day.
2.  For each trade, request historical market bars (1-minute or ticks) matching the interval `[Trade.OpenTime, Trade.CloseTime]`.
3.  Replay the price action against the trade's entry price and position size to reconstruct the maximum floating equity reached *inside* the trade.
4.  Set the initial value of $P_{day}$ based on the highest point found during the simulation.

---

## 3. Real-Time Processing (`OnUpdate`)
The real-time loop must be highly optimized ($O(1)$ complexity) to prevent UI thread blocking during high-volatility events (e.g., S&P 500 news drops).

1.  Calculate $E_t = \text{Balance} + \text{OpenPnL}$.
2.  If $E_t > P_{day}$, set $P_{day} = E_t$ and trigger asynchronous append to CSV.
3.  Calculate $DCL_t = P_{day} - E_t$.
4.  Calculate $DLL_{rem} = \text{MaxDailyLoss} - DCL_t$.
5.  Invalidate the panel chart to force a redraw.

---

## 4. Visual Rendering Specifications (`OnPaintChart`)

The indicator must render in a separate sub-window panel below the main price chart. The X-axis (Time) must be perfectly synchronized with the main chart's timeline.

### A. Curves to Plot
1.  **Equity Line ($E_t$):** Continuous line rendering the historical and real-time equity path. 
    * *Color encoding:* Green segment if $E_t \ge E_{t-1}$, Red segment if $E_t < E_{t-1}$ (visualizing intra-trade drawdowns / active $DCL_t$).
2.  **Daily Peak Line ($P_{day}$):** Step-line (staircase style) showing the progression of the session's peak. Color: **Solid Green/Lime**.
3.  **Liquidation Barrier:** Horizontal line calculating the absolute floor ($P_{day} - \text{MaxDailyLoss}$). Color: **Crimson Red**.

### B. Scaled Vertical Y-Axis (Right-hand side)
* The Y-axis must display relative dollar values remaining before liquidation ($DLL_{rem}$).
* **Level 0$:** Bottom line (Dead Zone / Liquidation).
* **Dynamic HUD Text:** Overlay a large, high-contrast text block in the upper right corner of the sub-panel showing current $DLL_{rem}$ and $DCL_t$ dynamically colored:
    * $DLL_{rem} > 50\%$ of Max: **Green**
    * $25\% \le DLL_{rem} \le 50\%$: **Orange**
    * $DLL_{rem} < 25\%$: **Flashing Red**

---

## 5. Developer Code Constraints
* Use `StreamWriter` with proper resource locking (`lock` statement) or non-blocking I/O routines for CSV management.
* Ensure full compatibility with `TradingPlatform.BusinessLayer` handles (`IAccount`, `HistoricalData`, `PaintChartArgs`).
* Implement clean error catching (`try-catch` blocks) on file system operations and historical data fetch calls to avoid crashing the Quantower platform.

## 6. Algorithmic Core Warning & Implementation Rules (CRITICAL)

### A. Anti-Accumulation Rule (No Tick-Incrementing)
* **DO NOT** listen to individual tick directions to manually increment or accumulate down-ticks (e.g., `DailyLoss += tick_delta`). This micro-accounting approach causes double-counting, memory bloat, and severe calculation drift due to market noise and bidirectional price churning.
* **DO NOT** track historical ticks inside an active trade in real-time. Quantower already updates the floating PnL natively.

### B. Correct State-Reduction Methodology
The Daily Cumulative Loss ($DCL_t$) must strictly be calculated as a **State-Subtraction** at any given instant $t$. Quantower's engine handles the continuous evaluation of `Account.OpenProfitLoss` internally. The indicator only needs to sample this state and track its historical peak.

The structural logic inside `OnUpdate()` must strictly follow this exact mathematical workflow:

```csharp
// 1. Snapshot current account state (O(1) complexity)
double currentEquity = selectedAccount.Balance + selectedAccount.OpenProfitLoss;

// 2. Track the running maximum of the session (Peak Balance)
if (currentEquity > dailyPeakBalance)
{
    dailyPeakBalance = currentEquity;
    
    // Asynchronous non-blocking write to CSV cache
    TriggerCacheAppendAsync(currentEquity, dailyPeakBalance); 
}

// 3. Evaluate the exact real-time gap (Daily Cumulative Loss)
double currentDCL = dailyPeakBalance - currentEquity;

// 4. Compute safe room before liquidation
double remainingDLL = MaxDailyLoss - currentDCL;
