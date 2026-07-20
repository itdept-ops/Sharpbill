# Wolfi supplies a minimal, continuously rebuilt runtime. Both the image manifest and
# installed language packages are pinned so a rebuild cannot silently change inputs.
FROM cgr.dev/chainguard/wolfi-base@sha256:02dab76bd852a70556b5b2002195c8a5fdab77d323c433bf6642aab080489795 AS base

USER 0
RUN apk add --no-cache \
      ca-certificates=20260413-r0 \
      python-3.13=3.13.14-r2 \
      py3.13-pip=26.1.2-r1 \
    && python3.13 -m venv /opt/venv

ENV HOME=/tmp \
    PATH=/opt/venv/bin:/usr/bin:/bin \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1
WORKDIR /app

ARG RDS_GLOBAL_BUNDLE_SHA256=e5bb2084ccf45087bda1c9bffdea0eb15ee67f0b91646106e466714f9de3c7e3
RUN python3.13 -c "import hashlib, pathlib, urllib.request; data=urllib.request.urlopen('https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem', timeout=30).read(); actual=hashlib.sha256(data).hexdigest(); expected='${RDS_GLOBAL_BUNDLE_SHA256}'; assert actual == expected, f'RDS CA checksum mismatch: {actual}'; target=pathlib.Path('/opt/kingfisher/certs/rds-global-bundle.pem'); target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)"

COPY requirements.lock ./
RUN pip install --require-hashes -r requirements.lock

# ---- dev: source bind-mounted at runtime, autoreload on ----
FROM base AS dev
COPY requirements-dev.lock ./
RUN pip install --require-hashes -r requirements-dev.lock
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--reload"]

# ---- prod runtime: interpreter only (no pip/setuptools), then application artifacts ----
FROM cgr.dev/chainguard/wolfi-base@sha256:02dab76bd852a70556b5b2002195c8a5fdab77d323c433bf6642aab080489795 AS runtime
USER 0
RUN apk add --no-cache \
      ca-certificates=20260413-r0 \
      python-3.13-base=3.13.14-r2
ENV HOME=/tmp \
    PATH=/opt/venv/bin:/usr/bin:/bin \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
WORKDIR /app
COPY --from=base /opt/venv /opt/venv
COPY --from=base /opt/kingfisher/certs/rds-global-bundle.pem /opt/kingfisher/certs/rds-global-bundle.pem
# The runtime needs the resolved packages, not an installer capable of mutating them.
RUN rm -rf \
      /opt/venv/bin/pip \
      /opt/venv/bin/pip3 \
      /opt/venv/bin/pip3.13 \
      /opt/venv/lib/python3.13/site-packages/pip \
      /opt/venv/lib/python3.13/site-packages/pip-*.dist-info

# ---- prod: only runtime source and migration assets, non-root, one process ----
FROM runtime AS prod
COPY --chown=65532:65532 app ./app
COPY --chown=65532:65532 alembic ./alembic
COPY --chown=65532:65532 alembic.ini ./alembic.ini
USER 65532:65532
EXPOSE 8000
# Rate limiting, replay state, and presence are process-local. Keep one worker until
# those controls are moved to shared storage; scale with multiple one-worker replicas.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
