FROM python:3.12-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSLo /app/rds-global-bundle.pem \
       https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem
COPY requirements.txt ./
RUN pip install -r requirements.txt

# ---- dev: source bind-mounted at runtime, autoreload on ----
FROM base AS dev
COPY requirements-dev.txt ./
RUN pip install -r requirements-dev.txt
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--reload"]

# ---- prod: code baked in, runs as a non-root user ----
FROM base AS prod
COPY . .
# Drop root: create an unprivileged user and hand it the app dir (incl. the RDS cert bundle).
RUN useradd -r -u 10001 appuser && chown -R appuser /app
USER appuser
EXPOSE 8000
# NOTE: the per-IP rate limiter (app/ratelimit.py), the token-replay guard (app/auth/replay.py),
# and the presence hub (app/routers/ws.py) are in-memory PER PROCESS. With >1 worker the limits
# loosen and the presence roster splits across workers — move that state to shared storage
# (e.g. Redis) before scaling workers beyond 1.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "2"]
