# Configurar NoDrive

Abrí la página de ajustes: icono de la bandeja, **Open NoDrive…** (o doble clic en el icono). Los cambios de límites e intervalos se aplican al instante; el resto necesita reiniciar.

Página de ajustes
- **Idioma**: inglés, español o igual que Windows. Las fechas, horas y números siempre siguen el formato regional y la zona horaria de Windows.
- **Ancho de banda**: límites de descarga y subida (0 = sin límite).
- **Sincronización**: cada cuánto busca cambios NoDrive y cuántas descargas corren en paralelo.
- **Descargas**: archivo completo al abrir (recomendado) o lecturas parciales (avanzado).

Archivo de configuración: `%LOCALAPPDATA%\NoDrive\config.json`. Campos: `syncRoot`, `clientId`, `language`, `downloadKiBps`, `uploadKiBps`, `pollActiveSeconds`, `pollIdleMaxSeconds`, `hydrationWorkers`, `hydrationPolicy`, `webPort`. Editalo con NoDrive cerrado.

Menú de la bandeja: Open NoDrive…, Pause/Resume syncing, Start with Windows, Open NoDrive folder, Quit. Aparece **Sign in…** cuando hay que iniciar sesión y **Allow N held deletions…** si se retuvo una eliminación masiva por tu seguridad.
