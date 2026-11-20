# Solución de problemas

- **Registros**: `%LOCALAPPDATA%\NoDrive\logs`. La página de Actividad muestra operaciones y problemas recientes.
- **Estado desde una terminal**: `NoDrive.Engine.exe --status` muestra el registro, las flags del shell y el estado del índice.
- **Falta iniciar sesión**: usá el botón de la página de Actividad o el ítem **Sign in…** de la bandeja.
- **Un archivo se abre como dañado en una app**: verificá que el modo de descarga sea **Archivo completo al abrir** (Ajustes > Descargas) y reiniciá NoDrive.
- **Faltan menús o iconos en el Explorador**: reiniciá el Explorador o cerrá e iniciá sesión de nuevo.
- **Eliminaciones en espera**: NoDrive retiene una ráfaga de más de 200 eliminaciones en 10 minutos. Revisalas y presioná **Permitirlas**. Lo que se elimina en la nube va a la papelera de OneDrive.
- **Quitar NoDrive**: cerralo y ejecutá `NoDrive.Engine.exe --cleanup`. Windows borra los archivos que nunca abriste al quitar el registro; antes abrí o mantené los que necesites.
- **La desinstalación termina con un error**: NoDrive encontró subidas, eliminaciones o cambios de nombre que todavía no llegaron a OneDrive. Iniciá NoDrive, esperá a que la página de Actividad no muestre nada pendiente y desinstalá de nuevo (`NoDrive.Engine.exe --pending` muestra la cantidad).
- **Versión de la Store: el Explorador sigue mostrando NoDrive después de quitarla**: abrí NoDrive otra vez (reinstalá si hace falta), usá Ayuda > **Preparar para desinstalar** y luego quitá la app. La limpieza automática se omite si quedaba algo por subir.
- **Windows bloquea NoDrive o el instalador**: los programas sin firmar pueden ser detenidos por Smart App Control o Control de aplicaciones (error 0x800711C7 en archivos recién compilados). Usá el MSI oficial; se planea una versión firmada.
- **La página de ajustes dice que está desactualizada**: NoDrive se reinició; abrila de nuevo desde el icono de la bandeja.
- **Cualquier otra cosa**: creá un informe y enviálo (mirá la guía de informar un problema): icono de la bandeja > Report a problem...
- **"Una directiva de Control de aplicaciones bloqueó este archivo"**: Smart App Control de Windows solo ejecuta programas firmados o conocidos y NoDrive todavía no está firmado. Pedí una versión firmada a quien te dio NoDrive; no intentes saltear el bloqueo.

---
Sitio web: <https://andresvettori.github.io/nodrive/>
