import asyncio
from types import SimpleNamespace

import pytest

from app.providers.anthropic_provider import AnthropicProvider
from app.providers.base import LlmError
from app.providers.gemini_provider import GeminiProvider


def run(coro):
    return asyncio.run(coro)


class FakeAnthropicClient:
    def __init__(self, content=None, error=None):
        self.calls = []
        self._content, self._error = content, error
        self.messages = SimpleNamespace(create=self._create)

    async def _create(self, **kwargs):
        self.calls.append(kwargs)
        if self._error:
            raise self._error
        return SimpleNamespace(content=self._content)


def text_block(text):
    return SimpleNamespace(type="text", text=text)


def test_anthropic_sends_prompts_and_returns_text():
    client = FakeAnthropicClient(content=[text_block(" - hello "), SimpleNamespace(type="thinking", text="x")])
    provider = AnthropicProvider("key", "model-x", client=client)

    result = run(provider.complete(system="SYS", user="USER", max_tokens=123))

    assert result == "- hello"
    call = client.calls[0]
    assert call["model"] == "model-x" and call["max_tokens"] == 123 and call["system"] == "SYS"
    assert call["messages"] == [{"role": "user", "content": "USER"}]


def test_anthropic_wraps_sdk_errors_without_leaking_details():
    provider = AnthropicProvider("key", "m", client=FakeAnthropicClient(error=RuntimeError("secret chat text")))
    with pytest.raises(LlmError) as exc:
        run(provider.complete(system="s", user="u", max_tokens=10))
    assert "secret chat text" not in str(exc.value) and "RuntimeError" in str(exc.value)


def test_anthropic_empty_response_is_an_error():
    provider = AnthropicProvider("key", "m", client=FakeAnthropicClient(content=[text_block("  ")]))
    with pytest.raises(LlmError):
        run(provider.complete(system="s", user="u", max_tokens=10))


class FakeGeminiClient:
    def __init__(self, text=None, error=None):
        self.calls = []
        self._text, self._error = text, error
        self.aio = SimpleNamespace(models=SimpleNamespace(generate_content=self._generate))

    async def _generate(self, **kwargs):
        self.calls.append(kwargs)
        if self._error:
            raise self._error
        return SimpleNamespace(text=self._text)


def test_gemini_sends_prompts_and_returns_text():
    client = FakeGeminiClient(text=" - hi ")
    provider = GeminiProvider("key", "gem-x", client=client)

    result = run(provider.complete(system="SYS", user="USER", max_tokens=55))

    assert result == "- hi"
    call = client.calls[0]
    assert call["model"] == "gem-x" and call["contents"] == "USER"
    assert call["config"].system_instruction == "SYS" and call["config"].max_output_tokens == 55


def test_gemini_wraps_errors_and_rejects_empty_text():
    with pytest.raises(LlmError):
        run(GeminiProvider("k", "m", client=FakeGeminiClient(error=ValueError("boom"))).complete(system="s", user="u", max_tokens=1))
    with pytest.raises(LlmError):
        run(GeminiProvider("k", "m", client=FakeGeminiClient(text=None)).complete(system="s", user="u", max_tokens=1))
