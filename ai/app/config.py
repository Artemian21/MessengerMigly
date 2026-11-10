from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

# The repository's root `.env` (git-ignored; the template is `.env.example`). Resolved from this file, not from the working
# directory, so the service finds it wherever it is started. The file also holds settings for the .NET API; those are ignored.
# In the container there is no such file: the settings come from real environment variables.
ENV_FILE = Path(__file__).resolve().parents[2] / ".env"


class Settings(BaseSettings):
    """Configuration from environment variables (or the root `.env`). Secrets never have defaults."""

    model_config = SettingsConfigDict(env_file=ENV_FILE, extra="ignore")

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
