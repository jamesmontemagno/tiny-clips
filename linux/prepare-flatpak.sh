#!/bin/sh
set -eu
cd "$(dirname "$0")"
# flatpak-builder confines local sources to the manifest directory. Stage the same
# contract the other platforms use; never maintain a Linux copy in source control.
mkdir -p .build/automation/fixtures
cp ../shared/automation/commands.json .build/automation/commands.json
cp ../shared/automation/fixtures/policy.json .build/automation/fixtures/policy.json
