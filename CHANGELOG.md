# 📝 Changelog

Todos los cambios relevantes de PDF Reader se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el versionado
sigue la sección 6 de la constitución: `ApplicationDisplayVersion` legible por el usuario y
`ApplicationVersion` entero incremental para Play Store.

## [2026.07.17.0] - 2026-07-17

`versionCode` 202607170.

### Corregido
- **El zoom con dos dedos no llegaba a dispararse.** El `PinchGestureRecognizer` estaba dentro de
  un `ScrollView`, que en Android se traga el multitouch: es un fallo conocido de MAUI/Xamarin
  (dotnet/maui#5614, #5612). El arreglo de 2026.07.16.3 corrigió la aritmética del gesto, pero el
  gesto nunca llegaba. Se retira el `ScrollView` y el desplazamiento pasa a un
  `PanGestureRecognizer` propio, que es el workaround documentado.

### Cambiado
- **El zoom ya no rerasteriza la página en cada paso.** Ahora es una transformación de vista: la
  página conserva su tamaño de layout y solo cambia su `Scale`, que es instantáneo. El bitmap se
  vuelve a rasterizar una sola vez, 250 ms después de que el zoom se detenga, y únicamente si la
  resolución se ha quedado visiblemente corta. Antes, cada paso de zoom rasterizaba y codificaba a
  PNG la página entera, que es de donde salía el spinner.
- La rasterización se limita a 3× el ancho de ajuste: por encima, el coste crece con el cuadrado
  del factor a cambio de detalle que la pantalla no puede mostrar.
- El indicador de carga solo aparece si el render tarda más de 150 ms, para que un render rápido no
  provoque un parpadeo.
- El aspect ratio de cada página se cachea: obtenerlo abría la página una segunda vez en cada
  render, y no cambia nunca.

- El afinado de nitidez tras el zoom es **silencioso y sin parpadeo**. La página nueva se
  rasteriza en una segunda capa que entra con un breve fundido sobre la anterior, que permanece
  visible por debajo hasta que la nueva ha decodificado y aparecido. Antes se ocultaba la capa
  vieja con un temporizador fijo, que destellaba cuando un bitmap ampliado tardaba en decodificar
  más que el temporizador. De borroso a nítido, el fundido se percibe como un reenfoque suave.

### Añadido
- Doble toque para alternar entre ajustar a pantalla y 2×.
- Desplazamiento con un dedo cuando la página está ampliada, con límites para que no se pueda
  arrastrar fuera de la vista.

### Gobernanza
- Submódulo `constitution` actualizado de `160c54c` a `725211c` (constitución §23: el anclaje se
  mueve de forma deliberada y se registra aquí). Incorpora la **sección 24, Sistema de Diseño
  Visual**, y el **anexo A.9**, extraídos de `FileManager` y `PDFReader`.
- **Desviación conocida de este proyecto frente a la sección 24**, pendiente de resolver: se
  mantiene la plantilla `Resources/Styles/Styles.xaml` de MAUI intacta y viva (405 líneas, ~27
  estilos implícitos) junto al diccionario propio `AppStyles.xaml`, y su `Colors.xaml` de fábrica
  define una segunda paleta muerta (`Primary` = `#512BD4`) que no es la de la aplicación
  (`PdfPrimary` = `#B3121E`). De ahí el prefijo `Pdf`: el nombre `Primary` está ocupado por la
  plantilla. `FileManager` ya está limpio (97 líneas, sin plantilla). Retirar la plantilla cambiaría
  el aspecto de la aplicación, porque sus estilos implícitos están activos, así que exige
  verificación en dispositivo.

## [2026.07.16.3] - 2026-07-16

`versionCode` 202607163.

### Corregido
- **El zoom con gestos de pellizco no hacía nada.** `PinchGestureUpdatedEventArgs.Scale` es la
  variación respecto a la actualización anterior, no respecto al inicio del gesto; el código
  multiplicaba el zoom inicial por ese valor, que ronda 1, así que la vista previa se quedaba
  clavada. Ahora se acumula.

### Añadido
- Segundo `intent-filter` para los gestores de ficheros que entregan un PDF como
  `application/octet-stream` en vez de `application/pdf`: sin él la app no aparecía en "Abrir con"
  desde esos gestores. Filtra por extensión, y declara `DataHost` porque Android **ignora
  `pathPattern` si el filtro no declara también `scheme` y `host`**: sin host, el filtro se
  ofrecería para cualquier fichero binario del dispositivo.

### Cambiado
- La paginación del lector muestra `1 / 10` en lugar de `Página 1 de 10`.

## [2026.07.16.2] - 2026-07-16

`versionCode` 202607162.

### Añadido
- **Búsqueda de texto** en el documento, con navegación entre coincidencias y resaltado. El
  resaltado se pinta sobre el bitmap durante el render, así que la página no necesita ninguna
  conversión de coordenadas PDF a vista.
- **Apertura de PDF protegidos con contraseña**, preguntándola al importar y al abrir desde la
  biblioteca, y reintentando mientras sea incorrecta.
- `PasswordPromptPage`: diálogo modal propio con la entrada enmascarada. `DisplayPromptAsync` de
  MAUI no permite ocultar el texto y dejaría la contraseña a la vista.

### Cambiado
- `IPdfDocumentService.OpenAsync` acepta la contraseña; `IPdfDocument` expone `SearchAsync` y
  `SupportsTextSearch`, y `RenderPageAsync` acepta las coincidencias a resaltar.
- Importar un PDF protegido ya no lo rechaza ni borra la entrada de la biblioteca: pide la
  contraseña. La contraseña se conserva solo en memoria durante la sesión de lectura; guardarla
  dejaría en disco la llave del documento del usuario (constitución, sección 5).
- `PdfOpenFailure` distingue `WrongPassword` y `PasswordUnsupported` de `PasswordProtected`, para
  poder explicar cada caso.

### Limitaciones conocidas
- La búsqueda y la apertura con contraseña **requieren Android 15 (API 35)**: `LoadParams` y
  `Page.searchText` llegaron ahí. Por debajo, el botón de búsqueda no se muestra y un PDF
  protegido sigue avisando de que no se puede abrir. Se descartó una librería externa por no
  romper la ausencia de dependencias de PDF, que es lo que sostiene la licencia MIT. Esto corrige
  la nota de 2026.07.15.0, que daba por hecho que ambas funciones exigían una librería de terceros.
- La búsqueda se detiene a las 500 coincidencias: recorrerlas todas obliga a abrir cada página.

## [2026.07.16.1] - 2026-07-16

`versionCode` 202607161.

### Añadido
- `Resources/AppIcon/play_store_icon.png`: icono de ficha de Play Console de 512×512, opaco,
  compuesto por `appicon.svg` (fondo `#B3121E`) y `appiconfg.svg` (documento con marcapáginas). El
  icono de launcher que genera MAUI dentro del AAB no lo usa Play Console como icono de tienda: es
  un asset aparte de la ficha.
- `publish_aab_to_play.ps1`: script de publicación en Play Console mediante Android Publisher API,
  adaptado del de `FileManager`. Hasta ahora este proyecto no tenía ninguno. No lleva credencial
  por defecto: se pasa con `-ServiceAccountJson` o por `GOOGLE_APPLICATION_CREDENTIALS`.
- `PlayStoreListing.es-ES.json`: textos de la ficha de Play Console. La ficha publicada tenía
  título pero las dos descripciones vacías, lo que impedía enviarla a revisión. La descripción
  corta evita palabras de precio o promoción, que Play rechaza en ese campo.
- `hiker-*.json` añadido al `.gitignore`: las credenciales de Google Cloud se descargan con ese
  nombre y ningún patrón anterior las cubría.

### Cambiado
- Versión y `versionCode` incrementados para poder subir un AAB nuevo: Play Console ya tenía
  202607160 y no admite reutilizar un `versionCode`. Sin cambios funcionales respecto a
  2026.07.16.0.

## [2026.07.16.0] - 2026-07-16

`versionCode` 202607160.

Versión preparada para el primer envío a Play Console. Sin cambios funcionales respecto a
2026.07.15.0: solo se fija la versión a la fecha de publicación (§A.4).

### Cambiado
- Versión y `versionCode` fijados a la fecha de publicación.
- El AAB de Release ya se firma con el keystore del proyecto (`socratic.keystore`) mediante
  `build_and_sign.ps1`, lo que resuelve la limitación de firma con clave de depuración
  anotada en 2026.07.15.0.

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
