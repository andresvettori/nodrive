# Instalación y primer uso

NoDrive es un cliente liviano de OneDrive (cuenta personal) para Windows 11. Muestra tus archivos en el Explorador y los descarga cuando los abrís.

1. Descargá `NoDrive-x64.msi` (el artefacto **NoDrive-installer-win-x64** de la última compilación correcta en GitHub, o la carpeta `installer` de la rama `ci-binaries`).
2. Hacé doble clic, aceptá la licencia e instalá. No se necesitan permisos de administrador. NoDrive se instala en `%LOCALAPPDATA%\Programs\NoDrive`, aparece en el menú Inicio y en Configuración > Aplicaciones, y se inicia al iniciar sesión. Instalación silenciosa: `msiexec /i NoDrive-x64.msi /qn AUTOSTART=0 SUPPRESSLAUNCH=1` (ambas opciones son opcionales).
3. NoDrive arranca solo. Aparece un icono de nube en la bandeja y se abre el navegador para iniciar sesión con tu cuenta Microsoft.
4. Tus archivos aparecen en `C:\Users\<vos>\NoDrive`. El primer escaneo de un OneDrive grande tarda unos minutos; podés seguir trabajando. Hacé doble clic en el icono de la bandeja para ver qué está haciendo NoDrive.

Notas
- NoDrive puede funcionar junto a la aplicación OneDrive con la misma cuenta (mirá la guía de OneDrive).
- Abrir un archivo en línea descarga el archivo completo primero (como OneDrive). Usá **Mantener siempre en este dispositivo** para tenerlo sin conexión y **Liberar espacio** para quitar la copia local.
- Windows puede bloquear programas sin firmar (Smart App Control, Control de aplicaciones). Si el instalador o NoDrive se bloquea, avisale al autor: se planea una versión firmada.
- Para actualizar, ejecutá el MSI nuevo sobre el anterior: NoDrive se detiene, se reemplazan los archivos y se conservan tu carpeta y tus ajustes.
- Para quitar NoDrive: Configuración > Aplicaciones > NoDrive > Desinstalar. Los archivos descargados quedan en la carpeta. Si se rechaza la desinstalación, mirá la guía de solución de problemas.
