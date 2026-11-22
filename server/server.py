#!/usr/bin/env python3
"""Couch Party sync server.

A tiny storage server for the Couch Party Zeepkist mod. It keeps one JSON document (profiles,
stats, leaderboards) and lets PCs with a valid key download and upload it. All merging happens
in the mod; the server only stores the newest version and refuses uploads based on an old one
(optimistic locking with ETag / If-Match), so two PCs syncing at once can't overwrite each other.

Standard library only. Commands:
    python server.py serve                 run the server
    python server.py addkey <name>         create a key for a PC (prints it once)
    python server.py removekey <name>      revoke a PC's key
    python server.py listkeys              list key names

Settings (environment variables):
    DATA_DIR   where data, keys and history are kept   (default: ./data)
    PORT       port to listen on                         (default: 8787)
    HOST       address to listen on                      (default: 0.0.0.0)
    HISTORY    how many old versions to keep             (default: 100)
    MAX_BODY   biggest accepted upload in bytes          (default: 20 MB)
"""

import hmac
import json
import os
import secrets
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DATA_DIR = os.environ.get("DATA_DIR", os.path.join(os.path.dirname(os.path.abspath(__file__)), "data"))
PORT = int(os.environ.get("PORT", "8787"))
HOST = os.environ.get("HOST", "0.0.0.0")
HISTORY = int(os.environ.get("HISTORY", "100"))
MAX_BODY = int(os.environ.get("MAX_BODY", str(20 * 1024 * 1024)))

DATA_FILE = os.path.join(DATA_DIR, "couchparty.json")
REV_FILE = os.path.join(DATA_DIR, "revision.txt")
KEYS_FILE = os.path.join(DATA_DIR, "keys.json")
HISTORY_DIR = os.path.join(DATA_DIR, "history")

write_lock = threading.Lock()


# ---------------------------------------------------------------- storage

def read_revision():
    try:
        with open(REV_FILE, encoding="utf-8") as f:
            return int(f.read().strip() or "0")
    except FileNotFoundError:
        return 0


def atomic_write(path, data: bytes):
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def save_version(body: bytes, revision: int):
    os.makedirs(HISTORY_DIR, exist_ok=True)
    atomic_write(DATA_FILE, body)
    atomic_write(REV_FILE, str(revision).encode())
    atomic_write(os.path.join(HISTORY_DIR, f"rev-{revision:06d}.json"), body)
    old = sorted(n for n in os.listdir(HISTORY_DIR) if n.startswith("rev-") and n.endswith(".json"))
    for name in old[:-HISTORY] if HISTORY > 0 else []:
        try:
            os.remove(os.path.join(HISTORY_DIR, name))
        except OSError:
            pass


# ---------------------------------------------------------------- keys

def load_keys():
    try:
        with open(KEYS_FILE, encoding="utf-8") as f:
            keys = json.load(f)
        return {str(k): str(v) for k, v in keys.items()} if isinstance(keys, dict) else {}
    except FileNotFoundError:
        return {}


def save_keys(keys):
    os.makedirs(DATA_DIR, exist_ok=True)
    atomic_write(KEYS_FILE, json.dumps(keys, indent=2).encode())
    try:
        os.chmod(KEYS_FILE, 0o600)
    except OSError:
        pass


def key_name(token: str):
    """Name of the PC the key belongs to, or None. Compares in constant time."""
    found = None
    for name, key in load_keys().items():
        if hmac.compare_digest(token.encode(), key.encode()):
            found = name
    return found


# ---------------------------------------------------------------- HTTP

