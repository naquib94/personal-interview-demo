#!/usr/bin/env bash
#
# triage-failures.sh - turn a directory of test artefacts into a triage summary.
#
# The job this does is the first fifteen minutes after a red build: which tests failed,
# what did they say, and is there a screenshot to look at. Doing it with grep/sed/awk
# means it works on any build agent, over SSH, inside a container, with no runtime to
# install - which is exactly when you need it most.
#
# Honest limitation: TRX is XML, and this script treats it as text. It extracts the first
# <Message> per result and assumes messages contain no raw '<'. That is true of the
# assertion libraries used here and false in general. When the text approach is not
# enough, scripts/analyse_test_results.py parses the file properly and clusters failures
# by root cause; this script is the zero-dependency first look.
#
# Second known limitation: a data-driven test emits a parent aggregate plus one result
# per case, so its failure is counted twice here. The Python analyser resolves that
# properly by walking <InnerResults>; getting it right in pure text is not worth it.

set -euo pipefail

readonly EXIT_USAGE=2

ARTEFACT_DIR=""
DETAIL_LINES=5
MAX_FAILURES=0   # 0 means "no limit"

usage() {
    cat <<'USAGE'
Usage: triage-failures.sh [options] <artefact-directory>

Summarises failing tests found in .trx files under the given directory, and lists any
captured screenshots alongside them.

Options:
  -n, --lines <N>       Lines of failure detail to show per test (default: 5)
  -m, --max <N>         Show at most N failures in detail; 0 for all (default: 0)
  -h, --help            Show this help and exit

Examples:
  triage-failures.sh artifacts/test-results
  triage-failures.sh --lines 12 --max 5 artifacts

Exit codes:
  0  the directory was scanned successfully (whether or not failures were found)
  2  bad arguments, or the directory does not exist
USAGE
}

