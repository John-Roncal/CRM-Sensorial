from fastapi import APIRouter, Request, Response, HTTPException
from uuid import uuid4, UUID
import logging
import asyncio
import json
import re
from datetime import datetime, timedelta
from typing import Optional, Any, Dict, List
import random
import os
import jwt  # pip install PyJWT

from app import db, schemas
from app.gemini_client import chat as gemini_chat

# Configurables vía env vars
JWT_SECRET = os.environ.get("CHAT_JWT_SECRET", "dev-secret-change-me")
JWT_ALG = os.environ.get("CHAT_JWT_ALG", "HS256")
SERVICE_TOKEN = os.environ.get("CHAT_SERVICE_TOKEN", None)


logger = logging.getLogger(__name__)

router = APIRouter(prefix="/chat", tags=["chat"])

def _extract_userid_from_bearer(auth_header: Optional[str]) -> Optional[int]:
    if not auth_header:
        return None
    parts = auth_header.split()
    if len(parts) != 2 or parts[0].lower() != "bearer":
        return None
    token = parts[1]
    try:
        payload = jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALG])
        uid = payload.get("sub") or payload.get("user_id") or payload.get("uid")
        if uid is None:
            return None
        return int(uid)
    except Exception:
        logger.exception("invalid jwt token")
        return None



def _as_uuid(val) -> Optional[UUID]:
    if val is None:
        return None
    if isinstance(val, UUID):
        return val
    try:
        return UUID(str(val))
    except Exception:
        return None


async def safe_gemini_chat(prompt: str, timeout_sec: float = 10.0) -> Optional[str]:
    try:
        # Aumentamos el timeout para dar más tiempo a Gemini
        return await asyncio.wait_for(gemini_chat(prompt), timeout=timeout_sec)
    except asyncio.TimeoutError:
        logger.warning("safe_gemini_chat: timeout after %.2fs", timeout_sec)
        return None
    except Exception:
        logger.exception("safe_gemini_chat: unexpected exception")
        return None


