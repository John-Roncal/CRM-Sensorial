# app/cohere_client.py
import os
import cohere
from fastapi.concurrency import run_in_threadpool

COHERE_KEY = os.getenv("COHERE_API_KEY")
if not COHERE_KEY:
    raise RuntimeError("COHERE_API_KEY no definido")

_client = cohere.Client(COHERE_KEY)

def choose_model_default():
    try:
        models = _client.list_models()
        ids = []
        for m in models:
            try:
                d = m.to_dict() if hasattr(m, "to_dict") else (m if isinstance(m, dict) else None)
                if isinstance(d, dict) and "id" in d:
                    ids.append(d["id"])
                else:
                    ids.append(str(m))
            except Exception:
                ids.append(str(m))
        if ids:
            return ids[0]
    except Exception:
        pass
    return "command-xlarge-nightly"

MODEL = choose_model_default()

def sync_chat(prompt: str, max_tokens: int = 300, temperature: float = 0.5):
    resp = _client.chat(model=MODEL, message=prompt, max_tokens=max_tokens, temperature=temperature)
    # Robust extraction similar a tu script
    try:
        d = resp.to_dict() if hasattr(resp, "to_dict") else (resp if isinstance(resp, dict) else None)
    except Exception:
        d = None
    text = None
    if isinstance(d, dict):
        for path in (("output",0,"content",0,"text"), ("choices",0,"message","content"), ("response",), ("text",), ("answer",)):
            try:
                cur = d
                for p in path:
                    cur = cur[p]
                if cur:
                    text = cur
                    break
            except Exception:
                continue
    if not text:
        try: text = getattr(resp, "text", None)
        except Exception: pass
    if not text:
        try: text = resp.output[0].content[0].text
        except Exception: pass
    return str(text or "").strip()

async def chat(prompt: str, max_tokens: int = 300, temperature: float = 0.5):
    return await run_in_threadpool(sync_chat, prompt, max_tokens, temperature)
