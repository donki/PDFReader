# 📝 Changelog

Todos los cambios relevantes de PDF Reader se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el versionado
sigue la sección 6 de la constitución: `ApplicationDisplayVersion` legible por el usuario y
`ApplicationVersion` entero incremental para Play Store.

## [2026.07.15.0] - 2026-07-15

Versión inicial. `versionCode` 202607150.

### Añadido
- **Lectura de PDF** con `android.graphics.pdf.PdfRenderer`, el renderizador nativo de Android.
  Sin librerías de PDF de terceros, lo que permite distribuir la aplicación bajo licencia MIT
  sin obligaciones adicionales.
- **Navegación por páginas** (anterior/siguiente) e ir a una página concreta.
- **Zoom** hasta 4× con gestos de pellizco y con botones. Cada nivel de zoom se rasteriza de nuevo,
  así que el texto se mantiene nítido.
- **Biblioteca de documentos recientes** con páginas, tamaño y fecha de última lectura.
- **Continuación de lectura**: cada documento reabre por la última página vista.
- **Apertura desde otras aplicaciones** mediante intent `ACTION_VIEW` con MIME `application/pdf`.
- **Español e inglés**, resueltos desde el idioma del sistema con inglés como valor por defecto,
  y selector de idioma en la pantalla "Acerca de".
- **Tema claro y oscuro** automáticos.
- **Pantalla "Acerca de"** con contacto por intent de correo, enlace de apoyo, aviso de privacidad,
  licencia y aviso legal.
- **Icono y splash** propios con la temática de documento PDF.

### Seguridad y privacidad
- **Cero permisos declarados** en el manifiesto (constitución, sección 5: mínimo privilegio).
  Los documentos se eligen con el Storage Access Framework, que concede acceso solo al archivo
  seleccionado.
- **Sin permiso de internet**: se retiraron `INTERNET` y `ACCESS_NETWORK_STATE` que traía la
  plantilla de MAUI, porque la aplicación no accede a la red.
- Los documentos se copian a la carpeta privada de la aplicación; nada sale del dispositivo.

### Decisiones técnicas
- **Sin ViewModels** (desviación consciente de la sección 14 de la constitución): así lo pide el
  documento de requisitos transversales del proyecto. La lógica de presentación vive en el
  code-behind y toda la lógica de negocio permanece en `Services/`, respetando la regla de
  dependencia de la sección 4.
- **El servicio de PDF se llama `IPdfDocumentService`** y no `IPdfRenderService` porque este último
  nombre colisiona con `Microsoft.Maui.Graphics.IPdfRenderService`.
- **Índice de la biblioteca en JSON con serialización generada en compilación**
  (`JsonSerializerContext`), necesaria porque el Release publica con `PublishTrimmed`.
- **Escritura del índice con archivo temporal y movimiento atómico**, para que una interrupción
  no deje la biblioteca truncada.

### Limitaciones conocidas
- Los **PDF protegidos con contraseña** no se pueden abrir: `PdfRenderer` no los descifra.
  La aplicación lo detecta y lo explica al usuario en lugar de fallar en silencio.
- **No hay búsqueda de texto**: `PdfRenderer` rasteriza páginas pero no extrae texto. Añadirla
  exigiría una librería externa cuya licencia habría que revisar frente al requisito MIT.
- El APK de Release se firma con la **clave de depuración**. Antes de publicar en Play hay que
  firmar con el keystore del proyecto y generar un AAB (constitución, sección 7).
