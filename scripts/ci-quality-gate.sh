#!/usr/bin/env bash
#
# ci-quality-gate.sh - the checks a pipeline should run before it calls a build good.
#
# Four gates, in increasing order of how long they take to fail:
#   1. Build warnings    - cheapest signal there is, and warnings are future defects.
#   2. Secret scan       - text only, seconds, and the one failure you cannot undo by
#                          reverting a commit.
#   3. Test results      - pass rate against a threshold, via the Python analyser.
#   4. Backend data      - the data-integrity invariants from database/validation.
#
# Design decisions worth stating:
#   * Every gate runs even after an earlier one fails, then the summary is printed once.
#     A gate script that stops at the first problem turns one CI run per defect into a
#     queue of runs, and people start skipping the gate.
#   * A gate whose tool is missing FAILS by default. A quality gate that passes because
#     Python was not installed is worse than no gate at all. Use --allow-missing-tools
#     locally, never in CI.
#   * The exit code is the contract: 0 means every gate passed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

readonly EXIT_GATE_FAILED=1
readonly EXIT_USAGE=2

MIN_PASS_RATE=95
MAX_WARNINGS=0
RESULTS_DIR="${REPO_ROOT}/artifacts/test-results"
DATABASE=""
ALLOW_MISSING_TOOLS="false"
ALLOW_DATA_VIOLATIONS="false"
SKIP_BUILD="false"
ONLY_GATE=""

usage() {
    cat <<'USAGE'
Usage: ci-quality-gate.sh [options]

Runs the composite quality gate: build warnings, secret scan, test pass rate and
backend data integrity. Prints a summary of every gate and exits non-zero if any failed.

Options:
      --min-pass-rate <pct>   Minimum test pass rate (default: 95)
      --max-warnings <N>      Maximum tolerated build warnings (default: 0)
      --results-dir <dir>     Where to look for .trx files
                              (default: artifacts/test-results)
      --database <path>       SQLite database for the data gate
                              (default: the demo database under src/)
      --only <gate>           Run a single gate: build, secrets, results or data. Used by CI
                              so that each gate is its own status check and a failure names
                              the gate in the job title.
      --skip-build            Skip the build-warnings gate, for when CI has already built
      --allow-missing-tools   Report a missing tool as SKIP instead of FAIL. For local
                              use only: in CI a gate that cannot run must fail.
      --allow-data-violations Treat the backend-data gate as informational. The committed
                              seed data contains one deliberate defect
                              (ORD-20240403-0006 is Filled with no fill rows), so this
                              gate fails by design when run against seed data.
  -h, --help                  Show this help and exit

Examples:
  ci-quality-gate.sh
  ci-quality-gate.sh --min-pass-rate 100 --max-warnings 5
  ci-quality-gate.sh --skip-build --allow-data-violations

Exit codes:
  0  every gate passed
  1  at least one gate failed
  2  bad arguments
USAGE
}

fail_usage() {
    printf 'ci-quality-gate.sh: %s\n\n' "$1" >&2
    usage >&2
    exit "${EXIT_USAGE}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --min-pass-rate)
            [[ $# -ge 2 ]] || fail_usage "--min-pass-rate requires a value"
            MIN_PASS_RATE="$2"; shift 2 ;;
        --max-warnings)
            [[ $# -ge 2 ]] || fail_usage "--max-warnings requires a value"
            MAX_WARNINGS="$2"; shift 2 ;;
        --results-dir)
            [[ $# -ge 2 ]] || fail_usage "--results-dir requires a value"
            RESULTS_DIR="$2"; shift 2 ;;
        --database)
            [[ $# -ge 2 ]] || fail_usage "--database requires a value"
            DATABASE="$2"; shift 2 ;;
        --only)
            # Lets a pipeline run one gate as its own job, so that a failure names the gate in
            # the job title rather than requiring somebody to read the log. The workflow uses
            # this for the secret scan, which is worth reporting as a distinct status check.
            [[ $# -ge 2 ]] || fail_usage "--only requires a gate name"
            case "$2" in
                build|secrets|results|data) ONLY_GATE="$2" ;;
                *) fail_usage "--only must be one of: build, secrets, results, data" ;;
            esac
            shift 2 ;;
        --skip-build)            SKIP_BUILD="true"; shift ;;
        --allow-missing-tools)   ALLOW_MISSING_TOOLS="true"; shift ;;
        --allow-data-violations) ALLOW_DATA_VIOLATIONS="true"; shift ;;
        -h|--help)               usage; exit 0 ;;
        *) fail_usage "unknown argument '$1'" ;;
    esac
