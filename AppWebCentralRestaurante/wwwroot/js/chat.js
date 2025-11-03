// wwwroot/js/chat.js
(() => {
    const CHAT_API = window.__CHAT_API || 'http://localhost:8000/chat';
    const RESERVAS_API = window.__RESERVAS_API || '/api/reservas';
    const chatWindow = document.getElementById('chatWindow');
    const chatInput = document.getElementById('chatInput');
    const btnSend = document.getElementById('btnSend');
    const btnOpenReserva = document.getElementById('btnOpenReserva');
    const statusBadge = document.getElementById('status-badge');

    const reservaModalEl = document.getElementById('reservaModal');
    const reservaModal = reservaModalEl ? new bootstrap.Modal(reservaModalEl) : null;
    const btnReservaCreate = document.getElementById('btnReservaCreate');

    let conversationId = null;
    let anonId = (() => { try { return localStorage.getItem('anon_id') || null; } catch (e) { return null; } })();
    let userId = window.__USER_ID || null;

    const renderedHashes = new Set();
    let awaitingField = null; // <-- si el servidor envía un "form", guardamos el campo esperado aquí


    console.log("JS detecta userId:", userId, "anonId:", anonId);

    function setStatus(text) { if (statusBadge) statusBadge.innerText = text; }

    function hashMessage(obj) {
        try { return btoa(unescape(encodeURIComponent(JSON.stringify(obj)))).slice(0, 64); }
        catch (e) { return String((obj && obj.text) || JSON.stringify(obj)).slice(0, 64); }
    }

    function appendMessage(sender, text, extraClass) {
        const h = hashMessage({ sender, text });
        if (renderedHashes.has(h)) return;
        renderedHashes.add(h);

        const wrap = document.createElement('div');
        wrap.className = 'mb-2 d-flex';
        const bubble = document.createElement('div');
        bubble.className = `p-2 rounded ${sender === 'user' ? 'bg-primary text-white ms-auto' : 'bg-light text-dark'} ${extraClass || ''}`;
        bubble.style.maxWidth = '85%';
        bubble.style.wordBreak = 'break-word';
        bubble.innerText = text;

        if (sender === 'user') wrap.style.justifyContent = 'flex-end';
        else wrap.style.justifyContent = 'flex-start';

        wrap.appendChild(bubble);
        if (chatWindow) { chatWindow.appendChild(wrap); chatWindow.scrollTop = chatWindow.scrollHeight; }
    }

    function appendChoice(question, options = []) {
        const h = hashMessage({ type: 'choice', question, options });
        if (renderedHashes.has(h)) return;
        renderedHashes.add(h);

        const wrapper = document.createElement('div');
        wrapper.className = 'mb-3';
        const q = document.createElement('div');
        q.className = 'mb-2 fw-semibold';
        q.innerText = question;
        wrapper.appendChild(q);

        const btnGroup = document.createElement('div');
        btnGroup.className = 'd-flex flex-wrap gap-2';

        options.forEach(opt => {
            const b = document.createElement('button');
            b.className = 'btn btn-outline-secondary btn-sm';
            b.type = 'button';
            b.innerText = opt;
            b.addEventListener('click', () => {
                let qkey = null;
                const qLower = (question || '').toLowerCase();
                if (qLower.includes('trae') || qLower.includes('qué te trae')) qkey = 'q1';
                else if (qLower.includes('con quién') || qLower.includes('con qu')) qkey = 'q2';
                else if (qLower.includes('estilo de cocina') || qLower.includes('cocina')) qkey = 'q3';
                if (qkey) sendChatMessage({ qKey: qkey, qAnswer: opt });
                else sendChatMessage({ message: opt });
            });
            btnGroup.appendChild(b);
        });

        wrapper.appendChild(btnGroup);
        if (chatWindow) { chatWindow.appendChild(wrapper); chatWindow.scrollTop = chatWindow.scrollHeight; }
    }

    function appendAction(actionObj) {
        let actionName = typeof actionObj === 'string' ? actionObj : actionObj.action;
        const label = (typeof actionObj === 'object' && actionObj.label) || (actionName === 'proceed_to_reserva' ? 'Ir a reservar' : actionName);

        const h = hashMessage({ type: 'action', actionName, url: actionObj && actionObj.url ? actionObj.url : '' });
        if (renderedHashes.has(h)) return;
        renderedHashes.add(h);

        const wrap = document.createElement('div');
        wrap.className = 'mb-2';
        const btn = document.createElement('button');
        btn.className = 'btn btn-sm btn-outline-primary';
        btn.innerText = label;
        btn.addEventListener('click', () => {
            if (actionName === 'proceed_to_reserva' || actionName === 'recommend') {
                if (reservaModal) reservaModal.show(); else window.location.href = '/Reservas';
            } else if (actionName === 'open_three_questions_form') {
                const url = (typeof actionObj === 'object' && actionObj.url) || '/Registro/TresPreguntas';
                window.location.href = url;
            } else {
                if (actionObj && actionObj.url) window.location.href = actionObj.url;
            }
        });
        wrap.appendChild(btn);
        if (chatWindow) { chatWindow.appendChild(wrap); chatWindow.scrollTop = chatWindow.scrollHeight; }
    }

    // Nota: ahora appendForm NO crea inputs: el chat pregunta en texto y guardamos awaitingField
    function appendForm(formObj) {
        if (!formObj || !formObj.field) return;
        const h = hashMessage(formObj);
        if (renderedHashes.has(h)) return;
        renderedHashes.add(h);

        // Mostrar como mensaje del bot (texto), y registrar el campo esperado
        const label = formObj.label || formObj.field || 'Por favor completa';
        appendMessage('bot', label);
        awaitingField = { field: formObj.field, input: formObj.input || 'text', placeholder: formObj.placeholder || null };
    }

    function appendExperiences(items = []) {
        if (!Array.isArray(items) || items.length === 0) return;
        const h = hashMessage({ type: 'experiences', items });
        if (renderedHashes.has(h)) return;
        renderedHashes.add(h);

        // Mostrar listado como texto para que sea legible
        const lines = items.map(it => `${it.id}. ${it.nombre || `Experiencia ${it.id}`} ${typeof it.precio !== 'undefined' ? `(Precio: ${Number(it.precio).toLocaleString()})` : ''}`);
        appendMessage('bot', 'Te recomiendo estas experiencias:\n' + lines.join('\n') + '\n\nResponde con el ID para seleccionar una, o haz clic en un botón.');

        // botones que envían explicitamente reservation_field/reservation_value (robusto)
        const wrapper = document.createElement('div');
        wrapper.className = 'mb-2 d-flex flex-wrap gap-2';
        items.forEach(it => {
            const b = document.createElement('button');
            b.className = 'btn btn-outline-primary btn-sm';
            b.type = 'button';
            b.innerText = `Seleccionar ${it.id}`;
            b.addEventListener('click', () => {
                appendMessage('user', String(it.id));
                sendChatMessage({ reservation_field: 'experiencia_id', reservation_value: Number(it.id) });
            });
            wrapper.appendChild(b);
        });
        if (chatWindow) { chatWindow.appendChild(wrapper); chatWindow.scrollTop = chatWindow.scrollHeight; }
    }

    function appendSummary(reservation) {
        const h = hashMessage({ type: 'summary', reservation });
        if (renderedHashes.has(h)) return;
        renderedHashes.add(h);

        const container = document.createElement('div');
        container.className = 'card mt-3';
        container.style.maxWidth = '85%';

        const nombre = reservation.nombre_reserva || reservation.nombre || 'N/A';
        const experiencia = reservation.experiencia_id || reservation.experiencia || 'N/A';
        const fecha = reservation.fecha_hora || reservation.fecha || 'N/A';
        const comensales = reservation.num_comensales || reservation.num_personas || 'N/A';
        const telefono = reservation.telefono || 'N/A';
        const id = reservation.reserva_id || reservation.id || null;
        const btnsId = `res-btns-${id || Math.random().toString(36).slice(2, 8)}`;

        container.innerHTML = `
        <div class="card-body">
            <h5 class="card-title">Resumen de reserva</h5>
            <p><strong>A nombre de:</strong> ${nombre}</p>
            <p><strong>Experiencia (ID):</strong> ${experiencia}</p>
            <p><strong>Fecha/hora:</strong> ${fecha}</p>
            <p><strong>Comensales:</strong> ${comensales}</p>
            <p><strong>Teléfono:</strong> ${telefono}</p>
            <div class="d-flex gap-2 mt-3" id="${btnsId}"></div>
        </div>
    `;

        // crear botones programáticamente (evita IDs duplicados y race conditions)
        const btnsWrap = container.querySelector(`#${btnsId}`);

        const btnConfirm = document.createElement('button');
        btnConfirm.className = 'btn btn-success btn-sm';
        btnConfirm.type = 'button';
        btnConfirm.textContent = 'Confirmar reserva';
        btnConfirm.addEventListener('click', () => {
            if (!id) { appendMessage('bot', 'ID de reserva no disponible.'); return; }
            confirmReserva(id, false);
        });

        const btnEdit = document.createElement('button');
        btnEdit.className = 'btn btn-secondary btn-sm';
        btnEdit.type = 'button';
        btnEdit.textContent = 'Editar';
        btnEdit.addEventListener('click', () => {
            if (!id) { appendMessage('bot', 'ID de reserva no disponible.'); return; }
            // Enviar señal al backend para iniciar edición por chat
            appendMessage('user', `Editar reserva ${id}`);
            sendChatMessage({ reservation_field: 'edit_reserva', reservation_value: id });
            // no abrir modal aquí
        });

        const btnCancel = document.createElement('button');
        btnCancel.className = 'btn btn-danger btn-sm';
        btnCancel.type = 'button';
        btnCancel.textContent = 'Cancelar';
        btnCancel.addEventListener('click', () => {
            if (!id) { appendMessage('bot', 'ID de reserva no disponible.'); return; }
            confirmReserva(id, 'cancelar');
        });

        btnsWrap.appendChild(btnConfirm);
        btnsWrap.appendChild(btnEdit);
        btnsWrap.appendChild(btnCancel);

        if (chatWindow) { chatWindow.appendChild(container); chatWindow.scrollTop = chatWindow.scrollHeight; }
    }


    // Mejor postJson con logging de errores para depuración (muestra status + body)
    async function postJson(url, body) {
        const headers = { 'Content-Type': 'application/json' };
        // Si hay token global, enviarlo
        if (window.__AUTH_TOKEN) headers['Authorization'] = 'Bearer ' + window.__AUTH_TOKEN;
        const resp = await fetch(url, {
            method: 'POST',
            headers,
            credentials: 'include',
            body: JSON.stringify(body)
        });
        const text = await resp.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = text; }
        if (!resp.ok) {
            console.error('POST error', { url, status: resp.status, bodySent: body, responseText: text });
            const msg = (data && (data.message || data.detail || data.error)) || text || `HTTP ${resp.status}`;
            throw new Error(msg);
        }
        return data;
    }

    function renderMessagesFromResponse(messages) {
        if (!messages || !Array.isArray(messages)) return;

        const hasProceed = messages.some(m => m && m.type === 'action' && m.action === 'proceed_to_reserva');
        const hasOpenThree = messages.some(m => m && m.type === 'action' && m.action === 'open_three_questions_form');

        if (hasOpenThree) {
            const openAction = messages.find(m => m && m.type === 'action' && m.action === 'open_three_questions_form');
            if (openAction && openAction.url) {
                window.location.href = openAction.url;
                return;
            }
        }

        messages.forEach(m => {
            if (!m) return;
            if (typeof m === 'string') appendMessage('bot', m);
            else if (m.type === 'text') appendMessage('bot', m.text);
            else if (m.type === 'choice') {
                if (hasProceed || hasOpenThree) return;
                appendChoice(m.question, m.options || []);
            }
            else if (m.type === 'action') appendAction(m);
            // NOTE: form => we now show a TEXT prompt and set awaitingField
            else if (m.type === 'form') {
                appendForm(m);
            }
            else if (m.type === 'experiences') {
                appendExperiences(m.items || m.experiences || []);
            }
            else if (m.type === 'summary') appendSummary(m.reservation || {});
            else appendMessage('bot', JSON.stringify(m));
        });
    }

    function extractExperienceIdFromText(text) {
        if (!text) return null;
        text = String(text);
        const patterns = [
            /\(id[:\s]*([0-9]{1,6})\)/i,
            /\bid[:\s]*([0-9]{1,6})\b/i,
            /experienc\w*\s*[:#]?\s*([0-9]{1,6})/i,
            /\(([0-9]{1,6})\)/,
            // last resort: single-number messages or trailing number; avoid accidental matches in long sentences
            /^\s*([0-9]{1,6})\s*$/
        ];
        for (let i = 0; i < patterns.length; i++) {
            const m = text.match(patterns[i]);
            if (m && m[1]) {
                const n = parseInt(m[1], 10);
                if (!isNaN(n)) {
                    return n;
                }
            }
        }
        return null;
    }

    async function startConversation() {
        setStatus('iniciando conversación...');
        try {
            const payload = {
                conversation_id: conversationId,
                anon_id: anonId,
                user_id: userId,
                locale: navigator.language || 'es'
            };

            const res = await postJson(`${CHAT_API}/start`, payload);

            conversationId = res?.conversation_id || res?.conversationId || conversationId;
            if (res?.anon_id || res?.anonId) {
                anonId = res.anon_id || res.anonId;
                try { localStorage.setItem('anon_id', anonId); } catch (e) { }
            }

            renderMessagesFromResponse(res.messages || []);
            setStatus('conectado');
        } catch (err) {
            console.error('startConversation error', err);
            setStatus('offline');
            appendMessage('bot', 'No puedo conectarme al servicio ahora. Intenta más tarde.');
        }
    }

    // Reemplazar la función sendChatMessage por esta versión
    async function sendChatMessage({ message = null, qKey = null, qAnswer = null, reservation_field = null, reservation_value = null }) {
        if (!conversationId) conversationId = (self.crypto && crypto.randomUUID) ? crypto.randomUUID() : (Date.now().toString());

        // Si el usuario escribió algo que contiene un ID de experiencia -> enviamos también ese ID COMO message
        if (message && !reservation_field && (reservation_value === null || typeof reservation_value === 'undefined') && !qAnswer) {
            const foundId = extractExperienceIdFromText(message);
            if (foundId) {
                appendMessage('user', message);
                setStatus('pensando...');

                // enviar message con el ID para que el backend lo procese por heurística, y además enviar reservation_field/value
                const payload = {
                    conversation_id: String(conversationId),
                    anon_id: anonId,
                    user_id: userId,
                    // enviamos message con el ID (clave): esto evita "No action." y permite que el backend lo reconozca
                    message: String(foundId),
                    qkey: null,
                    qanswer: null,
                    reservation_field: 'experiencia_id',
                    reservation_value: Number(foundId),

                    // variantes PascalCase por compatibilidad
                    ConversationId: String(conversationId),
                    AnonId: anonId,
                    UserId: userId,
                    Message: String(foundId),
                    QKey: null,
                    QAnswer: null,
                    ReservationField: 'experiencia_id',
                    ReservationValue: String(foundId)
                };

                try {
                    const res = await postJson(`${CHAT_API}/message`, payload);
                    conversationId = res?.conversation_id || conversationId;
                    if (res?.anon_id || res?.anonId) {
                        anonId = res.anon_id || res.anonId;
                        try { localStorage.setItem('anon_id', anonId); } catch (e) { }
                    }
                    renderMessagesFromResponse(res.messages || []);
                    setStatus('conectado');
                } catch (err) {
                    console.error('sendChatMessage (exp-id) error. payload:', payload, err);
                    appendMessage('bot', 'Error procesando tu selección.');
                    setStatus('error');
                }
                return;
            }
        }

        // echo UI
        if (reservation_value) {
            // botones/forms normalmente ya añadieron la burbuja del usuario
        } else if (qAnswer) appendMessage('user', qAnswer);
        else if (message) appendMessage('user', message);

        setStatus('pensando...');

        // construir payload siempre incluyendo message/qkey/qanswer (aunque vacíos)
        const payload = {
            conversation_id: conversationId ? String(conversationId) : null,
            anon_id: anonId,
            user_id: userId,
            message: (message != null ? message : ""),   // enviar "" si no hay texto
            qkey: (qKey != null ? qKey : null),
            qanswer: (qAnswer != null ? qAnswer : null),
            reservation_field: reservation_field,
            reservation_value: reservation_value
        };

        // variantes PascalCase
        payload.ConversationId = payload.conversation_id !== null && typeof payload.conversation_id !== 'undefined' ? String(payload.conversation_id) : undefined;
        payload.AnonId = payload.anon_id;
        payload.UserId = payload.user_id;
        payload.Message = payload.message;
        payload.QKey = payload.qkey;
        payload.QAnswer = payload.qanswer;
        payload.ReservationField = payload.reservation_field;
        if (typeof reservation_value === 'number') payload.ReservationValue = String(reservation_value);
        else payload.ReservationValue = reservation_value;

        try {
            const res = await postJson(`${CHAT_API}/message`, payload);

            conversationId = res?.conversation_id || res?.conversationId || conversationId;
            if (res?.anon_id || res?.anonId) {
                anonId = res.anon_id || res.anonId;
                try { localStorage.setItem('anon_id', anonId); } catch (e) { }
            }

            renderMessagesFromResponse(res.messages || []);
            setStatus('conectado');
        } catch (err) {
            console.error('sendChatMessage error. payload:', payload, err);
            appendMessage('bot', 'Error procesando tu mensaje.');
            setStatus('error');
        }
    }




    // Crear reserva (usa tu API de reservas)
    async function createReservaFromModal() {
        const expId = Number(document.getElementById('experienciaSelect')?.value || 0);
        const nombre = document.getElementById('resNombre')?.value || null;
        const personas = parseInt(document.getElementById('resPersonas')?.value || '1');
        const fechaHoraVal = document.getElementById('resFechaHora')?.value;
        const telefono = document.getElementById('resTelefono')?.value || null;
        const dni = document.getElementById('resDNI')?.value || null;
        const restricciones = document.getElementById('resRestricciones')?.value || null;
        const errEl = document.getElementById('reservaError');
        if (errEl) { errEl.style.display = 'none'; }

        if (!fechaHoraVal) {
            if (errEl) { errEl.innerText = 'Ingrese fecha y hora.'; errEl.style.display = 'block'; }
            return;
        }
        const fechaHoraIso = new Date(fechaHoraVal).toISOString();

        const payload = {
            AnonId: isUserAuthenticated() ? null : anonId,
            UserId: isUserAuthenticated() ? (Number(userId) || userId) : userId,
            ExperienciaId: expId,
            NumComensales: personas,
            FechaHora: fechaHoraIso,
            Restricciones: restricciones,
            NombreReserva: nombre || 'Reserva por Chat',
            DNI: dni,
            Telefono: telefono,
            EsOcasionEspecial: false,
            ReferenciaConversationId: conversationId ? conversationId.toString() : null
        };

        try {
            const res = await postJson(`${RESERVAS_API}/reservas/create`, payload);
            appendMessage('bot', `Reserva creada (ID ${res.reserva_id || res.reservaId}). Revisa el resumen y confirma cuando estés listo.`);
            renderReservationSummary({
                reservaId: res.reserva_id || res.reservaId,
                experienciaNombre: document.querySelector(`#experienciaSelect option:checked`)?.text || 'Experiencia',
                fechaHora: fechaHoraVal,
                numComensales: personas,
                restricciones: restricciones,
                nombreReserva: nombre,
                reserva_id: res.reserva_id || res.reservaId
            });
            if (reservaModal) reservaModal.hide();
        } catch (err) {
            console.error('createReserva error', err);
            if (errEl) { errEl.innerText = 'Error creando reserva: ' + (err.message || err); errEl.style.display = 'block'; }
            appendMessage('bot', 'Error creando la reserva. Intenta de nuevo.');
        }
    }

    function renderReservationSummary(model) {
        const container = document.getElementById('reservationSummaryContainer');
        if (!container) {
            appendSummary({
                id: model.reservaId,
                reserva_id: model.reservaId,
                experiencia: model.experienciaNombre,
                fecha_hora: model.fechaHora,
                num_comensales: model.numComensales,
                restricciones: model.restricciones,
                nombre_reserva: model.nombreReserva,
                telefono: model.telefono || null
            });
            return;
        }

        const id = model.reservaId || model.reserva_id || model.id || Math.random().toString(36).slice(2, 8);
        const btnsId = `panel-btns-${id}`;

        container.innerHTML = `
      <div class="card mt-3">
        <div class="card-body">
          <h5 class="card-title">Resumen de reserva</h5>
          <p><strong>A nombre de:</strong> ${model.nombreReserva || 'N/A'}</p>
          <p><strong>Experiencia:</strong> ${model.experienciaNombre}</p>
          <p><strong>Fecha/hora:</strong> ${model.fechaHora ? new Date(model.fechaHora).toLocaleString() : 'N/A'}</p>
          <p><strong>Comensales:</strong> ${model.numComensales}</p>
          <p><strong>Restricciones:</strong> ${model.restricciones || 'Ninguna'}</p>
          <div class="d-flex gap-2 mt-3" id="${btnsId}"></div>
        </div>
      </div>
    `;

        const btnsWrap = document.getElementById(btnsId);

        // Confirmar
        const btnConfirm = document.createElement('button');
        btnConfirm.className = 'btn btn-success btn-sm';
        btnConfirm.type = 'button';
        btnConfirm.textContent = 'Confirmar reserva';
        btnConfirm.addEventListener('click', () => confirmReserva(model.reservaId, false));
        btnsWrap.appendChild(btnConfirm);

        // Guardar preferencias / registrar
        const btnSavePrefs = document.createElement('button');
        btnSavePrefs.className = 'btn btn-outline-primary btn-sm';
        btnSavePrefs.type = 'button';
        btnSavePrefs.textContent = isUserAuthenticated() ? 'Confirmar y guardar preferencias' : 'Guardar preferencias y registrarme';
        btnSavePrefs.addEventListener('click', () => {
            if (isUserAuthenticated()) confirmReserva(model.reservaId, true);
            else window.location.href = `/Registro?preserve_reserva=${model.reservaId}`;
        });
        btnsWrap.appendChild(btnSavePrefs);

        // Editar -> enviar edit_reserva en vez de abrir modal
        const btnEdit = document.createElement('button');
        btnEdit.className = 'btn btn-secondary btn-sm';
        btnEdit.type = 'button';
        btnEdit.textContent = 'Editar';
        btnEdit.addEventListener('click', () => {
            if (!model.reservaId) { appendMessage('bot', 'ID de reserva no disponible.'); return; }
            appendMessage('user', `Editar reserva ${model.reservaId}`);
            sendChatMessage({ reservation_field: 'edit_reserva', reservation_value: model.reservaId });
        });
        btnsWrap.appendChild(btnEdit);

        // Cancelar
        const btnCancel = document.createElement('button');
        btnCancel.className = 'btn btn-danger btn-sm';
        btnCancel.type = 'button';
        btnCancel.textContent = 'Cancelar';
        btnCancel.addEventListener('click', () => confirmReserva(model.reservaId, 'cancelar'));
        btnsWrap.appendChild(btnCancel);
    }


    async function confirmReserva(reservaId, guardarPreferencias) {
        const body = { ReservaId: reservaId, Accion: guardarPreferencias === 'cancelar' ? 'cancelar' : 'confirmar', GuardarPreferencias: guardarPreferencias === true };
        try {
            const res = await postJson(`${RESERVAS_API}/reservas/confirm`, body);
            appendMessage('bot', `Reserva ${res.estado || res.estado_reserva || 'actualizada'} (ID ${res.reserva_id || res.reservaId}).`);
            if (guardarPreferencias === true) appendMessage('bot', 'Tus preferencias han sido guardadas en tu cuenta.');
            const container = document.getElementById('reservationSummaryContainer');
            if (container) container.innerHTML = '';
        } catch (err) {
            console.error(err);
            appendMessage('bot', 'Error confirmando reserva: ' + (err.message || err));
        }
    }

    function isUserAuthenticated() {
        try { return !!window.__USER_ID; } catch (e) { return false; }
    }

    // eventos UI
    if (btnSend) btnSend.addEventListener('click', () => {
        const text = (chatInput?.value || '').trim();
        if (!text) return;
        sendChatMessage({ message: text });
        if (chatInput) chatInput.value = '';
    });
    if (chatInput) chatInput.addEventListener('keyup', (e) => { if (e.key === 'Enter') btnSend && btnSend.click(); });
    if (btnOpenReserva) btnOpenReserva.addEventListener('click', () => reservaModal && reservaModal.show());
    if (btnReservaCreate) btnReservaCreate.addEventListener('click', createReservaFromModal);

    window.addEventListener('load', () => startConversation());
})();
