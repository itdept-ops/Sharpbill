# Build inputs are digest-pinned so a tag cannot silently change the toolchain used for a release.
FROM node:24-alpine@sha256:f70403e87646dc51b45295f4b8b70cdad0b63d2297c4c9899119b03f7af7a6b3 AS base
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
FROM nginx:1.31.3-alpine-slim@sha256:45b82ed5f285b90d63df07ba70430fdd8f25624b416617d9e6dc93412b2006dc AS prod
COPY --chown=101:101 nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build --chown=101:101 /app/dist /usr/share/nginx/html
RUN touch /var/run/nginx.pid \
    && chown -R 101:101 /var/cache/nginx /var/run/nginx.pid /etc/nginx/conf.d
USER 101:101
