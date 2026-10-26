"""Process-wide admission control for native OCR work (AG-02).

Every native OCR step — PDFium rendering / text-layer reads, image decoding,
RapidOCR/PaddleOCR inference — used to run on the shared asyncio default
thread pool via ``asyncio.to_thread``. Concurrent requests therefore drove
PDFium from several threads at once. PDFium is documented as "inherently not
thread-safe" (pypdfium2 README, *Known limitations*); under load this showed
up as spurious ``PdfiumError: Data format error`` on valid PDFs and, in
production, as a glibc heap abort (``realloc(): invalid old size``) that
killed the whole Agent process.

Two guarantees, both process-wide (they hold for every route — /ocr/*,
/documents/extract, /knowledge/extract — not just the Gateway-serialised one):

* ``PDFIUM_LOCK``: at most one thread inside PDFium, regardless of the
  configured OCR concurrency.
* ``OcrWorkGate``: native OCR work runs on a dedicated, bounded thread pool
  (``ocr_max_concurrent_jobs`` workers) with a bounded backlog
  (``ocr_queue_capacity``). A full backlog is refused immediately with
  ``OcrBusyError`` (HTTP 429) instead of piling up memory, and a caller that
  waited longer than ``ocr_job_timeout_seconds`` gets ``OcrTimeoutError``
  (HTTP 504).

Throughput limit: native OCR throughput is at most ``ocr_max_concurrent_jobs``
tasks at a time (default 1), and PDFium work is always one at a time. ONNX
Runtime already parallelises a single inference across cores, so a second
concurrent inference mostly competes for the same cores (see the AG-02 report
for the measurements). The LLM correction stage is NOT inside the gate, so a
slow LLM does not hold an OCR slot.

A running native call cannot be interrupted from Python. On timeout the
caller stops waiting, but the task keeps its slot (and counts against the
backlog) until the native call actually returns, so the concurrency bound
stays true. A task that times out while still queued is skipped and never
starts.
"""

from __future__ import annotations

import asyncio
import functools
import threading
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from typing import TypeVar

T = TypeVar("T")

PDFIUM_LOCK = threading.Lock()


class OcrBusyError(Exception):
    """The OCR backlog is full — retry later (HTTP 429)."""

    status_code = 429


class OcrTimeoutError(Exception):
    """The OCR task did not finish within the configured timeout (HTTP 504)."""

    status_code = 504


class _Abandoned(Exception):
    pass


class OcrWorkGate:
    def __init__(self, *, max_concurrent: int, queue_capacity: int, timeout_seconds: float) -> None:
        if max_concurrent < 1:
            raise ValueError("max_concurrent must be >= 1")
        if queue_capacity < 0:
            raise ValueError("queue_capacity must be >= 0")
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be > 0")
        self.max_concurrent = max_concurrent
        self.queue_capacity = queue_capacity
        self.timeout_seconds = timeout_seconds
        self._executor = ThreadPoolExecutor(
            max_workers=max_concurrent, thread_name_prefix="ocr-native"
        )
        self._lock = threading.Lock()
        self._pending = 0  # submitted and not yet finished (running + queued)

    @property
    def pending(self) -> int:
        return self._pending

    async def run(self, fn: Callable[..., T], /, *args: object, **kwargs: object) -> T:
        with self._lock:
            if self._pending >= self.max_concurrent + self.queue_capacity:
                raise OcrBusyError("OCR is at capacity — retry later.")
            self._pending += 1

        abandoned = threading.Event()

        def task() -> T:
            try:
                if abandoned.is_set():
                    raise _Abandoned()
                return fn(*args, **kwargs)
            finally:
                with self._lock:
                    self._pending -= 1

        try:
            future = self._executor.submit(task)
        except BaseException:
            with self._lock:
                self._pending -= 1
            raise
        result = asyncio.wrap_future(future)
        # An abandoned task's outcome is never awaited; mark it retrieved so
        # asyncio does not log "exception was never retrieved".
        result.add_done_callback(lambda f: f.cancelled() or f.exception())
        try:
            return await asyncio.wait_for(asyncio.shield(result), self.timeout_seconds)
        except TimeoutError as exc:
            abandoned.set()
            raise OcrTimeoutError(f"OCR did not finish within {self.timeout_seconds:g}s.") from exc
        except asyncio.CancelledError:
            abandoned.set()
            raise

    def shutdown(self) -> None:
        """Stop accepting work; queued tasks are dropped, running ones finish."""
        self._executor.shutdown(wait=False, cancel_futures=True)


@functools.lru_cache(maxsize=1)
def get_ocr_gate() -> OcrWorkGate:
    from aiq_agent.config import get_settings

    settings = get_settings()
    return OcrWorkGate(
        max_concurrent=settings.ocr_max_concurrent_jobs,
        queue_capacity=settings.ocr_queue_capacity,
        timeout_seconds=settings.ocr_job_timeout_seconds,
    )


async def run_ocr_native[T](fn: Callable[..., T], /, *args: object, **kwargs: object) -> T:
    return await get_ocr_gate().run(fn, *args, **kwargs)


__all__ = [
    "PDFIUM_LOCK",
    "OcrBusyError",
    "OcrTimeoutError",
    "OcrWorkGate",
    "get_ocr_gate",
    "run_ocr_native",
]
