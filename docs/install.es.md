# Instalación y primer uso

NoDrive es un cliente liviano de OneDrive (cuenta personal) para Windows 11. Muestra tus archivos en el Explorador y los descarga cuando los abrís.

1. Conseguí `NoDrive.Engine.exe` (la compilación Native AOT del CI del proyecto, rama `ci-binaries`, carpeta `engine`). Guardalo en una carpeta fija, por ejemplo `C:\Users\<vos>\NoDrive-app`.
2. Ejecutalo. Aparece un icono de nube en la bandeja. La primera vez se abre el navegador para iniciar sesión con tu cuenta Microsoft.
3. Tus archivos aparecen en `C:\Users\<vos>\NoDrive`. El primer escaneo de un OneDrive grande tarda unos minutos; podés seguir trabajando.
4. Opcional: clic derecho en el icono de la bandeja y tildá **Start NoDrive with Windows**.

Notas
- Detené OneDrive.exe para la misma cuenta antes de usar NoDrive. No deberían sincronizar la misma cuenta a la vez.
- Abrir un archivo descarga el archivo completo primero (como OneDrive). Usá **Mantener siempre en este dispositivo** para tenerlo sin conexión y **Liberar espacio** para quitar la copia local.
