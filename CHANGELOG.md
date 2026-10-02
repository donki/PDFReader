# 📝 Changelog

Todos los cambios relevantes de PDF Reader se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el versionado
sigue la sección 6 de la constitución: `ApplicationDisplayVersion` legible por el usuario y
`ApplicationVersion` entero incremental para Play Store.

## 2026.10.02.0 (2026100200)

### Cambiado
- Lo que se repetía en el lector de Android y en el de Windows (una página a la vez, índices
  válidos, documento cerrado, tope de 500 resultados de búsqueda, resaltado solo en su página) pasa
  a `Services/PdfDocumentBase.cs`; cada plataforma solo hace su parte nativa. La copia de los PDF
  que llegan de otras apps o del Explorador, a `Services/IncomingDocuments.cs`, y lo que las
  pantallas piden al dispositivo (selector, correo, navegador, pantalla, diálogos), a
  `Services/AppPlatform.cs`. La app hace lo mismo; así se puede probar.
- El correo de contacto en Android abre el selector del sistema desde la actividad principal.

### Pruebas
- El banco (General §8.6) pasa de 69 a 151 pruebas y ahora recorre las pantallas con su XAML real:
  biblioteca (abrir, importar, contraseña, PDF no válidos, borrar, documentos de otras apps, atrás),
  lector (página guardada, pasar página, zoom, pellizco, arrastre, ir a página, búsqueda y sus
  resultados, errores, cambio de tamaño), contraseña, Acerca de y la comprobación de versión.
  Cobertura sobre toda la app: **84,1 %** (antes 15,0 % con el recuento anterior). No llega al 90 %:
  lo que falta es casi todo código nativo de Android y Windows (plan en el fichero de tareas).

*English:* The rules shared by the Android and Windows renderers, the import of PDFs sent by other
apps and the device services used by the pages are now separate, testable classes; the app behaves
the same. Tests: 151, 84.1 % line coverage of the whole app.

## 2026.09.30.0 (2026093000)

### Corregido
- **Páginas muy alargadas** (un tique, un rollo escaneado): el tope de 12 Mpx del mapa de bits no se
  respetaba cuando el ancho ya estaba en su mínimo de 200 px, y una página así podía pedir cientos de
  megas de memoria y cerrar la app. Ahora se recorta el alto para no pasar del tope.

### Cambiado
- La geometría de página (`Services/PdfPageMath.cs`) y los textos de la lista de la biblioteca
  (`Services/LibraryFormatter.cs`) salen de la plataforma Android y de la página a clases propias,
  sin cambiar lo que muestran, para poder probarlos.

### Añadido
- **Pruebas automatizadas** (General §8.6): proyecto `PDFReader.Tests` (xUnit) con la biblioteca
  (importar, recientes, página y número de páginas, borrar, índice dañado o incompleto, importaciones
  a la vez), idiomas, geometría de página, resaltado de la búsqueda y textos de la lista. Se ejecutan
  con `dotnet test PDFReader.Tests`.

### English
- Very tall pages no longer ask for an oversized bitmap that could run the app out of memory.
- Automated tests for the app logic (`dotnet test PDFReader.Tests`).

## 2026.09.27.0 (2026092700)

### Añadido
- **Un error inesperado ya no cierra la aplicación** (constitución General §6.12): se registra con
  su traza en `crash.log` (carpeta de datos de la app, con tamaño acotado), se avisa en el idioma
  elegido en la aplicación y se sigue. Usa la pieza común `Shared/CrashGuard.cs`.

### Corregido
- **Botón de atrás** (Mobile §7): en Android 16 el «atrás predictivo» hacía que no llegase a las
  páginas (`enableOnBackInvokedCallback="false"`). Ahora, en el lector, con el buscador abierto
  primero lo cierra y si no vuelve a la biblioteca; en la biblioteca la aplicación se oculta sin
  cerrarse. La página de contraseña sigue cancelando con atrás.

## 2026.09.13.0 (2026091300)

### Eliminado
- **Las herramientas de edición del 12-09** (fusionar, dividir, organizar páginas, imágenes,
  contraseñas, numeración, marca de agua, anotar, firmar y exportar) y el nombre «PDF Editor».
  Decisión del autor: la aplicación vuelve a ser solo un lector, tal como estaba antes de
  añadirlas. Se retiran también PDFsharp y las tipografías que traían, y la ficha de Microsoft
  Store que las describía. Se conserva el destino Windows (WinUI) con su renderizador nativo.

## 2026.08.28.0 (202608280)

### Corregido
- **La aplicación abortaba nada más arrancar.** `LibraryPage` pide `UpdateService` por
  constructor y el servicio **no estaba registrado** en `MauiProgram`, así que el contenedor
  reventaba al crear la ventana: `InvalidOperationException: CannotResolveService,
  PDFReader.Services.UpdateService, PDFReader.Pages.LibraryPage`, en `App.CreateWindow`. Se veía
  el splash y la actividad se destruía acto seguido. Es el mismo fallo que tuvo File Manager el
  2026-08-01.

## [2026.08.01.0] - 2026-08-01

`versionCode` 202608010.

### Corregido
- El icono y el splash usaban todavía el rojo `#B3121E` de la marca anterior en el `.csproj`,
  cuando los SVG ya eran del índigo unificado `#3525CD` (su propio comentario lo decía). El fondo
  rojo asomaba detrás del splash.
- `Resources\AppIcon\play_store_icon.png` regenerado desde los SVG actuales: era el icono rojo
  anterior al rediseño del 28-jul y no se correspondía con el que muestra el dispositivo.

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