async def quick_recommendations_by_profile(anon_id: Optional[str], user_id: Optional[int], topk: int = 3) -> List[Dict[str, Any]]:
    q3 = None
    try:
        if user_id:
            row = await db.a_fetchone("SELECT Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
            if row:
                q3 = (row[0] or "").lower()
        elif anon_id:
            row = await db.a_fetchone("SELECT Q3 FROM dbo.Perfiles WHERE AnonId = ?", (str(anon_id),))
            if row:
                q3 = (row[0] or "").lower()
    except Exception:
        logger.exception("quick_recommendations_by_profile: error reading perfil")

    try:
        rows = await db.a_fetchall("SELECT Id, Nombre, Descripcion, Precio FROM dbo.Experiencias WHERE Activa = 1")
    except Exception:
        logger.exception("quick_recommendations_by_profile: error reading experiencias")
        return []

    scored = []
    for r in rows:
        try:
            eid, nombre, desc, precio = r
            score = 0.1
            text = f"{(nombre or '')} {(desc or '')}".lower()
            if q3:
                if "veget" in q3 and "veget" in text:
                    score += 1.0
                if "gourmet" in q3 and ("gourmet" in text or "degust" in text):
                    score += 1.0
            score += max(0, 0.1 - ((precio or 0) / 1000.0))
            scored.append((score, eid, nombre, precio))
        except Exception:
            continue

    scored.sort(reverse=True, key=lambda x: x[0])
    result = [{"id": int(s[1]), "nombre": s[2], "precio": float(s[3] or 0)} for s in scored[:topk]]
    return result


async def save_reservation_partial(conversation_id: str, anon_id: Optional[str], field: str, value: Any):
    try:
        payload = json.dumps({field: value}, default=str)
        await db.a_execute(
            "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
            ("reservation.partial", anon_id, conversation_id, "chatbot", payload)
        )
    except Exception:
        logger.exception("save_reservation_partial failed")


async def get_merged_reservation(conversation_id: str) -> Dict[str, Any]:
    merged = {}
    try:
        rows = await db.a_fetchall("SELECT Payload FROM dbo.Eventos WHERE ConversationId = ? AND EventType = ?", (conversation_id, "reservation.partial"))
        for r in rows:
            try:
                p = json.loads(r[0])
                if isinstance(p, dict):
                    merged.update(p)
            except Exception:
                continue
    except Exception:
        logger.exception("get_merged_reservation failed")
    return merged


async def create_provisional_reservation(merged: dict, user_id: Optional[int], anon_id: Optional[str], conversation_id: Optional[str] = None) -> Dict[str, Any]:
    """
    Crea la reserva en la BD y devuelve un dict con (reserva_id, reservation_obj).
    Se encarga de normalizar fecha, resolver user_id desde Perfiles si es necesario.
    """
    try:
        # normalizar fecha
        fecha_iso = merged.get("fecha_hora")
        fecha_dt = None
        if fecha_iso:
            try:
                fecha_norm = str(fecha_iso).replace(" ", "T")
                fecha_dt = datetime.fromisoformat(fecha_norm)
            except Exception:
                # intentar formato dd/mm/yyyy
                try:
                    m2 = re.search(r"(\d{1,2})/(\d{1,2})/(\d{4})(?:\s+(\d{1,2}):(\d{2}))?", str(fecha_iso))
                    if m2:
                        dd, mm, yyyy = int(m2.group(1)), int(m2.group(2)), int(m2.group(3))
                        hh = int(m2.group(4) or 19)
                        mi = int(m2.group(5) or 0)
                        fecha_dt = datetime(year=yyyy, month=mm, day=dd, hour=hh, minute=mi)
                except Exception:
                    fecha_dt = None

        # valores por defecto / parse
        try:
            num_com = int(merged.get("num_comensales") or 1)
        except Exception:
            num_com = 1
        try:
            exp_id = int(merged.get("experiencia_id") or 0)
        except Exception:
            exp_id = 0

        nombre_res = merged.get("nombre_reserva")
        restricciones = merged.get("restricciones")
        dni = merged.get("dni")
        telefono = merged.get("telefono")

        # Resolver user_id desde perfil si no hay user_id
        if not user_id and anon_id:
            try:
                rowp = await db.a_fetchone("SELECT UsuarioId FROM dbo.Perfiles WHERE AnonId = ?", (str(anon_id),))
                if rowp and rowp[0]:
                    user_id = int(rowp[0])
            except Exception:
                logger.exception("error resolving UsuarioId from Perfiles for anon_id")

        fecha_param = fecha_dt if fecha_dt is not None else (fecha_iso or None)

        # Validaciones mínimas
        # verificar existencia de experiencia y Activa = 1
        if exp_id:
            try:
                rowe = await db.a_fetchone("SELECT Id FROM dbo.Experiencias WHERE Id = ? AND Activa = 1", (exp_id,))
                if not rowe:
                    logger.warning("create_provisional_reservation: experiencia_id not found or inactive: %s", exp_id)
            except Exception:
                logger.exception("error checking experiencia existence for id %s", exp_id)

        # Insert robusto y recuperar id
        inserted = await db.a_fetchone(
            "INSERT INTO dbo.Reservas (UsuarioId, NombreReserva, NumComensales, ExperienciaId, Restricciones, FechaHora, Estado, EsTemporal, DNI, Telefono, CreadoEn) OUTPUT INSERTED.Id VALUES (?,?,?,?,?,?,?,?,?,? ,GETUTCDATE())",
            (
                user_id,
                nombre_res,
                num_com,
                exp_id,
                restricciones,
                fecha_param,
                "pendiente",
                1,
                dni,
                telefono
            )
        )
        reserva_id = int(inserted[0]) if inserted else None

        # registrar evento booking.initiated
        try:
            await db.a_execute(
                "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                ("booking.initiated", str(anon_id) if anon_id else None, str(conversation_id) if conversation_id else None, "chatbot", json.dumps({"reserva_id": reserva_id}))
            )
        except Exception:
            logger.exception("failed to log booking.initiated in helper")

        reservation_obj = {
            "reserva_id": reserva_id,
            "id": reserva_id,
            "experiencia_id": exp_id,
            "fecha_hora": fecha_iso,
            "num_comensales": num_com,
            "nombre_reserva": nombre_res,
            "telefono": telefono,
            "dni": dni,
            "restricciones": restricciones
        }

        return {"reserva_id": reserva_id, "reservation_obj": reservation_obj}
    except Exception as e:
        logger.exception("create_provisional_reservation failed: %s", e)
        raise



async def get_profile_summary(anon_id: Optional[str], user_id: Optional[int]) -> str:
    try:
        if user_id:
            row = await db.a_fetchone("SELECT Q1, Q1_Otro, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
        elif anon_id:
            row = await db.a_fetchone("SELECT Q1, Q1_Otro, Q2, Q3 FROM dbo.Perfiles WHERE AnonId = ?", (str(anon_id),))
        else:
            row = None
        if not row:
            return ""
        q1, q1_otro, q2, q3 = row
        parts = []
        if q1:
            parts.append(f"Motivo: {q1}" + (f" ({q1_otro})" if q1_otro else ""))
        if q2:
            parts.append(f"Compañía: {q2}")
        if q3:
            parts.append(f"Preferencias: {q3}")
        return ". ".join(parts)
    except Exception:
        logger.exception("get_profile_summary failed")
        return ""


async def build_llm_prompt(profile_ctx: str, merged_reservation: dict, experiencias: list, user_message: str) -> str:
    system = (
        "Eres 'Amigo Central', el asistente virtual del restaurante Central. "
        "Tu misión es ayudar a los clientes a explorar nuestras experiencias culinarias y a realizar reservas de una manera cálida, amigable y eficiente. "
        "Habla siempre en español, con un tono cercano pero profesional. Guía al usuario paso a paso en el proceso de reserva."
        "\nIMPORTANTE: Tu respuesta SIEMPRE debe incluir un bloque de código JSON al final, dentro de ```json ... ```."
    )

    instructions = (
        "Instrucciones de conversación y JSON:\n"
        "- Sé conversacional y amigable, no robótico.\n"
        "- Si faltan datos para la reserva, pide el siguiente dato en este orden: experiencia -> fecha/hora -> número de personas -> nombre -> teléfono -> restricciones.\n"
        "- Para pedir un dato, responde con una pregunta amigable y un JSON como: ```json {\"type\":\"form\",\"field\":\"fecha_hora\",\"label\":\"¿Para qué fecha y hora sería tu reserva?\"} ```\n"
        "- Si el usuario pide recomendaciones, responde con sugerencias y un JSON como: ```json {\"type\":\"experiences\",\"items\":[{\"id\":1,\"nombre\":\"Experiencia A\"}]} ```\n"
        "- Una vez que tengas todos los datos, muestra un resumen y el JSON: ```json {\"type\":\"summary\",\"reservation\":{...}} ```\n"
        "- Para respuestas de texto simples, usa: ```json {\"type\":\"text\",\"text\":\"Tu respuesta aquí.\"} ```"
    )

    prof = f"Contexto cliente: {profile_ctx}\n" if profile_ctx else ""
    merged_txt = ""
    if merged_reservation:
        merged_pairs = ", ".join(f"{k}={v}" for k, v in merged_reservation.items())
        merged_txt = f"Parciales de reserva actuales: {merged_pairs}\n"

    exper_txt = ""
    if experiencias:
        lines = [f"{e['id']}. {e['nombre']}" + (f" ({e['precio']})" if e.get('precio') else "") for e in experiencias]
        exper_txt = "Experiencias activas (id - nombre):\n" + "\n".join(lines) + "\n"

    prompt = "\n\n".join([system, instructions, prof + merged_txt + exper_txt, f"Usuario: {user_message}\nAsistente:"])
    return prompt


def extract_json_from_text(text: str):
    if not text:
        return None
    m = re.search(r"```json\s*(\{.*?\})\s*```", text, flags=re.S | re.I)
    candidate = None
    if m:
        candidate = m.group(1)
    else:
        m2 = re.search(r"(\{(?:.|\s)*\})\s*$", text, flags=re.S)
        if m2:
            candidate = m2.group(1)
    if not candidate:
        return None
    try:
        return json.loads(candidate)
    except Exception:
        try:
            first = candidate.find("{")
            last = candidate.rfind("}")
            candidate2 = candidate[first:last+1]
            return json.loads(candidate2)
        except Exception:
            return None
        

# -------------------- helper: parseador simple de fechas/hora en español --------------------
WEEKDAY_MAP = {
    "lunes": 0, "martes": 1, "miércoles": 2, "miercoles": 2, "jueves": 3,
    "viernes": 4, "sábado": 5, "sabado": 5, "domingo": 6
}

def parse_spanish_natural_datetime(text: str, ref: Optional[datetime] = None) -> Optional[str]:
    """
    Parsea expresiones simples en español como:
      - "mañana a las 3pm", "hoy 19:00", "pasado mañana a las 9", "el martes a las 20:30"
    Devuelve string ISO "YYYY-MM-DDTHH:MM" (sin segundos) o None si no reconoce.
    """
    if not text:
        return None
    try:
        t = text.lower()
        ref = ref or datetime.now()

        # detectar palabra día relativa
        day_offset = None
        if "pasado mañana" in t or "pasadomañana" in t:
            day_offset = 2
        elif "mañana" in t:
            day_offset = 1
        elif "hoy" in t:
            day_offset = 0
        else:
            # detectar día de la semana en español
            for name, wd in WEEKDAY_MAP.items():
                if re.search(r"\b" + re.escape(name) + r"\b", t):
                    today_wd = ref.weekday()
                    delta = (wd - today_wd) % 7
                    # si es hoy (delta == 0) asumimos la próxima ocurrencia (7 días)
                    if delta == 0:
                        delta = 7
                    day_offset = delta
                    break

        # detectar hora: patrones como "a las 3pm", "15:30", "3:00 pm", "3pm", "3 p.m."
        time_match = re.search(
            r"(?:a\s+las|a|al)?\s*([0-9]{1,2})(?::([0-9]{2}))?\s*(am|pm|a\.m\.|p\.m\.)?",
            t, flags=re.I
        )

        hour = None
        minute = 0
        if time_match:
            try:
                hour = int(time_match.group(1))
                if time_match.group(2):
                    minute = int(time_match.group(2))
                ampm = (time_match.group(3) or "").replace(".", "").lower()
                if ampm in ("pm", "p m", "pm"):
                    if hour < 12:
                        hour += 12
                elif ampm in ("am", "a m", "am"):
                    if hour == 12:
                        hour = 0
                # normalize hour bounds
                if hour == 24:
                    hour = 0
                if not (0 <= hour <= 23):
                    return None
            except Exception:
                return None

        # Si no detectamos día ni hora, devolvemos None
        if day_offset is None and hour is None:
            return None

        # determinar fecha base
        base_date = ref
        if day_offset is not None:
            base_date = (ref + timedelta(days=day_offset))
        else:
            # no hay 'mañana/hoy' ni weekday; si hay hora y la hora ya pasó hoy asumimos mañana
            if hour is not None:
                candidate = ref.replace(hour=hour, minute=minute, second=0, microsecond=0)
                if candidate <= ref:
                    base_date = ref + timedelta(days=1)
                else:
                    base_date = ref

        # si no vino hora, usar 19:00 por defecto
        if hour is None:
            hour = 19
            minute = 0

        try:
            dt = base_date.replace(hour=hour, minute=minute, second=0, microsecond=0)
        except Exception:
            dt = datetime(year=base_date.year, month=base_date.month, day=base_date.day, hour=hour, minute=minute)

        return dt.strftime("%Y-%m-%dT%H:%M")
    except Exception:
        logger.exception("parse_spanish_natural_datetime failed for text=%s", text)
        return None


# -------------------- helper: detección de campo por mención en texto --------------------
def detect_field_mention(text: str) -> Optional[str]:
    """
    Devuelve el nombre del campo lógico (ej: 'fecha_hora', 'num_comensales', 'nombre_reserva',
    'telefono', 'dni', 'restricciones', 'experiencia_id') si el texto menciona qué desea cambiar.
    """
    if not text:
        return None
    t = text.lower()
    # mapeo de palabras clave a campos
    if any(k in t for k in ("fecha", "hora", "día", "día/hora", "fecha y hora")):
        return "fecha_hora"
    if any(k in t for k in ("personas", "comensales", "cuántos", "cuantos", "personas", "número de comensales")):
        return "num_comensales"
    if any(k in t for k in ("nombre", "a nombre", "titular")):
        return "nombre_reserva"
    if any(k in t for k in ("tel", "telefono", "móvil", "celular", "número")):
        return "telefono"
    if any(k in t for k in ("dni", "documento", "ruc")):
        return "dni"
    if any(k in t for k in ("restric", "alerg", "alergia", "comida", "veget", "vegano", "vegetariano", "gourmet", "tradicional")):
        return "restricciones"
    if "experienc" in t or "experiencia" in t:
        return "experiencia_id"
    return None



# -------------------- helpers para texto libre -> campo --------------------
def try_parse_field_from_text(field: str, text: str):
    if not text:
        return None
    txt = text.strip()

    # fecha_hora: primero intentar parse natural en español, luego patrones ISO/dd/mm/yyyy
    if field == "fecha_hora":
        # intentar lenguaje natural (mañana, hoy, pasado mañana, "a las 3pm", "el martes a las 9")
        nat = parse_spanish_natural_datetime(txt)
        if nat:
            return nat

        # existing ISO / yyyy-mm-ddT HH:MM
        m = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})", txt)
        if m:
            return m.group(1)
        m = re.search(r"(\d{4}-\d{2}-\d{2})", txt)
        if m:
            return m.group(1) + "T19:00"
        m2 = re.search(r"(\d{1,2}/\d{1,2}/\d{4})(?:\s+(\d{1,2}:\d{2}))?", txt)
        if m2:
            d = m2.group(1)
            t = m2.group(2) or "19:00"
            parts = d.split("/")
            dd, mm, yyyy = parts[0].zfill(2), parts[1].zfill(2), parts[2]
            return f"{yyyy}-{mm}-{dd}T{t}"
        return None


    # num_comensales: primer número pequeño
    if field == "num_comensales":
        m = re.search(r"\b([1-9][0-9]?)\b", txt)
        if m:
            try:
                n = int(m.group(1))
                if 1 <= n <= 200:
                    return n
            except Exception:
                return None
        return None

    # telefono: extraer dígitos (mínimo 7)
    if field == "telefono":
        digits = re.sub(r"\D", "", txt)
        if len(digits) >= 7:
            return digits
        return None

    # nombre_reserva: texto libre (si no es puramente numérico)
    if field == "nombre_reserva":
        if txt and not txt.isdigit():
            return txt
        return txt  # aun si es númerico lo aceptamos como nombre

    # dni u otros: extraer dígitos razonables
    if field == "dni":
        digits = re.sub(r"\D", "", txt)
        if 6 <= len(digits) <= 12:
            return digits
        return None

    # restricciones / texto libre
    if field == "restricciones":
        return txt

    return None


