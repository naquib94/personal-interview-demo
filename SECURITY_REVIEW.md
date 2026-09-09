# Security and intellectual-property review

A record of what was checked before this repository was made public, how it was checked, and what
was deliberately left in place.

---

## 1. Statement of independent authorship

This repository was created from scratch as a portfolio piece. Every file in it was written for
this project.

**The application under test, its schema, its data and its domain are invented.** There is no
"Meridian Trading" platform. The instruments, users, accounts, orders and fills in
`database/seed/002_seed.sql` are synthetic values chosen to make specific tests meaningful — a
non-tradable instrument so there is a business rule to break, a user with no account so there is
a genuine 404 path, an order with two partial fills so aggregation can be verified.

**No proprietary code, data or configuration from any employer or client is present.** No source
file, test, page object, configuration file or pipeline definition was copied, renamed,
paraphrased or sanitised from any other repository.

**What did carry over is knowledge, which is not transferable property.** I have worked on
automation frameworks before, and I have opinions about layering, synchronisation, locator
strategy and pipeline design that were formed by doing that work. Those opinions are recorded in
[`docs/design-decisions.md`](docs/design-decisions.md) as arguments, alongside the alternatives
considered — which is the honest way to show reasoning without exposing anyone's implementation.
Several decisions here are deliberate *corrections* of patterns I have seen cause problems; the
patterns are described generically and no system is identifiable from them.

---

## 2. Automated scanning

### Secret scanning — enforced in CI

`scripts/ci-quality-gate.sh` runs a secret scan, and it is wired into
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) as its own job with `fetch-depth: 0`, so it
scans the **full history** rather than only the current tip. A secret that was committed and later
removed is still a leaked secret, and a tip-only scan would miss it entirely.

The patterns match the *shape* of a credential rather than the word "password":

| Pattern | Catches |
|---|---|
| `(password\|secret\|apikey\|access_token\|client_secret)\s*[:=]\s*"..."` | An assignment to a quoted literal of six or more characters |
| `BEGIN ... PRIVATE KEY` | PEM private keys |
| `AKIA[0-9A-Z]{16}` | AWS access key IDs |
| `gh[pousr]_[A-Za-z0-9]{20,}` | GitHub tokens |
| `xox[baprs]-...` | Slack tokens |
| `Bearer\s+[A-Za-z0-9._-]{30,}` | Hardcoded bearer tokens |

Deliberately narrow. A scan that greps for the word "password" fires on every column name and
validation message, everybody learns to ignore it, and the one real finding is lost in the noise.

The scan was verified against positive controls — a synthetic AWS key, a GitHub token, a PEM
header and a realistic-looking assignment were each planted in a temporary directory and each was
caught — so the gate is known to work rather than merely known to be green.

**Current result: PASS.** No credential-shaped literal outside the documented allowlist.

### Allowlist

Every entry needs a reason that survives being read out loud in a review. That constraint is the
control.

| Entry | Reason |
|---|---|
| `Demo!Pass123` | The single throwaway password for every seeded demo account. See section 4 |
| `password_hash`, `password_salt` | Column names in the schema, not credentials |
| `QA_DATA_SEED` | A random-number seed, despite the name |
| `"expected*"`, `"placeholder*"`, `"dummy*"`, `"sample*"`, `"synthetic*"` | Values that name themselves as placeholders |
| `not-a-real-*`, `not-the-password` | Self-describing negative fixtures. A test proving a *wrong* password is rejected must contain a wrong password |

The last two groups match on the **value**, not on a file path. That distinction matters: a
genuine-looking credential in the very same file still fails the gate. An allowlist keyed on file
paths would create permanently blind spots.

### Intellectual-property scan

A separate scan for terms that could identify a previous employer or its systems — company names,
product names, internal domain fragments, internal package feeds, build-agent pool names,
notification connector addresses, and the token prefixes used by specific third-party SaaS
vendors. Roughly thirty terms and patterns in total.

The term list itself is deliberately **not** reproduced in this document. Writing out a list of a
former employer's product and system names in a public repository would defeat the purpose of the
scan, which is a small point but exactly the kind of detail that gets overlooked.

**Result: zero matches across all 196 scanned files.**

Additional checks, all clean:

| Check | Result |
|---|---|
| Local absolute paths / developer username leaked into tracked files | None |
| Private package feeds, a `nuget.config`, or hosted-DevOps URLs | None — the repository restores from the public nuget.org feed only |
| Internal-looking hostnames (`.local`, `.corp`, `.internal`, `.intranet`) | None |
| Token-shaped literals | One match: the scanner's own detection patterns in `ci-quality-gate.sh` |
| Real email domains in test data | None — `example.invalid` (RFC 2606) throughout, so an address can never resolve |
| Build output, databases, evidence, `.trx`, generated code-behind staged for commit | None — verified against `git diff --cached --name-only` |

---

## 3. What the repository contains

| Category | Present? | Notes |
|---|---|---|
| Production source code from any employer | No | |
| Proprietary test code | No | |
| Internal project, product or system names | No | |
| Internal URLs, hostnames or IP addresses | No | Only `localhost:5199` and `localhost:5198` |
| API endpoints belonging to any employer | No | The only API is `src/TradingDemo.App` in this repository |
| Real credentials, API keys, tokens or certificates | No | See section 4 for the demo passwords |
| Connection strings | No | SQLite file paths, built at runtime from configuration |
| Internal database schemas | No | `database/schema/001_schema.sql` was designed for this project |
| Customer, production or pre-production data | No | All data synthetic |
| Internal screenshots or diagrams | No | The only diagrams are ASCII, drawn for this repository |
| Ticket or issue-tracker content | No | `DEFECT_REPORT.md` documents a defect planted in this repository's own seed data |
| Internal CI/CD configuration | No | `.github/workflows/ci.yml` was written for this repository |
| Internal package feeds | No | |
| Company logos or branding | No | |
| Third-party dependencies | Yes | All public packages from nuget.org, pinned in `Directory.Packages.props` |

