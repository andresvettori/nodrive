# Usar tu propio ID de app de Entra

NoDrive inicia sesión con una app de cliente público registrada en Microsoft Entra. Viene un ID por defecto. Si distribuís NoDrive o querés tu propia pantalla de consentimiento, registrá la tuya:

1. Centro de administración de Microsoft Entra > Registros de aplicaciones > **Nuevo registro**.
2. Tipos de cuenta admitidos: **Cuentas en cualquier directorio organizativo y cuentas personales de Microsoft**.
3. URI de redirección: plataforma **Aplicaciones móviles y de escritorio**, valor `http://localhost`.
4. Permisos de API (Microsoft Graph, delegados): `Files.ReadWrite` y `offline_access`.
5. Autenticación: activá **Permitir flujos de cliente público**.
6. Copiá el **ID de aplicación (cliente)** en `clientId` de `%LOCALAPPDATA%\NoDrive\config.json`, reiniciá NoDrive e iniciá sesión de nuevo.

Una cuenta personal de Microsoft no puede crear registros de aplicaciones por sí sola: creá antes un tenant gratuito de Entra.