@router.post("/start")
async def chat_start(request: Request, response: Response, payload: schemas.ChatStart):
    conversation_id = getattr(payload, "conversation_id", None) or getattr(payload, "ConversationId", None) or uuid4()

    incoming_anon_raw = getattr(payload, "anon_id", None) or getattr(payload, "AnonId", None)
    anon_id = _as_uuid(incoming_anon_raw)

    incoming_user_id = getattr(payload, "user_id", None) or getattr(payload, "UserId", None)
    auth_header = request.headers.get("authorization")
    jwt_user_id = _extract_userid_from_bearer(auth_header)
    if jwt_user_id:
        incoming_user_id = jwt_user_id
        logger.debug("chat_start: user_id obtained from JWT: %s", incoming_user_id)
    else:
        # 3) fallback a headers de servicio (si los usas)
        svc_token = request.headers.get("x-service-token")
        if SERVICE_TOKEN and svc_token == SERVICE_TOKEN:
            try:
                hdr_uid = request.headers.get("x-user-id")
                if hdr_uid:
                    incoming_user_id = int(hdr_uid)
                    logger.debug("chat_start: user_id obtained from x-user-id header (service token validated): %s", incoming_user_id)
            except Exception:
                logger.exception("chat_start: invalid x-user-id header")

    if not anon_id:
        cookie_val = request.cookies.get("anon_id")
        if cookie_val:
            parsed = _as_uuid(cookie_val)
            if parsed:
                anon_id = parsed
            else:
                anon_id = cookie_val

    logger.debug("chat_start called: conversation_id=%s user_id=%s anon_id=%s", conversation_id, incoming_user_id, anon_id)

    async def perfil_completo_por_usuario(user_id: int) -> bool:
        try:
            row = await db.a_fetchone("SELECT PerfilId, EstadoPerfilCompleto, Q1, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
            if not row:
                return False
            _, estado, q1, q2, q3 = row
            return bool(estado) or (q1 and q2 and q3)
        except Exception:
            logger.exception("perfil_completo_por_usuario error")
            return False

    async def perfil_completo_por_anon(aid) -> bool:
        try:
            row = await db.a_fetchone("SELECT PerfilId, EstadoPerfilCompleto, Q1, Q2, Q3 FROM dbo.Perfiles WHERE AnonId = ?", (str(aid),))
            if not row:
                return False
            _, estado, q1, q2, q3 = row
            return bool(estado) or (q1 and q2 and q3)
        except Exception:
            logger.exception("perfil_completo_por_anon error")
            return False

    # --- si perfil completo: en lugar de enviar una lista UI, enviamos RECOMENDACIONES (3 max)
    if incoming_user_id:
        try:
            if await perfil_completo_por_usuario(incoming_user_id):
                try:
                    exp_rows = await db.a_fetchall("SELECT Id, Nombre, Precio FROM dbo.Experiencias WHERE Activa = 1 ORDER BY Nombre")
                    experiencias = [{"id": int(r[0]), "nombre": r[1], "precio": float(r[2] or 0)} for r in exp_rows]
                except Exception:
                    logger.exception("failed to fetch experiencias")
                    experiencias = []

                try:
                    await db.a_execute(
                        "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                        ("conversation.started", str(anon_id) if anon_id else None, str(conversation_id), "fastapi-chatbot", "{}")
                    )
                except Exception:
                    logger.exception("failed to log event conversation.started (user)")

                # obtener recomendaciones; si none -> tomar al azar
                recs = await quick_recommendations_by_profile(None, incoming_user_id, topk=3)
                if not recs and experiencias:
                    recs = random.sample(experiencias, min(3, len(experiencias)))

                if recs:
                    lines = [f"{r['id']}. {r['nombre']} (Precio: {r['precio']})" for r in recs]
                    rec_text = "Te recomiendo estas experiencias:\n" + "\n".join(lines) + "\n\nResponde con el ID para seleccionar una experiencia, o escribe otra cosa para que te ayude."
                else:
                    rec_text = "No encuentro recomendaciones específicas ahora — dime qué buscas o escribe 'recomiéndame' para que te sugiera opciones."

                welcome_text = "Bienvenido de nuevo. Veo tus preferencias guardadas — esto me ayudará a recomendarte mejor."

                return {
                    "conversation_id": str(conversation_id),
                    "anon_id": str(anon_id) if anon_id else None,
                    "messages": [
                        {"type": "text", "text": welcome_text},
                        {"type": "text", "text": rec_text},
                        {"type": "action", "action": "proceed_to_reserva"}
                    ]
                }
        except Exception:
            logger.exception("error checking perfil por usuario; falling back to anon flow")

    if anon_id:
        try:
            if await perfil_completo_por_anon(anon_id):
                try:
                    exp_rows = await db.a_fetchall("SELECT Id, Nombre, Precio FROM dbo.Experiencias WHERE Activa = 1 ORDER BY Nombre")
                    experiencias = [{"id": int(r[0]), "nombre": r[1], "precio": float(r[2] or 0)} for r in exp_rows]
                except Exception:
                    logger.exception("failed to fetch experiencias")
                    experiencias = []

                try:
                    await db.a_execute(
                        "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                        ("conversation.started", str(anon_id) if anon_id else None, str(conversation_id), "fastapi-chatbot", "{}")
                    )
                except Exception:
                    logger.exception("failed to log event conversation.started (anon)")

                recs = await quick_recommendations_by_profile(str(anon_id), None, topk=3)
                if not recs and experiencias:
                    recs = random.sample(experiencias, min(3, len(experiencias)))

                if recs:
                    lines = [f"{r['id']}. {r['nombre']} (Precio: {r['precio']})" for r in recs]
                    rec_text = "¡Hola! Puedo recomendarte estas experiencias:\n" + "\n".join(lines) + "\n\nResponde con el ID para seleccionar una, o escribe otra cosa."
                else:
                    rec_text = "¡Hola! No tengo recomendaciones claras todavía. Dime qué prefieres o escribe 'recomiéndame'."

                return {
                    "conversation_id": str(conversation_id),
                    "anon_id": str(anon_id),
                    "messages": [
                        {"type": "text", "text": rec_text},
                        {"type": "action", "action": "proceed_to_reserva"}
                    ]
                }
        except Exception:
            logger.exception("error checking perfil por anon; continuing to ensure anon session")

    # ensure anon session exists (profile not complete)
    if not anon_id:
        new_anon = uuid4()
        try:
            await db.a_execute(
                "INSERT INTO dbo.AnonSessions (AnonId, Estado, CreadoEn) VALUES (?,?,GETUTCDATE())",
                (str(new_anon), "activo")
            )
        except Exception:
            logger.exception("failed to insert anon session for new anon")
        anon_id = new_anon
    else:
        try:
            r = await db.a_fetchone("SELECT AnonId FROM dbo.AnonSessions WHERE AnonId = ?", (str(anon_id),))
            if not r:
                await db.a_execute(
                    "INSERT INTO dbo.AnonSessions (AnonId, Estado, CreadoEn) VALUES (?,?,GETUTCDATE())",
                    (str(anon_id), "activo")
                )
        except Exception:
            logger.exception("error ensuring anon session (non-fatal)")

    try:
        secure_cookie = (request.url.scheme == "https")
        # Para cross-site frontend/backend, usar samesite="none" y secure=True (https req.)
        samesite_val = "none" if secure_cookie else "lax"
        response.set_cookie(
            key="anon_id",
            value=str(anon_id),
            max_age=60 * 60 * 24 * 90,
            httponly=True,
            samesite=samesite_val,
            secure=secure_cookie,
            path="/"
        )
    except Exception:
        logger.exception("failed to set anon_id cookie (non-fatal)")


    try:
        await db.a_execute(
                "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                 ("conversation.started", str(anon_id) if anon_id else None, str(conversation_id), "fastapi-chatbot", "{}")
        )
    except Exception:
        logger.exception("failed to log conversation.started event (anon)")

    return {
        "conversation_id": str(conversation_id),
        "anon_id": str(anon_id),
        "messages": [
            {"type": "text", "text": "¡Hola! ¿En qué puedo ayudarte hoy?"}
        ]
    }


@router.post("/message")
async def chat_message(request: Request, payload: schemas.ChatMessage):
    # usamos los atributos del payload (Pydantic) tal como venías haciendo para compatibilidad,
    # pero también aceptamos que el cliente envíe reservation_field/reservation_value
# --- REEMPLAZAR desde la extracción inicial hasta la validación del supplied_user_id ---

    conv = getattr(payload, "conversation_id", None) or getattr(payload, "ConversationId", None)
    if not conv:
        raise HTTPException(status_code=400, detail="conversation_id required")
    conversation_id = conv

    anon_raw = getattr(payload, "anon_id", None) or getattr(payload, "AnonId", None)
    anon_id = None
    try:
        # _as_uuid debe devolver UUID o None; si no, intenta normalizar a str
        maybe_uuid = _as_uuid(anon_raw)
        anon_id = str(maybe_uuid) if maybe_uuid else (str(anon_raw).strip() if anon_raw else None)
    except Exception:
        anon_id = str(anon_raw).strip() if anon_raw else None

    # --- EXTRAER user_id de fuentes en orden (con variables separadas) ---
    header_auth = request.headers.get("authorization")
    jwt_claim_user = _extract_userid_from_bearer(header_auth)  # puede ser None o string
    resolved_user_from_jwt = None
    jwt_payload = None

    if jwt_claim_user is not None and header_auth:
        # Intentar convertir claim directo a int
        try:
            resolved_user_from_jwt = int(jwt_claim_user)
        except Exception:
            # No es int: intentar decodificar token y resolver por email/external id
            try:
                # validar header_auth formato "Bearer <token>"
                parts = header_auth.split()
                if len(parts) == 2 and parts[0].lower() == "bearer":
                    token = parts[1]
                    jwt_payload = jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALG])
                    email = jwt_payload.get("email")
                    sub = jwt_payload.get("sub")
                    if email:
                        row = await db.a_fetchone("SELECT Id FROM dbo.Usuarios WHERE Email = ?", (email,))
                        if row and row[0]:
                            resolved_user_from_jwt = int(row[0])
                    if not resolved_user_from_jwt and sub:
                        # si tienes columna ExternalId para mapear sub no-numérico
                        row2 = await db.a_fetchone("SELECT Id FROM dbo.Usuarios WHERE ExternalId = ?", (str(sub),))
                        if row2 and row2[0]:
                            resolved_user_from_jwt = int(row2[0])
            except Exception:
                logger.exception("failed to decode/resolve JWT payload for user mapping")

    # Service header (x-service-token + x-user-id)
    user_id_from_header = None
    if SERVICE_TOKEN:
        svc_hdr = request.headers.get("x-service-token")
        if svc_hdr and svc_hdr == SERVICE_TOKEN:
            try:
                huid = request.headers.get("x-user-id")
                if huid:
                    user_id_from_header = int(huid)
            except Exception:
                user_id_from_header = None

    # payload.user_id (no confiable): usar solo como fallback tras validar en DB
    supplied_user_id = getattr(payload, "user_id", None) or getattr(payload, "UserId", None)

    # --- Construir user_id final con prioridad correcta ---
    # Prioridad: resolved_user_from_jwt (int) -> user_id_from_header -> supplied_user_id (validado contra DB)
    user_id = None
    if resolved_user_from_jwt:
        user_id = resolved_user_from_jwt
    elif user_id_from_header:
        user_id = user_id_from_header
    elif supplied_user_id:
        try:
            # validar existencia en BD
            row = await db.a_fetchone("SELECT Id FROM dbo.Usuarios WHERE Id = ?", (int(supplied_user_id),))
            if row:
                user_id = int(row[0])
                logger.debug("chat_message: accepted supplied_user_id after DB validation: %s", user_id)
            else:
                logger.debug("chat_message: supplied_user_id not found in DB -> ignored: %s", supplied_user_id)
        except Exception:
            logger.exception("chat_message: error validating supplied_user_id against DB")

    logger.debug(
        "chat_message called: conversation_id=%s user_id=%s anon_id_from_payload=%s (jwt_claim=%s, header_user=%s, supplied=%s)",
        conversation_id, user_id, getattr(payload, "anon_id", None), bool(jwt_claim_user), bool(user_id_from_header), supplied_user_id
    )



    # Mantén compatibilidad: seguir usando anon_id extraído del payload o de cookies
    conv = getattr(payload, "conversation_id", None) or getattr(payload, "ConversationId", None)

    qkey = getattr(payload, "qkey", None) or getattr(payload, "QKey", None)
    qanswer = getattr(payload, "qanswer", None) or getattr(payload, "QAnswer", None)

    # si vienen respuestas a preguntas (qkey) mantenemos el flujo igual
    if qkey and qanswer is not None:
        qk = qkey.strip().lower()
        try:
            if user_id:
                existing = await db.a_fetchone("SELECT PerfilId, Q1, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
                if existing:
                    perfil_id = existing[0]
                    if qk == "q1":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q1=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                    elif qk == "q1_otro":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q1_Otro=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                    elif qk == "q2":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q2=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                    elif qk == "q3":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q3=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                else:
                    q1 = qanswer if qk == "q1" else None
                    q1_otro = qanswer if qk == "q1_otro" else None
                    q2 = qanswer if qk == "q2" else None
                    q3 = qanswer if qk == "q3" else None
                    await db.a_execute(
                        "INSERT INTO dbo.Perfiles (UsuarioId, Q1, Q1_Otro, Q2, Q3, EstadoPerfilCompleto, CreadoEn) VALUES (?,?,?,?,?,?,GETUTCDATE())",
                        (user_id, q1, q1_otro, q2, q3, 0)
                    )
                perfil = await db.a_fetchone("SELECT PerfilId, Q1, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
            else:
                existing = await db.a_fetchone("SELECT PerfilId, Q1, Q2, Q3 FROM dbo.Perfiles WHERE AnonId = ?", (anon_id,))
                if existing:
                    perfil_id = existing[0]
                    if qk == "q1":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q1=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                    elif qk == "q1_otro":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q1_Otro=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                    elif qk == "q2":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q2=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                    elif qk == "q3":
                        await db.a_execute("UPDATE dbo.Perfiles SET Q3=?, ActualizadoEn=GETUTCDATE() WHERE PerfilId=?", (qanswer, perfil_id))
                else:
                    q1 = qanswer if qk == "q1" else None
                    q1_otro = qanswer if qk == "q1_otro" else None
                    q2 = qanswer if qk == "q2" else None
                    q3 = qanswer if qk == "q3" else None
                    await db.a_execute(
                        "INSERT INTO dbo.Perfiles (AnonId, Q1, Q1_Otro, Q2, Q3, EstadoPerfilCompleto, CreadoEn) VALUES (?,?,?,?,?,0,GETUTCDATE())",
                        (anon_id, q1, q1_otro, q2, q3)
                    )
                perfil = await db.a_fetchone("SELECT PerfilId, Q1, Q2, Q3 FROM dbo.Perfiles WHERE AnonId = ?", (anon_id,))
        except Exception:
            logger.exception("error saving profile qkey answer")

        try:
            await db.a_execute(
                "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                ("profile.question.answered", anon_id, str(conversation_id), "chatbot-proxy", "{}")
            )
        except Exception:
            logger.exception("failed to log profile.question.answered")

        if perfil:
            try:
                _, q1_val, q2_val, q3_val = perfil
            except Exception:
                q1_val = q2_val = q3_val = None
            if q1_val and q2_val and q3_val:
                try:
                    if user_id:
                        await db.a_execute("UPDATE dbo.Perfiles SET EstadoPerfilCompleto = 1, ActualizadoEn=GETUTCDATE() WHERE UsuarioId = ?", (user_id,))
                    else:
                        await db.a_execute("UPDATE dbo.Perfiles SET EstadoPerfilCompleto = 1, ActualizadoEn=GETUTCDATE() WHERE AnonId = ?", (anon_id,))
                except Exception:
                    logger.exception("failed to mark perfil EstadoPerfilCompleto")

                return {
                    "conversation_id": str(conversation_id),
                    "messages": [
                        {"type": "text", "text": "Perfecto, ya tengo tus preferencias. Buscando recomendaciones..."},
                        {"type": "action", "action": "proceed_to_reserva"}
                    ]
                }

        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Respuesta registrada."}]}

    # reservation_field/reservation_value handling (cuando cliente envía explicitamente)
    reservation_field = getattr(payload, "reservation_field", None) or getattr(payload, "ReservationField", None)
    reservation_value = getattr(payload, "reservation_value", None) or getattr(payload, "ReservationValue", None)
    if reservation_field and reservation_value is not None:
        # ---------- 1) Flow: iniciar edición si reservation_field == 'edit_reserva' ----------
        if reservation_field == 'edit_reserva':
            try:
                # reservation_value expected to be reserva id (int or str)
                rid = int(reservation_value)
            except Exception:
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "ID de reserva inválido para editar."}]}

            try:
                # fetch reserva
                row = await db.a_fetchone("SELECT Id, UsuarioId, NombreReserva, NumComensales, ExperienciaId, Restricciones, FechaHora, DNI, Telefono FROM dbo.Reservas WHERE Id = ?", (rid,))
                if not row:
                    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "No encontré esa reserva para editar."}]}
                # mapear y guardar como partials para que merged contenga valores actuales
                _, usuario_id_db, nombre_db, num_db, exp_db, restr_db, fecha_db, dni_db, tel_db = row
                # guardar los campos como reservation.partial para la conversación
                await save_reservation_partial(str(conversation_id), anon_id, "__editing_reserva_id", rid)
                if exp_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "experiencia_id", int(exp_db))
                if fecha_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "fecha_hora", str(fecha_db))
                if num_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "num_comensales", int(num_db))
                if nombre_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "nombre_reserva", nombre_db)
                if tel_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "telefono", tel_db)
                if dni_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "dni", dni_db)
                if restr_db is not None: await save_reservation_partial(str(conversation_id), anon_id, "restricciones", restr_db)

                # log evento
                await db.a_execute("INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                                   ("reservation.editing.started", anon_id, str(conversation_id), "chatbot", json.dumps({"reserva_id": rid})))
            except Exception:
                logger.exception("failed to start editing reservation")
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Error iniciando edición de la reserva."}]}

            # preguntar al usuario qué desea cambiar
            return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Perfecto — vamos a editar tu reserva. ¿Qué dato quieres cambiar? (fecha/hora, comensales, nombre, teléfono, DNI, restricciones o experiencia)"}]}

        # ---------- 2) Flow normal: guardar parcial (nuevo o existente) ----------
        try:
            await save_reservation_partial(str(conversation_id), anon_id, reservation_field, reservation_value)
        except Exception:
            logger.exception("failed to save reservation partial")

        merged = await get_merged_reservation(str(conversation_id))
        required = ["experiencia_id", "fecha_hora", "num_comensales", "nombre_reserva", "telefono", "restricciones"]

        missing = [f for f in required if not merged.get(f)]

        if missing:
            next_field = missing[0]
            question_text_map = {
                "fecha_hora": "¿Qué fecha y hora prefieres? (formato sugerido: YYYY-MM-DDTHH:MM o DD/MM/YYYY)",
                "num_comensales": "¿Cuántos comensales serán?",
                "nombre_reserva": "¿A nombre de quién va la reserva?",
                "telefono": "¿Cuál es tu teléfono de contacto?",
                "restricciones": "¿Tienes restricciones alimentarias o preferencias de comida?"
            }
            return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": question_text_map.get(next_field, "¿Puedes darme ese dato?")}]}
        else:
            # ya tenemos todos los partials --> crear reserva provisional usando helper central
            try:
                # intentar resolver UsuarioId desde perfil si hace falta (tal como ya lo haces)
                if not user_id and anon_id:
                    try:
                        rowp = await db.a_fetchone("SELECT UsuarioId FROM dbo.Perfiles WHERE AnonId = ?", (str(anon_id),))
                        if rowp and rowp[0]:
                            user_id = int(rowp[0])
                    except Exception:
                        logger.exception("error resolving UsuarioId from Perfiles for anon_id")

                # Recuperar merged (por si cambió)
                merged = await get_merged_reservation(str(conversation_id))

                # Llamar al helper (pásale conversation_id para que lo loguee)
                result = await create_provisional_reservation(merged, user_id, anon_id, str(conversation_id))
                reserva_id = result["reserva_id"]
                reservation_obj = result["reservation_obj"]
            except Exception:
                logger.exception("creating provisional reservation (reservation_field path) - failed")
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Error creando la reserva provisional."}]}

            return {
                "conversation_id": str(conversation_id),
                "messages": [
                    {"type": "text", "text": "He creado la reserva provisional. Aquí tienes el resumen:"},
                    {"type": "summary", "reservation": reservation_obj},
                    {"type": "action", "action": "proceed_to_reserva"}
                ]
            }

    # -------- heuristic: extract experience id from messages like "(ID 1)" or "ID 1" or "experiencia 1" --------
    message = getattr(payload, "message", None) or getattr(payload, "Message", None)
    
    # --- editar reserva: si estamos en modo edición (merged contiene __editing_reserva_id) manejamos aquí ---
    merged_for_edit = await get_merged_reservation(str(conversation_id))
    editing_rid = merged_for_edit.get("__editing_reserva_id")
    if editing_rid:
        # intentar detectar campo a editar desde el texto del usuario
        fld = detect_field_mention(message)
        if fld:
            # intentar parsear el nuevo valor directamente del texto
            try:
                parsed = try_parse_field_from_text(fld, message)
            except Exception:
                parsed = None

            if parsed is None:
                # pedir sólo el valor concreto para ese campo
                qmap = {
                    "fecha_hora": "Ok — ¿qué fecha y hora quieres? (Ej: 2025-11-02T19:30 o 02/11/2025 19:30)",
                    "num_comensales": "¿Cuántos comensales serán?",
                    "nombre_reserva": "Dime el nuevo nombre para la reserva.",
                    "telefono": "Dime el nuevo teléfono.",
                    "dni": "Dime el nuevo DNI.",
                    "restricciones": "Escribe las restricciones o preferencias de comida.",
                    "experiencia_id": "Dime el ID de la experiencia que quieres seleccionar."
                }
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": qmap.get(fld, "¿Cuál es el nuevo valor?")}]}
            else:
                # aplicar la actualización a la reserva en BD
                try:
                    rid = int(editing_rid)
                    col_map = {
                        "experiencia_id": "ExperienciaId",
                        "fecha_hora": "FechaHora",
                        "num_comensales": "NumComensales",
                        "nombre_reserva": "NombreReserva",
                        "telefono": "Telefono",
                        "dni": "DNI",
                        "restricciones": "Restricciones"
                    }
                    col = col_map.get(fld)
                    if not col:
                        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "No pude identificar qué campo actualizar."}]}

                    val_for_sql = parsed
                    if fld == "fecha_hora":
                        try:
                            if isinstance(parsed, str):
                                fecha_norm = str(parsed).replace(" ", "T")
                                dt = datetime.fromisoformat(fecha_norm)
                                val_for_sql = dt
                        except Exception:
                            val_for_sql = parsed

                    await db.a_execute(f"UPDATE dbo.Reservas SET {col} = ? , ActualizadoEn = GETUTCDATE() WHERE Id = ?", (val_for_sql, rid))

                    # registrar evento y limpiar flag de edición (finaliza la edición)
                    await db.a_execute(
                        "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                        ("reservation.editing.applied", anon_id, str(conversation_id), "chatbot", json.dumps({"reserva_id": rid, "field": fld, "value": str(parsed)}))
                    )
                    # limpiar el flag __editing_reserva_id guardando None (podrías optar por mantenerlo si quieres más cambios)
                    try:
                        await db.a_execute(
                            "DELETE FROM dbo.Eventos WHERE ConversationId = ? AND EventType = ? AND Payload LIKE ?",
                            (str(conversation_id), "reservation.partial", '%"__editing_reserva_id"%')
                        )
                    except Exception:
                        logger.exception("failed to clear __editing_reserva_id partials after apply")


                    # traer la reserva actualizada para devolver summary
                    rowu = await db.a_fetchone("SELECT Id, UsuarioId, NombreReserva, NumComensales, ExperienciaId, Restricciones, FechaHora, DNI, Telefono FROM dbo.Reservas WHERE Id = ?", (rid,))
                    if rowu:
                        _, usuario_id_db, nombre_db, num_db, exp_db, restr_db, fecha_db, dni_db, tel_db = rowu
                        reservation_obj = {
                            "reserva_id": int(rowu[0]),
                            "id": int(rowu[0]),
                            "experiencia_id": int(exp_db) if exp_db is not None else None,
                            "fecha_hora": fecha_db and (fecha_db.isoformat() if hasattr(fecha_db, "isoformat") else str(fecha_db)),
                            "num_comensales": int(num_db) if num_db is not None else None,
                            "nombre_reserva": nombre_db,
                            "telefono": tel_db,
                            "dni": dni_db,
                            "restricciones": restr_db
                        }
                    else:
                        reservation_obj = {"reserva_id": rid, "id": rid}

                    return {
                        "conversation_id": str(conversation_id),
                        "messages": [
                            {"type": "text", "text": "He actualizado la reserva. Aquí tienes el resumen actualizado:"},
                            {"type": "summary", "reservation": reservation_obj},
                            {"type": "action", "action": "proceed_to_reserva"}
                        ]
                    }
                except Exception:
                    logger.exception("failed applying edit to reservation")
                    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Error aplicando el cambio a la reserva."}]}

        # si no detectamos el campo: pedir aclaración
        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "¿Qué dato de la reserva quieres cambiar? (fecha/hora, comensales, nombre, teléfono, DNI, restricciones, experiencia)"}]}


    if message:
        # 1) intentar extraer explicitamente patrones como "(id 1)" "id 1" "experiencia 1"
        found_exp = None
        try:
            m = re.search(r"\(id[:\s]*([0-9]{1,6})\)", message, flags=re.I)
            if not m:
                m = re.search(r"\bid[:\s]*([0-9]{1,6})\b", message, flags=re.I)
            if not m:
                m = re.search(r"experienc\w*\s*[:#]?\s*([0-9]{1,6})", message, flags=re.I)
            if not m:
                # fallback: small numeric token inside parentheses
                m = re.search(r"\(([0-9]{1,6})\)", message)
            if m:
                found_exp = int(m.group(1))
        except Exception:
            found_exp = None

        if found_exp:
            try:
                await save_reservation_partial(str(conversation_id), anon_id, "experiencia_id", found_exp)
            except Exception:
                logger.exception("failed saving exper id partial")
            return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"He seleccionado la experiencia (ID {found_exp}). ¿Qué fecha y hora prefieres? (Ej: 2025-11-02T19:30)"}]}

        # 2) Si el mensaje es únicamente un número, decidir su uso según contexto / DB
        merged = await get_merged_reservation(str(conversation_id))
        required = ["experiencia_id", "fecha_hora", "num_comensales", "nombre_reserva", "telefono", "restricciones"]

        if message.strip().isdigit():
            val = int(message.strip())

            # A) Si falta experiencia_id, verificar que el id existe en Experiencias activas
            if not merged.get("experiencia_id"):
                try:
                    row = await db.a_fetchone("SELECT Id FROM dbo.Experiencias WHERE Id = ? AND Activa = 1", (val,))
                    if row:
                        try:
                            await save_reservation_partial(str(conversation_id), anon_id, "experiencia_id", val)
                        except Exception:
                            logger.exception("failed saving exper id partial (numeric fallback)")
                        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"He seleccionado la experiencia (ID {val}). ¿Qué fecha y hora prefieres? (Ej: 2025-11-02T19:30)"}]}
                except Exception:
                    logger.exception("error checking experiencia existence for id %s", val)
                    # si falla la verificación de BD, no asumimos nada y seguimos

            # B) Si falta telefono y el número parece un teléfono (>=7 dígitos) -> guardarlo como telefono
            if not merged.get("telefono") and len(message.strip()) >= 7:
                try:
                    await save_reservation_partial(str(conversation_id), anon_id, "telefono", message.strip())
                except Exception:
                    logger.exception("failed saving telefono from numeric message")
                # refrescar merged y preguntar siguiente campo pendiente
                merged = await get_merged_reservation(str(conversation_id))
                missing = [f for f in required if not merged.get(f)]
                nf = missing[0] if missing else None
                question_text_map = {
                    "experiencia_id": "¿Qué experiencia te interesa? Responde con el ID o el nombre.",
                    "fecha_hora": "¿Qué fecha y hora prefieres? (formato sugerido: YYYY-MM-DDTHH:MM o DD/MM/YYYY)",
                    "num_comensales": "¿Cuántos comensales serán?",
                    "nombre_reserva": "¿A nombre de quién va la reserva?"
                }
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"He guardado 'telefono'. {question_text_map.get(nf, '¿Puedes darme ese dato?') }"}]}

            # C) Si falta num_comensales y el número es razonable -> guardarlo como num_comensales
            if not merged.get("num_comensales") and 1 <= val <= 200:
                try:
                    await save_reservation_partial(str(conversation_id), anon_id, "num_comensales", val)
                except Exception:
                    logger.exception("failed saving num_comensales from numeric message")
                merged = await get_merged_reservation(str(conversation_id))
                missing = [f for f in required if not merged.get(f)]
                nf = missing[0] if missing else None
                question_text_map = {
                    "experiencia_id": "¿Qué experiencia te interesa? Responde con el ID o el nombre.",
                    "fecha_hora": "¿Qué fecha y hora prefieres? (formato sugerido: YYYY-MM-DDTHH:MM o DD/MM/YYYY)",
                    "nombre_reserva": "¿A nombre de quién va la reserva?",
                    "telefono": "¿Cuál es tu teléfono de contacto?"
                }
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"He guardado 'num_comensales'. {question_text_map.get(nf, '¿Puedes darme ese dato?') }"}]}

            # Si no encaja en los casos anteriores no asumimos nada (evita errores)
            # dejamos que el flujo normal lo maneje (LLM / parseo libre)

        # 3) Si hay campos pendientes, intentar inferir el siguiente campo desde el texto libre
        merged = await get_merged_reservation(str(conversation_id))
        missing = [f for f in required if not merged.get(f)]
        if missing:
            next_field = missing[0]
            try:
                parsed_val = try_parse_field_from_text(next_field, message)
            except Exception:
                logger.exception("error parsing field %s from text %s", next_field, message)
                parsed_val = None

            if parsed_val is not None:
                try:
                    await save_reservation_partial(str(conversation_id), anon_id, next_field, parsed_val)
                except Exception:
                    logger.exception("failed to save parsed partial from free text")

                merged = await get_merged_reservation(str(conversation_id))
                missing = [f for f in required if not merged.get(f)]
                if missing:
                    nf = missing[0]
                    question_text_map = {
                        "experiencia_id": "¿Qué experiencia te interesa? Responde con el ID o el nombre.",
                        "fecha_hora": "¿Qué fecha y hora prefieres? (formato sugerido: YYYY-MM-DDTHH:MM o DD/MM/YYYY)",
                        "num_comensales": "¿Cuántos comensales serán?",
                        "nombre_reserva": "¿A nombre de quién va la reserva?",
                        "telefono": "¿Cuál es tu teléfono de contacto?"
                    }
                    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"He guardado '{next_field}'. {question_text_map.get(nf, '¿Puedes darme ese dato?')}"}]}
                else:
                    # todos los campos completos -> crear reserva provisional (insert robusto + devolver reserva_id)
                    try:
                        result = await create_provisional_reservation(merged, user_id, anon_id, str(conversation_id))
                        reserva_id = result["reserva_id"]
                        reservation_obj = result["reservation_obj"]
                    except ValueError as ve:
                        logger.warning("validation error creating provisional reservation: %s", ve)
                        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": str(ve)}]}
                    except Exception as e:
                        logger.exception("creating provisional reservation failed (free-text path). merged=%s", merged)
                        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Error creando la reserva provisional."}]}

                    return {
                        "conversation_id": str(conversation_id),
                        "messages": [
                            {"type": "text", "text": "He creado la reserva provisional. Aquí tienes el resumen:"},
                            {"type": "summary", "reservation": reservation_obj},
                            {"type": "action", "action": "proceed_to_reserva"}
                        ]
                    }
        # 4) Si no pudimos inferir nada util de la respuesta, pedir el siguiente campo esperado (sin form)
        nf = next_field if 'next_field' in locals() else (missing[0] if missing else None)
        if nf:
            question_text_map = {
                "experiencia_id": "¿Qué experiencia te interesa? Responde con el ID o el nombre.",
                "fecha_hora": "¿Qué fecha y hora prefieres? (formato sugerido: YYYY-MM-DDTHH:MM o DD/MM/YYYY)",
                "num_comensales": "¿Cuántos comensales serán?",
                "nombre_reserva": "¿A nombre de quién va la reserva?",
                "telefono": "¿Cuál es tu teléfono de contacto?"
            }
            return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": question_text_map.get(nf, "¿Puedes darme ese dato?")}]}

        # 5) Si no hay campos pendientes o nada aplicable, seguimos con LLM / heurística de recomendaciones
        profile_ctx = await get_profile_summary(anon_id, user_id)
        merged = await get_merged_reservation(str(conversation_id))
        try:
            rows = await db.a_fetchall("SELECT Id, Nombre, Precio FROM dbo.Experiencias WHERE Activa = 1 ORDER BY Nombre")
            experiencias = [{"id": int(r[0]), "nombre": r[1], "precio": float(r[2] or 0)} for r in rows]
        except Exception:
            experiencias = []

        llm_prompt = await build_llm_prompt(profile_ctx, merged, experiencias, message)
        result = await safe_gemini_chat(llm_prompt, timeout_sec=15.0)
        if result is None:
            try:
                recs = await quick_recommendations_by_profile(anon_id, user_id)
                if recs:
                    lines = [f"- {r['nombre']} (ID: {r['id']})" for r in recs]
                    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Lo siento, el servicio de generación tardó. Mientras tanto, mira estas recomendaciones rápidas:"}, {"type": "text", "text": "\n".join(lines)}]}
            except Exception:
                logger.exception("fallback quick recs failed")
            return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Perdón, la IA tardó demasiado. ¿Quieres que muestre opciones rápidas mientras tanto?"}]}

        # registrar mensaje recibido
        try:
            await db.a_execute(
                "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                ("conversation.message", anon_id, str(conversation_id), "fastapi-chatbot", json.dumps({"message": message}) )
            )
        except Exception:
            logger.exception("failed to log conversation.message")

        # intentar extraer JSON estructurado del LLM
        parsed = extract_json_from_text(result)
        if parsed:
            t = parsed.get("type")
            if t == "text":
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": parsed.get("text", "")}]}
            elif t == "form":
                return {"conversation_id": str(conversation_id), "messages": [parsed]}
            elif t == "experiences":
                items = parsed.get("items") or experiencias
                return {"conversation_id": str(conversation_id), "messages": [{"type": "experiences", "items": items}]}
            elif t == "action":
                return {"conversation_id": str(conversation_id), "messages": [{"type": "action", "action": parsed.get("action")}]}
            elif t == "summary":
                summary_res = parsed.get("reservation") or {}
                return {"conversation_id": str(conversation_id), "messages": [{"type": "summary", "reservation": summary_res}]}
            else:
                text = parsed.get("text") or result
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": text}]}
        else:
            logger.warning("LLM response missing structured JSON; returning raw text")
            return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": result or "Lo siento, no pude generar respuesta estructurada."}]}

    # si no vino message ni otro tipo de payload conocido
    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "No action."}]}