done

[[ "${MAX_WARNINGS}" =~ ^[0-9]+$ ]] || fail_usage "--max-warnings must be a non-negative integer"
[[ "${MIN_PASS_RATE}" =~ ^[0-9]+([.][0-9]+)?$ ]] || fail_usage "--min-pass-rate must be a number"

# --------------------------------------------------------------------------------------
# Gate bookkeeping
# --------------------------------------------------------------------------------------
declare -a GATE_NAME=()
declare -a GATE_RESULT=()
declare -a GATE_NOTE=()

record_gate() {
    GATE_NAME+=("$1")
    GATE_RESULT+=("$2")
    GATE_NOTE+=("$3")
    printf '\n[%s] %s - %s\n' "$2" "$1" "$3"
}

# Result to use when a required tool is absent. See the note at the top of the file.
missing_tool_result() {
    if [[ "${ALLOW_MISSING_TOOLS}" == "true" ]]; then printf 'SKIP'; else printf 'FAIL'; fi
}

# Windows ships a python.exe stub that exists on PATH but exits non-zero and opens the
# Microsoft Store. `command -v` therefore proves nothing; the interpreter has to be
# executed to know whether it is real.
find_python() {
    local candidate
    for candidate in python3 python py; do
        if command -v "${candidate}" >/dev/null 2>&1; then
            if "${candidate}" -c 'import sys; sys.exit(0)' >/dev/null 2>&1; then
                printf '%s' "${candidate}"
                return 0
            fi
        fi
    done
    return 1
}

# || true because "no Python at all" is a state this script reports rather than dies on.
PYTHON="$(find_python || true)"

TEMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "${TEMP_DIR}"; }
trap cleanup EXIT

printf '%s\n' '=========================================================================='
printf 'QUALITY GATE\n'
printf 'Repository    : %s\n' "${REPO_ROOT}"
printf 'Min pass rate : %s%%\n' "${MIN_PASS_RATE}"
printf 'Max warnings  : %s\n' "${MAX_WARNINGS}"
printf 'Python        : %s\n' "${PYTHON:-<not available>}"
printf '%s\n' '=========================================================================='

