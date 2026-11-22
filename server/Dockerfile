FROM python:3.12-alpine
RUN adduser -D -u 1000 couchparty
WORKDIR /app
COPY server.py /app/server.py
ENV DATA_DIR=/data PORT=8787 HOST=0.0.0.0 PYTHONUNBUFFERED=1
VOLUME /data
EXPOSE 8787
USER couchparty
CMD ["python", "/app/server.py", "serve"]
