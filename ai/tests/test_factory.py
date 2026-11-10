import pytest

from app.config import Settings
from app.providers.anthropic_provider import AnthropicProvider
from app.providers.factory import ConfigError, create_provider
from app.providers.fake import FakeProvider
from app.providers.gemini_provider import GeminiProvider


def make_settings(**overrides) -> Settings:
    values = {"ai_provider": "fake", "ai_shared_secret": "x" * 16}
    values.update(overrides)
    return Settings(_env_file=None, **values)


def test_fake_provider_is_created():
    assert isinstance(create_provider(make_settings()), FakeProvider)


def test_provider_name_is_case_insensitive():
    assert isinstance(create_provider(make_settings(ai_provider="FAKE")), FakeProvider)


def test_unknown_provider_lists_available_ones():
    with pytest.raises(ConfigError) as exc:
        create_provider(make_settings(ai_provider="skynet"))
    message = str(exc.value)
    assert "skynet" in message and "anthropic" in message and "gemini" in message


@pytest.mark.parametrize("provider", ["anthropic", "gemini"])
def test_cloud_provider_requires_api_key(provider):
    with pytest.raises(ConfigError) as exc:
        create_provider(make_settings(ai_provider=provider))
    assert "API_KEY" in str(exc.value)


def test_anthropic_provider_is_created_with_default_model():
    provider = create_provider(make_settings(ai_provider="anthropic", anthropic_api_key="k"))
    assert isinstance(provider, AnthropicProvider)
    assert provider.model == "claude-haiku-5-5"


def test_gemini_provider_uses_explicit_model():
    provider = create_provider(
        make_settings(ai_provider="gemini", gemini_api_key="k", ai_model="gemini-custom")
    )
    assert isinstance(provider, GeminiProvider)
    assert provider.model == "gemini-custom"


def test_settings_require_provider_and_secret():
    with pytest.raises(Exception):
        Settings(_env_file=None, ai_shared_secret="x" * 16)  # no AI_PROVIDER
    with pytest.raises(Exception):
        Settings(_env_file=None, ai_provider="fake", ai_shared_secret="short")


def test_settings_read_the_repository_root_env_file_and_ignore_the_dotnet_keys(tmp_path):
    from app.config import ENV_FILE

    # the configured file really is at the repository root (next to the ai/ folder), whatever the working directory is
    assert (ENV_FILE.parent / "ai" / "app" / "config.py").exists() and ENV_FILE.name == ".env"

    env = tmp_path / ".env"
    env.write_text(
        "AI_PROVIDER=gemini\nGEMINI_API_KEY=k\nAI_SHARED_SECRET=0123456789abcdef\n"
        "Jwt__Secret=for-the-dotnet-api\nConnectionStrings__Default=Host=x\nAi__Enabled=true\n",
        encoding="utf-8",
    )

    settings = Settings(_env_file=env)

    assert (settings.ai_provider, settings.gemini_api_key) == ("gemini", "k")
