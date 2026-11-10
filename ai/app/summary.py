from app.prompts import build_merge_prompt, build_system_prompt, build_user_prompt, format_line
from app.providers.base import LlmProvider
from app.schemas import ChatMessage


def split_lines(lines: list[str], budget: int) -> list[list[str]]:
    """Greedy split into consecutive chunks of at most `budget` characters. An oversized line gets its own chunk."""
    chunks: list[list[str]] = []
    current: list[str] = []
    size = 0
    for line in lines:
        if current and size + len(line) > budget:
            chunks.append(current)
            current, size = [], 0
        current.append(line)
        size += len(line)
    if current:
        chunks.append(current)
    return chunks


class SummaryService:
    """Prompts + chunking. Knows nothing about which provider is behind `LlmProvider`."""

    def __init__(self, provider: LlmProvider, *, chunk_chars: int, max_tokens: int = 700) -> None:
        self._provider = provider
        self._chunk_chars = chunk_chars
        self._max_tokens = max_tokens

    async def summarize(self, language: str, messages: list[ChatMessage]) -> str:
        system = build_system_prompt(language)
        chunks = split_lines([format_line(m) for m in messages], self._chunk_chars)

        if len(chunks) == 1:
            return await self._ask(system, build_user_prompt(chunks[0]))

        # map: one summary per chunk (sequential: keeps provider rate limits and ordering simple)
        partials = [await self._ask(system, build_user_prompt(chunk)) for chunk in chunks]
        # reduce: merge the partial summaries
        return await self._ask(system, build_merge_prompt(partials))

    async def _ask(self, system: str, user: str) -> str:
        return await self._provider.complete(system=system, user=user, max_tokens=self._max_tokens)
