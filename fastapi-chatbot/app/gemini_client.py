# app/gemini_client.py
import os
import google.generativeai as genai
from fastapi.concurrency import run_in_threadpool

# --- Configuración del cliente de Gemini ---
GEMINI_API_KEY = os.environ.get("GEMINI_API_KEY")

# Verifica que la API key esté disponible
if not GEMINI_API_KEY:
    raise ValueError("La variable de entorno GEMINI_API_KEY no ha sido configurada.")

# Configura el SDK de Google
genai.configure(api_key=GEMINI_API_KEY)

# --- Modelo Generativo ---
# Seleccionamos el modelo de Gemini que vamos a utilizar
# 'gemini-pro' es un modelo robusto y versátil ideal para chat.
model = genai.GenerativeModel('gemini-2.5-flash')

def sync_chat(prompt: str):
    """
    Función síncrona para enviar un prompt a Gemini y obtener la respuesta.
    Se ejecuta en un thread separado para no bloquear el event loop de FastAPI.
    """
    try:
        # Enviamos el prompt al modelo y obtenemos la respuesta
        response = model.generate_content(prompt)
        # Devolvemos el texto generado por el modelo
        return response.text.strip()
    except Exception as e:
        # Manejo de errores en caso de que la API falle
        print(f"Error al contactar la API de Gemini: {e}")
        return "Lo siento, estoy teniendo problemas para conectarme con el servicio de IA en este momento."

async def chat(prompt: str):
    """
    Función asíncrona que es llamada desde los endpoints de la API.
    Utiliza run_in_threadpool para ejecutar la función síncrona de forma no bloqueante.
    """
    return await run_in_threadpool(sync_chat, prompt)
