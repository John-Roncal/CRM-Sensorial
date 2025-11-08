# app/db.py
import os
import pyodbc
from fastapi.concurrency import run_in_threadpool
from typing import Any, List, Optional, Tuple

DSN = "DRIVER={ODBC Driver 17 for SQL Server};SERVER=localhost;DATABASE=DBCRMSensorialCentral;Trusted_Connection=Yes;TrustServerCertificate=Yes;"

def get_conn():
    # Ajusta timeout/charset si lo necesitas
    return pyodbc.connect(DSN, autocommit=False)

def fetchone(query: str, params: Tuple = ()):
    with get_conn() as conn:
        cur = conn.cursor()
        cur.execute(query, params)
        row = cur.fetchone()
        return row

def fetchall(query: str, params: Tuple = ()):
    with get_conn() as conn:
        cur = conn.cursor()
        cur.execute(query, params)
        rows = cur.fetchall()
        return rows

def execute(query: str, params: Tuple = ()):
    with get_conn() as conn:
        cur = conn.cursor()
        cur.execute(query, params)
        conn.commit()
        return cur.rowcount

# Async wrappers
async def a_fetchone(query: str, params: Tuple = ()):
    return await run_in_threadpool(fetchone, query, params)

async def a_fetchall(query: str, params: Tuple = ()):
    return await run_in_threadpool(fetchall, query, params)

async def a_execute(query: str, params: Tuple = ()):
    return await run_in_threadpool(execute, query, params)
