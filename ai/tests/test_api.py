import asyncio

import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from app.providers.base import LlmError
from app.providers.fake import FakeProvider

SECRET = "s" * 16
HEADERS = {"X-Ai-Secret": SECRET}
PAYLOAD = {
    "language": "uk",
    "messages": [
        {"author": "Alice", "sentAt": "2026-11-10T17:06:00Z", "body": "Дедлайн лаби в пʼятницю"},
        {"author": "Bob", "sentAt": "2026-11-10T17:07:00Z", "body": "Окей, здаю завтра"},
    ],
}


def make_client(provider=None, **overrides):
    values = {"ai_provider": "fake", "ai_shared_secret": SECRET, **overrides}
    settings = Settings(_env_file=None, **values)
    return TestClient(create_app(settings, provider or FakeProvider()))


def test_health_needs_no_secret():
    assert make_client().get("/health").json() == {"status": "ok"}


def test_missing_or_wrong_secret_is_401_even_for_invalid_body():
    client = make_client()
    assert client.post("/v1/summarize", json=PAYLOAD).status_code == 401
    assert client.post("/v1/summarize", json=PAYLOAD, headers={"X-Ai-Secret": "nope"}).status_code == 401
    assert client.post("/v1/summarize", json={"language": "xx"}).status_code == 401


def test_summarize_returns_summary_and_count():
    response = make_client().post("/v1/summarize", json=PAYLOAD, headers=HEADERS)

    assert response.status_code == 200
    assert response.json() == {"summary": "- Fake summary of 2 message(s).", "messageCount": 2}


@pytest.mark.parametrize(
    "payload",
    [
        {**PAYLOAD, "language": "de"},
        {**PAYLOAD, "messages": []},
        {"language": "uk", "messages": [{"author": "", "sentAt": "2026-11-10T17:06:00Z", "body": "x"}]},
        {"language": "uk", "messages": [{"author": "A", "sentAt": "2026-11-10T17:06:00Z", "body": "x" * 4001}]},
    ],
)
def test_invalid_input_is_422(payload):
    assert make_client().post("/v1/summarize", json=payload, headers=HEADERS).status_code == 422


def test_too_many_messages_is_422():
    client = make_client(ai_max_messages=1)
    assert client.post("/v1/summarize", json=PAYLOAD, headers=HEADERS).status_code == 422


class FailingProvider:
    name = "failing"

    async def complete(self, *, system, user, max_tokens):
        raise LlmError("boom")


class SlowProvider:
    name = "slow"

    async def complete(self, *, system, user, max_tokens):
        await asyncio.sleep(5)
        return "never"


def test_provider_failure_is_502():
    response = make_client(FailingProvider()).post("/v1/summarize", json=PAYLOAD, headers=HEADERS)
    assert response.status_code == 502
    assert "Alice" not in response.text and "boom" not in response.text


def test_timeout_is_504():
    response = make_client(SlowProvider(), ai_timeout_seconds=0.05).post("/v1/summarize", json=PAYLOAD, headers=HEADERS)
    assert response.status_code == 504
