"""What the models cost, fixed on the day they were read (spec §8), so a report's costs can be redone.

Costs come from the tokens each transcript recorded, at these rates. A model not listed here has no
price, and its cost is reported as unknown, never as zero.
"""

from __future__ import annotations

from typing import NamedTuple


class Price(NamedTuple):
    """US dollars per 1M tokens."""

    input_per_million: float
    output_per_million: float


PRICES: dict[str, Price] = {
    # Global Standard, eastus2, short context, effective 2026-08-01.
    "gpt-5.6-luna": Price(0.20, 1.20),
    # Claude Haiku 4.5 in Foundry, billed through Azure Marketplace at Anthropic's list price.
    "claude-haiku-4-5": Price(1.00, 5.00),
}

SOURCE = (
    "gpt-5.6-luna from the Azure Retail Prices API (Global Standard, eastus2); "
    "Claude Haiku 4.5 at Anthropic's list price, billed through Azure Marketplace"
)

READ_ON = "2026-10-04"


def cost(model: str, input_tokens: int, output_tokens: int) -> float | None:
    """The cost in US dollars of these tokens on this model, or None when the model has no price here."""
    price = PRICES.get(model)
    if price is None:
        return None
    return (input_tokens * price.input_per_million + output_tokens * price.output_per_million) / 1_000_000
