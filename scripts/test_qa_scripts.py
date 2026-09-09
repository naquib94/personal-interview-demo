#!/usr/bin/env python3
"""Unit tests for the pure logic inside the scripting toolkit.

Run from the repository root with:

    python -m unittest discover scripts

Scope is deliberate. The parsing, clustering, invariant SQL and generation logic are
tested; the argparse wiring and file I/O are exercised only where a bug there would be
silent (the pass-rate gate, and the CSV line-ending trap on Windows). Tests that need a
database build a throwaway SQLite file, so there is nothing to install and nothing to
clean up. Standard library only, matching the scripts themselves.
"""

from __future__ import annotations

import contextlib
import csv
import io
import json
import random
import sqlite3
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

# Make the sibling scripts importable regardless of how discovery was invoked. Without
# this, `python -m unittest discover -t .` fails with an ImportError that tells the
# reader nothing useful.
_SCRIPTS_DIR = Path(__file__).resolve().parent
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

import analyse_test_results as analyse  # noqa: E402
import generate_test_data as generate  # noqa: E402
import verify_backend_data as verify  # noqa: E402


# A miniature but realistic TRX document: two failures sharing one root cause, a skip,
# and a data-driven test whose parent aggregate must not be double-counted.
SAMPLE_TRX = """<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="8f1c" name="sample" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="2024-04-05T09:00:00" start="2024-04-05T09:00:01" finish="2024-04-05T09:00:20" />
  <Results>
    <UnitTestResult testName="Api.Orders.Passes" duration="00:00:00.5000000" outcome="Passed" />
    <UnitTestResult testName="Api.Orders.Slow" duration="00:00:12.2500000" outcome="Passed" />
    <UnitTestResult testName="Api.Orders.FailsOne" duration="00:00:01.0000000" outcome="Failed">
      <Output>
        <ErrorInfo>
          <Message>Expected order.Status to be "Filled" but found "Pending" for ORD-20240403-0006.</Message>
          <StackTrace>at Api.Orders.FailsOne()</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="Api.Orders.FailsTwo" duration="00:00:02.0000000" outcome="Failed">
      <Output>
        <ErrorInfo>
          <Message>Expected order.Status to be "Filled" but found "Pending" for ORD-20240404-0007.</Message>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="Api.Orders.Ignored" outcome="NotExecuted" />
    <UnitTestResult testName="Api.Orders.DataDriven" duration="00:00:03.0000000" outcome="Failed">
      <InnerResults>
        <UnitTestResult testName="Api.Orders.DataDriven(1)" duration="00:00:01.0000000" outcome="Passed" />
        <UnitTestResult testName="Api.Orders.DataDriven(2)" duration="00:00:02.0000000" outcome="Failed">
          <Output>
            <ErrorInfo>
              <Message>Connection refused contacting http://localhost:5199/health</Message>
            </ErrorInfo>
          </Output>
        </UnitTestResult>
      </InnerResults>
    </UnitTestResult>
  </Results>
</TestRun>
"""


class ParseDurationTests(unittest.TestCase):
    def test_parses_trx_duration_format(self) -> None:
        self.assertAlmostEqual(analyse.parse_duration("00:00:01.2345678"), 1.2345678, places=6)
        self.assertAlmostEqual(analyse.parse_duration("00:01:30"), 90.0)
        self.assertAlmostEqual(analyse.parse_duration("01:00:00"), 3600.0)
        self.assertAlmostEqual(analyse.parse_duration("1:02:00:00"), 93600.0)

    def test_degrades_to_zero_instead_of_raising(self) -> None:
        for value in (None, "", "   ", "not-a-duration", "aa:bb:cc"):
            self.assertEqual(analyse.parse_duration(value), 0.0, msg=repr(value))


