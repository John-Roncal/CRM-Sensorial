// wwwroot/js/firebase-config.js
// Inicializa Firebase en el cliente (compat). Reemplaza los valores con tu proyecto.
(function () {
    if (window.firebase && window.firebase.apps && window.firebase.apps.length) {
        return; // ya inicializado
    }

    // Rellena estos valores desde Firebase Console -> Project settings
    const firebaseConfig = {
        apiKey: "AIzaSyD5I5Od-JTMGeCELF3vXzNfUJyXYbKrVmQ",
        authDomain: "esp32-sensores-lectura.firebaseapp.com",
        projectId: "esp32-sensores-lectura",
        // opcional:
        // storageBucket: "...",
        // messagingSenderId: "...",
        // appId: "..."
    };

    if (!window.firebase) {
        console.error("Firebase no cargado. Asegúrate de incluir firebase-app-compat.js y firebase-auth-compat.js antes de este archivo.");
        return;
    }

    firebase.initializeApp(firebaseConfig);
    // Opcional: configurar persistence si quieres (local/session)
    firebase.auth().setPersistence(firebase.auth.Auth.Persistence.LOCAL).catch(function (err) {
        console.warn("No se pudo configurar persistencia:", err);
    });
})();
