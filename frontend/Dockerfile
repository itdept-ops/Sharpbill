# Build inputs are digest-pinned so a tag cannot silently change the toolchain used for a release.
FROM node:24-alpine@sha256:a0b9bf06e4e6193cf7a0f58816cc935ff8c2a908f81e6f1a95432d679c54fbfd AS base
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

# ---- dev: Vite dev server, source bind-mounted at runtime ----
FROM base AS dev
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]

# ---- build: static assets; public OAuth IDs are loaded from the API at runtime ----
FROM base AS build
COPY . .
RUN npm run build

# ---- prod: nginx serves the SPA with a history fallback ----
# Digest-pinned Alpine 3.24 image verified with Docker Scout on 2026-07-20.
FROM nginx:1.31.4-alpine-slim@sha256:1870de6d59aafee152589b64404556d2535922cdd998e6dac1c4888c938ed8f9 AS prod
COPY --chown=101:101 nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build --chown=101:101 /app/dist /usr/share/nginx/html
RUN touch /var/run/nginx.pid \
    && chown -R 101:101 /var/cache/nginx /var/run/nginx.pid /etc/nginx/conf.d
USER 101:101
