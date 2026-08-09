#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

MODE="repo"
if [[ "${1:-}" == "--staged" ]]; then
  MODE="staged"
elif [[ -n "${1:-}" ]]; then
  echo "hygiene_check: unknown argument: $1" >&2
  exit 2
fi

gather_files() {
  if [[ "$MODE" == "staged" ]]; then
    git diff --cached --name-only --diff-filter=ACMR
  else
    git ls-files
  fi | while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    case "$f" in
      do/*|build/*|frontend/dist/*|node_modules/*|.git/*|scripts/hygiene_check.sh) continue ;;
      *.go|*.cs|*.swift|*.js|*.html|*.css|*.json|*.md|*.yml|*.yaml|*.toml|*.sh|*.ps1|*.cfg|*.ini) echo "$f" ;;
    esac
  done
}

files=()
while IFS= read -r line; do files+=("$line"); done < <(gather_files)

violations=0
warnings=0
absolute_paths=""
ai_mentions=""

for f in ${files[@]+"${files[@]}"}; do
  [[ -f "$f" ]] || continue
  if hits="$(rg -n '(/Users/[A-Za-z0-9._-]+|/home/[A-Za-z0-9._-]+|[A-Za-z]:[\\/]Users)' "$f" 2>/dev/null)"; then
    absolute_paths+="${f}:${hits}"$'\n'
  fi
  if hits="$(rg -ni '\b(codex|claude|gpt[- ]?pro|gpt-?5|openai|anthropic)\b' "$f" 2>/dev/null)"; then
    ai_mentions+="${f}:${hits}"$'\n'
  fi
done

if [[ -n "$absolute_paths" ]]; then
  echo "hygiene_check: FAIL - absolute system paths in public files:" >&2
  printf '%s' "$absolute_paths" >&2
  violations=$((violations + 1))
fi

if [[ -n "$ai_mentions" ]]; then
  count="$(printf '%s' "$ai_mentions" | grep -c .)"
  echo "hygiene_check: WARN - ${count} AI/tool-name mention(s) require manual classification:" >&2
  printf '%s' "$ai_mentions" | head -20 >&2
  warnings=$((warnings + 1))
fi

if [[ "$MODE" == "staged" ]]; then
  git diff --cached --check || violations=$((violations + 1))
else
  git diff --check || violations=$((violations + 1))
fi

# The supervision loop in play.cs has no automated coverage -- the self-test drives ProcessRead,
# not RunPlay's body -- and region edits in that large file have silently eaten load-bearing call
# sites three times. Assert the call sites exist. A missing one is a regression no test can see.
required_sites=(
  "ProcessRead(ctx, tmp, n, readTicks"        # the read becomes state
  "hardNow != lastHardRebuffer"               # the hard-rebuffer adoption point
  "policy.Recompute(readTicks"                # policy recomputation, on its owning thread
  "exchange.TakeMeasurement"                  # telemetry -> network handoff
  "exchange.PublishMeasurement"               # the other half of it
  "policy.ObservationSatisfied(readTicks)"    # the cold-open gate
  "OnConfirmedEntry"                          # confirmed-entry branch reachable from ProcessRead
)
for site in "${required_sites[@]}"; do
  if ! grep -qF "$site" windows/lib/play.cs; then
    echo "hygiene_check: FAIL - required call site missing from play.cs: $site" >&2
    violations=$((violations + 1))
  fi
done

# Generated squelch-profile bindings must match profile/squelch.profile. Parallel hand-written
# constants in Swift and C# can silently disagree, and disagreement is unsafe in one direction:
# a receiver confirming silence sooner than the sender suppresses forgives real transport stalls.
if ! python3 scripts/gen-squelch-profile.py --check; then
  echo "hygiene_check: FAIL - squelch profile bindings are stale" >&2
  violations=$((violations + 1))
fi

if [[ "$violations" -gt 0 ]]; then
  echo "hygiene_check: FAIL ($violations violation(s), $warnings warning(s))" >&2
  exit 1
fi

if [[ "$warnings" -gt 0 ]]; then
  echo "hygiene_check: pass with $warnings warning(s)"
else
  echo "hygiene_check: pass"
fi
