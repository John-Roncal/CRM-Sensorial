# app/routers/reservas.py
from fastapi import APIRouter, Depends, HTTPException, Header, Request
from typing import Optional
from app import db, schemas
from app.deps import validate_service_token, get_optional_token
from uuid import UUID

router = APIRouter(prefix="/reservas", tags=["reservas"])

@router.post("/create")
async def create_reserva(payload: schemas.CreateReserva, request: Request):
    # require either anon_id or service token or user_id (frontend may call with anon_id)
    token = get_optional_token(request)
    if not payload.anon_id and not payload.user_id and token is None:
        raise HTTPException(status_code=401, detail="anon_id o autenticación requerida")

    # validar experiencia
    row = await db.a_fetchone("SELECT Id FROM dbo.Experiencias WHERE Id = ? AND Activa = 1", (payload.experiencia_id,))
    if not row:
        raise HTTPException(status_code=400, detail="Experiencia no encontrada")

    # insertar reserva temporal
    anon_str = str(payload.anon_id) if payload.anon_id else None
    query = """
    INSERT INTO dbo.Reservas (UsuarioId, NombreReserva, NumComensales, ExperienciaId, Restricciones, FechaHora, Estado, EsTemporal, DNI, Telefono, CreadoEn)
    VALUES (?,?,?,?,?,?,?,?,?,?,GETUTCDATE());
    SELECT SCOPE_IDENTITY();
    """
    # pyodbc may not accept SELECT SCOPE_IDENTITY() in this flow; alternativa: insertar y luego SELECT TOP 1 ... ORDER BY Id DESC con filtros
    await db.a_execute("INSERT INTO dbo.Reservas (UsuarioId, NombreReserva, NumComensales, ExperienciaId, Restricciones, FechaHora, Estado, EsTemporal, DNI, Telefono, CreadoEn) VALUES (?,?,?,?,?,?,?,?,?,?,GETUTCDATE())",
                       (payload.user_id, payload.nombre_reserva, payload.num_comensales, payload.experiencia_id, payload.restricciones, payload.fecha_hora, "pendiente", 1, payload.dni, payload.telefono))
    # obtener id recien insertado (simple heurística: buscar por datos)
    r = await db.a_fetchone("SELECT TOP 1 Id FROM dbo.Reservas WHERE NombreReserva = ? AND ExperienciaId = ? ORDER BY Id DESC", (payload.nombre_reserva, payload.experiencia_id))
    reserva_id = int(r[0]) if r else None

    # link to anon session if present
    if payload.anon_id:
        await db.a_execute("UPDATE dbo.Reservas SET AnonId = ? WHERE Id = ?", (str(payload.anon_id), reserva_id))

    # crear evento booking.initiated
    await db.a_execute("INSERT INTO dbo.Eventos (EventType, UsuarioId, AnonId, SenderId, Payload) VALUES (?,?,?,?,?)",
                       ("booking.initiated", payload.user_id, str(payload.anon_id) if payload.anon_id else None, "fastapi-chatbot", "{}"))

    return {"reserva_id": reserva_id, "estado": "pendiente", "es_temporal": True}

@router.post("/confirm")
async def confirm_reserva(payload: schemas.ConfirmReserva, request: Request):
    token = get_optional_token(request)
    # obtener reserva
    r = await db.a_fetchone("SELECT Id, UsuarioId, AnonId FROM dbo.Reservas WHERE Id = ?", (payload.reserva_id,))
    if not r:
        raise HTTPException(status_code=404, detail="Reserva no encontrada")
    # confirmar
    await db.a_execute("UPDATE dbo.Reservas SET Estado = ?, EsTemporal = 0, ActualizadoEn = GETUTCDATE() WHERE Id = ?", ("confirmada" if (payload.accion is None or payload.accion=="confirmar") else "cancelada", payload.reserva_id))
    # si guardar preferencias y existe UsuarioId: copiar perfil->preferencias (si aplica)
    res_user = r[1]
    res_anon = r[2]
    if payload.guardar_preferencias and res_user:
        perfil = await db.a_fetchone("SELECT Q1, Q1_Otro, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ? OR (AnonId = ?)", (res_user, res_anon))
        if perfil:
            import json
            pref_json = json.dumps({"q1": perfil[0], "q1_otro": perfil[1], "q2": perfil[2], "q3": perfil[3]}, default=str)
            await db.a_execute("INSERT INTO dbo.Preferencias (UsuarioId, DatosJson, CreadoEn) VALUES (?,?,GETUTCDATE())", (res_user, pref_json))
    # evento booking.confirmed
    await db.a_execute("INSERT INTO dbo.Eventos (EventType, UsuarioId, AnonId, SenderId, Payload) VALUES (?,?,?,?,?)",
                       ("booking.confirmed", res_user, res_anon, "fastapi-chatbot", "{}"))
    return {"reserva_id": payload.reserva_id, "estado": "confirmada"}

@router.post("/merge_profile")
async def merge_profile(payload: schemas.MergeProfile, service_ok: bool = Depends(validate_service_token)):
    # Transferir perfil anon a usuario (simple)
    # asignar UsuarioId en Perfiles donde AnonId = payload.anon_id
    await db.a_execute("UPDATE dbo.Perfiles SET UsuarioId = ?, AnonId = NULL, ActualizadoEn = GETUTCDATE() WHERE AnonId = ?", (payload.user_id, str(payload.anon_id)))
    # reasignar eventos
    if payload.transfer_events:
        await db.a_execute("UPDATE dbo.Eventos SET UsuarioId = ? WHERE AnonId = ?", (payload.user_id, str(payload.anon_id)))
    # reasignar reservas
    await db.a_execute("UPDATE dbo.Reservas SET UsuarioId = ?, AnonId = NULL, ActualizadoEn = GETUTCDATE() WHERE AnonId = ?", (payload.user_id, str(payload.anon_id)))
    # marcar anon session merged
    await db.a_execute("UPDATE dbo.AnonSessions SET Estado = ? WHERE AnonId = ?", ("merged", str(payload.anon_id)))
    # evento profile.merged
    await db.a_execute("INSERT INTO dbo.Eventos (EventType, UsuarioId, AnonId, SenderId, Payload) VALUES (?,?,?,?,?)", ("profile.merged", payload.user_id, str(payload.anon_id), "fastapi-chatbot", "{}"))
    return {"merged": True, "user_id": payload.user_id}
