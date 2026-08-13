#!/usr/bin/env bash
set +x
set -uo pipefail

for variableName in UNITY_EMAIL UNITY_PASSWORD; do
  if [[ -z "${!variableName:-}" ]]; then
    echo "Unity license return requires $variableName." >&2
    exit 1
  fi
done
blankProject="${MTS_BLANK_PROJECT:-/BlankProject}"
defaultLicenseArtifacts='/root/.local/share/unity3d/Unity/Unity_lic.ulf:/root/.config/unity3d/Unity/Unity_lic.ulf:/root/.local/share/unity3d/Unity/UnityEntitlementLicense.xml:/root/.config/unity3d/Unity/UnityEntitlementLicense.xml'
IFS=':' read -r -a licenseArtifacts <<< "${MTS_UNITY_LICENSE_ARTIFACTS:-$defaultLicenseArtifacts}"
hadLicenseArtifact=false
for licenseArtifact in "${licenseArtifacts[@]}"; do
  if [[ -f "$licenseArtifact" ]]; then
    hadLicenseArtifact=true
  fi
done

returnLog="$(mktemp)"
cleanup() {
  rm -f -- "$returnLog"
  unset UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL UNITY_LICENSE blankProject
}
trap cleanup EXIT INT TERM

echo "Returning Unity license."
set +e
unity-editor \
  -batchmode \
  -nographics \
  -logFile "$returnLog" \
  -quit \
  -returnlicense \
  -username "$UNITY_EMAIL" \
  -password "$UNITY_PASSWORD" \
  -projectPath "$blankProject" \
  >/dev/null 2>&1
returnExitCode=$?
set -e

if grep -Eiq 'Failed to return license|return (request|license).*(failed|timed out|error)|license has not been activated|cannot be returned|No valid Unity Editor license|\[Licensing::(Client|Module)\].*(Error|Failed|Failure)' "$returnLog"; then
  if [[ $returnExitCode -eq 0 ]]; then
    returnExitCode=1
  fi
fi

licenseArtifactRemoved=false
if [[ "$hadLicenseArtifact" == true ]]; then
  licenseArtifactRemoved=true
  for licenseArtifact in "${licenseArtifacts[@]}"; do
    if [[ -f "$licenseArtifact" ]]; then
      licenseArtifactRemoved=false
    fi
  done
fi

returnSuccessMarker=false
if grep -Eiq '(success|complete).*(return|release|remove).*(license|entitlement)|(license|entitlement).*(return|release|remove).*(success|complete)|successfully[[:space:]]+(returned|released|removed).*(license|entitlement)' "$returnLog"; then
  returnSuccessMarker=true
fi

if [[ $returnExitCode -eq 0 ]] && \
   { [[ "$returnSuccessMarker" == true ]] || [[ "$licenseArtifactRemoved" == true ]]; }; then
  echo "Unity license returned."
  exit 0
fi

if [[ $returnExitCode -eq 0 ]]; then
  returnExitCode=1
fi
echo "Unity license return failed with exit code $returnExitCode; return details were suppressed." >&2
exit "$returnExitCode"
