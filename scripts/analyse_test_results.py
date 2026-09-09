#!/usr/bin/env python3
"""Summarise a .NET TRX test result file into something a human can act on.

The problem this solves: a CI log with 40 failures is unreadable, and 39 of those
failures are usually the same root cause (an environment that never came up, an
expired token, a schema that was not migrated). This script collapses failures
into clusters by normalised message, so triage starts from causes rather than
symptoms, and it can gate a pipeline on pass rate.

Standard library only, deliberately: a script that gates a pipeline must not be
the reason the pipeline cannot install its own dependencies.
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Sequence

# The TRX namespace. Every element in a TRX file is namespaced, which is why naive
# root.findall("Results/UnitTestResult") silently returns nothing - the single most
# common mistake when parsing these files.
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

EXIT_OK = 0
EXIT_GATE_FAILED = 1
EXIT_USAGE = 2

# TRX outcome vocabulary is larger than Passed/Failed/Skipped. Anything not listed as
# passed or skipped is counted as a failure on purpose: an unrecognised outcome must
# never be able to make a red build look green.
_PASSED = frozenset({"passed"})
_SKIPPED = frozenset(
    {"notexecuted", "skipped", "inconclusive", "pending", "notrunnable", "blocked", "disconnected"}
)

# Volatile fragments are replaced before clustering, otherwise every failure looks
# unique because it carries a different order reference or timestamp. Order matters:
# the specific patterns must run before the generic number pattern.
_VOLATILE_PATTERNS: Sequence[tuple[re.Pattern[str], str]] = (
    (re.compile(r"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b"), "<guid>"),
    (re.compile(r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?"), "<timestamp>"),
    (re.compile(r"\b(?:ORD|QA|DEMO)-[A-Za-z0-9-]+\b"), "<reference>"),
    (re.compile(r"\b0x[0-9a-fA-F]+\b"), "<hex>"),
    (re.compile(r"\b\d+(?:\.\d+)?\b"), "<n>"),
)

_MAX_SIGNATURE_LENGTH = 160


class TrxError(Exception):
    """Raised for anything that makes a TRX file unusable, so main() can print one
    clear line instead of a traceback."""


@dataclass(frozen=True)
class TestResult:
    name: str
    outcome: str
    duration_seconds: float
    message: str = ""
    stack_trace: str = ""

    @property
    def classification(self) -> str:
        return classify_outcome(self.outcome)


@dataclass(frozen=True)
class Summary:
    total: int
    passed: int
    failed: int
    skipped: int
    total_duration_seconds: float
    median_duration_seconds: float

    @property
    def executed(self) -> int:
        return self.passed + self.failed

    @property
    def pass_rate(self) -> float:
        """Pass rate over *executed* tests, so skipped tests neither help nor hurt.

        Trade-off, stated plainly: a suite that skips 90% of its tests would still
        report 100% here. That is why the skipped count is printed next to the rate
        and why a pipeline should also assert on total count, not just pass rate.
        """
        if self.executed == 0:
            return 0.0
        return self.passed / self.executed * 100.0


@dataclass(frozen=True)
class FailureCluster:
    signature: str
    count: int
    example_message: str
    test_names: tuple[str, ...]


def _local_name(tag: str) -> str:
    """Return an element's name without its namespace.

    Stripping the namespace rather than matching on it means the parser also copes
    with the occasional namespace-free TRX produced by third-party tooling.
    """
    return tag.rsplit("}", 1)[-1]


def classify_outcome(outcome: Optional[str]) -> str:
    """Map a TRX outcome attribute onto passed / failed / skipped."""
    normalised = (outcome or "").strip().lower()
    if normalised in _PASSED:
        return "passed"
    if normalised in _SKIPPED:
        return "skipped"
    return "failed"


def parse_duration(value: Optional[str]) -> float:
    """Parse a TRX duration ("00:00:01.2345678") into seconds.

    Returns 0.0 rather than raising on anything unexpected: a malformed duration on
    one test is not a reason to abandon the whole report.
    """
    if value is None:
        return 0.0
    text = value.strip()
    if not text:
        return 0.0
    parts = text.split(":")
    try:
        if len(parts) == 1:
            return float(parts[0])
        if len(parts) == 2:
            minutes, seconds = parts
            return int(minutes) * 60 + float(seconds)
        if len(parts) == 3:
            hours, minutes, seconds = parts
            return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        if len(parts) == 4:
            days, hours, minutes, seconds = parts
            return int(days) * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except ValueError:
        return 0.0
    return 0.0


def _element_to_result(node: ET.Element) -> TestResult:
    message = ""
    stack_trace = ""
    # ErrorInfo sits under Output, but its exact depth has varied between test hosts,
    # so search the subtree instead of hard-coding the path.
    for child in node.iter():
        name = _local_name(child.tag)
        if name == "Message" and not message:
            message = (child.text or "").strip()
        elif name == "StackTrace" and not stack_trace:
            stack_trace = (child.text or "").strip()

    return TestResult(
        name=node.get("testName") or "<unnamed test>",
        outcome=node.get("outcome") or "Unknown",
        duration_seconds=parse_duration(node.get("duration")),
        message=message,
        stack_trace=stack_trace,
    )


def _flatten_result(node: ET.Element) -> list[TestResult]:
    """Expand a result element, preferring inner results over their parent.

    Data-driven tests ([TestCase] in NUnit) emit a parent aggregate plus one inner
    result per case. Counting both double-counts every parameterised test, so the
    parent is dropped whenever it has children.
    """
    inner: list[TestResult] = []
    for holder in node:
        if _local_name(holder.tag) != "InnerResults":
            continue
        for child in holder:
            if _local_name(child.tag).endswith("TestResult"):
                inner.extend(_flatten_result(child))
    if inner:
        return inner
    return [_element_to_result(node)]


def _top_level_result_nodes(root: ET.Element) -> list[ET.Element]:
    """Find the direct children of <Results>, namespace first.

    The namespaced query is the correct one for a real TRX file and is tried first.
    The namespace-agnostic walk behind it is not belt-and-braces for its own sake:
    trimmed-down TRX files pasted into bug reports routinely lose the xmlns, and a
    triage tool that returns "0 tests" for one of those is useless exactly when it
    is needed.
    """
    nodes = [
        node
        for node in root.findall(f"{{{TRX_NAMESPACE}}}Results/*")
        if _local_name(node.tag).endswith("TestResult")
    ]
    if nodes:
        return nodes

    return [
        child
        for parent in root.iter()
        if _local_name(parent.tag) == "Results"
        for child in parent
        if _local_name(child.tag).endswith("TestResult")
    ]


def extract_results(root: ET.Element) -> list[TestResult]:
    """Pull every leaf test result out of a parsed TRX document."""
    results: list[TestResult] = []
    for node in _top_level_result_nodes(root):
        results.extend(_flatten_result(node))
    return results


def load_trx(path: Path) -> list[TestResult]:
    """Parse one TRX file, translating every failure mode into a readable message."""
    try:
        tree = ET.parse(str(path))
    except FileNotFoundError:
        raise TrxError(
            f"no TRX file at '{path}'. Run the suite with "
            '`dotnet test --logger "trx;LogFileName=results.trx"` first.'
        ) from None
    except PermissionError:
        raise TrxError(f"'{path}' cannot be read (permission denied).") from None
    except IsADirectoryError:
        raise TrxError(f"'{path}' is a directory; pass the .trx file itself.") from None
    except ET.ParseError as exc:
        raise TrxError(
            f"'{path}' is not well-formed XML ({exc}). A truncated TRX almost always "
            "means the test host crashed mid-run - check the runner log for a stack overflow "
            "or an OutOfMemoryException."
        ) from None
    except OSError as exc:
        raise TrxError(f"'{path}' could not be read: {exc}.") from None

    root = tree.getroot()
    if _local_name(root.tag) != "TestRun":
        raise TrxError(
            f"'{path}' does not look like a TRX file: the root element is "
            f"'{_local_name(root.tag)}', expected 'TestRun'."
        )
    return extract_results(root)


def load_many(paths: Sequence[Path]) -> list[TestResult]:
    """Merge several TRX files, which is what a multi-suite pipeline actually produces."""
    merged: list[TestResult] = []
    for path in paths:
        merged.extend(load_trx(path))
    return merged


def summarise(results: Sequence[TestResult]) -> Summary:
    passed = sum(1 for r in results if r.classification == "passed")
    failed = sum(1 for r in results if r.classification == "failed")
    skipped = sum(1 for r in results if r.classification == "skipped")
    durations = [r.duration_seconds for r in results if r.classification != "skipped"]
    return Summary(
        total=len(results),
        passed=passed,
        failed=failed,
        skipped=skipped,
        total_duration_seconds=sum(r.duration_seconds for r in results),
        median_duration_seconds=statistics.median(durations) if durations else 0.0,
    )


def normalise_message(message: str) -> str:
    """Reduce a failure message to a clusterable signature.

    Only the first non-empty line is used. Assertion libraries put the essential
    difference on that line and the rest is context, so including the tail would
    split one root cause into many clusters.
    """
    first_line = ""
    for line in message.splitlines():
        stripped = line.strip()
        if stripped:
            first_line = stripped
            break
    if not first_line:
        return "<no failure message captured>"

    signature = first_line
    for pattern, replacement in _VOLATILE_PATTERNS:
        signature = pattern.sub(replacement, signature)
    signature = re.sub(r"\s+", " ", signature).strip()
    if len(signature) > _MAX_SIGNATURE_LENGTH:
        signature = signature[:_MAX_SIGNATURE_LENGTH].rstrip() + "..."
    return signature


def cluster_failures(
    results: Sequence[TestResult], max_names_per_cluster: int = 5
) -> list[FailureCluster]:
    """Group failures by normalised message, largest cluster first."""
    buckets: dict[str, list[TestResult]] = {}
    for result in results:
        if result.classification != "failed":
            continue
        buckets.setdefault(normalise_message(result.message), []).append(result)

    clusters = [
        FailureCluster(
            signature=signature,
            count=len(members),
            example_message=members[0].message,
            test_names=tuple(m.name for m in members[:max_names_per_cluster]),
        )
        for signature, members in buckets.items()
    ]
    # Stable secondary sort on the signature keeps output diffable between runs.
    clusters.sort(key=lambda c: (-c.count, c.signature))
    return clusters


def slowest_tests(results: Sequence[TestResult], count: int) -> list[TestResult]:
    """The slowest executed tests. Skipped tests are excluded - a skip is not fast."""
    executed = [r for r in results if r.classification != "skipped"]
    executed.sort(key=lambda r: (-r.duration_seconds, r.name))
    return executed[: max(count, 0)]


def _first_line(text: str, limit: int = 200) -> str:
    for line in text.splitlines():
        stripped = line.strip()
        if stripped:
            return stripped[:limit]
    return ""


def render_text(
    summary: Summary,
    clusters: Sequence[FailureCluster],
    slowest: Sequence[TestResult],
    sources: Sequence[Path],
) -> str:
    lines: list[str] = []
    lines.append("=" * 78)
    lines.append("TEST RESULT SUMMARY")
    lines.append("=" * 78)
    lines.append("Source: " + ", ".join(str(p) for p in sources))
    lines.append("")
    lines.append(f"  Total tests    : {summary.total}")
    lines.append(f"  Passed         : {summary.passed}")
    lines.append(f"  Failed         : {summary.failed}")
    lines.append(f"  Skipped        : {summary.skipped}")
    lines.append(f"  Pass rate      : {summary.pass_rate:.2f}%  (of {summary.executed} executed)")
    lines.append(f"  Total duration : {summary.total_duration_seconds:.2f}s")
    lines.append(f"  Median test    : {summary.median_duration_seconds:.3f}s")
    lines.append("")

    lines.append("-" * 78)
    lines.append(f"FAILURE CLUSTERS ({len(clusters)} distinct cause(s) behind {summary.failed} failure(s))")
    lines.append("-" * 78)
    if not clusters:
        lines.append("  None.")
    for index, cluster in enumerate(clusters, start=1):
        lines.append(f"  [{index}] {cluster.count} failure(s): {cluster.signature}")
        for name in cluster.test_names:
            lines.append(f"        - {name}")
        remaining = cluster.count - len(cluster.test_names)
        if remaining > 0:
            lines.append(f"        ... and {remaining} more")
        example = _first_line(cluster.example_message)
        if example and example != cluster.signature:
            lines.append(f"        example: {example}")
        lines.append("")

    lines.append("-" * 78)
    lines.append(f"SLOWEST {len(slowest)} TEST(S)")
    lines.append("-" * 78)
    if not slowest:
        lines.append("  None.")
    for result in slowest:
        lines.append(f"  {result.duration_seconds:8.3f}s  {result.name}")
    lines.append("")
    return "\n".join(lines)


def render_json(
    summary: Summary,
    clusters: Sequence[FailureCluster],
    slowest: Sequence[TestResult],
    sources: Sequence[Path],
) -> str:
    payload = {
        "sources": [str(p) for p in sources],
        "summary": {
            "total": summary.total,
            "passed": summary.passed,
            "failed": summary.failed,
            "skipped": summary.skipped,
            "executed": summary.executed,
            "passRate": round(summary.pass_rate, 4),
            "totalDurationSeconds": round(summary.total_duration_seconds, 4),
            "medianDurationSeconds": round(summary.median_duration_seconds, 4),
        },
        "failureClusters": [
            {
                "signature": c.signature,
                "count": c.count,
                "exampleMessage": c.example_message,
                "testNames": list(c.test_names),
            }
            for c in clusters
        ],
        "slowestTests": [
            {"name": r.name, "durationSeconds": round(r.duration_seconds, 4), "outcome": r.outcome}
            for r in slowest
        ],
    }
    return json.dumps(payload, indent=2)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="analyse_test_results.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=(
            "Summarise one or more .NET TRX files: totals, pass rate, slowest tests, and "
            "failures grouped by root cause so that 40 failures with one cause read as one "
            "cluster instead of 40 lines."
        ),
        epilog=(
            "Examples:\n"
            "  analyse_test_results.py artifacts/test-results/api.trx\n"
            "  analyse_test_results.py artifacts/test-results/*.trx --slowest 10\n"
            "  analyse_test_results.py api.trx --fail-under 95 --format json\n"
            "\n"
            "Exit codes:\n"
            "  0  report produced (and the pass-rate gate, if any, was met)\n"
            "  1  --fail-under threshold not met, or the file contained no results\n"
            "  2  the TRX file is missing, unreadable, or not valid TRX\n"
        ),
    )
    parser.add_argument(
        "trx",
        nargs="+",
        type=Path,
        metavar="TRX",
        help="one or more .trx files; several are merged into a single report",
    )
    parser.add_argument(
        "--slowest",
        type=int,
        default=5,
        metavar="N",
        help="how many of the slowest tests to list (default: 5)",
    )
    parser.add_argument(
        "--max-clusters",
        type=int,
        default=10,
        metavar="N",
        help="how many failure clusters to print, largest first (default: 10)",
    )
    parser.add_argument(
        "--fail-under",
        type=float,
        default=None,
        metavar="PERCENT",
        help="exit 1 if the pass rate is below this percentage; use this to gate a pipeline",
    )
    parser.add_argument(
        "--format",
        choices=("text", "json"),
        default="text",
        help="text for humans, json for a downstream job (default: text)",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.slowest < 0 or args.max_clusters < 0:
        parser.error("--slowest and --max-clusters must not be negative")
    if args.fail_under is not None and not 0.0 <= args.fail_under <= 100.0:
        parser.error("--fail-under must be a percentage between 0 and 100")

    try:
        results = load_many(args.trx)
    except TrxError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_USAGE

    summary = summarise(results)
    clusters = cluster_failures(results)[: args.max_clusters]
    slowest = slowest_tests(results, args.slowest)

    renderer = render_json if args.format == "json" else render_text
    print(renderer(summary, clusters, slowest, args.trx))

    if args.fail_under is None:
        return EXIT_OK

    if summary.executed == 0:
        # A gate that passes on an empty result set is worse than no gate: it is the
        # failure mode where the runner never started and the build went green.
        print(
            "error: no executed tests were found, so the pass rate is meaningless. "
            "Treating this as a gate failure.",
            file=sys.stderr,
        )
        return EXIT_GATE_FAILED

    # Small epsilon so that a genuine 95.0% is not rejected by float representation.
    if summary.pass_rate < args.fail_under - 1e-9:
        print(
            f"error: pass rate {summary.pass_rate:.2f}% is below the required "
            f"{args.fail_under:.2f}%.",
            file=sys.stderr,
        )
        return EXIT_GATE_FAILED

    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
