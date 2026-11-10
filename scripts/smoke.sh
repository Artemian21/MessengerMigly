#!/usr/bin/env sh
# Smoke test: health → docs → register → verify (via Mailpit API) → login → protected call → 401 without token.
# Usage: scripts/smoke.sh http://localhost:5000 [mailpit-url]
# Against a deployment without Mailpit, pass an already verified account instead:
#   SMOKE_EMAIL=you@example.com SMOKE_PASSWORD=... scripts/smoke.sh https://<app>.onrender.com
set -eu

base=${1:?usage: smoke.sh <base-url> [mailpit-url]}
mailpit=${2:-http://localhost:8025}
json_field() { sed -E "s/.*\"$1\":\"([^\"]*)\".*/\1/"; }

echo "1. GET /health";        curl -fsS "$base/health"; echo
echo "2. GET /health/ready";  curl -fsS "$base/health/ready"; echo
echo "3. GET /docs";          curl -fsS -o /dev/null "$base/docs" && echo "ok"

if [ -z "${SMOKE_EMAIL:-}" ]; then
  suffix=$(date +%s)
  SMOKE_EMAIL="smoke$suffix@example.com"
  SMOKE_PASSWORD="Smoke-test-pass-1"
  echo "4. POST /api/v1/auth/register ($SMOKE_EMAIL)"
  curl -fsS -X POST "$base/api/v1/auth/register" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$SMOKE_EMAIL\",\"password\":\"$SMOKE_PASSWORD\",\"username\":\"smoke$suffix\",\"displayName\":\"Smoke Test\"}"
  echo
  sleep 2
  echo "5. Read the verification link from Mailpit"
  id=$(curl -fsS "$mailpit/api/v1/search?query=to:$SMOKE_EMAIL" | sed -E 's/.*"ID":"([^"]+)".*/\1/')
  token=$(curl -fsS "$mailpit/api/v1/message/$id" | grep -o 'token=[A-Za-z0-9_-]*' | head -1 | cut -d= -f2)
  curl -fsS -X POST "$base/api/v1/auth/verify-email" -H 'Content-Type: application/json' -d "{\"token\":\"$token\"}"
  echo "verified"
fi

echo "6. POST /api/v1/auth/login"
access=$(curl -fsS -X POST "$base/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$SMOKE_EMAIL\",\"password\":\"$SMOKE_PASSWORD\"}" | json_field accessToken)

echo "7. GET /api/v1/users/me with token"
curl -fsS "$base/api/v1/users/me" -H "Authorization: Bearer $access"; echo

echo "8. GET /api/v1/users/me without token (expect 401)"
code=$(curl -s -o /dev/null -w '%{http_code}' "$base/api/v1/users/me")
[ "$code" = "401" ] || { echo "expected 401, got $code"; exit 1; }
echo "401 ok"
echo "Smoke test passed."
