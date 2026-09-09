#!/usr/bin/env python3
"""Run the data-integrity invariants from database/validation against the demo database.

This is the "QA validates the backend after an API or UI operation" story, taken one
step further: instead of checking a single row after a single action, it asserts that
the whole data set is still self-consistent. Each query is written so that a healthy
database returns zero rows, which makes the pass condition trivial to automate and
leaves the query itself as the documentation of the rule.

The queries are the Group B set in database/validation/qa-validation-queries.sql. They
are duplicated here rather than parsed out of the .sql file: splitting a SQL script on
semicolons is a parser that fails silently the first time someone writes a semicolon
inside a string literal, and a silently empty check is worse than a duplicated one.
The docstring of each check names its counterpart so the two stay in step.

Against the committed seed data this script finds a real defect: order
ORD-20240403-0006 is marked Filled with no fill rows. That is deliberate - see the B1
comment in the SQL file.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional, Sequence

EXIT_OK = 0
EXIT_VIOLATIONS = 1
EXIT_CANNOT_RUN = 2

REQUIRED_TABLES = ("users", "accounts", "instruments", "orders", "order_fills")

# Where the demo application puts its SQLite file when it runs from the build output.
# Checked in order; the first that exists wins. Keeping this list short and explicit is
# better than a filesystem search that might pick up somebody's unrelated database.
DEFAULT_DATABASE_CANDIDATES = (
    Path("src/TradingDemo.App/bin/Debug/net10.0/trading-demo.db"),
    Path("src/TradingDemo.App/bin/Release/net10.0/trading-demo.db"),
    Path("src/TradingDemo.App/trading-demo.db"),
)


@dataclass(frozen=True)
class IntegrityCheck:
    """One invariant. `sql` must return zero rows when the data is healthy."""

    id: str
    title: str
    why: str
    sql: str


@dataclass
class CheckOutcome:
    check: IntegrityCheck
    rows: list[dict[str, Any]] = field(default_factory=list)
    error: Optional[str] = None

    @property
    def passed(self) -> bool:
        return self.error is None and not self.rows


# --------------------------------------------------------------------------------------
# The invariants. Mirrors of B1-B5 in database/validation/qa-validation-queries.sql.
# --------------------------------------------------------------------------------------
CHECKS: tuple[IntegrityCheck, ...] = (
    IntegrityCheck(
        id="B1",
        title="Fill reconciliation",
        why=(
            "An order marked Filled must have fills summing to its quantity. A mismatch means "
            "either the customer was told about an execution that never happened, or a fill was "
            "lost. Both are reportable incidents rather than test failures."
        ),
        sql="""
            SELECT
                o.order_reference,
                o.status,
                o.quantity                        AS ordered_quantity,
                COALESCE(SUM(f.fill_quantity), 0) AS filled_quantity,
                COUNT(f.id)                       AS fill_count
            FROM orders o
                LEFT JOIN order_fills f ON f.order_id = o.id
            WHERE o.status = 'Filled'
            GROUP BY o.id, o.order_reference, o.status, o.quantity
            HAVING COALESCE(SUM(f.fill_quantity), 0) <> o.quantity
            ORDER BY o.order_reference
        """,
    ),
    IntegrityCheck(
        id="B2",
        title="Referential orphans",
        why=(
            "SQLite only enforces foreign keys when PRAGMA foreign_keys is ON, so orphans are a "
            "live possibility rather than a theoretical one. LEFT JOIN with an IS NULL filter is "
            "the standard idiom for finding them."
        ),
        sql="""
            SELECT
                o.id,
                o.order_reference,
                o.account_id,
                o.instrument_id
            FROM orders o
                LEFT JOIN accounts    a ON a.id = o.account_id
                LEFT JOIN instruments i ON i.id = o.instrument_id
            WHERE a.id IS NULL
               OR i.id IS NULL
            ORDER BY o.id
        """,
    ),
    IntegrityCheck(
        id="B3",
        title="Duplicate order references",
        why=(
            "The order reference is the customer-facing identifier. Two orders sharing one is a "
            "support incident. The UNIQUE constraint should make this impossible, so this check "
            "is really asserting that the constraint still exists after every migration."
        ),
        sql="""
            SELECT
                order_reference,
                COUNT(*) AS occurrences
            FROM orders
            GROUP BY order_reference
            HAVING COUNT(*) > 1
            ORDER BY occurrences DESC
        """,
    ),
    IntegrityCheck(
        id="B4",
        title="Limit-price business rule",
        why=(
            "A Limit order requires a limit price and a Market order must not have one. Encoding "
            "the rule as a query checks it across all stored history, including rows created by "
            "import or back-office paths the functional tests never touch."
        ),
        sql="""
            SELECT
                order_reference,
                order_type,
                limit_price
            FROM orders
            WHERE (order_type = 'Limit'  AND limit_price IS NULL)
               OR (order_type = 'Market' AND limit_price IS NOT NULL)
            ORDER BY order_reference
        """,
    ),
    IntegrityCheck(
        id="B5",
        title="Quantity boundaries",
        why=(
            "Quantities must sit inside the instrument's tradable range. This is the same rule the "
            "API validates on the way in; running it over stored data proves the rule was always "
            "enforced and not just on the happy path."
        ),
        sql="""
            SELECT
                o.order_reference,
                i.symbol,
                o.quantity,
                i.min_quantity,
                i.max_quantity
            FROM orders o
                INNER JOIN instruments i ON i.id = o.instrument_id
            WHERE o.quantity < i.min_quantity
               OR o.quantity > i.max_quantity
            ORDER BY i.symbol, o.order_reference
        """,
    ),
)


class VerificationError(Exception):
    """Raised when the checks cannot be run at all, as opposed to failing."""


def read_only_uri(path: Path) -> str:
    """Build a `file:...?mode=ro` URI for sqlite3.connect(uri=True).

    Read-only is not politeness, it is a guarantee: a verification script must not be
    capable of changing the data it is judging, and mode=ro also stops SQLite from
    creating an empty database when the path is wrong - which would otherwise turn a
    typo into a suspiciously clean pass.

    Path.as_uri() handles Windows drive letters and percent-encoding, which manual
    string concatenation gets wrong on exactly the machines where it matters.
    """
    return f"{path.resolve().as_uri()}?mode=ro"


def connect_read_only(path: Path) -> sqlite3.Connection:
    if not path.exists():
        raise VerificationError(
            f"no database at '{path}'. Start the demo API once (dotnet run --project "
            "src/TradingDemo.App) so it creates and seeds the SQLite file, or pass --database."
        )
    try:
        connection = sqlite3.connect(read_only_uri(path), uri=True)
    except sqlite3.Error as exc:
        raise VerificationError(f"could not open '{path}' read-only: {exc}.") from None
    return connection


def assert_schema_present(connection: sqlite3.Connection) -> None:
    """Fail early and clearly when pointed at the wrong SQLite file.

    Without this, a wrong --database produces five separate "no such table" errors and
    the reader has to work out that the path, not the data, is the problem.
    """
    try:
        names = {
            row[0]
            for row in connection.execute("SELECT name FROM sqlite_master WHERE type = 'table'")
        }
    except sqlite3.Error as exc:
        raise VerificationError(
            f"the file could not be read as a SQLite database: {exc}."
        ) from None

    missing = [table for table in REQUIRED_TABLES if table not in names]
    if missing:
        raise VerificationError(
            "the database does not contain the expected trading schema (missing: "
            + ", ".join(missing)
            + "). Check that --database points at the demo database created from "
            "database/schema/001_schema.sql."
        )


def rows_to_dicts(cursor: sqlite3.Cursor) -> list[dict[str, Any]]:
    """Materialise a cursor as dictionaries so output rendering stays column-agnostic."""
    columns = [description[0] for description in cursor.description or []]
    return [dict(zip(columns, row)) for row in cursor.fetchall()]


def run_checks(
    connection: sqlite3.Connection, checks: Sequence[IntegrityCheck] = CHECKS
) -> list[CheckOutcome]:
    """Run every check, collecting rather than raising.

    One broken query must not hide the results of the other four - the same reason the
    demo API returns all validation errors at once instead of the first.
    """
    outcomes: list[CheckOutcome] = []
    for check in checks:
        try:
            cursor = connection.execute(check.sql)
        except sqlite3.Error as exc:
            outcomes.append(CheckOutcome(check=check, error=str(exc)))
            continue
        outcomes.append(CheckOutcome(check=check, rows=rows_to_dicts(cursor)))
    return outcomes


def _format_row(row: dict[str, Any]) -> str:
    return ", ".join(f"{key}={row[key]!r}" for key in row)


def render_text(outcomes: Sequence[CheckOutcome], database: Path, max_rows: int) -> str:
    lines: list[str] = []
    lines.append("=" * 78)
    lines.append("BACKEND DATA VERIFICATION")
    lines.append("=" * 78)
    lines.append(f"Database: {database} (opened read-only)")
    lines.append("")

    for outcome in outcomes:
        check = outcome.check
        if outcome.error is not None:
            status = "ERROR"
        elif outcome.rows:
            status = f"FAIL ({len(outcome.rows)} violation(s))"
        else:
            status = "PASS"
        lines.append(f"[{check.id}] {check.title:<30} {status}")

        if outcome.error is not None:
            lines.append(f"       query failed: {outcome.error}")
            lines.append(f"       why it matters: {check.why}")
            lines.append("")
            continue

        if outcome.rows:
            lines.append(f"       why it matters: {check.why}")
            for row in outcome.rows[:max_rows]:
                lines.append(f"       - {_format_row(row)}")
            hidden = len(outcome.rows) - max_rows
            if hidden > 0:
                lines.append(f"       ... and {hidden} more (raise --max-rows to see them)")
            lines.append("")

    failed = [o for o in outcomes if not o.passed]
    lines.append("-" * 78)
    if failed:
        lines.append(
            f"RESULT: {len(failed)} of {len(outcomes)} invariant(s) violated: "
            + ", ".join(o.check.id for o in failed)
        )
    else:
        lines.append(f"RESULT: all {len(outcomes)} invariant(s) hold.")
    lines.append("-" * 78)
    return "\n".join(lines)


def render_json(outcomes: Sequence[CheckOutcome], database: Path, max_rows: int) -> str:
    payload = {
        "database": str(database),
        "checks": [
            {
                "id": o.check.id,
                "title": o.check.title,
                "passed": o.passed,
                "error": o.error,
                "violationCount": len(o.rows),
                "violations": o.rows[:max_rows],
            }
            for o in outcomes
        ],
        "summary": {
            "total": len(outcomes),
            "violated": sum(1 for o in outcomes if not o.passed),
            "violatedIds": [o.check.id for o in outcomes if not o.passed],
        },
    }
    # default=str so that an unexpected column type (a BLOB, say) degrades to a string
    # instead of blowing up the whole report at serialisation time.
    return json.dumps(payload, indent=2, default=str)


def resolve_default_database(repo_root: Path) -> Optional[Path]:
    for candidate in DEFAULT_DATABASE_CANDIDATES:
        path = repo_root / candidate
        if path.exists():
            return path
    return None


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="verify_backend_data.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=(
            "Verify the demo trading database against the data-integrity invariants in "
            "database/validation/qa-validation-queries.sql. Each invariant must return zero "
            "rows; anything returned is a violation and is printed with the rule it breaks."
        ),
        epilog=(
            "Examples:\n"
            "  verify_backend_data.py\n"
            "  verify_backend_data.py --database /tmp/trading-demo.db --format json\n"
            "  verify_backend_data.py --check B1 --check B3\n"
            "\n"
            "Exit codes:\n"
            "  0  every invariant holds\n"
            "  1  at least one invariant was violated, or a query errored\n"
            "  2  the checks could not be run (missing database, wrong schema)\n"
            "\n"
            "Note: against the committed seed data B1 fails on purpose. Order\n"
            "ORD-20240403-0006 is Filled with no fill rows - a planted defect, so that this\n"
            "script demonstrably finds something rather than always printing green.\n"
        ),
    )
    parser.add_argument(
        "--database",
        type=Path,
        default=None,
        metavar="PATH",
        help="path to the SQLite database (default: the demo database under src/, if present)",
    )
    parser.add_argument(
        "--check",
        action="append",
        choices=[check.id for check in CHECKS],
        metavar="ID",
        help="run only this check; repeatable. Default: all of "
        + ", ".join(check.id for check in CHECKS),
    )
    parser.add_argument(
        "--format",
        choices=("text", "json"),
        default="text",
        help="text for humans, json for a downstream job (default: text)",
    )
    parser.add_argument(
        "--max-rows",
        type=int,
        default=20,
        metavar="N",
        help="maximum violating rows to show per check (default: 20)",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.max_rows < 1:
        parser.error("--max-rows must be at least 1")

    repo_root = Path(__file__).resolve().parent.parent
    database = args.database or resolve_default_database(repo_root)
    if database is None:
        print(
            "error: could not find the demo database. Looked for "
            + ", ".join(str(repo_root / c) for c in DEFAULT_DATABASE_CANDIDATES)
            + ". Run the demo API once to create it, or pass --database.",
            file=sys.stderr,
        )
        return EXIT_CANNOT_RUN

    selected = [c for c in CHECKS if args.check is None or c.id in args.check]

    connection: Optional[sqlite3.Connection] = None
    try:
        connection = connect_read_only(database)
        assert_schema_present(connection)
        outcomes = run_checks(connection, selected)
    except VerificationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_CANNOT_RUN
    finally:
        if connection is not None:
            connection.close()

    renderer = render_json if args.format == "json" else render_text
    print(renderer(outcomes, database, args.max_rows))

    return EXIT_OK if all(o.passed for o in outcomes) else EXIT_VIOLATIONS


if __name__ == "__main__":
    sys.exit(main())
