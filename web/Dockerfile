# syntax=docker/dockerfile:1.7
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json ./
# --legacy-peer-deps because parts of the React 18 ecosystem still pin
# React 18 in peerDependencies even when they support React 19.
RUN npm install --no-audit --no-fund --legacy-peer-deps

FROM node:22-alpine AS build
WORKDIR /app

# NEXT_PUBLIC_* env vars must be set at BUILD time — Next.js inlines them
# into the client bundle. Runtime-only secrets (CLERK_SECRET_KEY,
# GATEWAY_SHARED_SECRET) stay out of the build and arrive via env on the
# runtime container.
ARG NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY=""
ENV NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY=${NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY}

COPY --from=deps /app/node_modules ./node_modules
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0
RUN addgroup --system --gid 1001 app \
 && adduser  --system --uid 1001 app
COPY --from=build --chown=app:app /app/.next/standalone ./
COPY --from=build --chown=app:app /app/.next/static ./.next/static
COPY --from=build --chown=app:app /app/public ./public
USER app
EXPOSE 3000
CMD ["node", "server.js"]
