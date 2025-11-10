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


async def get_user_reservations(user_id: Optional[int]) -> List[Dict[str, Any]]:
    if not user_id:
        return []
    try:
        rows = await db.a_fetchall(
            "SELECT Id, FechaHora, NumComensales, NombreReserva, Estado FROM dbo.Reservas WHERE UsuarioId = ? AND Estado IN ('pendiente', 'confirmada') ORDER BY FechaHora ASC",
            (user_id,)
        )
        return [
            {
                "id": r[0],
                "fecha_hora": r[1].strftime("%Y-%m-%dT%H:%M") if r[1] else "No especificada",
                "num_comensales": r[2],
                "nombre_reserva": r[3],
                "estado": r[4]
            }
            for r in rows
        ]
    except Exception:
        logger.exception("get_user_reservations failed")
        return []


async def update_reservation(reserva_id: int, data: dict, user_id: int) -> bool:
    if not reserva_id or not data or not user_id:
        return False

    # Mapeo de claves de la API a columnas de la base de datos
    FIELD_MAP = {
        "fecha_hora": "FechaHora",
        "num_comensales": "NumComensales",
        "nombre_reserva": "NombreReserva",
        "restricciones": "Restricciones",
        "telefono": "Telefono",
        "dni": "DNI",
        "experiencia_id": "ExperienciaId"
    }

    # Construir la consulta de actualización dinámicamente
    set_clauses = []
    params = []
    for key, value in data.items():
        if key in FIELD_MAP:
            set_clauses.append(f"{FIELD_MAP[key]} = ?")
            params.append(value)

    if not set_clauses:
        logger.warning("update_reservation: no valid fields to update")
        return False

    # Añadir el ID de la reserva y el ID de usuario a los parámetros para el WHERE
    params.append(reserva_id)
    params.append(user_id)

    query = f"UPDATE dbo.Reservas SET {', '.join(set_clauses)} WHERE Id = ? AND UsuarioId = ?"

    try:
        # Ejecutar la consulta y verificar si se actualizó alguna fila
        result = await db.a_execute(query, tuple(params))
        # a_execute devuelve el número de filas afectadas
        return result > 0
    except Exception:
        logger.exception("update_reservation failed for reserva_id %s", reserva_id)
        return False


