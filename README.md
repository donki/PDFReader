# 📕 PDF Reader

Lector de PDF para Android desarrollado en .NET MAUI. Sin conexión, **sin ningún permiso** y con los
documentos siempre en el dispositivo.

## Dónde conseguirla

- **Google Play:** https://play.google.com/store/apps/details?id=com.socratic.pdfreader
- **Releases de GitHub** (APK / EXE / MSIX de cada versión): https://github.com/donki/PDFReader/releases

## ✨ Características

### 📖 Lectura
- **Apertura de PDF** desde el selector del sistema o desde cualquier app con "Abrir con"
- **Navegación por páginas** con botones e **ir a página** concreta
- **Zoom** con gestos de pellizco y con botones (hasta 4×)
- **Continúa donde lo dejaste**: recuerda la última página de cada documento

### 📚 Biblioteca
- **Documentos recientes** ordenados por última lectura
- **Información de cada documento**: páginas, tamaño y cuándo se abrió
- **Eliminación** con confirmación (el archivo original del dispositivo nunca se toca)

### 🎨 Interfaz
- **Diseño de tarjetas** con tema claro/oscuro automático
- **Español e inglés**, detectados del idioma del sistema (inglés como idioma por defecto)
- **Selector de idioma** en la pantalla "Acerca de"

### 🔒 Privacidad
- **Cero permisos**: el manifiesto no declara ninguno
- **Sin acceso a internet**: la app no puede enviar datos aunque quisiera
- **Almacenamiento privado**: los documentos se copian a la carpeta privada de la app

## 🧱 Cómo se renderizan los PDF

El renderizado usa **`android.graphics.pdf.PdfRenderer`**, incluido en Android desde la API 21.
No hay ninguna librería de PDF de terceros, así que la aplicación no arrastra obligaciones de
licencia ajenas y puede distribuirse bajo MIT sin restricciones.

Cada página se rasteriza bajo demanda al ancho que ocupa en pantalla y al nivel de zoom actual,
por lo que la memoria no crece con la longitud del documento.

## 🧪 Pruebas

151 pruebas automatizadas (xUnit), sin dispositivo ni red. Además de la lógica (biblioteca sobre una
carpeta temporal, idiomas, geometría de página, cajas de resaltado, textos de la lista, cola y copia
de los PDF que llegan de otras apps, reglas comunes de los documentos de `PdfDocumentBase`,
comprobación de versión), recorren las pantallas con su XAML real compilado para `net10.0` con
Microsoft.Maui.Controls: biblioteca (abrir, importar, contraseña, PDF no válidos, borrar, documentos
recibidos, atrás), lector (página guardada, pasar página, zoom, pellizco, arrastre, ir a página,
búsqueda, errores, cambio de tamaño), contraseña y Acerca de. Lo que pediría el dispositivo pasa por
`Services/AppPlatform.cs` y el renderizado nativo lo sustituye un documento de mentira.

| Fecha | Pruebas | Cobertura de lo instrumentado | Cobertura sobre toda la app | Tiempo del banco |
|---|---|---|---|---|
| 2026-10-02 | 151 (todas pasan) | 96,9 % (1410 / 1455 líneas) | **84,1 %** (1410 / 1676 líneas) | ~15 s de pruebas (el lector espera sus retardos reales) |
| 2026-09-30 | 69 (todas pasan) | 98,8 % (335 / 339 líneas) | 15,0 % (335 / 2239 líneas, recuento anterior) | ~1 s de pruebas |

```bash
dotnet test PDFReader.Tests                        # solo las pruebas
pwsh PDFReader.Tests/cobertura.ps1                 # pruebas + las dos coberturas + tiempo
```

Cómo se cuenta «toda la app» (desde el 2026-10-01): de cada fichero C# que el banco compila, sus
líneas ejecutables según coverlet; de los que no (`Platforms/`), todas sus líneas de código como no
cubiertas (sin llaves, `using`, firmas ni atributos, que no se ejecutan). Queda sin cubrir el código
nativo: `AndroidPdfDocumentService` (101 líneas: `PdfRenderer`, mapas de bits, búsqueda de Android
15), `MainActivity` (60: barras del sistema, intents, selector de correo),
`WindowsPdfDocumentService` y la `App` de Windows (54), y algunos `catch` de las pantallas. No
llega al 90 % de la constitución (General §8.6); el plan está en el fichero de tareas de la app.

