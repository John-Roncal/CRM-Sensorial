# app/deps.py
import os
from fastapi import Header, HTTPException, status, Request

SERVICE_TOKEN = os.getenv("SERVICE_TOKEN")

def validate_service_token(x_service_token: str = Header(None)):
    if not SERVICE_TOKEN:
        raise HTTPException(status_code=500, detail="SERVICE_TOKEN no configurado")
    if x_service_token != SERVICE_TOKEN:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Service token inválido")
    return True

def get_optional_token(request: Request):
    # helper que obtiene X-Service-Token o Authorization Bearer si existe
    t = request.headers.get("x-service-token")
    if not t:
        auth = request.headers.get("authorization")
        if auth and auth.lower().startswith("bearer "):
            t = auth[7:]
    return t
