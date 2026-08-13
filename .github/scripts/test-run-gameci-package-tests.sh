#!/usr/bin/env bash
set +x
set -Eeuo pipefail

scriptDirectory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repositoryRoot="$(cd -- "$scriptDirectory/../.." && pwd)"
runner="$scriptDirectory/run-gameci-package-tests.sh"
secureSteps="$scriptDirectory/gameci-secure-run-steps.sh"

fakeSerial='F4-AAAA-BBBB-CCCC-DDDD-EEEE'
fakeEmail='dedicated-ci@example.invalid'
fakePassword=$'secret "quote" backslash\\ semicolon; spaces'
hashValue() {
  printf '%s' "$1" | sha256sum | awk '{ print $1 }'
}
fakeSerialHash="$(hashValue "$fakeSerial")"
fakeEmailHash="$(hashValue "$fakeEmail")"
fakePasswordHash="$(hashValue "$fakePassword")"
developerData="$({
  FAKE_SERIAL="$fakeSerial" python3 -c '
import base64
import os

serial = os.environ["FAKE_SERIAL"].encode("latin-1")
print(base64.b64encode(b"\x00\x00\x00\x00" + serial).decode("ascii"), end="")
'
})"
fakeLicense="<root><DeveloperData Value=\"$developerData\"/></root>"
fakeLicenseHash="$(hashValue "$fakeLicense")"

temporaryRoot="$(mktemp -d)"
mkdir -p "$temporaryRoot/tmp"
export TMPDIR="$temporaryRoot/tmp"
cleanup() {
  rm -rf -- "$temporaryRoot"
}
trap cleanup EXIT

assertSecretsAbsent() {
  local output="$1"
  local description="$2"
  for secretValue in "$fakeLicense" "$fakeEmail" "$fakePassword" "$fakeSerial"; do
    if [[ "$output" == *"$secretValue"* ]]; then
      echo "$description exposed credential material." >&2
      exit 1
    fi
  done
}

dryRunOutput="$({
  GITHUB_WORKSPACE="$repositoryRoot" \
  RUNNER_TEMP="$temporaryRoot/motion take studio" \
  GAMECI_ACTION_PATH="$temporaryRoot/game ci/dist" \
  UNITY_IMAGE='unityci/editor:test digest' \
  UNITY_VERSION='2022.3.22f1' \
  PROJECT_PATH='Packages/com.buildsoft.motion-take-studio' \
  ARTIFACTS_PATH='artifacts with spaces' \
  GITHUB_ACTIONS=false \
  UNITY_LICENSE="$fakeLicense" \
  UNITY_EMAIL="$fakeEmail" \
  UNITY_PASSWORD="$fakePassword" \
  bash "$runner" --dry-run
})"
assertSecretsAbsent "$dryRunOutput" "Dry-run output"

maskedSerial="${fakeSerial:0:${#fakeSerial}-4}XXXX"
maskOutput="$({
  GITHUB_WORKSPACE="$repositoryRoot" \
  RUNNER_TEMP="$temporaryRoot/mask test" \
  GAMECI_ACTION_PATH="$temporaryRoot/game ci/dist" \
  UNITY_IMAGE='unityci/editor:test digest' \
  UNITY_VERSION='2022.3.22f1' \
  PROJECT_PATH='Packages/com.buildsoft.motion-take-studio' \
  ARTIFACTS_PATH='artifacts' \
  GITHUB_ACTIONS=true \
  UNITY_LICENSE="$fakeLicense" \
  UNITY_EMAIL="$fakeEmail" \
  UNITY_PASSWORD="$fakePassword" \
  bash "$runner" --dry-run
})"
if ! grep -Fxq -- "::add-mask::$fakeSerial" <<<"$maskOutput" || \
   ! grep -Fxq -- "::add-mask::$maskedSerial" <<<"$maskOutput"; then
  echo "The wrapper must register both full and Unity-masked serial forms." >&2
  exit 1
fi

if [[ "$dryRunOutput" == *'BuildSoft.MotionTakeStudio.Editor.Tests'* ]] || \
   [[ "$dryRunOutput" == *'BuildSoft.MotionTakeStudio.PlayMode.Tests'* ]]; then
  echo "Dry-run output expanded custom parameters into Docker arguments." >&2
  exit 1
fi
for environmentName in UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL CUSTOM_PARAMETERS; do
  if ! grep -Fxq -- "$environmentName" <<<"$dryRunOutput"; then
    echo "Dry-run output did not forward $environmentName by name." >&2
    exit 1
  fi
  if grep -Eq -- "${environmentName}=" <<<"$dryRunOutput"; then
    echo "Dry-run output embedded $environmentName in a Docker argument." >&2
    exit 1
  fi