## 🚀 Instalación

### Requisitos
- Android 7.0 (API 24) o superior

### Desde código fuente

```bash
dotnet restore
dotnet build -c Debug
```

Ejecutar en un dispositivo o emulador conectado:

```bash
dotnet build -t:Run -f net10.0-android36.0
```

### Generar el APK de publicación

```bash
dotnet publish -c Release -f net10.0-android36.0
```

El paquete se genera en:

```
bin/Release/net10.0-android36.0/publish/com.socratic.pdfreader-Signed.apk
```

> El APK de Release se firma con la clave de depuración por defecto
> (`AndroidKeyStore=false`). Para publicar en Google Play hay que firmarlo con el keystore
> del proyecto y generar un AAB, siguiendo el flujo de la sección 7 de la constitución.

## 🛠️ Desarrollo

### Tecnologías
- **.NET 10.0** y **.NET MAUI**
- **C#**
- **Android SDK** (API 36 de compilación, mínima 24)

### Estructura del proyecto

```
PDFReader/
├── Models/                 # Entidades de datos (PdfDocumentEntry, DocumentListItem)
├── Pages/                  # Páginas de la aplicación (Library, Reader, About)
├── Services/               # Lógica de negocio e interfaces
├── Platforms/Android/      # Código nativo: PdfRenderer, MainActivity, manifiesto
├── Resources/              # Icono, splash y estilos
└── MauiProgram.cs          # Inyección de dependencias
```

### Arquitectura

- **Sin ViewModels**, por requisito del proyecto: la lógica de presentación vive en el
  code-behind de cada página y toda la lógica de negocio está en `Services/`.
  Es una desviación consciente de la sección 14 de la constitución, documentada en `CHANGELOG.md`.
- **Inyección de dependencias** para todos los servicios y páginas.
- **Código Android encapsulado** en `Platforms/Android`; las páginas no conocen APIs de Android
  salvo el intent de correo de "Acerca de", aislado tras `#if ANDROID`.

### Servicios

| Servicio | Responsabilidad |
|----------|-----------------|
| `IPdfDocumentService` | Abre PDF y rasteriza páginas (implementado con `PdfRenderer`) |
| `ILibraryService` | Biblioteca: importar, listar, última página, borrar |
| `ILocalizationService` | Textos en español e inglés y preferencia de idioma |
| `PendingDocumentQueue` | Documentos que llegan por intent antes de que la UI exista |

## 📖 Uso

1. **Abre un PDF** con el botón "Abrir un PDF" y elige el archivo en el selector del sistema.
2. **Lee**: pasa páginas con ◀ ▶, haz zoom con pellizco o con ＋ －, y toca el número de página
   para saltar a otra.
3. **Vuelve a la biblioteca**: el documento queda en la lista y reabre por la última página leída.

También puedes abrir un PDF desde un gestor de archivos o el correo con **"Abrir con → PDF Reader"**.

## 🐛 Solución de problemas

### El PDF no se abre
- Los PDF **protegidos con contraseña** no son compatibles: `PdfRenderer` no puede descifrarlos.
- Un archivo dañado o que no sea un PDF se rechaza al importarlo y no llega a la biblioteca.

### Un documento desapareció de la lista
La biblioteca descarta las entradas cuyo archivo ya no existe (por ejemplo, tras borrar los datos
de la aplicación). El archivo original de tu dispositivo sigue intacto: vuelve a abrirlo.

## 📋 Roadmap

- [ ] Búsqueda de texto dentro del documento (requiere extracción de texto; `PdfRenderer` no la ofrece)
- [ ] Vista continua de páginas con desplazamiento vertical
- [ ] Miniaturas para saltar de página
- [ ] Marcadores por documento
- [ ] Modo noche para el contenido del PDF

## 📄 Licencia

Este proyecto está licenciado bajo la Licencia MIT - ver el archivo [LICENSE](LICENSE) para más detalles.

## 🙏 Agradecimientos

- **Microsoft** por .NET MAUI
- **AOSP** por `PdfRenderer`, que hace posible un lector sin dependencias externas
