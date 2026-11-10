# Migly AI service

Small stateless FastAPI service that summarizes unread chat messages with a cloud LLM. The .NET API calls it; browsers never do. It listens on `127.0.0.1` only and every request needs the `X-Ai-Secret` header.

```
POST /v1/summarize   {"language": "uk"|"en", "messages": [{"author", "sentAt", "body"}]}  ->  {"summary", "messageCount"}
GET  /health
```

Errors: `401` wrong secret · `422` invalid input or too many messages · `502` provider failed · `504` provider timed out.

## Providers

`app/providers/factory.py` maps `AI_PROVIDER` to an adapter implementing `LlmProvider.complete(system, user, max_tokens)`. Available: `anthropic`, `gemini`, `fake`. To add one: write an adapter and add one line to the registry.

## Run

```bash
cd ai
python -m venv .venv
.venv/Scripts/python -m pip install -r requirements-dev.txt     # Linux/macOS: .venv/bin/python
```

Configure through environment variables or the repository's root `.env` (copy `.env.example`, fill in the AI_* block; the file is found from any working directory), then:

```bash
AI_PROVIDER=fake AI_SHARED_SECRET=0123456789abcdef .venv/Scripts/python -m uvicorn app.main:create_app --factory --host 127.0.0.1 --port 8000
```

## Test

```bash
.venv/Scripts/python -m pytest -q
```

Tests never call a real provider (SDK clients are replaced by stubs). Real Anthropic/Gemini calls need a key and are checked by hand.
