#!/usr/bin/env bash
set +x
set -Eeuo pipefail

dryRun=false
if [[ "${1:-}" == "--dry-run" ]]; then
  dryRun=true
elif [[ $# -ne 0 ]]; then
  echo "Usage: $0 [--dry-run]" >&2
  exit 64
fi

requireEnvironmentVariable() {
  local variableName="$1"
  if [[ -z "${!variableName:-}" ]]; then
    echo "Missing required environment variable: $variableName" >&2
    exit 1
  fi
}

requireEnvironmentVariable GITHUB_WORKSPACE
requireEnvironmentVariable RUNNER_TEMP
requireEnvironmentVariable GAMECI_ACTION_PATH
requireEnvironmentVariable UNITY_IMAGE
requireEnvironmentVariable UNITY_VERSION
requireEnvironmentVariable PROJECT_PATH
requireEnvironmentVariable ARTIFACTS_PATH
requireEnvironmentVariable UNITY_LICENSE
requireEnvironmentVariable UNITY_EMAIL
requireEnvironmentVariable UNITY_PASSWORD

UNITY_SERIAL="$({
  python3 -c '
import base64
import os
import re

license_text = os.environ["UNITY_LICENSE"]
match = re.search(r"<DeveloperData Value=\"([^\"]+)\"/>", license_text)
if match is None:
    raise SystemExit("UNITY_LICENSE does not contain DeveloperData.")
decoded = base64.b64decode(match.group(1), validate=True)
if len(decoded) <= 4:
    raise SystemExit("UNITY_LICENSE DeveloperData is invalid.")
print(decoded[4:].decode("latin-1"), end="")
'
})"

