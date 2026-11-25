"""Reproduce the November 2026 Live filter audit from read-only localhost Seq queries."""

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import json
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import urlopen


def stamp(timestamp):
    return datetime.fromisoformat(timestamp).astimezone(timezone.utc)


def query_rows(columns, start, end, template, limit=""):
    query = (
        f"select {columns} from stream where "
        f"@Timestamp >= DateTime('{start.isoformat()}') and @Timestamp < DateTime('{end.isoformat()}') "
        f"and @MessageTemplate like '{template}' {limit}"
    )
    with urlopen("http://127.0.0.1:5341/api/data?" + urlencode({"q": query}), timeout=30) as result:
        return json.load(result, parse_float=Decimal)["Rows"]


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=Path, default=Path("docs/strategy-funnel-validation.json"))
args = parser.parse_args()
start, end = stamp("2026-11-14T05:13:06Z"), stamp("2026-11-21T05:13:06Z")
evaluation_template = "LIVE SIGNAL EVALUATION%"
candle_template = "Received closed 5m candle%"
evaluations, candles = [], []
for day in range(7):
    since = start + timedelta(days=day)
    until = since + timedelta(days=1)
    evaluations.extend(query_rows(
        "Symbol, Time, Evaluation.Regime, Evaluation.Reason, Direction, Evaluation.Adx, "
        "Evaluation.AtrPercent, Evaluation.Ema20, Evaluation.Ema50, Evaluation.Ema200, Evaluation.Rsi15m, Evaluation.Structure",
        since, until, evaluation_template, "limit 10000"
    ))
    candles.extend(query_rows("Symbol, OpenTime, CloseTime, Open, High, Low, Close, Volume",
                              since, until, candle_template, "limit 10000"))
for rows, template in ((evaluations, evaluation_template), (candles, candle_template)):
    [[expected]] = query_rows("count(*)", start, end, template)
    if not rows:
        raise RuntimeError(f"No events remain for {template}. Keeping the previous report.")
    if len(rows) != expected:
        raise RuntimeError(f"Truncated query for {template}: got {len(rows)}, expected {expected}.")

evaluations_by_key = {}
cross_tab = Counter()
details = defaultdict(Counter)
counts_by_symbol = defaultdict(Counter)
reason_details_by_key = {}
confirmed_signals = []
for symbol, close_at, regime, reason, direction, adx, atr, fast, slow, primary, rsi, structure in evaluations:
    close_at = stamp(close_at)
    counts_by_symbol[symbol]["evaluations"] += 1
    key = (symbol, close_at)
    if key in evaluations_by_key and evaluations_by_key[key] != (regime, reason, direction):
        raise RuntimeError(f"Conflicting evaluations for {key}.")
    evaluations_by_key[key] = (regime, reason, direction)
    if direction:
        counts_by_symbol[symbol]["technicalSignals"] += 1
        confirmed_signals.append({"symbol": symbol, "closeAtUtc": close_at.isoformat(), "direction": direction, "regime": regime})
        continue
    cross_tab[symbol, reason, regime] += 1
    counts_by_symbol[symbol]["rejected"] += 1
    if reason == "market regime does not permit a trend entry":
        if atr > 3 or atr < Decimal("0.1"):
            subtype = "atr_high" if atr > 3 else "atr_low"
        else:
            ema_stack = fast > slow > primary or fast < slow < primary
            if adx < 20:
                subtype = "adx_below_floor_ema_stack_aligned_price_unknown" if ema_stack else "adx_and_ema_stack"
            else:
                subtype = "adx_passed_ema_stack_failed_price_unknown" if not ema_stack else "ema_close_condition_by_elimination"
        reason_details_by_key[key] = ("1hFirstReject", subtype)
    if reason == "30m trend or rolling structure conflicts with 1h":
        expected = "HH/HL" if regime == "TrendingUp" else "LH/LL"
        subtype = "structure_failed_ema_unknown" if structure != expected else "structure_passed_ema_or_price_failed"
        reason_details_by_key[key] = ("30mFirstReject", subtype)
    if reason == "15m setup lacks momentum or entry is extended":
        rsi_wrong_side = (regime == "TrendingUp" and rsi < 50) or (regime == "TrendingDown" and rsi > 50)
        reason_details_by_key[key] = (
            "15mFirstReject", "rsi_failed_other_gates_unknown" if rsi_wrong_side else "rsi_passed_other_gate_failed"
        )
    if key in reason_details_by_key:
        stage, subtype = reason_details_by_key[key]
        details[stage][subtype] += 1

candles_by_symbol = defaultdict(dict)
for symbol, open_at, close_at, o, h, low, c, volume in candles:
    counts_by_symbol[symbol]["marketCandleEvents"] += 1
    key = stamp(close_at)
    row = (stamp(open_at), o, h, low, c, volume)
    if key in candles_by_symbol[symbol] and candles_by_symbol[symbol][key] != row:
        raise RuntimeError(f"Conflicting OHLCV versions for {symbol} {key}.")
    candles_by_symbol[symbol][key] = row

