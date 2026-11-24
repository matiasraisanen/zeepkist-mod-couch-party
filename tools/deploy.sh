#!/usr/bin/env bash
# Deploy Couch Party from a Linux dev machine to the Windows PC that runs Zeepkist, over SSH.
#
#   tools/deploy.sh libs     copy BepInEx.dll and 0Harmony.dll from the PC into lib/ (once)
#   tools/deploy.sh build    build, run the tests and the member scan (uses lib/)
#   tools/deploy.sh deploy   copy bin/Release/CouchParty.dll into BepInEx/plugins on the PC
#   tools/deploy.sh log      fetch BepInEx/LogOutput.log into logs/ (timestamped)
#   tools/deploy.sh all      build, then deploy
#
# Settings come from the environment (nothing personal lives in the repo):
#   COUCHPARTY_HOST   SSH host or alias of the Windows PC (default: windows-pc, set it up in ~/.ssh/config)
#   ZEEPKIST_DIR      Zeepkist folder on the PC, forward slashes
#                     (default: /C:/Program Files (x86)/Steam/steamapps/common/Zeepkist)
#   COUCHPARTY_LOG    full path of LogOutput.log on the PC, forward slashes
#                     (default: $ZEEPKIST_DIR/BepInEx/LogOutput.log)
#
# The fetched log goes to logs/LogOutput-<date>.log, with logs/latest.log pointing at the newest.
#
# The PC needs the Windows "OpenSSH Server" feature. The game must be closed when deploying,
# or Windows keeps the DLL locked.
set -euo pipefail

HOST="${COUCHPARTY_HOST:-windows-pc}"
ZEEPKIST_DIR="${ZEEPKIST_DIR:-/C:/Program Files (x86)/Steam/steamapps/common/Zeepkist}"
LOG_PATH="${COUCHPARTY_LOG:-$ZEEPKIST_DIR/BepInEx/LogOutput.log}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIB_DIR="$REPO/lib/bepinex-core"
DLL="$REPO/bin/Release/CouchParty.dll"

ssh_opts=(-o ConnectTimeout=8)

game_running() {
    # Windows OpenSSH runs cmd.exe by default. Stop here if the PC can't be reached (e.g. it's off).
    local tasks
    if ! tasks=$(ssh "${ssh_opts[@]}" "$HOST" 'tasklist /FI "IMAGENAME eq Zeepkist.exe" /NH'); then
        echo "Can't reach $HOST over SSH. Is the PC on?" >&2
        exit 1
    fi
    grep -qi 'Zeepkist.exe' <<<"$tasks"
}

cmd_libs() {
    mkdir -p "$LIB_DIR"
    scp "${ssh_opts[@]}" "$HOST:$ZEEPKIST_DIR/BepInEx/core/BepInEx.dll" \
                         "$HOST:$ZEEPKIST_DIR/BepInEx/core/0Harmony.dll" "$LIB_DIR/"
    ls -l "$LIB_DIR"
}

cmd_build() {
    [[ -f "$LIB_DIR/BepInEx.dll" && -f "$LIB_DIR/0Harmony.dll" ]] \
        || { echo "Missing $LIB_DIR/*.dll: run 'tools/deploy.sh libs' first." >&2; exit 1; }
    cd "$REPO"
    dotnet build CouchParty.csproj -c Release -p:BepInExCoreDir="$LIB_DIR"
    # The tests target net8.0; roll forward so a newer runtime (e.g. Ubuntu's .NET 10) can run them.
    DOTNET_ROLL_FORWARD=Major dotnet run --project tests/CouchParty.Tests -c Release | tee /dev/stderr \
        | grep -q 'ALL PASSED' || { echo "Tests did not print ALL PASSED." >&2; exit 1; }
    # memberrefs.py needs dnfile; use the repo's .venv if there is one.
    local py=python3
    [[ -x "$REPO/.venv/bin/python" ]] && py="$REPO/.venv/bin/python"
    "$py" tools/memberrefs.py "$DLL"
}

cmd_deploy() {
    [[ -f "$DLL" ]] || { echo "No $DLL: build first." >&2; exit 1; }
    if game_running; then
        echo "Zeepkist is running on $HOST. Close the game first (Windows locks the DLL)." >&2
        exit 1
    fi
    scp "${ssh_opts[@]}" "$DLL" "$HOST:$ZEEPKIST_DIR/BepInEx/plugins/CouchParty.dll"
    echo "Deployed $(stat -c '%s bytes, built %y' "$DLL") to $HOST."
}

cmd_log() {
    mkdir -p "$REPO/logs"
    local out="$REPO/logs/LogOutput-$(date +%Y%m%d-%H%M%S).log"
    scp "${ssh_opts[@]}" "$HOST:$LOG_PATH" "$out"
    ln -sfn "$(basename "$out")" "$REPO/logs/latest.log"
    summarize_log "$out"
}

summarize_log() {
    local f="$1"
    echo "Saved $f"
    grep -m1 'Couch Party .* loaded' "$f" || echo "  (no 'Couch Party ... loaded' line: did the mod load?)"
    echo "  $(grep -c '\[debug\]' "$f" || true) debug lines"
    # Errors and exceptions, from any mod: a failed patch elsewhere can still break things.
    local errs
    errs=$(grep -nE '^\[(Error|Fatal)|Exception' "$f" || true)
    if [[ -n "$errs" ]]; then
        echo "  $(wc -l <<<"$errs") error/exception lines, first ones:"
        head -10 <<<"$errs" | sed 's/^/    /'
    else
        echo "  no errors or exceptions"
    fi
}

case "${1:-}" in
    libs)   cmd_libs ;;
    build)  cmd_build ;;
    deploy) cmd_deploy ;;
    log)    cmd_log ;;
    all)    cmd_build; cmd_deploy ;;
    *)      sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
