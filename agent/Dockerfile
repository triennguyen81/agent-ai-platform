# syntax=docker/dockerfile:1.7
FROM python:3.12-slim AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

FROM base AS builder
RUN pip install --upgrade pip build
COPY pyproject.toml README.md ./
COPY src ./src
RUN pip wheel . --wheel-dir /wheels

FROM base AS runtime
RUN groupadd --system app && useradd --system --gid app --home /home/app --create-home app
COPY --from=builder /wheels /wheels
RUN pip install --no-index --find-links /wheels aiq-agent && rm -rf /wheels

# SKILL.md packages — loaded at app startup, see aiq_agent/main.py.
# Shipped outside the wheel so a skill change ships with a runtime
# rebuild only (no need to re-wheel the package).
COPY skills /app/skills

USER app
EXPOSE 8000

ENTRYPOINT ["uvicorn", "aiq_agent.main:app", "--host", "0.0.0.0", "--port", "8000"]
