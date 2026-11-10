class FakeProvider:
    """Deterministic provider for tests and local development without an API key."""

    name = "fake"

    async def complete(self, *, system: str, user: str, max_tokens: int) -> str:
        # Transcript lines start with "[<timestamp>]" (see prompts.format_line).
        count = sum(1 for line in user.splitlines() if line.startswith("["))
        return f"- Fake summary of {count} message(s)."
