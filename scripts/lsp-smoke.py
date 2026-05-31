#!/usr/bin/env python3
"""Smoke-test the Y# language server: spawn it, send LSP initialize, expect a response.

Usage:
    python3 scripts/lsp-smoke.py <ysharp-lsp launcher> [args...]

Examples:
    python3 scripts/lsp-smoke.py dotnet YSharp.LanguageServer/bin/Release/net10.0/ysharp-lsp.dll
    python3 scripts/lsp-smoke.py ./ysharp-lsp
"""
import json
import subprocess
import sys
import threading
import time


def frame(payload: dict) -> bytes:
    body = json.dumps(payload).encode()
    return f"Content-Length: {len(body)}\r\n\r\n".encode() + body


def read_messages(stream, inbox):
    while True:
        headers = b""
        while not headers.endswith(b"\r\n\r\n"):
            ch = stream.read(1)
            if not ch:
                return
            headers += ch
        length = 0
        for line in headers.decode().split("\r\n"):
            if line.lower().startswith("content-length:"):
                length = int(line.split(":", 1)[1].strip())
        body = stream.read(length)
        try:
            inbox.append(json.loads(body))
        except Exception:
            pass


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2

    proc = subprocess.Popen(
        sys.argv[1:],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    inbox: list[dict] = []
    threading.Thread(target=read_messages, args=(proc.stdout, inbox), daemon=True).start()

    def send(payload):
        proc.stdin.write(frame(payload))
        proc.stdin.flush()

    send({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"processId": None, "rootUri": "file:///tmp", "capabilities": {}}
    })
    send({"jsonrpc": "2.0", "method": "initialized", "params": {}})

    # Wait up to 5s for an initialize response.
    deadline = time.time() + 5
    while time.time() < deadline and not any(m.get("id") == 1 for m in inbox):
        time.sleep(0.1)

    proc.terminate()
    try:
        proc.wait(timeout=2)
    except subprocess.TimeoutExpired:
        proc.kill()

    init_response = next((m for m in inbox if m.get("id") == 1), None)
    if init_response is None:
        print("LSP smoke FAIL: no initialize response")
        return 1

    caps = init_response.get("result", {}).get("capabilities", {})
    if "textDocumentSync" not in caps:
        print(f"LSP smoke FAIL: unexpected capabilities {caps}")
        return 1

    print(f"LSP smoke OK: {init_response['result']['serverInfo']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