async def get_user_context_by_name(name: str) -> Optional[Dict[str, Any]]:
    """
    Busca un usuario por nombre y devuelve su contexto (ID, perfil, historial de reservas).
    """
    if not name:
        return None
    try:
        # Asumimos que la tabla Usuarios tiene una columna 'Nombre'. Usamos LIKE para una búsqueda flexible.
        # Hacemos la búsqueda insensible a mayúsculas/minúsculas.
        user_row = await db.a_fetchone("SELECT Id, Nombre FROM dbo.Usuarios WHERE LOWER(Nombre) LIKE LOWER(?)", (f"%{name.strip()}%",))

        if not user_row:
            logger.info("get_user_context_by_name: No user found for name '%s'", name)
            return None

        user_id, user_name = user_row

        # 1. Obtener el perfil del usuario
        profile_row = await db.a_fetchone("SELECT Q1, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
        perfil = {"Q1": profile_row[0], "Q2": profile_row[1], "Q3": profile_row[2]} if profile_row else {}

        # 2. Obtener las últimas 3 reservas completadas para dar contexto
        reservas_rows = await db.a_fetchall(
            "SELECT TOP 3 Id, FechaHora, NumComensales, ExperienciaId FROM dbo.Reservas WHERE UsuarioId = ? AND Estado IN ('completada', 'confirmada') ORDER BY FechaHora DESC",
            (user_id,)
        )
        reservas_pasadas = [
            {"id": r[0], "fecha_hora": r[1].strftime("%Y-%m-%d"), "num_comensales": r[2], "experiencia_id": r[3]}
            for r in reservas_rows
        ]

        logger.info("get_user_context_by_name: Found user_id %d for name '%s'", user_id, name)
        return {
            "user_id": user_id,
            "nombre": user_name,
            "perfil": perfil,
            "reservas_pasadas": reservas_pasadas
        }

    except Exception:
        logger.exception("get_user_context_by_name failed for name=%s", name)
        return None


async def get_user_context_by_id(user_id: int) -> Optional[Dict[str, Any]]:
    """
    Busca un usuario por ID y devuelve su contexto (nombre, perfil, historial de reservas).
    """
    if not user_id:
        return None
    try:
        user_row = await db.a_fetchone("SELECT Nombre FROM dbo.Usuarios WHERE Id = ?", (user_id,))
        if not user_row:
            return None

        # 1. Obtener el perfil
        profile_row = await db.a_fetchone("SELECT Q1, Q2, Q3 FROM dbo.Perfiles WHERE UsuarioId = ?", (user_id,))
        perfil = {"Q1": profile_row[0], "Q2": profile_row[1], "Q3": profile_row[2]} if profile_row else {}

        # 2. Obtener historial de reservas
        reservas_rows = await db.a_fetchall(
            "SELECT TOP 3 Id, FechaHora, NumComensales, ExperienciaId FROM dbo.Reservas WHERE UsuarioId = ? AND Estado IN ('completada', 'confirmada') ORDER BY FechaHora DESC",
            (user_id,)
        )
        reservas_pasadas = [
            {"id": r[0], "fecha_hora": r[1].strftime("%Y-%m-%d"), "num_comensales": r[2], "experiencia_id": r[3]}
            for r in reservas_rows
        ]

        return {
            "user_id": user_id,
            "nombre": user_row[0],
            "perfil": perfil,
            "reservas_pasadas": reservas_pasadas
        }
    except Exception:
        logger.exception("get_user_context_by_id failed for user_id=%s", user_id)
        return None


async def build_llm_prompt(user_context: Optional[Dict[str, Any]], merged_reservation: dict, experiencias: list, user_message: str) -> str:
    system = (
        "Eres 'Amigo Central', el asistente virtual del restaurante Central. "
        "Tu misión es ayudar a los clientes a explorar nuestras experiencias culinarias y a realizar o modificar reservas de una manera cálida, amigable y eficiente. "
        "Habla siempre en español, con un tono cercano pero profesional. Guía al usuario paso a paso."
        "\nIMPORTANTE: Tu respuesta SIEMPRE debe incluir un bloque de código JSON al final, dentro de ```json ... ```."
    )

    instructions = (
        "Instrucciones de conversación y JSON:\n"
        "1. PERSONALIZACIÓN:\n"
        "- Si tienes el contexto de un usuario existente (historial, preferencias), ¡ÚSALO! Dale una bienvenida personalizada y hazle recomendaciones basadas en sus visitas anteriores.\n"
        "- Ejemplo: '¡Hola de nuevo, [Nombre]! Veo que la última vez disfrutaste de la experiencia [X]. ¿Te gustaría probar [Y], que es similar, o prefieres algo nuevo?'\n"
        "\n2. Flujo de NUEVA RESERVA:\n"
        "- Si faltan datos para una nueva reserva, pide el siguiente dato en este orden: experiencia -> fecha/hora -> número de personas -> nombre -> teléfono -> restricciones.\n"
        "- Para pedir un dato, responde con una pregunta amigable y un JSON como: ```json {\"type\":\"form\",\"field\":\"fecha_hora\",\"label\":\"¿Para qué fecha y hora sería tu reserva?\"} ```\n"
        "- Una vez que tengas TODOS los datos, muestra un resumen final y OBLIGATORIAMENTE incluye esta acción: ```json {\"type\":\"action\",\"action\":\"create_provisional_reservation\"} ```\n"
        "\n3. Flujo de EDICIÓN DE RESERVA:\n"
        "- Si el usuario quiere 'cambiar' o 'editar' una reserva, usa el contexto de sus reservas activas para identificar cuál quiere modificar. Si solo hay una, asume que es esa.\n"
        "- Una vez que tengas el ID de la reserva y el nuevo valor, genera la acción para editarla. Ejemplo: ```json {\"type\":\"action\",\"action\":\"edit_reservation\",\"payload\":{\"reserva_id\":123,\"fecha_hora\":\"2025-12-24T20:00\"}} ```\n"
        "\n4. Reglas Generales:\n"
        "- MUY IMPORTANTE: Cuando manejes fechas y horas, DEBES normalizarlas al formato YYYY-MM-DDTHH:MM.\n"
        "- Para respuestas de texto simples, usa: ```json {\"type\":\"text\",\"text\":\"Tu respuesta aquí.\"} ```"
    )

    # Construcción del contexto para la IA
    context_parts = []
    if user_context:
        context_parts.append(f"Contexto del Usuario: Nombre: {user_context.get('nombre')}")
        if user_context.get('perfil'):
            context_parts.append(f"Preferencias: {json.dumps(user_context.get('perfil'))}")
        if user_context.get('reservas_pasadas'):
            context_parts.append(f"Historial de Reservas: {json.dumps(user_context.get('reservas_pasadas'))}")
        if user_context.get('reservas_activas'):
             context_parts.append(f"Reservas Activas: {json.dumps(user_context.get('reservas_activas'))}")

    if merged_reservation:
        context_parts.append(f"Datos para nueva reserva en progreso: {json.dumps(merged_reservation)}")

    if experiencias:
        exper_summary = ", ".join([f"{e['id']}-{e['nombre']}" for e in experiencias])
        context_parts.append(f"Experiencias disponibles (id-nombre): {exper_summary}")

    context_str = "\n".join(context_parts)
    prompt = "\n\n".join([system, instructions, context_str, f"Usuario: {user_message}\nAsistente:"])
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

    # El flujo de inicio ahora es siempre el mismo: pedir el nombre para personalizar.
    # Toda la lógica de si el perfil está completo o no se moverá al endpoint /message,
    # después de que tengamos el nombre del usuario.

    # Aseguramos que la sesión anónima exista y la cookie esté configurada.
    if not anon_id:
        new_anon = uuid4()
        try:
            await db.a_execute(
                "INSERT INTO dbo.AnonSessions (AnonId, Estado, CreadoEn) VALUES (?,?,GETUTCDATE())",
                (str(new_anon), "activo")
            )
            anon_id = new_anon
        except Exception:
            logger.exception("failed to insert anon session for new anon")
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

    # Registramos el inicio de la conversación
    try:
        await db.a_execute(
                "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
                 ("conversation.started", str(anon_id), str(conversation_id), "fastapi-chatbot", "{}")
        )
    except Exception:
        logger.exception("failed to log conversation.started event")

    # Enviamos el mensaje inicial para preguntar el nombre.
    return {
        "conversation_id": str(conversation_id),
        "anon_id": str(anon_id),
        "messages": [
            {
                "type": "form",
                "field": "user_name_lookup",
                "label": "¡Hola! Soy Amigo Central. Para darte una experiencia más personalizada, ¿podrías decirme tu nombre completo?"
            }
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

    message = getattr(payload, "message", None) or getattr(payload, "Message", None)

    # --- Lógica de BÚSQUEDA DE USUARIO POR NOMBRE ---
    # Este bloque se activa solo al principio de la conversación, cuando el frontend envía el nombre.
    if message and payload.reservation_field == "user_name_lookup":
        user_context = await get_user_context_by_name(message)

        if user_context:
            # Usuario encontrado: Personalizamos la bienvenida y guardamos su ID para el resto de la conversación.
            user_id = user_context.get("user_id")
            await save_reservation_partial(str(conversation_id), anon_id, "user_id", user_id)

            # Construimos un mensaje de bienvenida personalizado
            welcome_back_msg = f"¡Hola de nuevo, {user_context.get('nombre')}! Qué bueno verte por aquí."
            # Aquí podrías añadir más contexto, como "Veo que tu última visita fue..."

            return {
                "conversation_id": str(conversation_id),
                "messages": [
                    {"type": "text", "text": welcome_back_msg},
                    {"type": "text", "text": "¿Qué te gustaría hacer hoy? Puedo ayudarte a reservar una mesa o a explorar nuestras experiencias."}
                ]
            }
        else:
            # Usuario no encontrado: Iniciamos el flujo de perfilado para nuevos clientes.
            return {
                "conversation_id": str(conversation_id),
                "messages": [
                    {"type": "text", "text": "Parece que es tu primera vez por aquí. ¡Bienvenido!"},
                    {"type": "text", "text": "Para conocerte mejor, te haré 3 preguntas rápidas que nos ayudarán a darte las mejores recomendaciones."},
                    {"type": "action", "action": "redirect_to_tres_preguntas"}
                ]
            }

    # --- Lógica de perfilado (Manejo de QKey/QAnswer) ---
    if qkey and qanswer:
        # Validar qkey
        if qkey not in ("Q1", "Q2", "Q3"):
            raise HTTPException(status_code=400, detail=f"Invalid qkey: {qkey}")

        # Guardar la respuesta del perfil
        try:
            # Determinar si se usa user_id o anon_id
            identifier_col = "UsuarioId" if user_id else "AnonId"
            identifier_val = user_id if user_id else anon_id

            if not identifier_val:
                 raise HTTPException(status_code=400, detail="Cannot save profile answer without user_id or anon_id")

            # Comprobar si ya existe un perfil
            existing_profile = await db.a_fetchone(f"SELECT PerfilId FROM dbo.Perfiles WHERE {identifier_col} = ?", (identifier_val,))

            if existing_profile:
                # Actualizar perfil existente
                await db.a_execute(f"UPDATE dbo.Perfiles SET {qkey} = ? WHERE {identifier_col} = ?", (qanswer, identifier_val))
            else:
                # Crear nuevo perfil
                await db.a_execute(f"INSERT INTO dbo.Perfiles ({identifier_col}, {qkey}) VALUES (?, ?)", (identifier_val, qanswer))

            # Comprobar si el perfil ya está completo
            if qkey == "Q3":
                # Marcar como completo y proceder
                await db.a_execute(f"UPDATE dbo.Perfiles SET EstadoPerfilCompleto = 1 WHERE {identifier_col} = ?", (identifier_val,))
                return {"conversation_id": str(conversation_id), "messages": [{"type": "action", "action": "profile_complete_proceed_to_chat"}]}

        except Exception:
            logger.exception("Failed to save profile answer for qkey=%s", qkey)
            # No bloqueamos al usuario, solo logueamos el error y continuamos

    if not message:
        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Por favor, envía un mensaje."}]}

    # A partir de aquí, toda la lógica depende de Gemini para ser conversacional.

    # Primero, recuperamos el estado actual de la conversación
    merged = await get_merged_reservation(str(conversation_id))

    # Si ya hemos identificado al usuario en pasos anteriores, recuperamos su contexto completo.
    # El user_id se pudo haber obtenido del JWT, o guardado en la conversación tras la búsqueda por nombre.
    final_user_id = user_id or merged.get("user_id")
    user_context = None
    if final_user_id:
        user_context = await get_user_context_by_id(final_user_id)
        if user_context:
            # Añadimos también las reservas activas, que son importantes para el flujo de edición.
            user_context["reservas_activas"] = await get_user_reservations(final_user_id)

    try:
        rows = await db.a_fetchall("SELECT Id, Nombre, Precio FROM dbo.Experiencias WHERE Activa = 1 ORDER BY Nombre")
        experiencias = [{"id": int(r[0]), "nombre": r[1], "precio": float(r[2] or 0)} for r in rows]
    except Exception:
        experiencias = []

    llm_prompt = await build_llm_prompt(user_context, merged, experiencias, message)
    result = await safe_gemini_chat(llm_prompt, timeout_sec=15.0)

    if result is None:
        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Lo siento, la IA está tardando mucho en responder. Por favor, intenta de nuevo."}]}

    # Registramos el mensaje del usuario en los eventos
    try:
        await db.a_execute(
            "INSERT INTO dbo.Eventos (EventType, AnonId, ConversationId, SenderId, Payload) VALUES (?,?,?,?,?)",
            ("conversation.message", anon_id, str(conversation_id), "user", json.dumps({"message": message}))
        )
    except Exception:
        logger.exception("Fallo al registrar el evento del mensaje")

    # Extraemos el JSON de la respuesta de Gemini
    parsed = extract_json_from_text(result)

    # Preparamos la respuesta final para el frontend
    if parsed:
        # Si Gemini incluye datos de la reserva en su respuesta, los guardamos
        reservation_data = parsed.get("reservation")
        if isinstance(reservation_data, dict):
            for key, value in reservation_data.items():
                if value is not None:
                    await save_reservation_partial(str(conversation_id), anon_id, key, value)

        # Si Gemini nos pide crear la reserva, lo hacemos ahora
        if parsed.get("type") == "action" and parsed.get("action") == "create_provisional_reservation":
            try:
                # Obtenemos todos los datos guardados en la conversación
                merged_data = await get_merged_reservation(str(conversation_id))
                # Creamos la reserva provisional
                result = await create_provisional_reservation(merged_data, user_id, anon_id, str(conversation_id))

                # Devolvemos un resumen final al usuario
                return {
                    "conversation_id": str(conversation_id),
                    "messages": [
                        {"type": "text", "text": "¡Perfecto! He creado tu reserva provisional con los siguientes datos:"},
                        {"type": "summary", "reservation": result.get("reservation_obj", {})}
                    ]
                }
            except Exception as e:
                logger.exception("Error al crear la reserva provisional")
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"Lo siento, ha ocurrido un error al crear tu reserva: {e}"}]}

        # Si Gemini nos pide editar la reserva, lo hacemos
        if parsed.get("type") == "action" and parsed.get("action") == "edit_reservation":
            payload = parsed.get("payload", {})
            reserva_id = payload.pop("reserva_id", None)

            if not user_id:
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "Lo siento, necesitas haber iniciado sesión para poder editar una reserva."}]}
            if not reserva_id:
                 return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": "No he entendido qué reserva quieres modificar. ¿Podrías indicarme su número de referencia?"}]}

            try:
                success = await update_reservation(reserva_id, payload, user_id)
                if success:
                    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"¡Listo! He actualizado tu reserva con ID {reserva_id}."}]}
                else:
                    return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"No pude actualizar la reserva {reserva_id}. Es posible que no te pertenezca o que ya no esté activa."}]}
            except Exception as e:
                logger.exception("Error al editar la reserva")
                return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": f"Lo siento, ha ocurrido un error al editar tu reserva: {e}"}]}

        # Devolvemos el mensaje, que puede ser texto, un formulario, etc.
        return {"conversation_id": str(conversation_id), "messages": [parsed]}
    else:
        # Si Gemini no devuelve un JSON, enviamos su respuesta como texto plano
        logger.warning("La respuesta de Gemini no contenía un JSON estructurado. Se devuelve como texto plano.")
        return {"conversation_id": str(conversation_id), "messages": [{"type": "text", "text": result or "No he podido procesar tu solicitud. ¿Podrías repetirla?"}]}

