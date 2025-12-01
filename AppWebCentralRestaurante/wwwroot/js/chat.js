document.addEventListener('DOMContentLoaded', () => {

    const CHAT_API_URL = 'https://backend-crm-cwxe.onrender.com/chat';

    const chatWindow = document.getElementById('chatWindow');
    const chatInput = document.getElementById('chatInput');
    const btnSend = document.getElementById('btnSend');
    const statusBadge = document.getElementById('status-badge');

    const chatContainer = document.getElementById('chat-container');
    const currentUserId = chatContainer ? (chatContainer.dataset.userId || null) : null;

    let currentSessionId = sessionStorage.getItem('chatSessionId');
    if (!currentSessionId) {
        currentSessionId = 'session_' + Math.random().toString(36).substr(2, 9);
        sessionStorage.setItem('chatSessionId', currentSessionId);
    }

    console.log("Chat JS iniciado. UserID:", currentUserId, "SessionID:", currentSessionId);
    if (statusBadge) statusBadge.innerText = 'Conectado';

    function appendMessage(sender, text) {
        if (!chatWindow) return;

        const wrap = document.createElement('div');
        wrap.className = 'mb-2 d-flex';

        const bubble = document.createElement('div');
        bubble.className = `p-2 rounded ${sender === 'user' ? 'bg-primary text-white ms-auto' : 'bg-light text-dark'}`;
        bubble.style.maxWidth = '85%';
        bubble.style.wordBreak = 'break-word';

        if (sender === 'bot') {
            bubble.innerHTML = text.replace(/\n/g, '<br>');
        } else {
            bubble.innerText = text;
        }

        wrap.appendChild(bubble);
        chatWindow.appendChild(wrap);
        chatWindow.scrollTop = chatWindow.scrollHeight;
    }

    function showTypingIndicator() {
        if (statusBadge) statusBadge.innerText = 'pensando...';
    }

    function hideTypingIndicator() {
        if (statusBadge) statusBadge.innerText = 'Conectado';
    }

    async function sendMessage(userMessage) {
        showTypingIndicator();

        const payload = {
            message: userMessage,
            session_id: currentSessionId,
            user_id: currentUserId ? parseInt(currentUserId) : null
        };

        try {
            const response = await fetch(CHAT_API_URL, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                throw new Error(`Error de red: ${response.statusText}`);
            }

            const data = await response.json();

            appendMessage('bot', data.response);

            currentSessionId = data.session_id;
            sessionStorage.setItem('chatSessionId', currentSessionId);

        } catch (error) {
            console.error('Error al enviar mensaje a FastAPI:', error);
            appendMessage('bot', 'Hubo un error al conectar con el asistente. Por favor, intenta de nuevo.');
        } finally {
            hideTypingIndicator();
        }
    }

    if (btnSend) {
        btnSend.addEventListener('click', () => {
            const text = (chatInput?.value || '').trim();
            if (!text) return;

            appendMessage('user', text);
            sendMessage(text);

            if (chatInput) chatInput.value = '';
        });
    }

    if (chatInput) {
        chatInput.addEventListener('keyup', (e) => {
            if (e.key === 'Enter') {
                btnSend && btnSend.click();
            }
        });
    }
    sendMessage("Hola");
});