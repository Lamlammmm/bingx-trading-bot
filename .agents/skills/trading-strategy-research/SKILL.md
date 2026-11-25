---
name: trading-strategy-research
description: Research falsifiable strategy hypotheses for this bot across trend, reversion, breakouts, momentum, regimes, adaptive indicators, scoring, and exits using diagnostics and primary sources.
---

# Trading strategy research

Start with `docs/strategy-funnel-validation.md` and `docs/trading-strategy-research.md`. State what observed failure or missed opportunity the hypothesis addresses. Separate repeated candle evaluations from distinct entry candidates, accepted signals and fills. Attribute rejection only to the stages actually observed. Distinguish a price-pattern proxy and replay evidence from observed live setup decisions.

Define the hypothesis before choosing parameters. For trend following, reversion, breakout/retest or momentum, specify market, regime, horizon, confirmation and setup invalidation. For adaptive features or multi-timeframe rules, specify when each input becomes available. For scoring, compare against the existing hard gates and document how the score would be validated. For dynamic SL or TP, specify loss boundaries, trailing behavior and partial-fill assumptions. Isolate individual changes so a useful gate is not removed simply because it occurs with another.

Consult primary studies or provider specifications. [Moskowitz, Ooi and Pedersen](https://doi.org/10.1016/j.jfineco.2011.11.003) study higher-horizon time-series momentum. [Lo and MacKinlay](https://www.nber.org/papers/w2977) investigate how contrarian portfolio returns can arise. Their horizons and microstructure differ from this bot. Cite data, timeframe, method, limits of transfer and what would falsify the proposed mechanism.

Summarize the frozen Current baseline, candidate rules, acceptance criteria and out-of-sample plan. Use the repo's [quantitative backtesting skill](../quantitative-backtesting/SKILL.md) to test on the same data and costs before recommending an implementation or promotion. Report null or unfavorable outcomes, uncertainty, costs and regime concentration. Keep Experimental in Shadow/Paper; a favorable short window never authorizes tuning Current, loosening risk controls or changing Live mode.
