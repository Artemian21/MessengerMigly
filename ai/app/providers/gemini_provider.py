from google import genai
from google.genai import types

from app.providers.base import LlmError


class GeminiProvider:
    name = "gemini"

    def __init__(self, api_key: str, model: str, client: genai.Client | None = None) -> None:
        self.model = model
        self._client = client or genai.Client(api_key=api_key)

    async def complete(self, *, system: str, user: str, max_tokens: int) -> str:
        try:
            response = await self._client.aio.models.generate_content(
                model=self.model,
                contents=user,
                config=types.GenerateContentConfig(system_instruction=system, max_output_tokens=max_tokens),
            )
        except Exception as exc:  # the SDK raises many error types; callers only need "it failed"
            raise LlmError(f"Gemini request failed ({type(exc).__name__})") from exc

        text = (response.text or "").strip()
        if not text:
            raise LlmError("Gemini returned an empty response")
        return text
