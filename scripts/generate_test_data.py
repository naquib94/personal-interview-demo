#!/usr/bin/env python3
"""Generate synthetic order or user test data as CSV or JSON.

Two properties matter more than the data itself, and the second is the one usually
missing:

  * Identifiable. Every generated value carries the `QA-` prefix used by
    QaFramework.Core.TestData.DataGenerator, so automation-created rows can always be
    told apart from seeded rows and cleaned up with one predicate.
  * Reproducible. Everything derives from `random.Random(seed)`, and the seed is echoed
    to stderr. Random data with an unrecorded seed produces the worst class of failure
    there is: one that cannot be replayed, and therefore cannot be triaged or proven
    fixed.

Instrument names and quantity ranges mirror database/seed/002_seed.sql, so generated
orders are actually accepted by POST /api/orders rather than being rejected on
validation before they exercise anything.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import random
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Optional, Sequence

EXIT_OK = 0
EXIT_USAGE = 2

# Matches DataGenerator.Prefix in src/QaFramework.Core/TestData/DataGenerator.cs.
PREFIX = "QA"

# RFC 2606 reserves .invalid, so an address here can never resolve and a misconfigured
# test system can never email a real person. That has caused real incidents elsewhere.
EMAIL_DOMAIN = "example.invalid"

_HEX = "0123456789ABCDEF"


@dataclass(frozen=True)
class Instrument:
    """A tradable instrument with the bounds the API enforces on quantity."""

    symbol: str
    mid_price: float
    min_quantity: float
    max_quantity: float


# Mirrors the tradable rows in database/seed/002_seed.sql. GOLD-SPOT is excluded on
# purpose: it is non-tradable, so orders against it are rejected, and mixing guaranteed
# rejections into general-purpose test data makes results ambiguous. Negative cases
# deserve their own explicit test, not a random sprinkling.
INSTRUMENTS: tuple[Instrument, ...] = (
    Instrument("EURUSD", 1.08427, 0.01, 50.00),
    Instrument("GBPUSD", 1.26520, 0.01, 50.00),
    Instrument("USDJPY", 157.20850, 0.01, 50.00),
    Instrument("XAUUSD", 2318.57500, 0.01, 20.00),
    Instrument("BTCUSD", 61267.50000, 0.01, 2.00),
    Instrument("US500", 5238.55000, 0.10, 25.00),
)

SIDES = ("Buy", "Sell")
ORDER_TYPES = ("Market", "Limit")
COUNTRY_CODES = ("GB", "AE", "DE", "SG", "MY", "AU")
CURRENCIES = ("USD", "EUR", "GBP")


def run_segment(rng: random.Random) -> str:
    """A short run-scoped segment shared by everything one invocation generates.

    The same structure as DataGenerator.UniqueIdentifier: `QA-4F2A9C-0001`. The segment
    groups a run, the counter makes each value unique within it, and together they let a
    cleanup query remove exactly one run's data.
    """
    return "".join(rng.choice(_HEX) for _ in range(6))


def _reference(segment: str, index: int) -> str:
    return f"{PREFIX}-{segment}-{index:04d}"


def _quantity(rng: random.Random, instrument: Instrument, boundary: Optional[str]) -> float:
    if boundary == "min":
        return round(instrument.min_quantity, 2)
    if boundary == "max":
        return round(instrument.max_quantity, 2)
    # Two decimal places so the value survives a round trip through JSON and a REAL
    # column without a floating-point surprise in an assertion.
    return round(rng.uniform(instrument.min_quantity, instrument.max_quantity), 2)


def generate_orders(
    rng: random.Random,
    count: int,
    segment: str,
    include_boundaries: bool = False,
) -> list[dict[str, Any]]:
    """Generate order payloads shaped for POST /api/orders.

    `expected_status` encodes the API's documented behaviour - market orders fill
    immediately, limit orders rest as Pending - so a data file is enough to assert on
    without hard-coding expectations in every test.
    """
    rows: list[dict[str, Any]] = []
    for index in range(1, count + 1):
        instrument = rng.choice(INSTRUMENTS)
        side = rng.choice(SIDES)
        order_type = rng.choice(ORDER_TYPES)

        boundary: Optional[str] = None
        if include_boundaries:
            # Every third row sits exactly on a limit. Boundaries are where defects live,
            # and a uniform random draw essentially never lands on one.
            boundary = ("min", "max", None)[index % 3]

        quantity = _quantity(rng, instrument, boundary)

        limit_price: Optional[float] = None
        if order_type == "Limit":
            # A resting order: buys below the mid, sells above it. Priced through the mid
            # would fill instantly and stop testing the Pending path.
            drift = rng.uniform(0.005, 0.02)
            factor = (1 - drift) if side == "Buy" else (1 + drift)
            limit_price = round(instrument.mid_price * factor, 5)

        rows.append(
            {
                "external_reference": _reference(segment, index),
                "symbol": instrument.symbol,
                "side": side,
                "order_type": order_type,
                "quantity": quantity,
                "limit_price": limit_price,
                "expected_status": "Filled" if order_type == "Market" else "Pending",
                "boundary_case": boundary or "",
            }
        )
    return rows


def generate_users(rng: random.Random, count: int, segment: str) -> list[dict[str, Any]]:
    """Generate user and account registration data.

    No password field, on purpose. Credentials belong in configuration or a secret
    store, never in a generated data file that ends up as a build artefact - and the
    demo API's seeded accounts all share one documented throwaway password anyway.
    """
    rows: list[dict[str, Any]] = []
    for index in range(1, count + 1):
        identifier = f"{PREFIX.lower()}.{segment.lower()}.{index:04d}"
        rows.append(
            {
                "username": identifier,
                "email": f"{identifier}@{EMAIL_DOMAIN}",
                "country_code": rng.choice(COUNTRY_CODES),
                # Weighted towards Active: negative-path states are valuable but a data set
                # that is mostly suspended accounts cannot drive a happy-path suite.
                "status": rng.choices(("Active", "Suspended", "Closed"), weights=(8, 1, 1), k=1)[0],
                "account_currency": rng.choice(CURRENCIES),
                "account_type": "Demo",
                "opening_balance": round(rng.uniform(1000.0, 50000.0), 2),
            }
        )
    return rows


def to_json(rows: Sequence[dict[str, Any]]) -> str:
    return json.dumps(list(rows), indent=2)


def to_csv(rows: Sequence[dict[str, Any]]) -> str:
    """Render rows as CSV.

    lineterminator is pinned to "\\n" because the default "\\r\\n" combined with a text
    stream on Windows produces "\\r\\r\\n", which some parsers read as a blank row.
    """
    if not rows:
        return ""
    buffer = io.StringIO()
    writer = csv.DictWriter(buffer, fieldnames=list(rows[0].keys()), lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    return buffer.getvalue()


def render(rows: Sequence[dict[str, Any]], output_format: str) -> str:
    return to_csv(rows) if output_format == "csv" else to_json(rows)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="generate_test_data.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=(
            "Generate reproducible synthetic order or user test data. Every value carries the "
            "QA- prefix so automation-created data is greppable and removable, and all email "
            "addresses use the reserved example.invalid domain."
        ),
        epilog=(
            "Examples:\n"
            "  generate_test_data.py --count 25 --seed 1234\n"
            "  generate_test_data.py --entity users --format csv --output artifacts/users.csv\n"
            "  generate_test_data.py --count 12 --include-boundaries --seed 7\n"
            "\n"
            "Exit codes:\n"
            "  0  data generated\n"
            "  2  bad arguments, or the output file could not be written\n"
        ),
    )
    parser.add_argument(
        "--entity",
        choices=("orders", "users"),
        default="orders",
        help="what to generate (default: orders)",
    )
    parser.add_argument(
        "--count", type=int, default=10, metavar="N", help="how many rows to generate (default: 10)"
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=0,
        metavar="N",
        help="seed for the generator; the same seed always produces the same data (default: 0)",
    )
    parser.add_argument(
        "--format", choices=("csv", "json"), default="json", help="output format (default: json)"
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        metavar="PATH",
        help="file to write; omit to write to stdout so the data can be piped",
    )
    parser.add_argument(
        "--include-boundaries",
        action="store_true",
        help="force some quantities onto the instrument's exact minimum and maximum, "
        "which uniform random values essentially never hit",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.count < 1:
        parser.error("--count must be at least 1")

    rng = random.Random(args.seed)
    segment = run_segment(rng)

    if args.entity == "orders":
        rows = generate_orders(rng, args.count, segment, args.include_boundaries)
    else:
        rows = generate_users(rng, args.count, segment)

    text = render(rows, args.format)

    # The seed and segment go to stderr, not stdout, so that piping the data stays clean
    # while the information needed to reproduce it is still recorded in the build log.
    print(
        f"generated {len(rows)} {args.entity} row(s) with seed={args.seed} "
        f"segment={segment} (replay with --seed {args.seed})",
        file=sys.stderr,
    )

    if args.output is None:
        sys.stdout.write(text)
        if text and not text.endswith("\n"):
            sys.stdout.write("\n")
        return EXIT_OK

    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        # newline="" is required for csv; harmless for json. Without it the csv module's
        # line endings are translated a second time by the text layer on Windows.
        with args.output.open("w", encoding="utf-8", newline="") as handle:
            handle.write(text)
    except OSError as exc:
        print(f"error: could not write '{args.output}': {exc}", file=sys.stderr)
        return EXIT_USAGE

    print(f"wrote {args.output}", file=sys.stderr)
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