done
if grep -Fxq -- UNITY_LICENSE <<<"$dryRunOutput"; then
  echo "The raw ULF must not enter the Unity test container." >&2
  exit 1
fi

fakeEditorDirectory="$temporaryRoot/fake-editor"
fakeSteps="$temporaryRoot/fake-steps"
mkdir -p "$fakeEditorDirectory" "$fakeSteps"
callLog="$temporaryRoot/calls.log"
testEnvironmentState="$temporaryRoot/test-environment.state"
fakeLicenseArtifact="$temporaryRoot/fake-license.ulf"

cat > "$fakeEditorDirectory/unity-editor" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail

logFile=''
isReturn=false
serial=''
email=''
password=''
arguments=("$@")
while [[ $# -gt 0 ]]; do
  case "$1" in
    -logFile) shift; logFile="$1" ;;
    -returnlicense) isReturn=true ;;
    -serial) shift; serial="$1" ;;
    -username) shift; email="$1" ;;
    -password) shift; password="$1" ;;
  esac
  shift
done

# Deliberately echo argv to prove the caller suppresses launcher output.
printf 'launcher argv: %s\n' "${arguments[*]}" >&2
hashValue() {
  printf '%s' "$1" | sha256sum | awk '{ print $1 }'
}
if [[ -z "$logFile" || \
      "$(hashValue "$email")" != "$TEST_EXPECTED_EMAIL_HASH" || \
      "$(hashValue "$password")" != "$TEST_EXPECTED_PASSWORD_HASH" ]]; then
  exit 90
fi

if [[ "$isReturn" == true ]]; then
  printf '%s\n' return >> "$TEST_CALL_LOG"
  case "$TEST_RETURN_MODE" in
    success)
      rm -f -- "$TEST_LICENSE_ARTIFACT"
      printf '%s\n' 'Successfully returned the Unity license.' "$password" > "$logFile"
      exit 0
      ;;
    block)
      : > "$TEST_RETURN_BLOCK_STARTED"
      sleep 2
      rm -f -- "$TEST_LICENSE_ARTIFACT"
      printf '%s\n' 'Successfully returned the Unity license.' "$password" > "$logFile"
      exit 0
      ;;
    block-timeout)
      : > "$TEST_RETURN_BLOCK_STARTED"
      sleep 8
      rm -f -- "$TEST_LICENSE_ARTIFACT"
      printf '%s\n' 'Successfully returned the Unity license.' "$password" > "$logFile"
      exit 0
      ;;
    no-marker) printf '%s\n' 'Unity exited without a return result.' > "$logFile"; exit 0 ;;
    generic-error) printf '%s\n' '[Licensing::Client] Error: unknown return response.' > "$logFile"; exit 0 ;;
    false-success) printf '%s\n' 'Failed to return license: timed out.' > "$logFile"; exit 0 ;;
    failure) printf '%s\n' 'Failed to return license.' > "$logFile"; exit 9 ;;
    *) exit 91 ;;
  esac
fi

printf '%s\n' activate >> "$TEST_CALL_LOG"
if [[ "$(hashValue "$serial")" != "$TEST_EXPECTED_SERIAL_HASH" ]]; then
  exit 92
fi
case "$TEST_ACTIVATION_MODE" in
  success)
    printf '%s\n' active > "$TEST_LICENSE_ARTIFACT"
    printf '%s\n' '[Licensing::Module] Serial number assigned to: "masked"' "$password" > "$logFile"
    exit 0
    ;;
  no-marker)
    printf '%s\n' 'Unity exited without an activation result.' > "$logFile"
    exit 0
    ;;
  false-success)
    printf '%s\n' '[Licensing::Module] Serial number assigned to: "masked"' 'Failed to activate license.' > "$logFile"
    exit 0
    ;;
  failure)
    printf '%s\n' '[Licensing::Module] Serial number assigned to: "masked"' > "$logFile"
    exit 7
    ;;
  block)
    : > "$TEST_ACTIVATION_BLOCK_STARTED"
    sleep 8
    printf '%s\n' active > "$TEST_LICENSE_ARTIFACT"
    printf '%s\n' '[Licensing::Module] Serial number assigned to: "masked"' "$password" > "$logFile"
    exit 0
    ;;
  *) exit 93 ;;
esac
EOF
chmod 700 "$fakeEditorDirectory/unity-editor"

