# Informar un problema

Si NoDrive hace algo mal, un informe pequeño nos permite encontrar la causa rápido. Lleva un minuto.

1. Clic derecho en el icono de NoDrive de la bandeja y elegí **Report a problem...** (o abrí NoDrive y andá a la pestaña **Ayuda**).
2. Dejá marcado **Incluir nombres de archivos** salvo que los nombres sean privados. Sin nombres el informe es más difícil de leer.
3. Presioná **Crear informe**. NoDrive crea un archivo zip en tu Escritorio, con un nombre como `NoDrive-report-20261120-101500-a1b2c3.zip`, y abre la carpeta.
4. Anotá qué estabas haciendo cuando falló (por ejemplo "renombré una carpeta y mostró un error").
5. Enviá el zip y tu nota a la persona que te dio NoDrive.

Qué contiene el zip: un informe de estado, los registros de NoDrive de los últimos 7 días, la lista de transferencias recientes, tus ajustes de NoDrive y el registro de archivos en la nube de Windows. Nunca contiene tu token de inicio de sesión, contraseñas ni el contenido de tus archivos. Los nombres de archivos aparecen en los registros y en la lista de transferencias; podés abrir el zip antes y mirarlo.

Sin el icono de la bandeja: abrí PowerShell y ejecutá `& "$env:LOCALAPPDATA\Programs\NoDrive\NoDrive.Engine.exe" --report`. `--doctor` muestra el informe de estado en pantalla.

El **ID de soporte** al pie de cada página de NoDrive también aparece en el registro: mencionalo en tu mensaje.
