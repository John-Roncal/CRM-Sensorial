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
    "http://localhost:5242",
    "https://localhost:7085",
    "https://localhost:44327"
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
