// wwwroot/js/verify.js
(function () {
    if (!window.firebase) {
        console.error("Firebase no está cargado. Incluye firebase antes de verify.js");
        return;
    }
    const auth = firebase.auth();

    async function completarFlow() {
        try {
            const url = window.location.href;
            console.log("[verify] URL:", url);

            if (!auth.isSignInWithEmailLink(url)) {
                console.log("[verify] No es signInWithEmailLink");
                showMessage("Enlace inválido o ya procesado.", true);
                // ocultar loading para no quedar con "Procesando..."
                hideLoading();
                return;
            }

            // obtener email desde localStorage o pedirlo
            let email = localStorage.getItem('emailForSignIn');
            console.log("[verify] emailFromStorage:", email);
            if (!email) {
                email = prompt('Introduce el correo con el que solicitaste el enlace:');
                if (!email) {
                    showMessage('Correo requerido para completar el acceso.', true);
                    hideLoading();
                    return;
                }
            }

            let signResult;
            try {
                signResult = await auth.signInWithEmailLink(email, url);
            } catch (err) {
                console.error("[verify] signInWithEmailLink error:", err);
                showMessage('No se pudo completar la verificación con el enlace. Revisa la consola.', true);
                hideLoading();
                return;
            }

            const user = signResult.user;
            console.log("[verify] usuario:", user && { uid: user.uid, email: user.email, verified: user.emailVerified });

            // ocultar loading siempre que hayamos llegado acá
            hideLoading();

            // mostrar panelCrearPassword (compatible con style="display:none" y con class="d-none")
            const panel = document.getElementById('panelCrearPassword');
            if (panel) {
                // si usa bootstrap
                panel.classList.remove('d-none');
                // también setear inline style por compatibilidad
                panel.style.display = 'block';
            }

            const form = document.getElementById('formCrearPassword');
            if (!form) {
                // Si no hay formulario, mandar token sin password
                try {
                    const idToken = await user.getIdToken(true);
                    const nombre = localStorage.getItem('tmpNombre') || '';
                    await enviarTokenAlServidor(idToken, nombre, null, null);
                } catch (err) {
                    console.error("[verify] error enviando token sin form:", err);
                    showMessage('Error interno. Revisa la consola.', true);
                }
                return;
            }

            // manejar envío del form
            form.addEventListener('submit', async (e) => {
                e.preventDefault();
                const pwd = document.getElementById('Password')?.value?.trim();
                const confirm = document.getElementById('ConfirmPassword')?.value?.trim();

                if (!pwd || pwd.length < 6) {
                    alert('La contraseña debe tener al menos 6 caracteres.');
                    return;
                }
                if (pwd !== confirm) {
                    alert('Las contraseñas no coinciden.');
                    return;
                }

                try {
                    // actualizar contraseña en Firebase (usuario ya autenticado tras signInWithEmailLink)
                    await user.updatePassword(pwd);
                } catch (err) {
                    console.error("[verify] updatePassword error:", err);
                    alert('No se pudo establecer la contraseña en Firebase. Intenta iniciar sesión luego.');
                    return;
                }

                try {
                    const idToken = await user.getIdToken(true);
                    const nombre = localStorage.getItem('tmpNombre') || '';
                    await enviarTokenAlServidor(idToken, nombre, pwd, confirm);
                } catch (err) {
                    console.error("[verify] error obteniendo token o enviando al servidor:", err);
                    alert('Error finalizando el registro. Revisa la consola.');
                }
            });

        } catch (err) {
            console.error("[verify] error general:", err);
            showMessage('Ocurrió un error: ' + (err.message || err), true);
            hideLoading();
        }
    }

    async function enviarTokenAlServidor(idToken, nombre, password, confirmPassword) {
        try {
            const payload = {
                IdToken: idToken,
                Nombre: nombre || '',
                Password: password || '',
                ConfirmPassword: confirmPassword || ''
            };

            console.log("[verify] enviar payload:", payload);

            const res = await fetch('/Registro/Finalizar', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (!res.ok) {
                const text = await res.text();
                console.error("[verify] Finalizar response error:", res.status, text);
                alert('Error finalizando el registro: ' + (text || res.statusText));
                return;
            }

            // limpieza
            localStorage.removeItem('emailForSignIn');
            localStorage.removeItem('tmpNombre');

            // redirect
            window.location.href = '/Cliente';
        } catch (err) {
            console.error("[verify] enviarTokenAlServidor error:", err);
            alert('Error comunicando con el servidor.');
        }
    }

    function showMessage(text, isError = false) {
        const el = document.getElementById('mensaje');
        if (!el) {
            if (isError) alert(text); else console.log(text);
            return;
        }
        el.classList.remove('d-none');
        el.innerText = text;
        el.classList.toggle('alert-danger', !!isError);
        el.classList.toggle('alert-info', !isError);
    }

    function hideLoading() {
        const loading = document.getElementById('loading');
        if (loading) loading.style.display = 'none';
    }

    window.addEventListener('DOMContentLoaded', completarFlow);
})();
