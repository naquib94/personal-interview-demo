#!/usr/bin/env bash
#
# run-tests.sh - a friendly front end for `dotnet test`.
#
# Why this exists: the raw command needed to run one suite with a TRX logger, a category
# filter and an environment variable is long enough that people either mistype it or
# stop using the filters altogether. Wrapping it once means everyone - and CI - runs the
# suite the same way, and the resolved command is printed so nobody has to trust the
# wrapper blindly.
#
# It intentionally does NOT hide dotnet's output or exit code. A wrapper that swallows
# either is a wrapper that cannot be debugged.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

readonly EXIT_TESTS_FAILED=1
readonly EXIT_USAGE=2
readonly EXIT_PREREQUISITE=3

SUITE="all"
FILTER=""
ENVIRONMENT="local"
HEADED="false"
CONFIGURATION="Debug"
RESULTS_DIR="${REPO_ROOT}/artifacts/test-results"
DRY_RUN="false"

usage() {
    cat <<'USAGE'
Usage: run-tests.sh [options]

Runs the .NET test suites with a consistent logger, filter and environment setup.

Options:
  -s, --suite <name>        api | ui | mobile | framework | all   (default: all)
  -f, --filter <tag>        Test filter. A bare word is treated as a category, so
                            "--filter smoke" becomes --filter TestCategory=smoke.
                            Anything containing '=' or '|' is passed through verbatim.
  -e, --environment <env>   Exported as QA_ENVIRONMENT for the run (default: local)
      --headed              Exported as QA_HEADED=1, for UI runs you want to watch
  -c, --configuration <cfg> Build configuration (default: Debug)
      --results-dir <dir>   Where TRX files are written
                            (default: artifacts/test-results)
      --dry-run             Print the resolved commands and exit without running them
  -h, --help                Show this help and exit

Examples:
  run-tests.sh --suite api
  run-tests.sh --suite ui --filter smoke --environment ci --headed
  run-tests.sh --suite all --filter 'TestCategory=regression|TestCategory=smoke'

Exit codes:
  0  every selected suite passed
  1  at least one test failed
  2  bad arguments
  3  a prerequisite is missing (no dotnet SDK, or no matching test project)
USAGE
}

fail_usage() {
    printf 'run-tests.sh: %s\n\n' "$1" >&2
    usage >&2
    exit "${EXIT_USAGE}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -s|--suite)
            [[ $# -ge 2 ]] || fail_usage "--suite requires a value"
            SUITE="$2"; shift 2 ;;
        -f|--filter)
            [[ $# -ge 2 ]] || fail_usage "--filter requires a value"
            FILTER="$2"; shift 2 ;;
        -e|--environment)
            [[ $# -ge 2 ]] || fail_usage "--environment requires a value"
            ENVIRONMENT="$2"; shift 2 ;;
        -c|--configuration)
            [[ $# -ge 2 ]] || fail_usage "--configuration requires a value"
            CONFIGURATION="$2"; shift 2 ;;
        --results-dir)
            [[ $# -ge 2 ]] || fail_usage "--results-dir requires a value"
            RESULTS_DIR="$2"; shift 2 ;;
        --headed)  HEADED="true"; shift ;;
        --dry-run) DRY_RUN="true"; shift ;;
        -h|--help) usage; exit 0 ;;
        # An unrecognised flag is a mistake, not something to pass through to dotnet.
        # Silently forwarding it produces a confusing MSBuild error two minutes later.
        *) fail_usage "unknown argument '$1'" ;;
    esac
done

case "${SUITE}" in
    api|ui|mobile|framework|all) ;;
    *) fail_usage "unknown suite '${SUITE}' (expected api, ui, mobile, framework or all)" ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    printf 'run-tests.sh: the dotnet SDK is not on PATH.\n' >&2
    printf '  Install it from https://dotnet.microsoft.com/download and reopen the shell.\n' >&2
    exit "${EXIT_PREREQUISITE}"
fi

# Suites are discovered from the project path rather than listed in a hard-coded table,
# so adding tests/Something.Api.Tests needs no change here. The convention that buys
# that: a test project's path contains its suite name.
suite_pattern() {
    case "$1" in
        api)       printf '%s' 'api' ;;
        ui)        printf '%s' 'ui' ;;
        mobile)    printf '%s' 'mobile' ;;
        framework) printf '%s' 'framework' ;;
        all)       printf '%s' '.' ;;
    esac
}

