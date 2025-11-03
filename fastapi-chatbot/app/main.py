# app/main.py
import os
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from app.routers import chat, reservas, events
from dotenv import load_dotenv

load_dotenv()  # carga .env en desarrollo

app = FastAPI(title="Chatbot FastAPI - Central", version="0.1")

# CORS: ajusta orígenes
origins = [
    "http://localhost:4200",
    "http://localhost:3000",
    "http://localhost:8000",
    "http://localhost:5242",   # <- agrega el puerto de tu app ASP.NET (ejemplo)
    "http://127.0.0.1:5242"    # opcional: versión con 127.0.0.1
]
app.add_middleware(
    CORSMiddleware,
    allow_origins=origins,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# Routers
app.include_router(chat.router)
app.include_router(reservas.router)
app.include_router(events.router)

@app.get("/health")
async def health():
    return {"status":"ok"}
