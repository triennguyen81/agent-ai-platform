# syntax=docker/dockerfile:1.7
FROM python:3.12-slim AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

FROM node:22-slim AS claude-cli
RUN npm install -g @anthropic-ai/claude-code@2.1.246

FROM base AS builder
ARG APP_PYPI_INDEX_URL=https://mirrors.aliyun.com/pypi/simple
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    env -u PIP_NO_CACHE_DIR PIP_INDEX_URL=${APP_PYPI_INDEX_URL} \
    pip install pip==26.2.1 build==1.5.0
COPY pyproject.toml README.md ./
COPY src ./src
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    env -u PIP_NO_CACHE_DIR PIP_INDEX_URL=${APP_PYPI_INDEX_URL} \
    pip wheel . --wheel-dir /wheels

FROM base AS runtime-dependencies
RUN groupadd --system app && useradd --system --gid app --home /home/app --create-home app

# Node 22 + pinned Claude CLI — required by claude-agent-sdk. Copying from
# the official Node image avoids a second apt repository/index during builds.
COPY --from=claude-cli /usr/local/bin/node /usr/local/bin/node
COPY --from=claude-cli /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && ln -s /usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe /usr/local/bin/claude

# Keep the slow, stable OCR graph independent from application source. Exact
# top-level versions plus binary-only installs prevent resolver/source-build
# surprises; BuildKit retains downloaded wheels between clean image builds.
ARG OCR_PYPI_INDEX_URL=https://mirrors.aliyun.com/pypi/simple
COPY requirements-ocr.txt /tmp/requirements-ocr.txt
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    python -c "import urllib.request; urllib.request.urlretrieve('http://deb.debian.org/debian/pool/main/g/gcc-14/libgomp1_14.2.0-19_amd64.deb', '/tmp/libgomp1.deb')" \
 && dpkg -i /tmp/libgomp1.deb \
 && rm /tmp/libgomp1.deb \
 && env -u PIP_NO_CACHE_DIR PIP_INDEX_URL=${OCR_PYPI_INDEX_URL} python -m pip install --only-binary=:all: -r /tmp/requirements-ocr.txt \
 && python -m pip uninstall -y opencv-python opencv-contrib-python \
 && env -u PIP_NO_CACHE_DIR PIP_INDEX_URL=${OCR_PYPI_INDEX_URL} python -m pip install --only-binary=:all: opencv-contrib-python-headless==4.11.0.86

FROM runtime-dependencies AS runtime

COPY --from=builder /wheels /wheels
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    pip install --no-index --find-links /wheels aiq-agent && rm -rf /wheels

# OCR extras — enables /ocr/pdf + /ocr/image endpoints.
#
# Pinned to PaddleOCR 2.9.x / PaddlePaddle 2.6.x on purpose:
#   - PaddleOCR 3.0 rewrote the API (dropped use_angle_cls, use_gpu, cls=True)
#     and ships a new paddle static runner that throws
#     `NotImplementedError: ConvertPirAttribute2RuntimeAttribute` under
#     oneDNN on CPU (verified in agent logs 2026-08).
#   - 2.9.1 + paddlepaddle 2.6.2 = last combo with stable, working CPU OCR.
#   - When paddleocr 3.x + paddlepaddle 3.x ship a fixed CPU path,
#     unpin here and update agent/src/aiq_agent/ocr/extractor.py.
# We also ship rapidocr-onnxruntime as fallback (in requirements-ocr.txt) —
# PaddleOCR SIGSEGVs randomly on some VN invoice images; RapidOCR uses same
# PP-OCRv4 models but on ONNX runtime, no PaddlePaddle framework, no segfault.
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

FROM runtime AS test
ARG TEST_PYPI_INDEX_URL=https://mirrors.aliyun.com/pypi/simple
USER root
COPY requirements-test.txt /tmp/requirements-test.txt
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    env -u PIP_NO_CACHE_DIR PIP_INDEX_URL=${TEST_PYPI_INDEX_URL} python -m pip install -r /tmp/requirements-test.txt
COPY tests /app/tests
ENTRYPOINT ["python", "-m", "pytest"]
CMD ["-q", "/app/tests"]
