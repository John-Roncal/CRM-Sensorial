// wwwroot/js/chat.js
(function () {
    const form = document.getElementById('chatForm');
    const input = document.getElementById('pregunta');
    const chatWindow = document.getElementById('chatWindow');
    const draftStatus = document.getElementById('draftStatus');
    const resumenContainer = document.getElementById('resumenContainer');
    const sendBtn = document.getElementById('sendBtn');

    window.__lastDraftForModal = window.__lastDraftForModal || null;

    function getCsrfToken() {
        const m = document.querySelector('meta[name="csrf-token"]');
        return m ? m.getAttribute('content') : null;
    }

    function applyCsrfHeaders(headers) {
        const token = getCsrfToken();
        if (token) headers['RequestVerificationToken'] = token;
    }

    async function parseJsonSafe(res) {
        const ct = (res.headers.get('content-type') || '').toLowerCase();
        if (ct.includes('application/json')) return await res.json().catch(() => null);
        const txt = await res.text().catch(() => '');
        try { return JSON.parse(txt); } catch { return null; }
    }

    function sanitizeHtml(s) {
        if (!s) return '';
        return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    function addMessage(user, text) {
        const div = document.createElement('div');
        div.className = 'msg ' + (user === 'Tú' ? 'user' : 'bot');

        const meta = document.createElement('div');
        meta.className = 'meta';
        meta.innerText = `${user} • ${new Date().toLocaleTimeString()}`;

        const body = document.createElement('div');
        body.className = 'body';
        body.innerHTML = sanitizeHtml(text).replace(/\n/g, "<br/>");

        div.appendChild(meta);
        div.appendChild(body);
        chatWindow.appendChild(div);
        chatWindow.scrollTop = chatWindow.scrollHeight;
        return div;
    }

    // Normalización: NO convertir "gluten" por defecto.
    function normalizeAndDedupeRestrictions(raw) {
        if (!raw) return null;
        const parts = raw.split(/[,;\/\|\.]|(?:\s+y\s+)|(?:\s+or\s+)/i)
            .map(p => p.trim().toLowerCase())
            .filter(Boolean)
            .map(p => p.replace(/\s+/g, ' ').trim());

        const uniq = [];
        parts.forEach(p => {
            let token = p.replace(/(^no me gusta mucho |^no me gusta )/, 'no me gusta ');
            // ya no forzamos 'gluten' a 'contiene gluten' - el backend debe registrar explícitos
            if (!uniq.includes(token)) uniq.push(token);
        });

        if (uniq.length === 0) return null;
        return uniq.join('; ');
    }

    function updateDraftPanel(draft) {
        if (!draft || Object.keys(draft).length === 0) {
            draftStatus.innerHTML = '<div class="small text-muted">Aquí verás el progreso: experiencia, personas, restricciones, día y hora.</div>';
            return;
        }

        const experienciaText = draft.experienciaNombre ? sanitizeHtml(draft.experienciaNombre) : (draft.experienciaId ? ('ID ' + sanitizeHtml(String(draft.experienciaId))) : '—');
        const personasText = draft.personas ? sanitizeHtml(String(draft.personas)) : '—';
        const diaText = draft.dia ? sanitizeHtml(draft.dia) : '—';
        const horaText = draft.hora ? sanitizeHtml(draft.hora) : '—';

        const restrRaw = draft.restricciones || draft.restriction || draft.restriccionesText || '';
        const restrText = normalizeAndDedupeRestrictions(restrRaw) || '—';

        const pasoText = draft.step ? sanitizeHtml(draft.step) : '—';

        const clienteNombre = draft.nombreUsuario || draft.clienteNombre || draft.nombre || null;
        const clienteDni = draft.dni || draft.clienteDni || null;
        const clienteTelefono = draft.telefono || draft.clienteTelefono || null;

        let html = `
            <div class="draft-field"><strong>Paso:</strong> ${pasoText}</div>
            <div class="draft-field"><strong>Experiencia:</strong> ${experienciaText}</div>
            <div class="draft-field"><strong>Personas:</strong> ${personasText}</div>
            <div class="draft-field"><strong>Día:</strong> ${diaText}</div>
            <div class="draft-field"><strong>Hora:</strong> ${horaText}</div>
            <div class="draft-field"><strong>Restricciones:</strong> ${sanitizeHtml(restrText)}</div>
        `;

        if (clienteNombre) html += `<div class="draft-field"><strong>Nombre:</strong> ${sanitizeHtml(clienteNombre)}</div>`;
        if (clienteDni) html += `<div class="draft-field"><strong>DNI:</strong> ${sanitizeHtml(clienteDni)}</div>`;
        if (clienteTelefono) html += `<div class="draft-field"><strong>Teléfono:</strong> ${sanitizeHtml(clienteTelefono)}</div>`;

        draftStatus.innerHTML = html;
    }

    // showResumenModal: solo muestra una instancia a la vez
    function showResumenModal(text, reservaId, draft, summaryObj) {
        // evitar abrir varias veces
        if (!resumenContainer) return;
        resumenContainer.innerHTML = '';

        const backdrop = document.createElement('div');
        backdrop.className = 'modal-backdrop';

        const card = document.createElement('div');
        card.className = 'modal-card';

        const body = document.createElement('div');
        body.innerHTML = `<div style="white-space:pre-wrap;">${sanitizeHtml(text)}</div>`;

        const details = document.createElement('div');
        details.className = 'small text-muted';
        details.style.marginTop = '10px';

        const restrText = normalizeAndDedupeRestrictions(draft?.restricciones || draft?.restriction || '') || '—';
        const clienteNombre = draft?.nombreUsuario || draft?.clienteNombre || '—';
        const clienteDni = draft?.dni || '—';
        const clienteTelefono = draft?.telefono || '—';

        let priceHtml = '';
        if (summaryObj) {
            try {
                const unit = summaryObj.unitPrice != null ? Number(summaryObj.unitPrice) : null;
                const tot = summaryObj.total != null ? Number(summaryObj.total) : null;
                priceHtml = `
                    <div><strong>Precio por persona:</strong> ${unit != null ? unit.toLocaleString('es-PE', { style: 'currency', currency: 'PEN' }) : '—'}</div>
                    <div><strong>Total:</strong> ${tot != null ? tot.toLocaleString('es-PE', { style: 'currency', currency: 'PEN' }) : '—'}</div>
                `;
            } catch (e) {
                priceHtml = '';
            }
        }

        details.innerHTML = `
            <div><strong>ID:</strong> ${reservaId ?? '—'}</div>
            <div><strong>Experiencia:</strong> ${sanitizeHtml(draft?.experienciaNombre || (draft?.experienciaId ? String(draft.experienciaId) : '—'))}</div>
            <div><strong>Día:</strong> ${sanitizeHtml(draft?.dia ?? '—')}</div>
            <div><strong>Hora:</strong> ${sanitizeHtml(draft?.hora ?? '—')}</div>
            <div><strong>Personas:</strong> ${sanitizeHtml(String(draft?.personas ?? '—'))}</div>
            <div><strong>Restricciones:</strong> ${sanitizeHtml(restrText ?? '—')}</div>
            <div><strong>Nombre reserva:</strong> ${sanitizeHtml(clienteNombre)}</div>
            <div><strong>DNI:</strong> ${sanitizeHtml(clienteDni)}</div>
            <div><strong>Teléfono:</strong> ${sanitizeHtml(clienteTelefono)}</div>
            ${priceHtml}
        `;

        const actions = document.createElement('div');
        actions.className = 'modal-actions';

        const btnClose = document.createElement('button');
        btnClose.className = 'btn';
        btnClose.innerText = 'Cerrar';
        btnClose.onclick = () => { resumenContainer.innerHTML = ''; };

        const btnNew = document.createElement('button');
        btnNew.className = 'btn';
        btnNew.innerText = 'Nueva reserva';
        btnNew.onclick = () => { location.reload(); };

        const btnMy = document.createElement('a');
        btnMy.className = 'btn';
        btnMy.style.textDecoration = 'none';
        btnMy.href = '/Cliente';
        btnMy.innerText = 'Ir a mis reservas';

        const btnConfirm = document.createElement('button');
        btnConfirm.className = 'btn btn-primary';
        btnConfirm.innerText = 'Confirmar reserva';
        btnConfirm.onclick = async () => {
            btnConfirm.disabled = true;
            btnConfirm.innerText = 'Confirmando...';
            window.__lastDraftForModal = draft || window.__lastDraftForModal;
            await confirmReservation(window.__lastDraftForModal);
            btnConfirm.disabled = false;
            btnConfirm.innerText = 'Confirmar reserva';
        };

        const btnSavePrefs = document.createElement('button');
        btnSavePrefs.className = 'btn';
        btnSavePrefs.innerText = 'Guardar preferencias';
        btnSavePrefs.onclick = () => savePreferencesFromDraft(actions);

        const btnEdit = document.createElement('button');
        btnEdit.className = 'btn';
        btnEdit.innerText = 'Editar';
        btnEdit.onclick = () => { resumenContainer.innerHTML = ''; input.focus(); };

        actions.appendChild(btnClose);
        actions.appendChild(btnNew);
        actions.appendChild(btnMy);
        actions.appendChild(btnConfirm);

        if (draft && (draft.restricciones || draft.preferenciasJson)) actions.appendChild(btnSavePrefs);

        actions.appendChild(btnEdit);

        card.appendChild(body);
        card.appendChild(details);
        card.appendChild(actions);
        backdrop.appendChild(card);
        resumenContainer.appendChild(backdrop);
        chatWindow.scrollTop = chatWindow.scrollHeight;
    }

    function renderActions(container, actions) {
        const existing = container.querySelector('.bot-actions');
        if (existing) existing.remove();
        if (!Array.isArray(actions) || actions.length === 0) return;

        const btnWrap = document.createElement('div');
        btnWrap.className = 'bot-actions';
        btnWrap.style.marginTop = '8px';
        actions.forEach(a => {
            const btn = document.createElement('button');
            btn.className = 'btn btn-sm me-2';
            btn.innerText = a.label || (a.action || 'Opción');
            btn.onclick = () => handleAction(a.action, a, container);
            btnWrap.appendChild(btn);
        });
        container.appendChild(btnWrap);
        chatWindow.scrollTop = chatWindow.scrollHeight;
    }

    async function handleAction(action, actionObj, container) {
        if (!action) return;
        switch (action) {
            case 'save_prefs':
                await savePreferencesFromDraft(container);
                break;
            case 'confirm':
                // asegurarse que use el último draft conocido
                await confirmReservation(window.__lastDraftForModal || null);
                break;
            case 'new':
                location.reload();
                break;
            case 'goto_client':
                window.location.href = '/Cliente';
                break;
            case 'edit':
                input.focus();
                break;
            default:
                if (actionObj && actionObj.payload) sendMessage(actionObj.payload);
                else sendMessage(action);
                break;
        }
    }


    async function savePreferencesFromDraft(container) {
        const indicator = document.createElement('span');
        indicator.className = 'small text-muted ms-2';
        indicator.innerText = 'Guardando preferencias...';
        container.appendChild(indicator);

        try {
            const headers = {};
            applyCsrfHeaders(headers);

            const res = await fetch('/Chat/GuardarPreferenciasDesdeDraft', { method: 'POST', headers, credentials: 'same-origin' });
            const json = await parseJsonSafe(res);
            if (res.ok) {
                indicator.innerText = (json && (json.message || json.ok)) ? (json.message || 'Preferencias guardadas.') : 'Preferencias guardadas.';
                if (json && json.draft) {
                    window.__lastDraftForModal = json.draft;
                    updateDraftPanel(json.draft);
                }
                setTimeout(() => indicator.remove(), 2000);
            } else {
                indicator.innerText = 'Error guardando preferencias: ' + ((json && json.message) ? json.message : res.statusText);
                setTimeout(() => indicator.remove(), 4000);
            }
        } catch (err) {
            indicator.innerText = 'Error de red al guardar preferencias.';
            setTimeout(() => indicator.remove(), 4000);
        }
    }

    async function clearDraftOnServer() {
        try {
            const headers = {};
            applyCsrfHeaders(headers);
            await fetch('/Chat/ClearDraft', { method: 'POST', headers, credentials: 'same-origin' });
        } catch (err) {
            console.warn('No se pudo limpiar draft en servidor', err);
        }
    }

    async function confirmReservation(draftArg) {
        try {
            const headers = {};
            applyCsrfHeaders(headers);

            const res = await fetch('/Chat/ConfirmReservation', { method: 'POST', headers, credentials: 'same-origin' });
            const json = await parseJsonSafe(res);

            if (res.ok && json && json.ok) {
                const message = json.message || 'Reserva confirmada.';
                const reservaId = json.reservaId || null;
                // Mostrar confirmación en el chat (evitar modal duplicado)
                addMessage('Bot', message);

                // limpiar draft en servidor y panel local
                await clearDraftOnServer();
                updateDraftPanel(null);
                window.__lastDraftForModal = null;

                // opcional: refrescar historial y mostrar mensaje inicial
                setTimeout(() => {
                    chatWindow.innerHTML = '';
                    const initialEl = document.getElementById('initialBotText');
                    if (initialEl && initialEl.textContent) addMessage('Bot', initialEl.textContent.trim());
                    else addMessage('Bot', '¡Hola! Si quieres hacer otra reserva, dime para qué día o persona.');
                }, 300);

                return;
            }

            if (json && json.needLogin) {
                const redirect = json.redirect || '/Registro';
                alert(json.message || 'Necesitas iniciar sesión para confirmar la reserva. Serás redirigido.');
                window.location.href = redirect;
                return;
            }

            const errMsg = (json && (json.message || json.error)) ? (json.message || json.error) : `Error confirmando reserva (status ${res.status})`;
            // Mostrar error en chat
            addMessage('Bot', '⚠️ ' + errMsg);
        } catch (err) {
            console.error('confirmReservation error', err);
            addMessage('Bot', '⚠️ Error de conexión al confirmar la reserva');
        }
    }


    async function sendMessage(text) {
        if (!text) return;
        addMessage('Tú', text);

        const provisional = addMessage('Bot', '⏳ Procesando...');
        const formData = new FormData();
        formData.append('userText', text);

        sendBtn.disabled = true;
        try {
            const headers = {};
            applyCsrfHeaders(headers);

            const res = await fetch('/Chat/SendMessage', { method: 'POST', body: formData, headers, credentials: 'same-origin' });
            const json = await parseJsonSafe(res);

            const bodyEl = provisional.querySelector('.body');
            if (res.ok) {
                // Mostrar texto del bot si viene (sin mezclar modal)
                const botText = (json && json.bot) ? json.bot : 'Sin respuesta.';
                if (bodyEl) bodyEl.innerHTML = sanitizeHtml(botText).replace(/\n/g, "<br/>");

                // Acciones (botones) si vienen
                if (json && (json.actions || json.buttons)) {
                    const actions = json.actions || json.buttons;
                    renderActions(provisional, actions);
                }

                // Actualizar draft (panel lateral)
                if (json && json.draft) {
                    window.__lastDraftForModal = json.draft;
                    updateDraftPanel(json.draft);
                }

                // Si el backend devuelve un summary PREVIO a confirmación (no guardada aún)
                // ahora lo ponemos como mensaje en el chat (no modal) — pero sólo si no está ya dentro de botText
                if (json && json.summary && !json.done) {
                    const summaryText = (json.summary && json.summary.text) ? json.summary.text : (json.bot || 'Resumen de la reserva');
                    const draftForModal = json.draft || window.__lastDraftForModal || {};
                    window.__lastDraftForModal = draftForModal;
                    // Evitar duplicar si botText ya contiene summaryText
                    if (!botText || !botText.includes(summaryText)) {
                        addMessage('Bot', summaryText);
                    }
                    // NO limpiamos el draft en servidor: el usuario debe confirmar.
                }

                // Si backend indica done=true (reserva completada en servidor)
                if (json && json.done) {
                    // Backend idealmente ya incluyó el resumen dentro de `bot`; simplemente limpiamos estado y actualizamos UI.
                    updateDraftPanel(null);
                    await clearDraftOnServer();
                    window.__lastDraftForModal = null;

                    // Si el backend no incluyó el resumen en `bot`, mostramos su `summary.text` como mensaje
                    if (json.summary && json.summary.text) {
                        const finalText = json.summary.text;
                        addMessage('Bot', finalText);
                    }

                    // Reiniciar conversación en UI (pequeña pausa para que el usuario lea)
                }
            } else {
                let errMsg = 'Error en la petición';
                if (json && (json.bot || json.error)) errMsg = json.bot || json.error;
                else if (res.status === 502) errMsg = 'Error contactando el servicio de IA.';
                else if (res.status === 499) errMsg = 'Petición cancelada por el cliente.';
                else if (res.status === 401 || res.status === 403) errMsg = 'No autorizado. Inicia sesión.';
                if (bodyEl) bodyEl.innerText = '⚠️ ' + errMsg;
            }
        } catch (err) {
            const bodyEl = provisional.querySelector('.body');
            if (bodyEl) bodyEl.innerText = '⚠️ Error de conexión';
            console.error('chat send error', err);
        } finally {
            sendBtn.disabled = false;
            chatWindow.scrollTop = chatWindow.scrollHeight;
        }
    }


    if (form) {
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            const txt = input.value.trim();
            if (!txt) return;
            input.value = '';
            sendMessage(txt);
        });
    }

    if (input) {
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                if (form) form.dispatchEvent(new Event('submit', { cancelable: true }));
            }
        });
    }

    (function tryInitDraftFromPage() {
        const el = document.getElementById('initialDraftJson');
        if (el && el.textContent) {
            try {
                const d = JSON.parse(el.textContent);
                updateDraftPanel(d);
                window.__lastDraftForModal = d;
                return;
            } catch { /* ignore malformed */ }
        }
        updateDraftPanel(null);
    })();

    (async function tryConfirmPendingAfterLogin() {
        try {
            const res = await fetch('/Chat/ConfirmPendingAfterLogin', { credentials: 'same-origin' });
            if (!res) return;
            if (res.status === 401 || res.status === 403) return;
            const json = await parseJsonSafe(res);
            if (json && json.ok && json.pending && json.draft) {
                window.__lastDraftForModal = json.draft;
                const draftForModal = json.draft;
                const summaryText = "Tienes una reserva pendiente guardada antes de iniciar sesión. ¿Deseas confirmarla ahora?";
                // Mostrar en chat y agregar botones para confirmar/editar
                const el = addMessage('Bot', summaryText);
                // acciones: confirmar / editar
                const actions = [
                    { label: 'Confirmar reserva', action: 'confirm' },
                    { label: 'Editar detalles', action: 'edit' }
                ];
                renderActions(el, actions);
            }
        } catch (err) {
            // no crítico
        }
    })();


})();
