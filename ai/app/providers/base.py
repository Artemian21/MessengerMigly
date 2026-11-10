from typing import Protocol


class LlmError(Exception):
    """A provider call failed (network, quota, bad or empty response). The message never contains chat text."""


class LlmProvider(Protocol):
    """The only thing the rest of the service knows about a language model."""

    name: str

    async def complete(self, *, system: str, user: str, max_tokens: int) -> str:
        """Send one system + user prompt, return the model's plain-text answer."""
        ...
