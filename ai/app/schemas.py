from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class ChatMessage(BaseModel):
    """Limits mirror the server's validation (display name ≤ 64, message ≤ 4000 characters)."""

    model_config = ConfigDict(populate_by_name=True)

    author: str = Field(min_length=1, max_length=64)
    sent_at: datetime = Field(alias="sentAt")
    body: str = Field(min_length=1, max_length=4000)


class SummarizeRequest(BaseModel):
    language: Literal["uk", "en"]
    messages: list[ChatMessage] = Field(min_length=1)


class SummarizeResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    summary: str
    message_count: int = Field(alias="messageCount")