class OutcomeClassificationTests(unittest.TestCase):
    def test_known_outcomes(self) -> None:
        self.assertEqual(analyse.classify_outcome("Passed"), "passed")
        self.assertEqual(analyse.classify_outcome("passed"), "passed")
        self.assertEqual(analyse.classify_outcome("NotExecuted"), "skipped")
        self.assertEqual(analyse.classify_outcome("Inconclusive"), "skipped")
        self.assertEqual(analyse.classify_outcome("Failed"), "failed")
        self.assertEqual(analyse.classify_outcome("Timeout"), "failed")

    def test_unknown_outcome_counts_as_failure(self) -> None:
        # Fail-safe: an outcome we do not recognise must never make a build look green.
        self.assertEqual(analyse.classify_outcome("SomethingNew"), "failed")
        self.assertEqual(analyse.classify_outcome(None), "failed")


class TrxExtractionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.results = analyse.extract_results(ET.fromstring(SAMPLE_TRX))

    def test_namespaced_results_are_found(self) -> None:
        self.assertTrue(self.results, "the TRX namespace was not handled")

    def test_inner_results_replace_their_parent(self) -> None:
        names = [r.name for r in self.results]
        self.assertIn("Api.Orders.DataDriven(1)", names)
        self.assertIn("Api.Orders.DataDriven(2)", names)
        self.assertNotIn("Api.Orders.DataDriven", names)
        self.assertEqual(len(self.results), 7)

    def test_failure_message_and_stack_trace_are_captured(self) -> None:
        failure = next(r for r in self.results if r.name == "Api.Orders.FailsOne")
        self.assertIn("ORD-20240403-0006", failure.message)
        self.assertIn("at Api.Orders.FailsOne()", failure.stack_trace)

    def test_summary_counts_and_pass_rate(self) -> None:
        summary = analyse.summarise(self.results)
        self.assertEqual(summary.total, 7)
        self.assertEqual(summary.passed, 3)
        self.assertEqual(summary.failed, 3)
        self.assertEqual(summary.skipped, 1)
        self.assertEqual(summary.executed, 6)
        self.assertAlmostEqual(summary.pass_rate, 50.0)

    def test_pass_rate_of_empty_run_is_zero_not_a_division_error(self) -> None:
        self.assertEqual(analyse.summarise([]).pass_rate, 0.0)

    def test_slowest_excludes_skips_and_sorts_descending(self) -> None:
        slowest = analyse.slowest_tests(self.results, 3)
        self.assertEqual([r.name for r in slowest][0], "Api.Orders.Slow")
        self.assertEqual(len(slowest), 3)
        self.assertGreaterEqual(slowest[0].duration_seconds, slowest[-1].duration_seconds)
        self.assertEqual(len(analyse.slowest_tests(self.results, 100)), 6)


class FailureClusteringTests(unittest.TestCase):
    def test_volatile_values_are_normalised_away(self) -> None:
        first = analyse.normalise_message('Order ORD-20240403-0006 was not found (attempt 3).')
        second = analyse.normalise_message('Order ORD-20240404-0007 was not found (attempt 11).')
        self.assertEqual(first, second)

    def test_guid_and_timestamp_are_normalised(self) -> None:
        signature = analyse.normalise_message(
            "Token 4f2a9c1d-3b5e-4a7f-9c8d-1e2f3a4b5c6d expired at 2024-04-05T09:00:00"
        )
        self.assertIn("<guid>", signature)
        self.assertIn("<timestamp>", signature)

    def test_only_the_first_meaningful_line_forms_the_signature(self) -> None:
        signature = analyse.normalise_message("\n\nExpected 1 item\n  at SomeMethod()\n")
        self.assertEqual(signature, "Expected <n> item")

    def test_missing_message_is_labelled_rather_than_empty(self) -> None:
        self.assertEqual(analyse.normalise_message("   \n "), "<no failure message captured>")

    def test_forty_failures_with_one_cause_form_one_cluster(self) -> None:
        results = [
            analyse.TestResult(
                name=f"Suite.Test{index}",
                outcome="Failed",
                duration_seconds=0.1,
                message=f"Connection refused contacting http://localhost:5199/health (try {index})",
            )
            for index in range(40)
        ]
        clusters = analyse.cluster_failures(results)
        self.assertEqual(len(clusters), 1)
        self.assertEqual(clusters[0].count, 40)
        # Only a handful of names are kept: the cluster is the unit of triage, not the list.
        self.assertEqual(len(clusters[0].test_names), 5)

    def test_clusters_are_ordered_largest_first(self) -> None:
        results = analyse.extract_results(ET.fromstring(SAMPLE_TRX))
        clusters = analyse.cluster_failures(results)
        self.assertEqual(len(clusters), 2)
        self.assertEqual(clusters[0].count, 2)
        self.assertIn("Filled", clusters[0].signature)
        self.assertEqual(clusters[1].count, 1)

    def test_passed_results_are_never_clustered(self) -> None:
        results = [
            analyse.TestResult(name="ok", outcome="Passed", duration_seconds=0.1, message="")
        ]
        self.assertEqual(analyse.cluster_failures(results), [])


