# Scoring WSJF

WSJF (Weighted Shortest Job First) sequences a queue so the most value is delivered per unit of
effort. It answers *what should we start next*, and nothing else — once work starts, its score is
history.

```
                 cost of delay          business value + time criticality + risk/opportunity
WSJF  =  ─────────────────────────  =  ────────────────────────────────────────────────────────
                   job size                              job size
```

All four inputs use the modified-Fibonacci scale: **1, 2, 3, 5, 8, 13, 20**.

## The scale is relative

This is the part most often got wrong. The numbers mean nothing on their own — they only mean
something *against the other items in the queue*. Two consequences:

- In each column, the smallest item should be a **1**. If nothing is a 1, everything is inflated.
- Before scoring, run `backlog list --top 15` and calibrate against items already scored. A new
  item's business value of 8 should be genuinely comparable to the other 8s. The top of the queue
  is what you are calibrating against, so the whole list is rarely worth pulling — and if you need
  the individual dimensions rather than the WSJF totals, ask for them:
  `backlog list --top 15 --json --fields id,bv,tc,rroe,size,title`.

The gaps in the scale are deliberate. Being forced to choose between 5 and 8 — with no 6 or 7 —
is what stops everything drifting to the middle.

## The four dimensions

Each is a Sheet column of the same name; the short flag and the spelled-out one are the same
option.

**`--bv` / `--business-value` — Business Value.** What the organization or its users gain. Revenue,
cost avoided, a customer unblocked, a risk of churn removed. Ask: if we shipped only this, who
would notice?

**`--tc` / `--time-criticality` — Time Criticality.** How fast the value decays. A 1 means it is
worth the same next quarter. A 20 means a hard external deadline, a compliance date, or a window
that closes. Ask: what changes if we ship this three months later?

**`--rroe` / `--risk-opportunity` — Risk & Opportunity.** Risk reduction and opportunity
enablement: value that is not the feature itself — removing a risk, retiring a liability, or
unlocking work that cannot start until this lands. This is where enabling and platform work earns
its place against user-visible features; without it, infrastructure work never ranks.

**`--size` / `--job-size` — Job Size.** Effort, as a proxy for duration. Not value. Because it is
the denominator, halving the size does more for rank than doubling the value — which is the
mechanism that pushes small, high-value work to the front, and the main reason to split large
items rather than let them sit at the bottom forever.

## Worked example

| item | Business Value | Time Criticality | Risk & Opportunity | Job Size | Cost of Delay | WSJF |
|---|---|---|---|---|---|---|
| Fix Slack thread replay | 8 | 8 | 2 | 2 | 18 | **9.00** |
| WSJF index tool | 8 | 3 | 2 | 5 | 13 | 2.60 |
| Rebrand the docs site | 3 | 1 | 1 | 8 | 5 | 0.63 |

The Slack fix wins not because it is the most valuable in absolute terms, but because it is
nearly as valuable as the index tool and a fraction of the work.

## Practical notes

- **Unscored is a legitimate state.** An item with no scores sorts last and shows a blank rank.
  Filing something unscored is better than inventing numbers; say it needs scoring.
- **Score is not commitment.** The top of the queue is a suggestion; a person still pulls.
- **Rescore when the world changes**, not on a schedule. A deadline appearing changes time
  criticality; a dependency landing changes risk & opportunity.
- **A size of 13 or 20 is a smell.** Suggest splitting it — two 5s that can ship independently
  will almost always outrank one 13.
- **Started work cannot be rescored.** `backlog score` refuses it. The frozen numbers on the
  In Progress and Archive tabs are the historical record used to calibrate future estimates.
