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
# The master binds :80 as root and drops workers to the unprivileged `nginx` user.
FROM nginx:1.31.3-alpine-slim@sha256:45b82ed5f285b90d63df07ba70430fdd8f25624b416617d9e6dc93412b2006dc AS prod
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
