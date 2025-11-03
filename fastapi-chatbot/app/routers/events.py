# app/routers/events.py
from fastapi import APIRouter, Depends
from app import db, schemas
from app.deps import validate_service_token

router = APIRouter(prefix="/events", tags=["events"])

@router.post("/", dependencies=[Depends(validate_service_token)])
async def receive_event(evt: schemas.EventIn):
    # Insert minimal
    await db.a_execute("INSERT INTO dbo.Eventos (EventType, UsuarioId, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?,?)",
                       (evt.event_type, evt.usuario_id, str(evt.anon_id) if evt.anon_id else None, str(evt.conversation_id) if evt.conversation_id else None, evt.sender_id or "external", "{}"))
    return {"accepted": True}
