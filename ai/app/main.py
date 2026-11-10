import asyncio
import hmac
import logging
import time

from fastapi import Depends, FastAPI, Header, HTTPException

from app.config import Settings
from app.providers.base import LlmError, LlmProvider
from app.providers.factory import create_provider
from app.schemas import SummarizeRequest, SummarizeResponse
from app.summary import SummaryService

log = logging.getLogger("migly.ai")


def create_app(settings: Settings | None = None, provider: LlmProvider | None = None) -> FastAPI:
    """Run with: uvicorn app.main:create_app --factory --host 127.0.0.1 --port 8000"""
    settings = settings or Settings()
    provider = provider or create_provider(settings)  # fails fast on bad configuration
    service = SummaryService(provider, chunk_chars=settings.ai_chunk_chars)
    secret = settings.ai_shared_secret.encode()

    app = FastAPI(title="Migly AI", docs_url=None, redoc_url=None, openapi_url=None)

    def require_secret(x_ai_secret: str = Header(default="")) -> None:
        if not hmac.compare_digest(x_ai_secret.encode(), secret):
            raise HTTPException(status_code=401, detail="unauthorized")

    @app.get("/health")
    async def health() -> dict[str, str]:
        return {"status": "ok"}

    @app.post("/v1/summarize", response_model=SummarizeResponse, dependencies=[Depends(require_secret)])
    async def summarize(request: SummarizeRequest) -> SummarizeResponse:
        count = len(request.messages)
        if count > settings.ai_max_messages:
            raise HTTPException(status_code=422, detail=f"at most {settings.ai_max_messages} messages")

        started = time.perf_counter()
        try:
            summary = await asyncio.wait_for(
                service.summarize(request.language, request.messages), timeout=settings.ai_timeout_seconds
            )
        except TimeoutError:
            log.warning("summarize timed out provider=%s messages=%d", provider.name, count)
            raise HTTPException(status_code=504, detail="provider_timeout") from None
        except LlmError as exc:
            log.warning("summarize failed provider=%s messages=%d error=%s", provider.name, count, exc)
            raise HTTPException(status_code=502, detail="provider_unavailable") from None

        log.info("summarized provider=%s messages=%d ms=%.0f", provider.name, count, (time.perf_counter() - started) * 1000)
        return SummarizeResponse(summary=summary, message_count=count)

    return app