class AnalyseCommandLineTests(unittest.TestCase):
    """The gate behaviour is worth an end-to-end test: a wrong exit code here is silent."""

    def _run(self, argv: list[str]) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = analyse.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_missing_file_reports_clearly_and_exits_two(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            missing = str(Path(directory) / "absent.trx")
            code, _, err = self._run([missing])
        self.assertEqual(code, analyse.EXIT_USAGE)
        self.assertIn("no TRX file", err)

    def test_malformed_xml_reports_clearly_and_exits_two(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "truncated.trx"
            path.write_text("<TestRun><Results><UnitTestResult", encoding="utf-8")
            code, _, err = self._run([str(path)])
        self.assertEqual(code, analyse.EXIT_USAGE)
        self.assertIn("not well-formed XML", err)

    def test_pass_rate_gate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "sample.trx"
            path.write_text(SAMPLE_TRX, encoding="utf-8")

            below, _, err = self._run([str(path), "--fail-under", "90"])
            self.assertEqual(below, analyse.EXIT_GATE_FAILED)
            self.assertIn("below the required", err)

            at_threshold, _, _ = self._run([str(path), "--fail-under", "50"])
            self.assertEqual(at_threshold, analyse.EXIT_OK)

            ungated, _, _ = self._run([str(path)])
            self.assertEqual(ungated, analyse.EXIT_OK)

    def test_empty_result_set_fails_the_gate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "empty.trx"
            path.write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                "<Results /></TestRun>",
                encoding="utf-8",
            )
            code, _, err = self._run([str(path), "--fail-under", "0"])
        self.assertEqual(code, analyse.EXIT_GATE_FAILED)
        self.assertIn("no executed tests", err)

    def test_json_output_is_valid_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "sample.trx"
            path.write_text(SAMPLE_TRX, encoding="utf-8")
            code, out, _ = self._run([str(path), "--format", "json"])
        self.assertEqual(code, analyse.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["summary"]["total"], 7)
        self.assertEqual(len(payload["failureClusters"]), 2)


# A cut-down copy of database/schema/001_schema.sql holding only the columns the
# invariants touch. UNIQUE is deliberately omitted from order_reference so that the
# duplicate-reference invariant can be given something to find - in the real schema the
# constraint prevents it, which is exactly what B3 is asserting still holds.
_TEST_SCHEMA = """
CREATE TABLE users (id INTEGER PRIMARY KEY, username TEXT NOT NULL);
CREATE TABLE accounts (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, account_number TEXT);
CREATE TABLE instruments (
    id INTEGER PRIMARY KEY, symbol TEXT NOT NULL,
    min_quantity REAL NOT NULL, max_quantity REAL NOT NULL
);
CREATE TABLE orders (
    id INTEGER PRIMARY KEY, order_reference TEXT NOT NULL, account_id INTEGER NOT NULL,
    instrument_id INTEGER NOT NULL, side TEXT NOT NULL, order_type TEXT NOT NULL,
    quantity REAL NOT NULL, limit_price REAL NULL, status TEXT NOT NULL
);
CREATE TABLE order_fills (
    id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL, fill_quantity REAL NOT NULL,
    fill_price REAL NOT NULL
);
"""


def _healthy_database() -> sqlite3.Connection:
    connection = sqlite3.connect(":memory:")
    connection.executescript(_TEST_SCHEMA)
    connection.executescript(
        """
        INSERT INTO users VALUES (1, 'trader.demo');
        INSERT INTO accounts VALUES (1, 1, 'DEMO-1000001');
        INSERT INTO instruments VALUES (1, 'EURUSD', 0.01, 50.0);
        INSERT INTO orders VALUES
            (1, 'ORD-20240401-0001', 1, 1, 'Buy',  'Market', 1.0, NULL,    'Filled'),
            (2, 'ORD-20240401-0002', 1, 1, 'Sell', 'Limit',  2.0, 1.27000, 'Pending');
        INSERT INTO order_fills VALUES (1, 1, 1.0, 1.08435);
        """
    )
    return connection


class IntegrityCheckTests(unittest.TestCase):
    def _run(self, connection: sqlite3.Connection) -> dict[str, verify.CheckOutcome]:
        return {outcome.check.id: outcome for outcome in verify.run_checks(connection)}

    def test_healthy_data_violates_nothing(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            outcomes = self._run(connection)
        self.assertEqual(len(outcomes), len(verify.CHECKS))
        for identifier, outcome in outcomes.items():
            self.assertTrue(outcome.passed, msg=f"{identifier} unexpectedly reported {outcome.rows}")

    def test_b1_finds_a_filled_order_with_no_fills(self) -> None:
        # This is the shape of the planted defect in the committed seed data:
        # ORD-20240403-0006 is Filled and has no fill rows at all.
        with contextlib.closing(_healthy_database()) as connection:
            connection.execute(
                "INSERT INTO orders VALUES (6, 'ORD-20240403-0006', 1, 1, 'Buy', 'Market', 3.0, NULL, 'Filled')"
            )
            outcomes = self._run(connection)

        b1 = outcomes["B1"]
        self.assertFalse(b1.passed)
        self.assertEqual(len(b1.rows), 1)
        self.assertEqual(b1.rows[0]["order_reference"], "ORD-20240403-0006")
        self.assertEqual(b1.rows[0]["fill_count"], 0)
        self.assertEqual(b1.rows[0]["ordered_quantity"], 3.0)

    def test_b1_accepts_a_partial_fill_that_aggregates_correctly(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            connection.executescript(
                """
                INSERT INTO orders VALUES (3, 'ORD-20240402-0003', 1, 1, 'Buy', 'Market', 5.0, NULL, 'Filled');
                INSERT INTO order_fills VALUES (2, 3, 2.0, 2318.75), (3, 3, 3.0, 2318.80);
                """
            )
            outcomes = self._run(connection)
        self.assertTrue(outcomes["B1"].passed)

    def test_b2_finds_orphaned_orders(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            connection.execute(
                "INSERT INTO orders VALUES (7, 'ORD-X-0007', 999, 1, 'Buy', 'Market', 1.0, NULL, 'Pending')"
            )
            outcomes = self._run(connection)
        self.assertFalse(outcomes["B2"].passed)
        self.assertEqual(outcomes["B2"].rows[0]["account_id"], 999)

    def test_b3_finds_duplicate_order_references(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            connection.execute(
                "INSERT INTO orders VALUES (8, 'ORD-20240401-0001', 1, 1, 'Buy', 'Market', 1.0, NULL, 'Pending')"
            )
            outcomes = self._run(connection)
        self.assertFalse(outcomes["B3"].passed)
        self.assertEqual(outcomes["B3"].rows[0]["occurrences"], 2)

    def test_b4_finds_both_directions_of_the_limit_price_rule(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            connection.executescript(
                """
                INSERT INTO orders VALUES (9,  'ORD-X-0009', 1, 1, 'Buy', 'Limit',  1.0, NULL, 'Pending');
                INSERT INTO orders VALUES (10, 'ORD-X-0010', 1, 1, 'Buy', 'Market', 1.0, 1.5,  'Pending');
                """
            )
            outcomes = self._run(connection)
        self.assertFalse(outcomes["B4"].passed)
        self.assertEqual(len(outcomes["B4"].rows), 2)

    def test_b5_finds_quantities_outside_the_instrument_range(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            connection.executescript(
                """
                INSERT INTO orders VALUES (11, 'ORD-X-0011', 1, 1, 'Buy', 'Market', 500.0, NULL, 'Pending');
                INSERT INTO orders VALUES (12, 'ORD-X-0012', 1, 1, 'Buy', 'Market', 0.001, NULL, 'Pending');
                """
            )
            outcomes = self._run(connection)
        self.assertFalse(outcomes["B5"].passed)
        self.assertEqual(len(outcomes["B5"].rows), 2)

    def test_a_broken_query_is_reported_without_hiding_the_others(self) -> None:
        broken = verify.IntegrityCheck(
            id="X1", title="Deliberately broken", why="proves errors are collected",
            sql="SELECT * FROM table_that_does_not_exist",
        )
        with contextlib.closing(_healthy_database()) as connection:
            outcomes = verify.run_checks(connection, (broken,) + verify.CHECKS)
        self.assertIsNotNone(outcomes[0].error)
        self.assertFalse(outcomes[0].passed)
        self.assertEqual(len(outcomes), len(verify.CHECKS) + 1)
        self.assertTrue(all(o.passed for o in outcomes[1:]))

    def test_rows_to_dicts_uses_column_names(self) -> None:
        with contextlib.closing(_healthy_database()) as connection:
            cursor = connection.execute("SELECT order_reference, status FROM orders ORDER BY id")
            rows = verify.rows_to_dicts(cursor)
        self.assertEqual(rows[0], {"order_reference": "ORD-20240401-0001", "status": "Filled"})


class ReadOnlyUriTests(unittest.TestCase):
    def test_uri_requests_read_only_mode(self) -> None:
        uri = verify.read_only_uri(Path("some-database.db"))
        self.assertTrue(uri.startswith("file:"), uri)
        self.assertTrue(uri.endswith("?mode=ro"), uri)

    def test_read_only_connection_refuses_writes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "ro.db"
            with contextlib.closing(sqlite3.connect(str(path))) as setup:
                setup.executescript(_TEST_SCHEMA)
                setup.commit()

            with contextlib.closing(
                sqlite3.connect(verify.read_only_uri(path), uri=True)
            ) as connection:
                # The point of mode=ro: a verification script cannot alter its evidence.
                with self.assertRaises(sqlite3.OperationalError):
                    connection.execute("INSERT INTO users VALUES (1, 'x')")

    def test_missing_database_is_reported_not_created(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "absent.db"
            with self.assertRaises(verify.VerificationError):
                verify.connect_read_only(path)
            self.assertFalse(path.exists(), "the verifier must never create a database")

    def test_wrong_schema_is_reported_clearly(self) -> None:
        with contextlib.closing(sqlite3.connect(":memory:")) as connection:
            connection.execute("CREATE TABLE unrelated (id INTEGER PRIMARY KEY)")
            with self.assertRaises(verify.VerificationError) as raised:
                verify.assert_schema_present(connection)
        self.assertIn("orders", str(raised.exception))


class GenerateOrdersTests(unittest.TestCase):
    def _orders(self, seed: int, count: int = 12, boundaries: bool = False) -> list:
        rng = random.Random(seed)
        segment = generate.run_segment(rng)
        return generate.generate_orders(rng, count, segment, boundaries)

    def test_same_seed_reproduces_identical_data(self) -> None:
        self.assertEqual(self._orders(1234), self._orders(1234))

    def test_different_seeds_produce_different_data(self) -> None:
        self.assertNotEqual(self._orders(1234), self._orders(4321))

    def test_references_carry_the_automation_prefix(self) -> None:
        for row in self._orders(7):
            self.assertTrue(
                row["external_reference"].startswith(f"{generate.PREFIX}-"),
                row["external_reference"],
            )

    def test_references_are_unique(self) -> None:
        rows = self._orders(7, count=200)
        self.assertEqual(len({r["external_reference"] for r in rows}), 200)

    def test_quantities_respect_instrument_bounds(self) -> None:
        bounds = {i.symbol: i for i in generate.INSTRUMENTS}
        for row in self._orders(99, count=200):
            instrument = bounds[row["symbol"]]
            self.assertGreaterEqual(row["quantity"], instrument.min_quantity)
            self.assertLessEqual(row["quantity"], instrument.max_quantity)

    def test_limit_price_matches_the_api_business_rule(self) -> None:
        for row in self._orders(5, count=100):
            if row["order_type"] == "Limit":
                self.assertIsNotNone(row["limit_price"])
                self.assertGreater(row["limit_price"], 0)
                self.assertEqual(row["expected_status"], "Pending")
            else:
                self.assertIsNone(row["limit_price"])
                self.assertEqual(row["expected_status"], "Filled")

    def test_boundary_rows_sit_exactly_on_the_limits(self) -> None:
        bounds = {i.symbol: i for i in generate.INSTRUMENTS}
        rows = self._orders(3, count=9, boundaries=True)
        cases = {row["boundary_case"] for row in rows}
        self.assertIn("min", cases)
        self.assertIn("max", cases)
        for row in rows:
            instrument = bounds[row["symbol"]]
            if row["boundary_case"] == "min":
                self.assertEqual(row["quantity"], round(instrument.min_quantity, 2))
            elif row["boundary_case"] == "max":
                self.assertEqual(row["quantity"], round(instrument.max_quantity, 2))


class GenerateUsersTests(unittest.TestCase):
    def setUp(self) -> None:
        rng = random.Random(2024)
        self.rows = generate.generate_users(rng, 20, generate.run_segment(rng))

    def test_all_email_addresses_use_the_reserved_domain(self) -> None:
        for row in self.rows:
            self.assertTrue(row["email"].endswith(f"@{generate.EMAIL_DOMAIN}"), row["email"])

    def test_usernames_are_prefixed_and_unique(self) -> None:
        for row in self.rows:
            self.assertTrue(row["username"].startswith(generate.PREFIX.lower() + "."))
        self.assertEqual(len({r["username"] for r in self.rows}), len(self.rows))

    def test_no_credential_field_is_emitted(self) -> None:
        # Generated data files end up as build artefacts, so they must never carry secrets.
        for row in self.rows:
            self.assertNotIn("password", row)


class RenderingTests(unittest.TestCase):
    def setUp(self) -> None:
        rng = random.Random(11)
        self.rows = generate.generate_orders(rng, 5, generate.run_segment(rng))

    def test_csv_round_trips_with_a_single_newline_per_row(self) -> None:
        text = generate.to_csv(self.rows)
        self.assertNotIn("\r", text)
        parsed = list(csv.DictReader(io.StringIO(text)))
        self.assertEqual(len(parsed), 5)
        self.assertEqual(parsed[0]["symbol"], self.rows[0]["symbol"])

    def test_csv_writes_an_empty_cell_for_an_absent_limit_price(self) -> None:
        rows = [{"external_reference": "QA-ABCDEF-0001", "limit_price": None}]
        self.assertEqual(generate.to_csv(rows), "external_reference,limit_price\nQA-ABCDEF-0001,\n")

    def test_csv_of_no_rows_is_empty_rather_than_an_index_error(self) -> None:
        self.assertEqual(generate.to_csv([]), "")

    def test_json_round_trips(self) -> None:
        self.assertEqual(json.loads(generate.to_json(self.rows)), self.rows)


if __name__ == "__main__":
    unittest.main()
