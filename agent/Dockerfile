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

# Node 22 + Claude CLI — required by claude-agent-sdk (Python), which spawns
# the `claude` CLI as a subprocess. Without this, ClaudeSDKClient throws
# "claude executable not found" at startup. ~150 MB image growth.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g @anthropic-ai/claude-code \
 && apt-get purge -y --auto-remove curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=builder /wheels /wheels
RUN pip install --no-index --find-links /wheels aiq-agent && rm -rf /wheels

# SKILL.md packages — loaded at app startup, see aiq_agent/main.py.
# Shipped outside the wheel so a skill change ships with a runtime
# rebuild only (no need to re-wheel the package).
COPY skills /app/skills

# Make the same skills directory discoverable by claude-agent-sdk via its
# project-source path (<cwd>/.claude/skills) so ClaudeAgentOptions(skills=[…])
# matches our SKILL.md files without copying.
RUN mkdir -p /app/.claude && ln -s /app/skills /app/.claude/skills \
 && mkdir -p /home/app/.claude && ln -s /app/skills /home/app/.claude/skills \
 && chown -R app:app /home/app/.claude

USER app
EXPOSE 8000

ENTRYPOINT ["uvicorn", "aiq_agent.main:app", "--host", "0.0.0.0", "--port", "8000"]
