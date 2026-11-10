from anthropic import AsyncAnthropic

from app.providers.base import LlmError


class AnthropicProvider:
    name = "anthropic"

    def __init__(self, api_key: str, model: str, client: AsyncAnthropic | None = None) -> None:
        self.model = model
        self._client = client or AsyncAnthropic(api_key=api_key)

    async def complete(self, *, system: str, user: str, max_tokens: int) -> str:
        try:
            response = await self._client.messages.create(
                model=self.model,
                max_tokens=max_tokens,
                system=system,
                messages=[{"role": "user", "content": user}],
            )
        except Exception as exc:  # the SDK raises many error types; callers only need "it failed"
            raise LlmError(f"Anthropic request failed ({type(exc).__name__})") from exc

        text = "".join(block.text for block in response.content if getattr(block, "type", None) == "text").strip()
        if not text:
            raise LlmError("Anthropic returned an empty response")
        return text
