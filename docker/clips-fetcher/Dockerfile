# clips-fetcher — browser-TLS fetch proxy used to pull Kick clip listings past
# Cloudflare. Build context = this directory (docker/clips-fetcher).
FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py .

ENV PORT=8080
EXPOSE 8080

# /healthz needs no secret, so it's safe for container healthchecks.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD python -c "import os,urllib.request; urllib.request.urlopen('http://127.0.0.1:%s/healthz' % os.environ.get('PORT','8080'))"

CMD ["python", "app.py"]
