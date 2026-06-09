"""OpenTelemetry tracing setup."""

from aiq_agent.observability.tracing import configure_tracing, instrument_fastapi

__all__ = ["configure_tracing", "instrument_fastapi"]
