#!/bin/sh
# One attempt per process, six attempts total; only transient failures get a 60s wait.
# No command/connection details are logged. The runtime emits redacted classifications.
set -u
child=''
shutdown() {
    code=$1
    trap '' TERM INT
    if [ -n "$child" ]; then
        kill -TERM "$child" 2>/dev/null || :
        wait "$child" 2>/dev/null || :
    fi
    exit "$code"
}
trap 'shutdown 143' TERM
trap 'shutdown 130' INT

attempt=1
while [ "$attempt" -le 6 ]; do
    dotnet MudXtra.ThemeCreator.UI.dll --postgres-probe "$@" &
    child=$!
    wait "$child"
    status=$?
    child=''
    case "$status" in
        0) exec dotnet MudXtra.ThemeCreator.UI.dll "$@" ;;
        10) ;;
        *) exit "$status" ;;
    esac
    if [ "$attempt" -eq 6 ]; then
        echo 'PostgreSQL startup: transient budget exhausted (6 attempts)' >&2
        exit 10
    fi
    echo 'PostgreSQL startup: transient failure; waiting 60 seconds' >&2
    sleep 60 &
    child=$!
    wait "$child"
    status=$?
    child=''
    [ "$status" -eq 0 ] || exit "$status"
    attempt=$((attempt + 1))
done
