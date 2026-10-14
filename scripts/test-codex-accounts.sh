#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")/.."

test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

swiftc \
    Sources/Usage/CodexAccountSwitcher.swift \
    Tests/CodexAccountSwitcherTests.swift \
    -o "$test_dir/codex-account-tests"

"$test_dir/codex-account-tests"
