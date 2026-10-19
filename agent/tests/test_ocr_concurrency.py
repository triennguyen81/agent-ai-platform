"""AG-02: native OCR admission control and PDFium serialisation."""

from __future__ import annotations

import asyncio
import io
import threading
import time

import pytest

from aiq_agent.ocr.concurrency import PDFIUM_LOCK, OcrBusyError, OcrTimeoutError, OcrWorkGate


class _Probe:
    """Records the peak number of callers inside a native section."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.active = 0
        self.peak = 0
        self.calls = 0

    def work(self, seconds: float) -> str:
        with self._lock:
            self.active += 1
            self.calls += 1
            self.peak = max(self.peak, self.active)
        try:
            time.sleep(seconds)
            return threading.current_thread().name
        finally:
            with self._lock:
                self.active -= 1


@pytest.mark.asyncio
async def test_gate_never_exceeds_max_concurrent() -> None:
    gate = OcrWorkGate(max_concurrent=2, queue_capacity=20, timeout_seconds=30)
    probe = _Probe()
    try:
        names = await asyncio.gather(*(gate.run(probe.work, 0.05) for _ in range(10)))
    finally:
        gate.shutdown()
    assert probe.calls == 10
    assert probe.peak == 2
    assert all(name.startswith("ocr-native") for name in names)
    assert gate.pending == 0


@pytest.mark.asyncio
async def test_full_backlog_is_refused_without_running() -> None:
    gate = OcrWorkGate(max_concurrent=1, queue_capacity=1, timeout_seconds=30)
    probe = _Probe()
    try:
        running = asyncio.ensure_future(gate.run(probe.work, 0.3))
        queued = asyncio.ensure_future(gate.run(probe.work, 0.0))
        await asyncio.sleep(0.05)
        with pytest.raises(OcrBusyError) as busy:
            await gate.run(probe.work, 0.0)
        assert busy.value.status_code == 429
        await asyncio.gather(running, queued)
    finally:
        gate.shutdown()
    assert probe.calls == 2
    assert gate.pending == 0


@pytest.mark.asyncio
async def test_timeout_keeps_slot_until_native_call_returns() -> None:
    gate = OcrWorkGate(max_concurrent=1, queue_capacity=0, timeout_seconds=0.1)
    probe = _Probe()
    try:
        with pytest.raises(OcrTimeoutError) as timeout:
            await gate.run(probe.work, 0.4)
        assert timeout.value.status_code == 504
        # The native call is still running: its slot is not handed out.
        assert gate.pending == 1
        with pytest.raises(OcrBusyError):
            await gate.run(probe.work, 0.0)
        await asyncio.sleep(0.5)
        assert gate.pending == 0
        assert await gate.run(lambda: "ok") == "ok"
    finally:
        gate.shutdown()
    assert probe.peak == 1


@pytest.mark.asyncio
async def test_task_abandoned_while_queued_never_starts() -> None:
    gate = OcrWorkGate(max_concurrent=1, queue_capacity=5, timeout_seconds=0.2)
    probe = _Probe()
    started: list[str] = []
    try:
        blocker = asyncio.ensure_future(gate.run(probe.work, 0.5))
        await asyncio.sleep(0.02)
        with pytest.raises(OcrTimeoutError):
            await gate.run(started.append, "queued")
        await asyncio.gather(blocker, return_exceptions=True)
        # The blocker timed out too; wait for its native call to return.
        await asyncio.sleep(0.5)
    finally:
        gate.shutdown()
    assert started == []
    assert gate.pending == 0


@pytest.mark.asyncio
async def test_native_exception_propagates_and_frees_slot() -> None:
    gate = OcrWorkGate(max_concurrent=1, queue_capacity=0, timeout_seconds=5)

    def boom() -> None:
        raise ValueError("bad file")

    try:
        with pytest.raises(ValueError, match="bad file"):
            await gate.run(boom)
        assert gate.pending == 0
        assert await gate.run(lambda: 42) == 42
    finally:
        gate.shutdown()


def test_invalid_configuration_is_rejected() -> None:
    with pytest.raises(ValueError):
        OcrWorkGate(max_concurrent=0, queue_capacity=1, timeout_seconds=1)
    with pytest.raises(ValueError):
        OcrWorkGate(max_concurrent=1, queue_capacity=-1, timeout_seconds=1)
    with pytest.raises(ValueError):
        OcrWorkGate(max_concurrent=1, queue_capacity=1, timeout_seconds=0)


def test_pdfium_calls_hold_the_process_wide_lock(monkeypatch: pytest.MonkeyPatch) -> None:
    pytest.importorskip("pypdfium2")
    import pypdfium2 as pdfium

    from aiq_agent.ocr import pdf as pdf_module

    seen: list[bool] = []
    real = pdfium.PdfDocument

    def spy(*args: object, **kwargs: object) -> object:
        seen.append(PDFIUM_LOCK.locked())
        return real(*args, **kwargs)

    monkeypatch.setattr(pdfium, "PdfDocument", spy)
    data = _synthetic_pdf(pages=1)
    pdf_module.render_pdf_pages(data, dpi=50)
    pdf_module.extract_pdf_text_layer(data)
    assert seen == [True, True]


def test_concurrent_pdfium_use_from_threads_is_correct() -> None:
    """Real PDFium, real threads. Before AG-02 this produced spurious
    'PdfiumError: Failed to load document (Data format error)' on valid PDFs
    (15 of 320 reads in the isolated reproduction)."""
    pytest.importorskip("pypdfium2")
    pytest.importorskip("PIL")
    from aiq_agent.ocr.pdf import extract_pdf_text_layer, render_pdf_pages

    documents = [_synthetic_pdf(pages=2, seed=i) for i in range(4)]
    errors: list[BaseException] = []

    def worker(index: int) -> None:
        for round_ in range(25):
            data = documents[(index + round_) % len(documents)]
            try:
                assert len(extract_pdf_text_layer(data)) == 2
                if round_ % 10 == 0:
                    assert len(render_pdf_pages(data, dpi=40)) == 2
            except BaseException as exc:  # noqa: BLE001 — collected and asserted below
                errors.append(exc)

    threads = [threading.Thread(target=worker, args=(i,)) for i in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert errors == []


def _synthetic_pdf(*, pages: int, seed: int = 0) -> bytes:
    from PIL import Image, ImageDraw

    images = []
    for page in range(pages):
        image = Image.new("RGB", (600, 800), "white")
        ImageDraw.Draw(image).text((40, 40), f"AG02 test {seed}-{page}", fill="black")
        images.append(image)
    buffer = io.BytesIO()
    images[0].save(buffer, format="PDF", save_all=True, append_images=images[1:])
    return buffer.getvalue()
