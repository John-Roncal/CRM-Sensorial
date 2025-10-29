// wwwroot/js/registro.js
// Controla el envío del formulario de registro (nombre + email).
// Requiere firebase-config.js y Firebase Auth (compat).

(function () {
    if (!window.firebase) return;

    const auth = firebase.auth();

    async function enviarLink(email) {
        const actionCodeSettings = {
            // La URL a la que Firebase redirigirá tras procesar el enlace.
            // Debe ser un dominio autorizado en Firebase Console (Authentication -> Authorized domains)
            url: `${location.protocol}//${location.host}/Registro/VerificarEmail`,
            handleCodeInApp: true
        };

        return auth.sendSignInLinkToEmail(email, actionCodeSettings);
    }

    function init() {
        const form = document.getElementById('formRegistro');
        if (!form) return;

        form.addEventListener('submit', async function (e) {
            e.preventDefault();
            const nombre = (document.getElementById('Nombre')?.value || '').trim();
            const email = (document.getElementById('Email')?.value || '').trim();

            if (!email) {
                alert('Ingresa un correo válido.');
                return;
            }

            // guardar datos temporales en localStorage para el flujo
            localStorage.setItem('emailForSignIn', email);
            localStorage.setItem('tmpNombre', nombre);

            try {
                await enviarLink(email);
                // redirige a la vista informativa
                window.location.href = `/Registro/RevisaTuCorreo?email=${encodeURIComponent(email)}`;
            } catch (err) {
                console.error('Error enviando link:', err);
                alert('No se pudo enviar el enlace. Revisa la consola para más detalles.');
            }
        });
    }

    window.addEventListener('DOMContentLoaded', init);
})();
