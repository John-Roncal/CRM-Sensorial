# app/schemas.py
from pydantic import BaseModel
from typing import Optional, Any, List, Dict
from uuid import UUID
from datetime import datetime

class ChatStart(BaseModel):
    conversation_id: Optional[UUID]
    anon_id: Optional[UUID]
    user_id: Optional[int]
    locale: Optional[str]

class ChatMessage(BaseModel):
    conversation_id: UUID
    anon_id: Optional[UUID]
    user_id: Optional[int]
    message: Optional[str]
    qkey: Optional[str]
    qanswer: Optional[str]

class CreateReserva(BaseModel):
    anon_id: Optional[UUID]
    user_id: Optional[int]
    experiencia_id: int
    num_comensales: int = 1
    fecha_hora: datetime
    restricciones: Optional[str] = None
    nombre_reserva: str
    dni: Optional[str] = None
    telefono: Optional[str] = None
    es_ocasion_especial: bool = False
    referencia_conversation_id: Optional[str] = None

class ConfirmReserva(BaseModel):
    reserva_id: int
    accion: Optional[str] = "confirmar"
    guardar_preferencias: bool = False

class MergeProfile(BaseModel):
    user_id: int
    anon_id: UUID
    transfer_events: bool = True
    transfer_profile: bool = True

class EventIn(BaseModel):
    event_type: str
    usuario_id: Optional[int]
    anon_id: Optional[UUID]
    conversation_id: Optional[UUID]
    sender_id: Optional[str]
    payload: Optional[Dict[str, Any]]

class ScoreRequest(BaseModel):
    request_id: Optional[str]
    user_id: Optional[int]
    anon_id: Optional[UUID]
    context: Optional[Dict[str, Any]]
    candidates: Optional[List[Dict[str, Any]]]
    top_k: int = 5
