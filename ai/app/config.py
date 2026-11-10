from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Configuration from environment variables (or a local `.env`). Secrets never have defaults."""

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    # Required on purpose: no silent fallback to the fake provider in production.
    ai_provider: str
    ai_model: str = ""  # empty -> the provider's default model
    anthropic_api_key: str = ""
    gemini_api_key: str = ""

    # Shared with the .NET API (header X-Ai-Secret); the service only listens on localhost.
    ai_shared_secret: str = Field(min_length=16)

    ai_timeout_seconds: float = Field(default=20.0, gt=0)
    ai_max_messages: int = Field(default=300, ge=1)
    ai_chunk_chars: int = Field(default=24000, ge=500)
