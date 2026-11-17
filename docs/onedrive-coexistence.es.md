# Usar NoDrive junto con OneDrive

NoDrive y la aplicación oficial de OneDrive pueden ejecutarse en la misma PC con la misma cuenta personal. Se comportan como dos computadoras: un cambio hecho en una llega a la otra a través de la nube. Usan carpetas distintas, así que nunca pongas la carpeta de NoDrive dentro de la carpeta de OneDrive.

## Respaldo de carpetas (Escritorio, Documentos, Imágenes)
Si OneDrive respalda estas carpetas, viven dentro de la carpeta de OneDrive y OneDrive sigue administrándolas. NoDrive también muestra sus archivos en su propia carpeta. NoDrive no mueve ni cambia nada.

Para que NoDrive tome el control, primero detené el respaldo en OneDrive: icono de OneDrive > Configuración > Sincronización y copia de seguridad > Administrar copia de seguridad > desactivá las carpetas. OneDrive te ofrece conservar los archivos en las carpetas originales. Elegí conservarlos allí, verificá que estén completos y luego seguí con NoDrive.

## Antes de detener la copia de seguridad de carpetas
1. En OneDrive: icono > Configuración > Sincronización y copia de seguridad > Configuración avanzada > Archivos a petición > **Descargar todos los archivos** de las carpetas que respaldás, y esperá las marcas verdes. Detener la copia falla con archivos solo en línea.
2. Tras detenerla, verificá que las carpetas volvieron a tu perfil: en PowerShell, `[Environment]::GetFolderPath('Desktop')`, `('MyDocuments')` y `('MyPictures')` deben mostrar `C:\Users\<vos>\...`, no una ruta de OneDrive. Si no, clic derecho en la carpeta del Explorador > Propiedades > Ubicación > **Restaurar valores predeterminados**.
3. Cuando OneDrive pregunte, elegí conservar los archivos en sus carpetas originales de esta PC (no elijas quitarlos de la PC ni de OneDrive).

## Pasarte por completo a NoDrive
1. Verificá que NoDrive muestre todos los archivos que necesitás (abrí algunos o usá Mantener siempre en este dispositivo en las carpetas importantes).
2. Detené el respaldo de carpetas como se indicó.
3. Cerrá OneDrive y, cuando estés conforme, desvinculá la cuenta en los ajustes de OneDrive.

Hacelo de a poco: nada en NoDrive borra datos sin pasar por la papelera de OneDrive.

La página de ajustes tiene una pestaña opcional **Pasarse desde OneDrive** que compara tu carpeta con la nube (solo lectura), te guía por los pasos en OneDrive y permite que NoDrive tome la carpeta. Nunca se ejecuta si no la abrís.
