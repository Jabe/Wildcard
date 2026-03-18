#!/usr/bin/env bash
set -euo pipefail

# ============================================================================
# Benchmark: Unix native tools vs Wildcard (wcg)
# Target directory: ~/Code (~5k .cs files, ~5k .json files)
# ============================================================================

BOLD='\033[1m'
CYAN='\033[36m'
GREEN='\033[32m'
YELLOW='\033[33m'
MAGENTA='\033[35m'
RESET='\033[0m'

TARGET_DIR="$HOME/Code"
RUNS=1

header() {
    echo ""
    echo -e "${BOLD}${MAGENTA}═══════════════════════════════════════════════════════════════${RESET}"
    echo -e "${BOLD}${MAGENTA}  $1${RESET}"
    echo -e "${BOLD}${MAGENTA}═══════════════════════════════════════════════════════════════${RESET}"
}

section() {
    echo ""
    echo -e "${BOLD}${CYAN}── $1 ──${RESET}"
}

# Run a command N times, report avg/min/max in milliseconds
bench() {
    local label="$1"
    shift
    local cmd="$*"

    local total=0
    local min=999999
    local max=0

    for ((i = 1; i <= RUNS; i++)); do
        if command -v gdate &>/dev/null; then
            local start_ms=$(gdate +%s%3N)
            eval "$cmd" > /dev/null 2>&1
            local end_ms=$(gdate +%s%3N)
            local elapsed=$((end_ms - start_ms))
        else
            local start_ns=$(perl -MTime::HiRes=time -e 'printf "%.0f\n", time*1000')
            eval "$cmd" > /dev/null 2>&1
            local end_ns=$(perl -MTime::HiRes=time -e 'printf "%.0f\n", time*1000')
            local elapsed=$((end_ns - start_ns))
        fi

        total=$((total + elapsed))
        ((elapsed < min)) && min=$elapsed
        ((elapsed > max)) && max=$elapsed
    done

    local avg=$((total / RUNS))
    printf "  ${GREEN}%-40s${RESET} avg: ${BOLD}%5d ms${RESET}  min: %5d ms  max: %5d ms\n" "$label" "$avg" "$min" "$max"
}

# ============================================================================
header "Benchmark: Unix Native vs Wildcard (wcg)"
echo -e "  Target:  ${BOLD}$TARGET_DIR${RESET}"
echo -e "  Runs:    ${BOLD}$RUNS${RESET} per command"
echo ""

cs_count=$(find "$TARGET_DIR" -name "*.cs" 2>/dev/null | wc -l | tr -d ' ')
json_count=$(find "$TARGET_DIR" -name "*.json" 2>/dev/null | wc -l | tr -d ' ')
echo -e "  .cs files:   ${BOLD}$cs_count${RESET}"
echo -e "  .json files: ${BOLD}$json_count${RESET}"

# Warmup wcg
echo ""
echo -e "${YELLOW}Warming up...${RESET}"
wcg "$TARGET_DIR/**/*.cs" > /dev/null 2>&1 || true
find "$TARGET_DIR" -name "*.cs" > /dev/null 2>&1 || true
echo -e "${GREEN}Ready.${RESET}"

# ============================================================================
section "1. File Discovery — find all .cs files recursively"
bench "find (native)" "find '$TARGET_DIR' -name '*.cs'"
bench "wcg (wildcard)" "wcg '$TARGET_DIR/**/*.cs'"

# ============================================================================
section "2. File Discovery — find all .json files recursively"
bench "find (native)" "find '$TARGET_DIR' -name '*.json'"
bench "wcg (wildcard)" "wcg '$TARGET_DIR/**/*.json'"

# ============================================================================
section "3. Deep glob — **/bin/**/*.dll"
bench "find (native)" "find '$TARGET_DIR' -path '*/bin/*.dll'"
bench "wcg (wildcard)" "wcg '$TARGET_DIR/**/bin/**/*.dll'"

# ============================================================================
section "4. Content search — 'namespace' in .cs files"
bench "find+grep (native)" "find '$TARGET_DIR' -name '*.cs' -exec grep -l 'namespace' {} +"
bench "grep -r (native)" "grep -rl 'namespace' --include='*.cs' '$TARGET_DIR'"
bench "rg (ripgrep)" "rg -l 'namespace' --type cs '$TARGET_DIR'"
bench "wcg (wildcard)" "wcg '$TARGET_DIR/**/*.cs' '*namespace*'"
bench "wcg -l (files only)" "wcg -l '$TARGET_DIR/**/*.cs' '*namespace*'"

# ============================================================================
section "5. Content search — 'TODO' in .cs files"
bench "find+grep (native)" "find '$TARGET_DIR' -name '*.cs' -exec grep -l 'TODO' {} +"
bench "grep -r (native)" "grep -rl 'TODO' --include='*.cs' '$TARGET_DIR'"
bench "rg (ripgrep)" "rg -l 'TODO' --type cs '$TARGET_DIR'"
bench "wcg (wildcard)" "wcg '$TARGET_DIR/**/*.cs' '*TODO*'"
bench "wcg -l (files only)" "wcg -l '$TARGET_DIR/**/*.cs' '*TODO*'"

# ============================================================================
section "6. Content search — case-insensitive 'error' in .json"
bench "grep -ri (native)" "grep -rli 'error' --include='*.json' '$TARGET_DIR'"
bench "rg -i (ripgrep)" "rg -li 'error' --type json '$TARGET_DIR'"
bench "wcg -i (wildcard)" "wcg '$TARGET_DIR/**/*.json' '*error*' -i"
bench "wcg -l -i (files only)" "wcg -l '$TARGET_DIR/**/*.json' '*error*' -i"

# ============================================================================
header "Done!"
echo ""