discover_projects() {
    local pattern
    pattern="$(suite_pattern "$1")"
    # grep returning 1 for "no matches" is not an error here, hence the || true; without
    # it, pipefail would abort before the friendlier message below could be printed.
    find "${REPO_ROOT}/tests" -maxdepth 3 -name '*.csproj' 2>/dev/null \
        | grep -iE "${pattern}" \
        | sort \
        || true
}

mapfile -t PROJECTS < <(discover_projects "${SUITE}")

if [[ ${#PROJECTS[@]} -eq 0 ]]; then
    printf 'run-tests.sh: no test project matched suite "%s" under %s/tests.\n' \
        "${SUITE}" "${REPO_ROOT}" >&2
    printf '  Projects are matched on path, so a suite lives in e.g. tests/TradingDemo.Api.Tests.\n' >&2
    printf '  Available test projects:\n' >&2
    if find "${REPO_ROOT}/tests" -maxdepth 3 -name '*.csproj' 2>/dev/null | grep -q .; then
        find "${REPO_ROOT}/tests" -maxdepth 3 -name '*.csproj' | sed 's|^|    |' >&2
    else
        printf '    (none found)\n' >&2
    fi
    exit "${EXIT_PREREQUISITE}"
fi

# A bare word is by far the common case ("--filter smoke"), and TestCategory is the
# VSTest property NUnit's [Category] maps onto. Expressions are left untouched so the
# full filter syntax stays available.
resolve_filter() {
    local raw="$1"
    if [[ -z "${raw}" ]]; then
        printf '%s' ''
    elif [[ "${raw}" == *=* || "${raw}" == *"|"* || "${raw}" == *"&"* ]]; then
        printf '%s' "${raw}"
    else
        printf 'TestCategory=%s' "${raw}"
    fi
}

RESOLVED_FILTER="$(resolve_filter "${FILTER}")"

export QA_ENVIRONMENT="${ENVIRONMENT}"
if [[ "${HEADED}" == "true" ]]; then
    export QA_HEADED=1
fi

mkdir -p "${RESULTS_DIR}"

printf '%s\n' '--------------------------------------------------------------------------'
printf 'Suite         : %s (%d project(s))\n' "${SUITE}" "${#PROJECTS[@]}"
printf 'Environment   : QA_ENVIRONMENT=%s\n' "${QA_ENVIRONMENT}"
printf 'Headed        : %s\n' "${HEADED}"
printf 'Configuration : %s\n' "${CONFIGURATION}"
printf 'Results       : %s\n' "${RESULTS_DIR}"
printf 'Filter        : %s\n' "${RESOLVED_FILTER:-<none>}"
printf '%s\n' '--------------------------------------------------------------------------'

overall_status=0

for project in "${PROJECTS[@]}"; do
    project_name="$(basename "${project}" .csproj)"
    trx_name="${project_name}.trx"

    command_line=(dotnet test "${project}"
        --configuration "${CONFIGURATION}"
        --results-directory "${RESULTS_DIR}"
        --logger "trx;LogFileName=${trx_name}"
        --logger "console;verbosity=normal")

    if [[ -n "${RESOLVED_FILTER}" ]]; then
        command_line+=(--filter "${RESOLVED_FILTER}")
    fi

    # Printing the resolved command is the whole point of a wrapper like this: anyone can
    # copy it, run it by hand, and reproduce exactly what CI did.
    printf '\n> '
    printf '%q ' "${command_line[@]}"
    printf '\n\n'

    if [[ "${DRY_RUN}" == "true" ]]; then
        continue
    fi

    # `if !` rather than a bare call so that one failing suite does not abort the others
    # under set -e. Running every suite gives a complete picture in one go.
    if ! "${command_line[@]}"; then
        printf '\nrun-tests.sh: %s reported failures.\n' "${project_name}" >&2
        overall_status="${EXIT_TESTS_FAILED}"
    fi
done

if [[ "${DRY_RUN}" == "true" ]]; then
    printf 'Dry run: nothing was executed.\n'
    exit 0
fi

if [[ "${overall_status}" -eq 0 ]]; then
    printf '\nAll selected suites passed. TRX files are in %s\n' "${RESULTS_DIR}"
else
    printf '\nOne or more suites failed. Triage with:\n' >&2
    printf '  scripts/triage-failures.sh %s\n' "${RESULTS_DIR}" >&2
    printf '  python scripts/analyse_test_results.py %s/*.trx\n' "${RESULTS_DIR}" >&2
fi

exit "${overall_status}"