fail_usage() {
    printf 'triage-failures.sh: %s\n\n' "$1" >&2
    usage >&2
    exit "${EXIT_USAGE}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -n|--lines)
            [[ $# -ge 2 ]] || fail_usage "--lines requires a value"
            DETAIL_LINES="$2"; shift 2 ;;
        -m|--max)
            [[ $# -ge 2 ]] || fail_usage "--max requires a value"
            MAX_FAILURES="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        -*) fail_usage "unknown option '$1'" ;;
        *)
            [[ -z "${ARTEFACT_DIR}" ]] || fail_usage "only one artefact directory may be given"
            ARTEFACT_DIR="$1"; shift ;;
    esac
done

[[ -n "${ARTEFACT_DIR}" ]] || fail_usage "an artefact directory is required"
[[ "${DETAIL_LINES}" =~ ^[0-9]+$ ]] || fail_usage "--lines must be a non-negative integer"
[[ "${MAX_FAILURES}" =~ ^[0-9]+$ ]] || fail_usage "--max must be a non-negative integer"

if [[ ! -d "${ARTEFACT_DIR}" ]]; then
    printf 'triage-failures.sh: no such directory: %s\n' "${ARTEFACT_DIR}" >&2
    printf '  Run the suite first: scripts/run-tests.sh --suite all\n' >&2
    exit "${EXIT_USAGE}"
fi

# XML entities and CDATA wrappers make raw TRX text unreadable. Decoding them here keeps
# the reporting code below focused on layout. &amp; is decoded last, otherwise a literal
# "&amp;lt;" would be double-decoded.
decode_xml() {
    sed -e 's/&#x0*D;//g' \
        -e 's/&#x0*A;/\n/g' \
        -e 's/&#10;/\n/g' \
        -e 's/&lt;/</g' \
        -e 's/&gt;/>/g' \
        -e 's/&quot;/"/g' \
        -e 's/&apos;/'"'"'/g' \
        -e 's/&amp;/\&/g'
}

# TRX files are usually a single enormous line, so the file is first split into one line
# per <UnitTestResult>. The marker makes those records greppable without needing a
# multi-character record separator, which not every awk implementation supports.
readonly RECORD_MARKER='@@TRXRESULT@@'

split_results() {
    sed "s|<UnitTestResult |\\n${RECORD_MARKER}|g" "$1" | grep "^${RECORD_MARKER}" || true
}

attribute() {
    # $1 = record, $2 = attribute name. Returns empty if the attribute is absent.
    printf '%s' "$1" | sed -n "s|.*$2=\"\\([^\"]*\\)\".*|\\1|p" | head -n 1
}

first_message() {
    printf '%s' "$1" | grep -o '<Message>[^<]*</Message>' \
        | head -n 1 \
        | sed -e 's|<Message>||' -e 's|</Message>||' \
        || true
}

mapfile -t TRX_FILES < <(find "${ARTEFACT_DIR}" -type f -name '*.trx' | sort || true)

printf '%s\n' '=========================================================================='
printf 'FAILURE TRIAGE: %s\n' "${ARTEFACT_DIR}"
printf '%s\n' '=========================================================================='

if [[ ${#TRX_FILES[@]} -eq 0 ]]; then
    printf 'No .trx files found under %s.\n' "${ARTEFACT_DIR}" >&2
    printf 'Run the suite with a TRX logger first: scripts/run-tests.sh --suite all\n' >&2
    exit 0
fi

total_passed=0
total_failed=0
total_skipped=0
shown_failures=0
declare -a FAILED_NAMES=()

for trx in "${TRX_FILES[@]}"; do
    file_failed=0
    printf '\n--- %s\n' "${trx}"

    while IFS= read -r record; do
        [[ -n "${record}" ]] || continue
        outcome="$(attribute "${record}" 'outcome')"
        name="$(attribute "${record}" 'testName')"
        [[ -n "${name}" ]] || continue

        case "${outcome}" in
            Passed)
                total_passed=$((total_passed + 1)) ;;
            NotExecuted|Inconclusive|Skipped)
                total_skipped=$((total_skipped + 1)) ;;
            Failed|Timeout|Aborted|Error)
                total_failed=$((total_failed + 1))
                file_failed=$((file_failed + 1))
                FAILED_NAMES+=("${name}")

                if [[ "${MAX_FAILURES}" -ne 0 && "${shown_failures}" -ge "${MAX_FAILURES}" ]]; then
                    continue
                fi
                shown_failures=$((shown_failures + 1))

                printf '\n  FAILED: %s\n' "${name}"
                duration="$(attribute "${record}" 'duration')"
                if [[ -n "${duration}" ]]; then
                    printf '    duration: %s\n' "${duration}"
                fi

                message="$(first_message "${record}")"
                if [[ -n "${message}" ]]; then
                    printf '%s\n' "${message}" \
                        | decode_xml \
                        | head -n "${DETAIL_LINES}" \
                        | sed 's|^|    |'
                else
                    printf '    (no failure message captured - check the console log)\n'
                fi
                ;;
            *)
                # An unrecognised outcome is reported rather than ignored: a silently
                # dropped result is how a red run gets read as green.
                printf '\n  UNKNOWN OUTCOME "%s": %s\n' "${outcome:-<none>}" "${name}" ;;
        esac
    done < <(split_results "${trx}")

    if [[ "${file_failed}" -eq 0 ]]; then
        printf '  No failures in this file.\n'
    fi
done

# Screenshots are listed separately because they are usually written by a teardown hook
# with a name matching the test, and pairing them by name is guesswork. Listing them with
# timestamps lets the reader do the pairing reliably in one glance.
mapfile -t SCREENSHOTS < <(
    find "${ARTEFACT_DIR}" -type f \
        \( -iname '*.png' -o -iname '*.jpg' -o -iname '*.jpeg' \) | sort || true
)

printf '\n%s\n' '--------------------------------------------------------------------------'
printf 'SCREENSHOTS (%d)\n' "${#SCREENSHOTS[@]}"
printf '%s\n' '--------------------------------------------------------------------------'
if [[ ${#SCREENSHOTS[@]} -eq 0 ]]; then
    printf '  None captured.\n'
else
    for shot in "${SCREENSHOTS[@]}"; do
        # ls -l rather than stat: the format differs between GNU and BSD stat, and this
        # only needs to be readable, not machine-parsed.
        printf '  %s\n' "$(ls -lh "${shot}" | awk '{print $5, $6, $7, $8}') ${shot}"
    done
fi

printf '\n%s\n' '=========================================================================='
printf 'SUMMARY\n'
printf '%s\n' '=========================================================================='
printf '  TRX files scanned : %d\n' "${#TRX_FILES[@]}"
printf '  Passed            : %d\n' "${total_passed}"
printf '  Failed            : %d\n' "${total_failed}"
printf '  Skipped           : %d\n' "${total_skipped}"
printf '  Screenshots       : %d\n' "${#SCREENSHOTS[@]}"

if [[ "${total_failed}" -gt 0 ]]; then
    printf '\n  Failing tests:\n'
    printf '    %s\n' "${FAILED_NAMES[@]}"
    printf '\n  Next step: group these by root cause before opening any of them -\n'
    printf '    python scripts/analyse_test_results.py %s/*.trx\n' "${ARTEFACT_DIR}"
fi

# Exit 0 on found failures on purpose: this is a reporting tool, and the gate that
# decides whether the build is red is scripts/ci-quality-gate.sh.
exit 0