---

## 4. The one deliberate exception: seeded demo passwords

`database/seed/002_seed.sql` and each `Environment.*.json` contain the string `Demo!Pass123`. This
is intentional, and the reasoning belongs in the open.

**What it is.** The password for four accounts in a SQLite database that the test suite creates on
your own machine, in an application that exists only in this repository. It is stored as
`Base64(SHA-256(salt + ':' + password))`, and it grants access to nothing else. There is no
deployed instance of this application anywhere.

**Why it is committed.** So that `dotnet build && dotnet test` works immediately after a clone. A
demonstration repository that requires a credential-provisioning step before it will run once is a
demonstration nobody runs.

**Why this is a narrow exception rather than the pattern.** The framework's own configuration
loader is built for the opposite case, and enforces it:

- A user with **no** password is a **hard failure**, not an empty default. The message names the
  role and the exact environment variable that would fix it — see
  `ConfigurationLoader.ValidateUsers`. An empty default produces authentication failures that look
  precisely like a product defect, and the resulting investigation is expensive.
- Real secrets have a documented channel with the highest precedence:
  `QA_Users__ActiveTrader__Password` as an environment variable, overriding both the git-ignored
  `Environment.Overrides.json` and the committed `Environment.{Env}.json`. On a real product the
  password line would simply not exist in a committed file.
- `Environment.Overrides.json` is git-ignored and is the only file a developer should ever put a
  credential in. `Environment.Overrides.example.json` is committed as a template.
- The `.gitignore` also excludes `*.pfx`, `*.p12`, `*.pem`, `*.key`, `secrets.json`, `.env` and
  built database files, as defence in depth rather than as the primary control.

**Also worth noting: the demo application generates its own signing key at startup**
(`src/TradingDemo.App/Domain/TokenService.cs`). It is held in memory only, never written to disk
and never committed, so there is no signing secret in this repository to leak. Restarting the
application invalidates every previously issued token, which is also the behaviour a test suite
wants.

### Two acknowledged shortcuts in the demo application

Both are documented at the point they occur, because a shortcut that is not labelled is
indistinguishable from a mistake.

**SHA-256 for password hashing** (`Domain/PasswordHasher.cs`). Not appropriate for production
software — it is fast, which is what an attacker wants. A real system uses a memory-hard KDF:
Argon2id, scrypt, or PBKDF2 with a high iteration count. It is used here because the goal was a
hash the committed seed script could reproduce in one readable line, against accounts that guard
nothing. The XML documentation on that class says exactly this. The comparison is still
timing-safe (`CryptographicOperations.FixedTimeEquals`), because that costs one method call and is
a habit worth keeping.

**Test-support endpoints** (`/health` and `/test-support/reset`). A deliberate, documented test
hook that lets a scenario guarantee its own preconditions in milliseconds instead of tolerating
order-dependent tests. The honest cost is that it must never reach production; in a real system it
would be gated behind an environment check or compiled out entirely. That gate is represented here
by the `Demo:EnableTestSupport` configuration flag, and the trade-off is argued in
`src/TradingDemo.App/Program.cs` rather than left implicit.

---

## 5. Findings in the reference material

During the research phase I read several existing automation frameworks in order to form a view
about architecture. That reading surfaced security problems in those repositories: credentials
committed in plaintext, including a device-cloud access key and a third-party platform access
token.

**None of those values appear anywhere in this repository**, in any form, including in the
research notes. They were reported to the owner of the material for rotation, and they are
described here only as a category — because they are the direct reason several controls in this
repository exist:

- Fail-fast on a missing credential rather than a silent default.
- Environment variables as the only channel for a real secret.
- A committed `*.example.json` template alongside a git-ignored override file.
- A secret scan as a **blocking** CI gate, over full history, rather than an advisory report.

The most useful thing to take from that experience is that every one of those repositories was
built by competent engineers. Credentials do not get committed through carelessness; they get
committed because the framework made the correct path harder than the incorrect one. That is a
design problem, and it is the reason `ConfigurationLoader` fails loudly rather than defaulting
quietly.

---

## 6. Verification you can repeat

Nothing above needs to be taken on trust.

```powershell
# Secret scan over the working tree
bash scripts/ci-quality-gate.sh --only secrets

# Confirm no build output, database or evidence would be committed
git add -A
git diff --cached --name-only | Select-String -Pattern '/(bin|obj)/|\.db$|evidence/|\.trx$'
git reset

# The full gate: build warnings, secrets, pass rate, backend data integrity
bash scripts/ci-quality-gate.sh --allow-data-violations
```

The last command reports the backend-data gate as a violation, and that is correct: the seed data
contains one deliberately planted defect, documented in
[`DEFECT_REPORT.md`](DEFECT_REPORT.md). `--allow-data-violations` downgrades it to informational so
the rest of the gate can be demonstrated.

---

## 7. Conclusion

This repository was independently created, contains no intentional proprietary artefacts, and
contains no live credential. The one committed password belongs to a synthetic local demo
application and is documented above. Every claim in this document can be re-verified with the
commands in section 6, and the secret scan runs on every pull request as a blocking gate rather
than as an advisory report.

Where shortcuts were taken, they are labelled in the code as well as here — because a documented
simplification is an engineering decision, and an undocumented one is a vulnerability.
