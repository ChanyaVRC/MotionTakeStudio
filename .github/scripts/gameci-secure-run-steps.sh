#!/usr/bin/env bash
set +x
set -Euo pipefail

stepsDirectory="${MTS_GAMECI_STEPS_DIRECTORY:-/steps}"
export GIT_CONFIG_EXTENSIONS="${GIT_CONFIG_EXTENSIONS:-}"
for requiredScript in \
  set_extra_git_configs.sh set_gitcredential.sh activate.sh run_tests.sh return_license.sh; do
  if [[ ! -f "$stepsDirectory/$requiredScript" ]]; then
    echo "GameCI step is missing: $requiredScript" >&2
    exit 1
  fi
done

for variableName in UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL; do
  if [[ -z "${!variableName:-}" ]]; then
    echo "Secure GameCI execution requires $variableName." >&2
    exit 1
  fi
done

unityEmail="$UNITY_EMAIL"
unityPassword="$UNITY_PASSWORD"
activationAttempted=false
returnAttempted=false
returnExitCode=0
activeChildProcessId=""
activeChildProcessIsGroup=false
activeChildPhase=""
phaseTemporaryRoot="$(mktemp -d)"
chmod 700 "$phaseTemporaryRoot"

cleanupPhaseTemporaryRoot() {
  rm -rf -- "$phaseTemporaryRoot"
}
trap cleanupPhaseTemporaryRoot EXIT

clearCredentialEnvironment() {
  unset UNITY_LICENSE UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL
}

waitForActiveChild() {
  while kill -0 "$activeChildProcessId" 2>/dev/null; do
    sleep 0.1
  done
  wait "$activeChildProcessId" 2>/dev/null
}

returnLicense() {
  if [[ "$activationAttempted" != true || "$returnAttempted" == true ]]; then
    return 0
  fi

  returnAttempted=true
  export UNITY_EMAIL="$unityEmail"
  export UNITY_PASSWORD="$unityPassword"
  returnCommand=(env TMPDIR="$phaseTemporaryRoot" /bin/bash --noprofile --norc "$stepsDirectory/return_license.sh")
  if command -v setsid >/dev/null 2>&1; then
    returnCommand=(setsid "${returnCommand[@]}")
    activeChildProcessIsGroup=true
  fi
  set +e
  activeChildPhase=return
  "${returnCommand[@]}" &
  activeChildProcessId=$!
  waitForActiveChild
  returnExitCode=$?
  activeChildProcessId=""
  activeChildProcessIsGroup=false
  activeChildPhase=""
  set -e
  clearCredentialEnvironment
  if [[ $returnExitCode -ne 0 ]]; then
    echo "Unity license return failed; the dedicated CI seat may need manual recovery." >&2
  fi
}

stopActiveChildProcess() {
  if [[ -z "$activeChildProcessId" ]] || ! kill -0 "$activeChildProcessId" 2>/dev/null; then
    activeChildProcessId=""
    activeChildProcessIsGroup=false
    activeChildPhase=""
    return 0
  fi

  if [[ "$activeChildProcessIsGroup" == true ]]; then
    kill -TERM -- "-$activeChildProcessId" 2>/dev/null || true
  else
    kill -TERM "$activeChildProcessId" 2>/dev/null || true
  fi
  for _ in {1..30}; do
    kill -0 "$activeChildProcessId" 2>/dev/null || break
    sleep 0.1
  done
  if kill -0 "$activeChildProcessId" 2>/dev/null; then
    if [[ "$activeChildProcessIsGroup" == true ]]; then
      kill -KILL -- "-$activeChildProcessId" 2>/dev/null || true
    else
      kill -KILL "$activeChildProcessId" 2>/dev/null || true
    fi
  fi
  set +e
  wait "$activeChildProcessId" 2>/dev/null
  set -e
  activeChildProcessId=""
  activeChildProcessIsGroup=false
  activeChildPhase=""
}

finishInFlightReturnOnSignal() {
  for _ in {1..50}; do
    kill -0 "$activeChildProcessId" 2>/dev/null || break
    sleep 0.1
  done
  if kill -0 "$activeChildProcessId" 2>/dev/null; then
    if [[ "$activeChildProcessIsGroup" == true ]]; then
      kill -KILL -- "-$activeChildProcessId" 2>/dev/null || true
    else
      kill -KILL "$activeChildProcessId" 2>/dev/null || true
    fi
    set +e
    wait "$activeChildProcessId" 2>/dev/null
    set -e
    activeChildProcessId=""
    activeChildProcessIsGroup=false
    activeChildPhase=""
    clearCredentialEnvironment
    returnExitCode=1
    echo "Unity license return did not finish during cancellation; manual recovery may be required." >&2
    return 1
  fi

  set +e
  wait "$activeChildProcessId" 2>/dev/null
  returnExitCode=$?
  set -e
  activeChildProcessId=""
  activeChildProcessIsGroup=false
  activeChildPhase=""
  clearCredentialEnvironment
  if [[ $returnExitCode -ne 0 ]]; then
    echo "Unity license return was not confirmed during cancellation; manual recovery may be required." >&2
    return 1
  fi
  return 0
}

handleSignal() {
  trap - INT TERM
  if [[ "$activeChildPhase" == return ]]; then
    finishInFlightReturnOnSignal || true
    exit 130
  fi
  stopActiveChildProcess
  returnLicense
  exit 130
}
trap handleSignal INT TERM

source "$stepsDirectory/set_extra_git_configs.sh"
source "$stepsDirectory/set_gitcredential.sh"

activationAttempted=true
activeChildPhase=activation
activationCommand=(env TMPDIR="$phaseTemporaryRoot" /bin/bash --noprofile --norc "$stepsDirectory/activate.sh")
if command -v setsid >/dev/null 2>&1; then
  activationCommand=(setsid "${activationCommand[@]}")
  activeChildProcessIsGroup=true
fi
set +e
"${activationCommand[@]}" &
activeChildProcessId=$!
waitForActiveChild
activationExitCode=$?
activeChildProcessId=""
activeChildProcessIsGroup=false
activeChildPhase=""
set -e
if [[ $activationExitCode -ne 0 ]]; then
  returnLicense
  exit "$activationExitCode"
fi

clearCredentialEnvironment
testCommand=(/bin/bash --noprofile --norc -c '
  set +e
  TEST_RUNNER_EXIT_CODE=0
  source "$1"
  exit "${TEST_RUNNER_EXIT_CODE:-1}"
' _ "$stepsDirectory/run_tests.sh")
if command -v setsid >/dev/null 2>&1; then
  testCommand=(setsid "${testCommand[@]}")
  activeChildProcessIsGroup=true
fi
set +e
activeChildPhase=test
env \
  -u UNITY_LICENSE \
  -u UNITY_EMAIL \
  -u UNITY_PASSWORD \
  -u UNITY_SERIAL \
  "${testCommand[@]}" &
activeChildProcessId=$!
waitForActiveChild
testExitCode=$?
activeChildProcessId=""
activeChildProcessIsGroup=false
activeChildPhase=""
set -e

returnLicense
unset unityEmail unityPassword

if [[ $testExitCode -ne 0 ]]; then
  exit "$testExitCode"
fi
if [[ $returnExitCode -ne 0 ]]; then
  exit "$returnExitCode"
fi

echo "Unity package tests completed and the dedicated CI license was returned."
