# Configurar NoDrive

Abrí la página de ajustes: icono de la bandeja, **Open NoDrive…** (o doble clic en el icono). Los cambios de límites e intervalos se aplican al instante; el resto necesita reiniciar.

Página de ajustes
- **Idioma**: inglés, español o igual que Windows. Las fechas, horas y números siempre siguen el formato regional y la zona horaria de Windows.
- **Ancho de banda**: límites de descarga y subida (0 = sin límite).
- **Sincronización**: cada cuánto busca cambios NoDrive y cuántas descargas corren en paralelo.
- **Descargas**: archivo completo al abrir (recomendado) o lecturas parciales (avanzado).

Archivo de configuración: `%LOCALAPPDATA%\NoDrive\config.json`. Campos: `syncRoot`, `clientId`, `language`, `downloadKiBps`, `uploadKiBps`, `pollActiveSeconds`, `pollIdleMaxSeconds`, `hydrationWorkers`, `hydrationPolicy`, `webPort`. Editalo con NoDrive cerrado.

Menú de la bandeja: Open NoDrive…, Pause/Resume syncing, Start with Windows, Open NoDrive folder, Quit. Aparece **Sign in…** cuando hay que iniciar sesión y **Allow N held deletions…** si se retuvo una eliminación masiva por tu seguridad.

Pestaña Actividad: transferencias en curso con progreso y la actividad reciente (podés mostrar los últimos 25, 50, 100, 200 o 400 elementos; se conservan al reiniciar NoDrive). La pestaña **Pasarse desde OneDrive** es opcional y no hace nada si no la usás. La página solo se abre desde el icono de la bandeja; si dice que está desactualizada, NoDrive se reinició: abrila de nuevo desde el icono.