cat > "$fakeSteps/set_extra_git_configs.sh" <<'EOF'
#!/usr/bin/env bash
# Match GameCI v4.3.1's nounset-sensitive contract. The production wrapper
# must forward this variable even when there are no extra Git config entries.
: "${GIT_CONFIG_EXTENSIONS?GIT_CONFIG_EXTENSIONS must be defined}"
EOF
cp "$fakeSteps/set_extra_git_configs.sh" "$fakeSteps/set_gitcredential.sh"
cp "$scriptDirectory/gameci-activate-online.sh" "$fakeSteps/activate.sh"
cp "$scriptDirectory/gameci-return-license.sh" "$fakeSteps/return_license.sh"
cat > "$fakeSteps/run_tests.sh" <<'EOF'
#!/usr/bin/env bash
credentialPresent=false
for variableName in UNITY_LICENSE UNITY_EMAIL UNITY_PASSWORD UNITY_SERIAL; do
  if [[ -n "${!variableName:-}" ]]; then
    credentialPresent=true
  fi
done
IFS=':' read -r -a credentialHashes <<< "$TEST_CREDENTIAL_HASHES"
while IFS= read -r -d '' environmentEntry; do
  environmentValue="${environmentEntry#*=}"
  environmentHash="$(printf '%s' "$environmentValue" | sha256sum | awk '{ print $1 }')"
  for credentialHash in "${credentialHashes[@]}"; do
    if [[ "$environmentHash" == "$credentialHash" ]]; then
      credentialPresent=true
    fi
  done
done < <(env -0)
printf 'credential_present=%s\n' "$credentialPresent" > "$TEST_ENVIRONMENT_STATE"
if [[ "${TEST_BLOCK_UNTIL_SIGNAL:-false}" == true ]]; then
  : > "$TEST_BLOCK_STARTED"
  # Finite fallback keeps a broken signal-path regression from hanging CI.
  sleep 8
