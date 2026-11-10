from collections.abc import Callable

from app.config import Settings
from app.providers.anthropic_provider import AnthropicProvider
from app.providers.base import LlmProvider
from app.providers.fake import FakeProvider
from app.providers.gemini_provider import GeminiProvider


class ConfigError(Exception):
    """Invalid or incomplete provider configuration. Raised at startup so the service fails fast."""


# Used when AI_MODEL is empty. Override with AI_MODEL; models are retired over time.
DEFAULT_MODELS = {
    "anthropic": "claude-haiku-5-5",
    "gemini": "gemini-2.5-flash",
}


def _anthropic(settings: Settings) -> LlmProvider:
    if not settings.anthropic_api_key:
        raise ConfigError("AI_PROVIDER=anthropic requires ANTHROPIC_API_KEY")
    return AnthropicProvider(settings.anthropic_api_key, settings.ai_model or DEFAULT_MODELS["anthropic"])


def _gemini(settings: Settings) -> LlmProvider:
    if not settings.gemini_api_key:
        raise ConfigError("AI_PROVIDER=gemini requires GEMINI_API_KEY")
    return GeminiProvider(settings.gemini_api_key, settings.ai_model or DEFAULT_MODELS["gemini"])


def _fake(settings: Settings) -> LlmProvider:
    return FakeProvider()


# Registry: adding a provider means writing an adapter and one line here.
_BUILDERS: dict[str, Callable[[Settings], LlmProvider]] = {
    "anthropic": _anthropic,
    "gemini": _gemini,
    "fake": _fake,
}


def create_provider(settings: Settings) -> LlmProvider:
    name = settings.ai_provider.strip().lower()
    builder = _BUILDERS.get(name)
    if builder is None:
        raise ConfigError(f"Unknown AI_PROVIDER '{settings.ai_provider}'. Available: {', '.join(sorted(_BUILDERS))}")
    return builder(settings)
