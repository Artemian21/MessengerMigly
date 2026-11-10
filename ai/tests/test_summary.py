import asyncio
from datetime import datetime, timezone

from app.schemas import ChatMessage
from app.summary import SummaryService, split_lines


class RecordingProvider:
    name = "recording"

    def __init__(self):
        self.calls = []

    async def complete(self, *, system, user, max_tokens):
        self.calls.append({"system": system, "user": user, "max_tokens": max_tokens})
        return f"- answer {len(self.calls)}"


def messages(count, body="x" * 20):
    return [
        ChatMessage(author="A", sentAt=datetime(2026, 11, 10, 17, i % 60, tzinfo=timezone.utc), body=f"{body}{i}")
        for i in range(count)
    ]


def test_split_lines_respects_budget_and_order():
    chunks = split_lines(["aaaa", "bbbb", "cccc", "dddd", "eeee"], budget=9)
    assert chunks == [["aaaa", "bbbb"], ["cccc", "dddd"], ["eeee"]]


def test_split_lines_gives_an_oversized_line_its_own_chunk():
    assert split_lines(["a", "b" * 50, "c"], budget=10) == [["a"], ["b" * 50], ["c"]]


def test_short_chat_needs_one_call():
    provider = RecordingProvider()
    summary = asyncio.run(SummaryService(provider, chunk_chars=10_000).summarize("uk", messages(5)))

    assert summary == "- answer 1" and len(provider.calls) == 1
    assert "Ukrainian" in provider.calls[0]["system"]
    assert provider.calls[0]["user"].count("[2026-11-10") == 5


def test_long_chat_is_summarized_in_parts_then_merged():
    provider = RecordingProvider()
    # every line is ~45 chars; a 120-char budget fits 2 lines per chunk -> 3 chunks for 6 messages
    summary = asyncio.run(SummaryService(provider, chunk_chars=120).summarize("en", messages(6)))

    assert len(provider.calls) == 4  # 3 parts + 1 merge
    assert summary == "- answer 4"
    assert "<partial_summaries>" in provider.calls[3]["user"]
    assert "- answer 1" in provider.calls[3]["user"] and "- answer 3" in provider.calls[3]["user"]
    # order is preserved across chunks
    first, second, third = (provider.calls[i]["user"] for i in range(3))
    assert "x0" in first and "x1" in first and "x2" not in first
    assert "x2" in second and "x3" in second and "x4" not in second
    assert "x4" in third and "x5" in third