fi
TEST_RUNNER_EXIT_CODE="$TEST_REQUESTED_EXIT_CODE"
EOF
chmod 700 "$fakeSteps"/*.sh

runSecureCase() {
  local activationMode="$1"
  local testExitCode="$2"
  local returnMode="$3"
  local expectedExitCode="$4"
  local useXtrace="${5:-false}"
  local output
  local bashArguments=(--noprofile --norc "$secureSteps")
  if [[ "$useXtrace" == true ]]; then
    bashArguments=(--noprofile --norc -x "$secureSteps")
  fi

  : > "$callLog"
  rm -f -- "$fakeLicenseArtifact"
  rm -f "$testEnvironmentState"
  set +e
  output="$({
    PATH="$fakeEditorDirectory:$PATH" \
    MTS_GAMECI_STEPS_DIRECTORY="$fakeSteps" \
    MTS_BLANK_PROJECT="$temporaryRoot/BlankProject" \
    TEST_EXPECTED_SERIAL_HASH="$fakeSerialHash" \
    TEST_EXPECTED_EMAIL_HASH="$fakeEmailHash" \
    TEST_EXPECTED_PASSWORD_HASH="$fakePasswordHash" \
    TEST_CREDENTIAL_HASHES="$fakeLicenseHash:$fakeSerialHash:$fakeEmailHash:$fakePasswordHash" \
    TEST_ACTIVATION_MODE="$activationMode" \
    TEST_RETURN_MODE="$returnMode" \
    TEST_REQUESTED_EXIT_CODE="$testExitCode" \
    TEST_CALL_LOG="$callLog" \
    TEST_ENVIRONMENT_STATE="$testEnvironmentState" \
    TEST_LICENSE_ARTIFACT="$fakeLicenseArtifact" \
    MTS_UNITY_LICENSE_ARTIFACTS="$fakeLicenseArtifact" \
    UNITY_LICENSE="$fakeLicense" \
    UNITY_SERIAL="$fakeSerial" \
    UNITY_EMAIL="$fakeEmail" \
    UNITY_PASSWORD="$fakePassword" \
    bash "${bashArguments[@]}"
  } 2>&1)"
  status=$?
  set -e

  assertSecretsAbsent "$output" "Secure GameCI $activationMode/$returnMode output"
  if [[ $status -ne $expectedExitCode ]]; then
    echo "Secure GameCI returned $status; expected $expectedExitCode." >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

runSecureCase success 0 success 0
grep -Fxq -- 'credential_present=false' "$testEnvironmentState"
grep -Fxq -- activate "$callLog"
grep -Fxq -- return "$callLog"

runSecureCase success 2 success 2
grep -Fxq -- return "$callLog"

runSecureCase success 0 false-success 1
runSecureCase success 0 no-marker 1
runSecureCase success 0 generic-error 1
runSecureCase no-marker 0 success 1
runSecureCase false-success 0 success 1
runSecureCase failure 0 success 7
runSecureCase success 0 success 0 true

runSecureSignalCase() {
  local phase="$1"
  local activationMode=success
  local returnMode=success
  local blockTest=false
  local blockStarted="$temporaryRoot/$phase-block-started"
  local signalOutput="$temporaryRoot/$phase-signal-output.log"
  case "$phase" in
    activation) activationMode=block ;;
    test) blockTest=true ;;
    return) returnMode=block ;;
    return-timeout) returnMode=block-timeout ;;
    *) echo "Unknown signal phase: $phase" >&2; exit 1 ;;
  esac

  : > "$callLog"
  rm -f -- "$fakeLicenseArtifact"
  rm -f "$testEnvironmentState" "$blockStarted"
  set +e
  PATH="$fakeEditorDirectory:$PATH" \
  MTS_GAMECI_STEPS_DIRECTORY="$fakeSteps" \
  MTS_BLANK_PROJECT="$temporaryRoot/BlankProject" \
  TEST_EXPECTED_SERIAL_HASH="$fakeSerialHash" \
  TEST_EXPECTED_EMAIL_HASH="$fakeEmailHash" \
  TEST_EXPECTED_PASSWORD_HASH="$fakePasswordHash" \
  TEST_CREDENTIAL_HASHES="$fakeLicenseHash:$fakeSerialHash:$fakeEmailHash:$fakePasswordHash" \
  TEST_ACTIVATION_MODE="$activationMode" \
  TEST_RETURN_MODE="$returnMode" \
  TEST_REQUESTED_EXIT_CODE=0 \
  TEST_BLOCK_UNTIL_SIGNAL="$blockTest" \
  TEST_BLOCK_STARTED="$blockStarted" \
  TEST_ACTIVATION_BLOCK_STARTED="$blockStarted" \
  TEST_RETURN_BLOCK_STARTED="$blockStarted" \
  TEST_CALL_LOG="$callLog" \
  TEST_ENVIRONMENT_STATE="$testEnvironmentState" \
  TEST_LICENSE_ARTIFACT="$fakeLicenseArtifact" \
  MTS_UNITY_LICENSE_ARTIFACTS="$fakeLicenseArtifact" \
  UNITY_LICENSE="$fakeLicense" \
  UNITY_SERIAL="$fakeSerial" \
  UNITY_EMAIL="$fakeEmail" \
  UNITY_PASSWORD="$fakePassword" \
  bash --noprofile --norc "$secureSteps" >"$signalOutput" 2>&1 &
  securePid=$!
  for _ in {1..100}; do
    [[ -f "$blockStarted" ]] && break
    sleep 0.05
  done
  if [[ ! -f "$blockStarted" ]]; then
    echo "The signal regression did not reach its blocking $phase child." >&2
    kill -KILL "$securePid" 2>/dev/null || true
    wait "$securePid" 2>/dev/null || true
    exit 1
  fi
  signalStart=$SECONDS
  kill -TERM "$securePid"
  wait "$securePid"
  signalStatus=$?
  signalElapsed=$((SECONDS - signalStart))
  set -e
  signalText="$(<"$signalOutput")"
  assertSecretsAbsent "$signalText" "Secure GameCI $phase signal output"
  if [[ $signalStatus -ne 130 || $signalElapsed -gt 7 ]]; then
    echo "$phase TERM handling returned $signalStatus after ${signalElapsed}s; expected 130 within 7s." >&2
    exit 1
  fi
  if [[ $(grep -Fxc -- return "$callLog") -ne 1 ]]; then
    echo "$phase TERM handling must attempt exactly one Unity license return." >&2
    exit 1
  fi
  if [[ "$phase" == return && -f "$fakeLicenseArtifact" ]]; then
    echo "TERM during return must allow the in-flight license return to finish." >&2
    exit 1
  fi
  if [[ "$phase" == return-timeout ]]; then
    if [[ ! -f "$fakeLicenseArtifact" ]] || \
       [[ "$signalText" != *'manual recovery'* ]]; then
      echo "A timed-out in-flight return must preserve explicit seat-recovery evidence." >&2
      exit 1
    fi
  fi
}

runSecureSignalCase activation
runSecureSignalCase test
runSecureSignalCase return
runSecureSignalCase return-timeout

fakeDockerDirectory="$temporaryRoot/fake-docker"
fakeGameCiDist="$temporaryRoot/fake-gameci/dist"
fakeDockerLog="$temporaryRoot/fake-docker.log"
fakeDockerStarted="$temporaryRoot/fake-docker.started"
fakeDockerStopped="$temporaryRoot/fake-docker.stopped"
mkdir -p \
  "$fakeDockerDirectory" \
  "$fakeGameCiDist/platforms/ubuntu" \
  "$fakeGameCiDist/BlankProject" \
  "$fakeGameCiDist/test-standalone-scripts" \
  "$fakeGameCiDist/unity-config"
printf '%s\n' '#!/usr/bin/env bash' > "$fakeGameCiDist/platforms/ubuntu/entrypoint.sh"

cat > "$fakeDockerDirectory/docker" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
commandName="${1:-}"
shift || true
case "$commandName" in
  run)
    cidFile=''
    arguments=("$@")
    while [[ $# -gt 0 ]]; do
      if [[ "$1" == --cidfile ]]; then
        shift
        cidFile="$1"
      fi
      shift
    done
    printf 'run\n' >> "$FAKE_DOCKER_LOG"
    printf '%s\n' fake-container > "$cidFile"
    : > "$FAKE_DOCKER_STARTED"
    while [[ ! -f "$FAKE_DOCKER_STOPPED" ]]; do sleep 0.05; done
    exit 143
    ;;
  stop)
    printf 'stop %s\n' "$*" >> "$FAKE_DOCKER_LOG"
    : > "$FAKE_DOCKER_STOPPED"
    ;;
  rm)
    printf 'rm %s\n' "$*" >> "$FAKE_DOCKER_LOG"
    ;;
  *) echo "Unexpected fake docker command: $commandName" >&2; exit 95 ;;
esac
EOF
chmod 700 "$fakeDockerDirectory/docker"

outerOutput="$temporaryRoot/outer-signal-output.log"
outerRunnerTemp="$temporaryRoot/outer-runner-temp"
mkdir -p "$outerRunnerTemp"
set +e
PATH="$fakeDockerDirectory:$PATH" \
FAKE_DOCKER_LOG="$fakeDockerLog" \
FAKE_DOCKER_STARTED="$fakeDockerStarted" \
FAKE_DOCKER_STOPPED="$fakeDockerStopped" \
GITHUB_WORKSPACE="$repositoryRoot" \
RUNNER_TEMP="$outerRunnerTemp" \
GAMECI_ACTION_PATH="$fakeGameCiDist" \
UNITY_IMAGE='unityci/editor:test' \
UNITY_VERSION='2022.3.22f1' \
PROJECT_PATH='Packages/com.buildsoft.motion-take-studio' \
ARTIFACTS_PATH='artifacts' \
GITHUB_ACTIONS=false \
UNITY_LICENSE="$fakeLicense" \
UNITY_EMAIL="$fakeEmail" \
UNITY_PASSWORD="$fakePassword" \
bash --noprofile --norc "$runner" >"$outerOutput" 2>&1 &
outerPid=$!
for _ in {1..100}; do
  [[ -f "$fakeDockerStarted" ]] && break
  sleep 0.05
done
if [[ ! -f "$fakeDockerStarted" ]]; then
  echo "The outer signal regression did not start fake Docker." >&2
  kill -KILL "$outerPid" 2>/dev/null || true
  wait "$outerPid" 2>/dev/null || true
  exit 1
fi
outerSignalStart=$SECONDS
kill -TERM "$outerPid"
wait "$outerPid"
outerStatus=$?
outerSignalElapsed=$((SECONDS - outerSignalStart))
set -e
assertSecretsAbsent "$(<"$outerOutput")" "Outer GameCI signal output"
assertSecretsAbsent "$(<"$fakeDockerLog")" "Outer fake Docker log"
if [[ $outerStatus -ne 130 || $outerSignalElapsed -gt 7 ]]; then
  echo "Outer TERM handling returned $outerStatus after ${outerSignalElapsed}s; expected 130 within 7s." >&2
  exit 1
fi
if ! grep -Fxq -- 'stop --timeout 6 fake-container' "$fakeDockerLog"; then
  echo "Outer TERM handling must begin a six-second graceful Docker stop." >&2
  exit 1
fi
if find "$outerRunnerTemp" -type f -print -quit | grep -q .; then
  echo "Outer TERM handling left a runner temporary file behind." >&2
  exit 1
fi

if grep -R -F -q -- "$fakePassword" "$temporaryRoot"; then
  echo "Credential material was persisted to a temporary file." >&2
  grep -R -F -l -- "$fakePassword" "$temporaryRoot" | sed 's#^.*/##' >&2
  exit 1
fi

echo "Credential-safe GameCI regression tests passed."
