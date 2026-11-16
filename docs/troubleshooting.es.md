# Solución de problemas

- **Registros**: `%LOCALAPPDATA%\NoDrive\logs`. La página de Actividad muestra operaciones y problemas recientes.
- **Estado desde una terminal**: `NoDrive.Engine.exe --status` muestra el registro, las flags del shell y el estado del índice.
- **Falta iniciar sesión**: usá el botón de la página de Actividad o el ítem **Sign in…** de la bandeja.
- **Un archivo se abre como dañado en una app**: verificá que el modo de descarga sea **Archivo completo al abrir** (Ajustes > Descargas) y reiniciá NoDrive.
- **Faltan menús o iconos en el Explorador**: reiniciá el Explorador o cerrá e iniciá sesión de nuevo.
- **Eliminaciones en espera**: NoDrive retiene una ráfaga de más de 200 eliminaciones en 10 minutos. Revisalas y presioná **Permitirlas**. Lo que se elimina en la nube va a la papelera de OneDrive.
- **Quitar NoDrive**: cerralo y ejecutá `NoDrive.Engine.exe --cleanup`. Windows borra los archivos que nunca abriste al quitar el registro; antes abrí o mantené los que necesites.