class Handler(BaseHTTPRequestHandler):
    server_version = "CouchPartySync/1"
    # HTTP/1.1 so "Expect: 100-continue" uploads get their go-ahead (Unity and Apache use it);
    # with HTTP/1.0 the client waits for the go-ahead while the server waits for the data.
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stdout.write("%s %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), fmt % args))
        sys.stdout.flush()

    def send(self, code, body=b"", content_type="application/json", headers=None):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        if body and self.command != "HEAD":
            self.wfile.write(body)

    def error(self, code, message):
        self.send(code, json.dumps({"error": message}).encode())

    def route(self):
        # Works with or without a reverse-proxy prefix (e.g. /couchparty/api/data).
        path = self.path.split("?", 1)[0].rstrip("/")
        for name in ("/api/data", "/api/health"):
            if path.endswith(name):
                return name
        return None

    def authorize(self):
        header = self.headers.get("Authorization", "")
        token = header[7:].strip() if header.lower().startswith("bearer ") else ""
        name = key_name(token) if token else None
        if name is None:
            time.sleep(1)  # slow down key guessing
            self.error(401, "unknown key")
        return name

    def read_body(self):
        """Request body, sent with Content-Length or in chunks. None if missing or too large."""
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            data = bytearray()
            while True:
                size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline()  # blank line after the last chunk
                    break
                data += self.rfile.read(size)
                self.rfile.readline()  # CRLF after each chunk
                if len(data) > MAX_BODY:
                    return None
            return bytes(data) or None
        length = int(self.headers.get("Content-Length", "0") or "0")
        if length <= 0 or length > MAX_BODY:
            return None
        return self.rfile.read(length)

    def do_GET(self):
        route = self.route()
        if route == "/api/health":
            return self.send(200, b'{"ok":true}')
        if route != "/api/data":
            return self.error(404, "not found")
        if self.authorize() is None:
            return
        revision = read_revision()
        if revision == 0 or not os.path.exists(DATA_FILE):
            return self.send(404, b'{"error":"no data yet"}', headers={"ETag": '"0"'})
        with open(DATA_FILE, "rb") as f:
            body = f.read()
        self.send(200, body, headers={"ETag": f'"{revision}"'})

    def do_PUT(self):
        if self.route() != "/api/data":
            return self.error(404, "not found")
        who = self.authorize()
        if who is None:
            return
        body = self.read_body()
        if body is None:
            return self.error(413, "body missing or too large")
        length = len(body)
        try:
            doc = json.loads(body)
            if not isinstance(doc, dict) or "Profiles" not in doc or "Leaderboard" not in doc:
                raise ValueError("not a Couch Party document")
        except ValueError as e:
            return self.error(400, f"invalid data: {e}")

        if_match = (self.headers.get("If-Match") or "").strip().strip('"')
        if not if_match:
            return self.error(428, "If-Match header required")

        with write_lock:
            current = read_revision()
            if if_match != str(current):
                # Someone uploaded since this PC downloaded: it has to merge again.
                return self.send(412, b'{"error":"data changed, sync again"}', headers={"ETag": f'"{current}"'})
            revision = current + 1
            save_version(body, revision)
        self.log_message("PUT by %s: revision %d (%d bytes)", who, revision, length)
        self.send(200, json.dumps({"revision": revision}).encode(), headers={"ETag": f'"{revision}"'})


# ---------------------------------------------------------------- commands

def main(argv):
    command = argv[1] if len(argv) > 1 else "serve"
    if command == "serve":
        os.makedirs(DATA_DIR, exist_ok=True)
        if not load_keys():
            print("No keys yet. Create one with: python server.py addkey <pc-name>")
        httpd = ThreadingHTTPServer((HOST, PORT), Handler)
        print(f"Couch Party sync server on {HOST}:{PORT}, data in {DATA_DIR}")
        httpd.serve_forever()
    elif command == "addkey" and len(argv) == 3:
        keys = load_keys()
        if argv[2] in keys:
            sys.exit(f"A key named '{argv[2]}' already exists (remove it first).")
        keys[argv[2]] = secrets.token_urlsafe(32)
        save_keys(keys)
        print(f"Key for '{argv[2]}' (enter it in the game: F8 > Sync):\n{keys[argv[2]]}")
    elif command == "removekey" and len(argv) == 3:
        keys = load_keys()
        if keys.pop(argv[2], None) is None:
            sys.exit(f"No key named '{argv[2]}'.")
        save_keys(keys)
        print(f"Removed key '{argv[2]}'.")
    elif command == "listkeys":
        for name in load_keys():
            print(name)
    else:
        print(__doc__)
        sys.exit(1)


if __name__ == "__main__":
    main(sys.argv)
