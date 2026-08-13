#!/usr/bin/env bash
set +x
set -uo pipefail

for variableName in UNITY_SERIAL UNITY_EMAIL UNITY_PASSWORD; do
  if [[ -z "${!variableName:-}" ]]; then
    echo "Online Unity activation requires $variableName." >&2
    exit 1
  fi
done
blankProject="${MTS_BLANK_PROJECT:-/BlankProject}"

activationLog="$(mktemp)"
cleanup() {
  rm -f -- "$activationLog"
  unset UNITY_LICENSE UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL blankProject
}
trap cleanup EXIT INT TERM

echo "Requesting online Unity activation."
set +e
unity-editor \
  -batchmode \
  -nographics \
  -logFile "$activationLog" \
  -quit \
  -serial "$UNITY_SERIAL" \
  -username "$UNITY_EMAIL" \
  -password "$UNITY_PASSWORD" \
  -projectPath "$blankProject" \
  >/dev/null 2>&1
unityExitCode=$?
set -e

activationSucceeded=false
if [[ $unityExitCode -eq 0 ]] && \
   grep -Fq 'Serial number assigned to:' "$activationLog" && \
   ! grep -Eiq 'Failed to (load|activate|update).*license|No valid Unity Editor license|License is not active|HasEntitlements will fail|activation (failed|error)|machine.*(mismatch|invalid)|entitlement.*(failed|missing|not)' "$activationLog"; then
  activationSucceeded=true
fi

if [[ "$activationSucceeded" == true ]]; then
  echo "Online Unity activation complete."
  exit 0
fi

if [[ $unityExitCode -eq 0 ]]; then
  unityExitCode=1
fi
echo "Online Unity activation failed with exit code $unityExitCode; activation details were suppressed." >&2
exit "$unityExitCode"
