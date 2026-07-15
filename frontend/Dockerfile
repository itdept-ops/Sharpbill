FROM node:24-alpine AS base
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

# ---- dev: Vite dev server, source bind-mounted at runtime ----
FROM base AS dev
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]

# ---- build: static assets; VITE_* baked in at BUILD time ----
FROM base AS build
COPY . .
ARG VITE_GOOGLE_CLIENT_ID
ARG VITE_AZURE_CLIENT_ID
ENV VITE_GOOGLE_CLIENT_ID=$VITE_GOOGLE_CLIENT_ID \
    VITE_AZURE_CLIENT_ID=$VITE_AZURE_CLIENT_ID
RUN npm run build

# ---- prod: nginx serves the SPA with a history fallback ----
# Pinned to a specific nginx version (was the floating `nginx:alpine`). The master binds :80 as
# root and drops workers to the unprivileged `nginx` user, per the official image.
FROM nginx:1.27-alpine AS prod
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