# --------------------------------------------------------------------------------------
# Gate 1: build warnings
# --------------------------------------------------------------------------------------
gate_build_warnings() {
    if [[ "${SKIP_BUILD}" == "true" ]]; then
        record_gate "Build warnings" "SKIP" "skipped by --skip-build"
        return
    fi
    if ! command -v dotnet >/dev/null 2>&1; then
        record_gate "Build warnings" "$(missing_tool_result)" "the dotnet SDK is not on PATH"
        return
    fi

    local -a projects=()
    mapfile -t projects < <(
        find "${REPO_ROOT}/src" "${REPO_ROOT}/tests" -maxdepth 3 -name '*.csproj' 2>/dev/null \
            | sort || true
    )
    if [[ ${#projects[@]} -eq 0 ]]; then
        record_gate "Build warnings" "$(missing_tool_result)" "no .csproj found under src/ or tests/"
        return
    fi

    local log="${TEMP_DIR}/build.log"
    local build_failed="false"
    local project
    for project in "${projects[@]}"; do
        printf '  building %s\n' "$(basename "${project}")"
        if ! dotnet build "${project}" --configuration Release --nologo >>"${log}" 2>&1; then
            build_failed="true"
        fi
    done

    if [[ "${build_failed}" == "true" ]]; then
        record_gate "Build warnings" "FAIL" "the build itself failed - see the errors below"
        grep -E ': error ' "${log}" | sort -u | head -n 20 | sed 's|^|    |' || true
        return
    fi

    # Counting distinct warning lines rather than raw ones: MSBuild repeats the same
    # warning once per referencing project, which would otherwise inflate the number.
    # The `|| true` matters: grep exits 1 when it finds nothing, which under pipefail
    # would abort the script at exactly the moment there is good news to report.
    local count
    count="$({ grep -E ': warning [A-Z]+[0-9]+' "${log}" || true; } | sort -u | wc -l | tr -d ' ')"

    if [[ "${count}" -gt "${MAX_WARNINGS}" ]]; then
        record_gate "Build warnings" "FAIL" "${count} distinct warning(s), limit is ${MAX_WARNINGS}"
        grep -E ': warning [A-Z]+[0-9]+' "${log}" | sort -u | head -n 20 | sed 's|^|    |' || true
    else
        record_gate "Build warnings" "PASS" "${count} distinct warning(s), limit is ${MAX_WARNINGS}"
    fi
}

# --------------------------------------------------------------------------------------
# Gate 2: secret scan
# --------------------------------------------------------------------------------------
# Deliberately narrow patterns. A scan that greps for the word "password" fires on every
# column name and validation message, everyone learns to ignore it, and the one real
# finding is lost in the noise. These match the *shape* of a credential instead:
# an assignment to a quoted literal, or a well-known token format.
readonly SECRET_PATTERNS=(
    '(password|passwd|pwd|secret|apikey|api_key|api-key|access_token|client_secret)[[:space:]]*[:=][[:space:]]*["'"'"'][^"'"'"']{6,}["'"'"']'
    'BEGIN[[:space:]]+([A-Z]+[[:space:]]+)?PRIVATE[[:space:]]+KEY'
    'AKIA[0-9A-Z]{16}'
    'gh[pousr]_[A-Za-z0-9]{20,}'
    'xox[baprs]-[A-Za-z0-9-]{10,}'
    'Bearer[[:space:]]+[A-Za-z0-9._-]{30,}'
)

# Documented allowlist. Each entry needs a reason, and the reason has to survive being
# read out loud in a review - that is the whole control here.
readonly SECRET_ALLOWLIST=(
    'Demo!Pass123'   # The single throwaway password for every seeded demo account. It
                     # unlocks nothing but a local SQLite file, is documented in
                     # database/seed/002_seed.sql, and exists so the demo is runnable.
    'password_hash'  # Column names in the schema, not credentials.
    'password_salt'
    'QA_DATA_SEED'   # A random-number seed, not a secret, despite the name.
    # Values that name themselves as placeholders. Matching on the *value* rather than
    # on a file path is what keeps this honest: it allows "expectedPassword" anywhere,
    # and still fails on a real-looking string in the same file.
    '["'"'"'](expected|sample|dummy|placeholder|changeme|redacted|example|synthetic)[a-z0-9_.!-]*["'"'"']'
    # Self-describing negative fixtures. A test that proves a WRONG password is rejected has to
    # contain a wrong password, and "not-a-real-password" says so in the value itself. Same
    # reasoning as the entry above: the allowlist matches the value, so a genuine-looking
    # credential in the very same file still fails the gate.
    'not-a-real-[a-z-]*'
    'not-the-password'
)

join_by_pipe() {
    local IFS='|'
    printf '%s' "$*"
}

gate_secret_scan() {
    local patterns allow findings count
    patterns="$(join_by_pipe "${SECRET_PATTERNS[@]}")"
    allow="$(join_by_pipe "${SECRET_ALLOWLIST[@]}")"

    # This script is excluded from its own scan: the patterns above are, by construction,
    # things that look like secrets.
    # -i because the same key appears as apiKey, ApiKey and api_key across C#, JSON and
    # shell, and a case-sensitive scan would only ever catch one of the three.
    findings="$(
        grep -rIniE --binary-files=without-match \
            --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj \
            --exclude-dir=artifacts --exclude-dir=node_modules --exclude-dir=packages \
            --exclude='*.trx' --exclude='ci-quality-gate.sh' \
            -e "${patterns}" "${REPO_ROOT}" 2>/dev/null \
            | grep -viE "${allow}" || true
    )"

    if [[ -z "${findings}" ]]; then
        record_gate "Secret scan" "PASS" "no credential-shaped literals outside the allowlist"
        return
    fi

    count="$(printf '%s\n' "${findings}" | wc -l | tr -d ' ')"
    record_gate "Secret scan" "FAIL" "${count} suspicious line(s) - review each before merging"
    printf '%s\n' "${findings}" | head -n 20 | sed 's|^|    |'
    printf '    (allowlist entries are documented in %s)\n' "$(basename "${BASH_SOURCE[0]}")"
}

# --------------------------------------------------------------------------------------
# Gate 3: test results
# --------------------------------------------------------------------------------------
gate_test_results() {
    if [[ -z "${PYTHON}" ]]; then
        record_gate "Test pass rate" "$(missing_tool_result)" \
            "no working Python interpreter found (tried python3, python, py)"
        return
    fi
    if [[ ! -d "${RESULTS_DIR}" ]]; then
        record_gate "Test pass rate" "FAIL" \
            "no results directory at ${RESULTS_DIR} - the suite did not run"
        return
    fi

    local -a trx=()
    mapfile -t trx < <(find "${RESULTS_DIR}" -type f -name '*.trx' | sort || true)
    if [[ ${#trx[@]} -eq 0 ]]; then
        record_gate "Test pass rate" "FAIL" \
            "no .trx files under ${RESULTS_DIR} - treating an absent result set as a failure"
        return
    fi

    if "${PYTHON}" "${SCRIPT_DIR}/analyse_test_results.py" "${trx[@]}" \
        --fail-under "${MIN_PASS_RATE}" --slowest 5; then
        record_gate "Test pass rate" "PASS" "pass rate is at or above ${MIN_PASS_RATE}%"
    else
        record_gate "Test pass rate" "FAIL" "pass rate is below ${MIN_PASS_RATE}% (see the clusters above)"
    fi
}

# --------------------------------------------------------------------------------------
# Gate 4: backend data integrity
# --------------------------------------------------------------------------------------
gate_backend_data() {
    if [[ -z "${PYTHON}" ]]; then
        record_gate "Backend data integrity" "$(missing_tool_result)" \
            "no working Python interpreter found (tried python3, python, py)"
        return
    fi

    local -a command_line=("${PYTHON}" "${SCRIPT_DIR}/verify_backend_data.py")
    if [[ -n "${DATABASE}" ]]; then
        command_line+=(--database "${DATABASE}")
    fi

    local status=0
    "${command_line[@]}" || status=$?

    if [[ "${status}" -eq 0 ]]; then
        record_gate "Backend data integrity" "PASS" "every invariant holds"
    elif [[ "${status}" -eq 1 && "${ALLOW_DATA_VIOLATIONS}" == "true" ]]; then
        record_gate "Backend data integrity" "SKIP" \
            "violations found but downgraded by --allow-data-violations (expected against seed data)"
    elif [[ "${status}" -eq 1 ]]; then
        record_gate "Backend data integrity" "FAIL" "at least one data invariant was violated"
    else
        record_gate "Backend data integrity" "FAIL" \
            "the verifier could not run (exit ${status}) - check the database path"
    fi
}

# Every gate runs before the summary is printed, deliberately: stopping at the first failure
# would mean fixing one problem only to discover the next on the following run.
if [[ -n "${ONLY_GATE}" ]]; then
    case "${ONLY_GATE}" in
        build)   gate_build_warnings ;;
        secrets) gate_secret_scan ;;
        results) gate_test_results ;;
        data)    gate_backend_data ;;
    esac
else
    gate_build_warnings
    gate_secret_scan
    gate_test_results
    gate_backend_data
fi

# --------------------------------------------------------------------------------------
# Summary
# --------------------------------------------------------------------------------------
printf '\n%s\n' '=========================================================================='
printf 'GATE SUMMARY\n'
printf '%s\n' '=========================================================================='

failed=0
skipped=0
for index in "${!GATE_NAME[@]}"; do
    printf '  %-6s %-24s %s\n' "${GATE_RESULT[$index]}" "${GATE_NAME[$index]}" "${GATE_NOTE[$index]}"
    case "${GATE_RESULT[$index]}" in
        FAIL) failed=$((failed + 1)) ;;
        SKIP) skipped=$((skipped + 1)) ;;
    esac
done

printf '%s\n' '--------------------------------------------------------------------------'
if [[ "${failed}" -gt 0 ]]; then
    printf 'RESULT: FAILED - %d gate(s) failed, %d skipped.\n' "${failed}" "${skipped}" >&2
    exit "${EXIT_GATE_FAILED}"
fi

if [[ "${skipped}" -gt 0 ]]; then
    printf 'RESULT: PASSED with %d skipped gate(s). A skipped gate proves nothing;\n' "${skipped}"
    printf '        CI should run without --allow-missing-tools so skips become failures.\n'
else
    printf 'RESULT: PASSED - all %d gates.\n' "${#GATE_NAME[@]}"
fi
exit 0