if [[ ${#UNITY_SERIAL} -ne 27 ]]; then
  echo "UNITY_LICENSE DeveloperData did not contain a valid 27-character serial." >&2
  exit 1
fi
UNITY_SERIAL_MASKED="${UNITY_SERIAL:0:${#UNITY_SERIAL}-4}XXXX"
if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
  printf '::add-mask::%s\n' "$UNITY_SERIAL" "$UNITY_SERIAL_MASKED"
fi
unset UNITY_LICENSE UNITY_SERIAL_MASKED

PACKAGE_NAME="$({
  python3 -c '
import json
import pathlib
import sys

package_json = pathlib.Path(sys.argv[1])
data = json.loads(package_json.read_text(encoding="utf-8"))
name = data.get("name")
if not isinstance(name, str) or not name:
    raise SystemExit("package.json must contain a non-empty string name.")
print(name, end="")
' "$GITHUB_WORKSPACE/$PROJECT_PATH/package.json"
})"

if [[ "$dryRun" != true ]]; then
  if [[ ! -d "$GAMECI_ACTION_PATH/platforms/ubuntu" ]]; then
    echo "Pinned GameCI Ubuntu scripts were not found." >&2
    exit 1
  fi
  if [[ ! -d "$GAMECI_ACTION_PATH/BlankProject" ]]; then
    echo "Pinned GameCI BlankProject was not found." >&2
    exit 1
  fi
fi

export UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL
export UNITY_VERSION PROJECT_PATH ARTIFACTS_PATH PACKAGE_NAME
export COVERAGE_OPTIONS=""
export COVERAGE_RESULTS_PATH="CodeCoverage"
export PACKAGE_MODE="true"
export SCOPED_REGISTRY_URL=""
export REGISTRY_SCOPES=""
export PRIVATE_REGISTRY_TOKEN=""
export GIT_PRIVATE_TOKEN=""
export GIT_CONFIG_EXTENSIONS=""
export CUSTOM_PARAMETERS="-nographics -assemblyNames BuildSoft.MotionTakeStudio.Editor.Tests;BuildSoft.MotionTakeStudio.PlayMode.Tests"
export RUN_AS_HOST_USER="false"
export CHOWN_FILES_TO=""
export UNITY_LICENSING_SERVER=""
export USE_EXIT_CODE="true"
export TEST_PLATFORMS="playmode;editmode;COMBINE_RESULTS"

githubHome="$RUNNER_TEMP/mts-github-home"
githubWorkflow="$RUNNER_TEMP/mts-github-workflow"
containerIdFile="$RUNNER_TEMP/mts-unity-tests-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}.cid"
containerName="mts-unity-tests-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}"
patchedSteps="$RUNNER_TEMP/mts-gameci-steps.dry-run"
blankProject="$RUNNER_TEMP/mts-blank-project.dry-run"
dockerProcessId=""
containerStopRequested=false

mkdir -p "$githubHome" "$githubWorkflow"

if [[ "$dryRun" != true ]]; then
  patchedSteps="$(mktemp -d "$RUNNER_TEMP/mts-gameci-steps.XXXXXX")"
  blankProject="$(mktemp -d "$RUNNER_TEMP/mts-blank-project.XXXXXX")"
  cp -R "$GAMECI_ACTION_PATH/platforms/ubuntu/." "$patchedSteps/"
  cp -R "$GAMECI_ACTION_PATH/BlankProject/." "$blankProject/"
  cp "$GITHUB_WORKSPACE/.github/scripts/gameci-activate-online.sh" "$patchedSteps/activate.sh"
  cp "$GITHUB_WORKSPACE/.github/scripts/gameci-return-license.sh" "$patchedSteps/return_license.sh"
  cp "$GITHUB_WORKSPACE/.github/scripts/gameci-secure-run-steps.sh" "$patchedSteps/run_steps.sh"
  chmod 700 \
    "$patchedSteps/activate.sh" \
    "$patchedSteps/return_license.sh" \
    "$patchedSteps/run_steps.sh"
fi

getContainerTarget() {
  if [[ -s "$containerIdFile" ]]; then
    cat "$containerIdFile"
  else
    printf '%s' "$containerName"
  fi
}

stopContainerGracefully() {
  local timeoutSeconds="$1"
  if [[ "$containerStopRequested" == true ]]; then
    return 0
  fi
  containerStopRequested=true
  docker stop --timeout "$timeoutSeconds" "$(getContainerTarget)" >/dev/null 2>&1 || true
}

cleanupContainer() {
  if [[ -n "$dockerProcessId" ]] || [[ -s "$containerIdFile" ]]; then
    # Normal EXIT gets a larger recovery window. Signal handling calls this
    # function only after beginning its shorter, bounded graceful stop.
    stopContainerGracefully 20
    docker rm --force --volumes "$(getContainerTarget)" >/dev/null 2>&1 || true
  fi
  rm -f "$containerIdFile"
  if [[ -n "${patchedSteps:-}" && "$patchedSteps" == "$RUNNER_TEMP"/mts-gameci-steps.* ]]; then
    rm -rf -- "$patchedSteps"
  fi
  if [[ -n "${blankProject:-}" && "$blankProject" == "$RUNNER_TEMP"/mts-blank-project.* ]]; then
    rm -rf -- "$blankProject"
  fi
  rm -rf -- "$githubHome" "$githubWorkflow"
}

if [[ "$dryRun" != true ]]; then
  trap cleanupContainer EXIT
  handleHostSignal() {
    trap - INT TERM
    stopContainerGracefully 6
    if [[ -n "$dockerProcessId" ]]; then
      set +e
      wait "$dockerProcessId" 2>/dev/null
      set -e
      dockerProcessId=""
    fi
    exit 130
  }
  trap handleHostSignal INT TERM
fi

environmentNames=(
  UNITY_EMAIL
  UNITY_PASSWORD
  UNITY_SERIAL
  UNITY_VERSION
  PROJECT_PATH
  COVERAGE_OPTIONS
  COVERAGE_RESULTS_PATH
  ARTIFACTS_PATH
  PACKAGE_MODE
  PACKAGE_NAME
  SCOPED_REGISTRY_URL
  REGISTRY_SCOPES
  PRIVATE_REGISTRY_TOKEN
  GIT_PRIVATE_TOKEN
  GIT_CONFIG_EXTENSIONS
  CUSTOM_PARAMETERS
  RUN_AS_HOST_USER
  CHOWN_FILES_TO
  UNITY_LICENSING_SERVER
  USE_EXIT_CODE
  TEST_PLATFORMS
  GITHUB_REF
  GITHUB_SHA
  GITHUB_REPOSITORY
  GITHUB_ACTOR
  GITHUB_WORKFLOW
  GITHUB_HEAD_REF
  GITHUB_BASE_REF
  GITHUB_EVENT_NAME
  GITHUB_ACTION
  GITHUB_EVENT_PATH
  RUNNER_OS
  RUNNER_TOOL_CACHE
  RUNNER_TEMP
  RUNNER_WORKSPACE
)

dockerArguments=(
  run
  --workdir /github/workspace
  --name "$containerName"
  --cidfile "$containerIdFile"
  --rm
  --security-opt no-new-privileges
  --pids-limit=1024
)

for environmentName in "${environmentNames[@]}"; do
  dockerArguments+=("--env" "$environmentName")
done

dockerArguments+=(
  "--env" "GITHUB_WORKSPACE=/github/workspace"
  "--volume" "$githubHome:/root:z"
  "--volume" "$githubWorkflow:/github/workflow:z"
  "--volume" "$GITHUB_WORKSPACE:/github/workspace:z"
  "--volume" "$GAMECI_ACTION_PATH/test-standalone-scripts:/UnityStandaloneScripts:ro,z"
  "--volume" "$patchedSteps:/steps:ro,z"
  "--volume" "$GAMECI_ACTION_PATH/unity-config:/usr/share/unity3d/config/:ro,z"
  "--volume" "$blankProject:/BlankProject:z"
  "--cpus=2"
  "--memory=6656m"
  "$UNITY_IMAGE"
  /bin/bash
  /steps/entrypoint.sh
)

if [[ "$dryRun" == true ]]; then
  printf '%s\n' "${dockerArguments[@]}"
  exit 0
fi

set +e
docker "${dockerArguments[@]}" &
dockerProcessId=$!
wait "$dockerProcessId"
dockerExitCode=$?
dockerProcessId=""
set -e
exit "$dockerExitCode"
