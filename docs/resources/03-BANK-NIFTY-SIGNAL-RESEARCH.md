# Bank Nifty Constituents — Research Summary

Compiled: 9 October 2026 | Related discussions: 28 September and 6 October

## Why constituent data belongs in the dashboard

The same index move can be supported by many banks or dominated by a few heavyweights. Jeevan needs to see this quickly while evaluating and monitoring options. Weighted participation and contributor views explain the move; whether they predict the next move is a separate hypothesis.

Use effective-date membership and weights. Do not reuse an earlier discussion's bank count, top-five weight percentage or a repository's static definitions as today's authoritative index data.

## Features and cautions

| Feature | Definition/question | Caution |
| --- | --- | --- |
| Weighted return contribution | weight × return over an aligned window | Approximation, not exact index points |
| Weighted VWAP participation | Fraction of index weight above/below each bank's own traded VWAP | Show missing weight and approximation method |
| Unweighted breadth | Number/fraction of valid banks moving with the index | Does not account for index weights |
| Heavyweight alignment | Largest contributors support or oppose the move | Dominance can mask weak breadth |
| Relative momentum | Same-window changes and normalized mini-charts | One timeframe should not silently substitute for another |
| Volume participation | Current versus a defined comparable history | Day-total volume is not five-minute traded volume |
| Futures agreement | Futures VWAP/opening range versus bank direction | Futures may price information before stocks; leadership cannot be assumed |

Cash-index VWAP is invalid because the index has no traded volume. Use traded futures or bank stocks, and distinguish live trade-derived from candle-derived estimates.

## Dashboard application

Put weight, return, estimated contribution, VWAP distance and quote age beside each bank. Show largest positive/negative contributors and weight coverage. Pin important banks; do not reorder the list on every tick. Highlight a change in participation since an actual or journalled entry.

## Validation

Test whether bank features add value beyond the same futures/opening-range policy. Align observations by availability time. Compare several lookback windows only within the research period and log the trials. Evaluate delayed data and missing constituents. Lead/lag analysis must exclude future information and account for different update rates.

Reference: [Bank Nifty dashboard](https://github.com/chandan016/bnf-dashboard), discussed on 6 October. Its constituent-strength approach is a feature idea; its reported performance is not our evidence.
