#!/usr/bin/env python3
"""
极小的 OpenAI 兼容 mock 服务，用于验证 AyuTranslate 的翻译链路。

    python tools/mock_openai.py 8765

它实现 /v1/chat/completions，并按 AyuTranslate 的批量协议
（###<n>###<译文>）返回结果。
"""
import json
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# 一个够用的假词典：把常见词换成中文，其余原样返回并加前缀，便于肉眼确认。
DICT = [
    ("hello", "你好"),
    ("hi", "嗨"),
    ("how are you", "你好吗"),
    ("thanks", "谢谢"),
    ("thank you", "谢谢你"),
    ("good morning", "早上好"),
    ("world", "世界"),
    ("test", "测试"),
    ("connection", "连接"),
    ("this is", "这是"),
]


def fake_translate(text: str) -> str:
    low = text.lower()
    for en, zh in DICT:
        # 用词边界匹配，避免把 "Chinese" 里的 "hi" 也换掉
        if re.search(r"\b" + re.escape(en) + r"\b", low):
            return "[ZH] " + re.sub(r"\b" + re.escape(en) + r"\b", zh, text, flags=re.IGNORECASE)
    return "[ZH] " + text


BATCH_HEADER = re.compile(r"###\s*\d+\s*###")


def split_batch(user: str):
    """
    把批量请求拆成各段。
    只取第一个 ###<n>### 之后的内容，避免把提示词前言当成第 0 段。
    """
    marks = list(BATCH_HEADER.finditer(user))
    if not marks:
        return None
    segs = []
    for i, m in enumerate(marks):
        start = m.end()
        end = marks[i + 1].start() if i + 1 < len(marks) else len(user)
        segs.append(user[start:end])
    return segs


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    counter = 0
    lock = threading.Lock()

    def log_message(self, fmt, *args):
        sys.stderr.write("mock: " + (fmt % args) + "\n")

    def _json(self, obj, status=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self._json({"error": {"message": "not found: " + self.path}}, 404)
            return

        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length).decode("utf-8", errors="replace")

        auth = self.headers.get("Authorization", "")
        with Handler.lock:
            Handler.counter += 1
            req_no = Handler.counter

        sys.stderr.write(f"\n--- 请求 #{req_no} auth='{auth}' ---\n{raw[:1200]}\n")

        try:
            payload = json.loads(raw)
        except Exception as exc:
            self._json({"error": {"message": "bad json: %s" % exc}}, 400)
            return

        messages = payload.get("messages") or []
        user = ""
        for m in messages:
            if m.get("role") == "user":
                user = m.get("content") or ""

        # —— 批量协议：###1###文本
        segments = split_batch(user) if "###" in user else None
        if segments is not None and len(segments) > 1:
            out = []
            for i, seg in enumerate(segments, start=1):
                seg = seg.replace("\\n", "\n").strip()
                out.append(f"###{i}###" + fake_translate(seg))
            content = "\n".join(out)
        else:
            content = fake_translate(user.strip())

        self._json({
            "id": "chatcmpl-mock-%d" % req_no,
            "object": "chat.completion",
            "model": payload.get("model", "mock"),
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": content},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": len(user), "completion_tokens": len(content), "total_tokens": len(user) + len(content)},
        })

    def do_GET(self):
        if self.path.rstrip("/").endswith("/models"):
            self._json({"object": "list", "data": [{"id": "mock-model", "object": "model"}]})
            return
        self._json({"error": {"message": "not found: " + self.path}}, 404)


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    sys.stderr.write(f"mock OpenAI server listening on http://127.0.0.1:{port}/v1\n")
    server.serve_forever()


if __name__ == "__main__":
    main()