bar = timedelta(minutes=5)
candidate_bars, episode_anchors, eligible_by_symbol = Counter(), Counter(), Counter()
episode_cross_tab = Counter()
episode_details = defaultdict(Counter)
evaluation_missing_candle = sum(close_at not in candles_by_symbol[symbol] for symbol, close_at in evaluations_by_key)
for symbol, rows in candles_by_symbol.items():
    previous_candidate = None
    for close_at, (open_at, o, h, low, c, volume) in sorted(rows.items()):
        # Only use observed continuous bars. Never bridge missing market data with a synthetic candle.
        previous = [rows.get(close_at - n * bar) for n in range(1, 13)]
        if any(p is None for p in previous) or open_at + bar != close_at:
            continue
        eligible_by_symbol[symbol] += 1
        direction = "Long" if c > o and c > max(p[2] for p in previous) else (
            "Short" if c < o and c < min(p[3] for p in previous) else None
        )
        if not direction:
            continue
        regime, reason, signal = evaluations_by_key.get(
            (symbol, close_at), ("Unknown", "no matching Live evaluation", None)
        )
        if signal:
            reason = "technical signal confirmed on anchor"
        candidate_bars[reason] += 1
        if previous_candidate != (close_at - bar, direction):
            episode_anchors[reason] += 1
            episode_cross_tab[symbol, reason, regime, direction] += 1
            if (symbol, close_at) in reason_details_by_key:
                stage, subtype = reason_details_by_key[symbol, close_at]
                episode_details[stage][subtype] += 1
        previous_candidate = (close_at, direction)

by_reason = Counter()
by_regime = Counter()
for (_, reason, regime), count in cross_tab.items():
    by_reason[reason] += count
    by_regime[regime] += count
assert sum(by_reason.values()) + len(confirmed_signals) == len(evaluations)
assert sum(details["1hFirstReject"].values()) == by_reason["market regime does not permit a trend entry"]
assert sum(details["30mFirstReject"].values()) == by_reason["30m trend or rolling structure conflicts with 1h"]
assert sum(details["15mFirstReject"].values()) == by_reason["15m setup lacks momentum or entry is extended"]

result = {
    "source": "Seq localhost /api/data, application logs from Live strategy evaluation and market data",
    "eventTimeWindowUtc": {"fromInclusive": start.isoformat(), "toExclusive": end.isoformat()},
    "entryTimeframe": "5m",
    "evaluationEvents": len(evaluations),
    "distinctEvaluationCandles": len(evaluations_by_key),
    "rejectedEvaluationEvents": sum(by_reason.values()),
    "confirmedSignals": confirmed_signals,
    "rejectionByReason": dict(by_reason.most_common()),
    "rejectionByRegime": dict(by_regime.most_common()),
    "rejectionBySymbolRegimeReason": [
        {"symbol": symbol, "timeframe": "5m", "regime": regime, "reason": reason, "count": count}
        for (symbol, reason, regime), count in sorted(cross_tab.items())
    ],
    "bySymbol": dict(sorted(counts_by_symbol.items())),
    "observedReasonDetails": details,
    "macdDecisionEvents": query_rows("count(*)", start, end, "%MACD %")[0][0],
    "marketCandleEvents": len(candles),
    "distinctMarketCandles": sum(map(len, candles_by_symbol.values())),
    "evaluationCandlesMissingMarketLog": evaluation_missing_candle,
    "potentialBreakoutProxy": {
        "definition": "Anchors based on directional close outside the preceding 12 contiguous logged 5m bars. Consecutive same-direction anchors collapse into one episode, attributed to the first anchor.",
        "eligibleCandles": sum(eligible_by_symbol.values()),
        "eligibleCandlesBySymbol": dict(sorted(eligible_by_symbol.items())),
        "priceBreakoutBars": sum(candidate_bars.values()),
        "priceBreakoutBarsByLiveFirstReason": dict(candidate_bars.most_common()),
        "episodes": sum(episode_anchors.values()),
        "episodesByLiveFirstReasonAtAnchor": dict(episode_anchors.most_common()),
        "observedReasonDetailsAtEpisodeAnchor": episode_details,
        "episodesBySymbolRegimeReasonDirection": [
            {"symbol": symbol, "timeframe": "5m", "regime": regime, "direction": direction, "reason": reason, "count": count}
            for (symbol, reason, regime, direction), count in sorted(episode_cross_tab.items())
        ],
    },
}
args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
print(f"Wrote {args.output}: {len(evaluations)} evaluations, {sum(by_reason.values())} rejects, "
      f"{len(confirmed_signals)} signals and {sum(episode_anchors.values())} breakout proxy episodes.")
