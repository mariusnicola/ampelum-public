#!/usr/bin/env bash
# Rebrand verification: check for stray "Ampelum" NAMESPACE references (not class names)
set -euo pipefail

cd "$(dirname "$0")/.."

results=$(grep -rn "namespace Ampelum[.;]" src/ --include="*.cs" 2>/dev/null || true)

if [ -n "$results" ]; then
  echo "FAIL: Found Ampelum namespace declarations (should all be SignalGrid.Ampelum.*):"
  echo "$results"
  exit 1
fi

# Check for using Ampelum (not SignalGrid.Ampelum)
results=$(grep -rn "^\s*using Ampelum[.;]" src/ --include="*.cs" 2>/dev/null || true)

if [ -n "$results" ]; then
  echo "FAIL: Found using Ampelum directive (should be using SignalGrid.Ampelum):"
  echo "$results"
  exit 1
fi

echo "PASS: All namespaces migrated to SignalGrid.Ampelum."
echo "Note: Class names (AmpelumContext, AmpelumBuilder, etc.) and"
echo "experimental attributes (AMPELUM_V09_PREVIEW) are intentionally kept."
