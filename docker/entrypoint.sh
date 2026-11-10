#!/bin/sh
# Container entrypoint. With Ai__Enabled=true it starts the Python AI service (ai/) next to the API and waits until
# it is healthy; then it runs the API as PID 1, so the platform's SIGTERM reaches it.
set -eu

AI_PYTHON="${AI_PYTHON:-/opt/ai-venv/bin/python}"
AI_DIR="${AI_DIR:-/app/ai}"
AI_WAIT_SECONDS="${AI_WAIT_SECONDS:-30}"

case "$(printf %s "${Ai__Enabled:-false}" | tr '[:upper:]' '[:lower:]')" in
  true)
    # One secret for both sides: the API sends Ai__SharedSecret, the service checks AI_SHARED_SECRET.
    export AI_SHARED_SECRET="${AI_SHARED_SECRET:-${Ai__SharedSecret:-}}"
    # The port has to match Ai__BaseUrl (default http://127.0.0.1:8000). Loopback only: nothing outside the container can reach it.
    (cd "$AI_DIR" && exec "$AI_PYTHON" -m uvicorn app.main:create_app --factory --host 127.0.0.1 --port 8000) &
    ai_pid=$!

    waited=0
    until curl -fsS -o /dev/null http://127.0.0.1:8000/health 2>/dev/null; do
      if ! kill -0 "$ai_pid" 2>/dev/null; then
        echo "entrypoint: the AI service exited during startup (check AI_PROVIDER, AI_MODEL, the API key and AI_SHARED_SECRET)" >&2
        exit 1
      fi
      waited=$((waited + 1))
      if [ "$waited" -ge "$AI_WAIT_SECONDS" ]; then
        echo "entrypoint: the AI service was not healthy after ${AI_WAIT_SECONDS}s" >&2
        kill "$ai_pid" 2>/dev/null || true
        exit 1
      fi
      sleep 1
    done
    echo "entrypoint: AI service is up"
    ;;
esac

# Render passes the port in $PORT.
export ASPNETCORE_URLS="http://0.0.0.0:${PORT:-10000}"
exec dotnet Messenger.Api.dll
