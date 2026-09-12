# 📕 PDF Editor

> Antes «PDF Reader». El paquete sigue siendo `com.socratic.pdfreader` (mismo id en Play, misma
> carpeta `Mobile/PDFReader`); solo cambia el nombre que ve el usuario.

Lector y editor de PDF para **Android y Windows** desarrollado en .NET MAUI (un solo proyecto para
las dos plataformas). Sin conexión, **sin ningún permiso** y con los documentos siempre en el
dispositivo.

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

### 🛠️ Herramientas
Cada herramienta crea un documento nuevo en la biblioteca; el original no se toca.
- **Fusionar** varios PDF en el orden elegido
- **Dividir** por rangos de páginas («1-3, 4-6») o una página por documento
- **Organizar páginas**: miniaturas para girar, reordenar, borrar o extraer páginas
- **Imágenes a PDF** (JPG, PNG, BMP; página A4 o del tamaño de la imagen) y **PDF a imágenes** (PNG en un ZIP)
- **Proteger con contraseña** y **quitar contraseña**
- **Numerar páginas** y **marca de agua** en diagonal
- **Guardar fuera de la app** desde el lector («Guardar como» en Windows, creador de documentos en Android)

### ✍️ Anotar y firmar
Desde el lector (icono de lápiz). Todo se guarda **aplanado** en un PDF nuevo, visible en cualquier
visor; el original no se toca.
- **Bolígrafo**, **subrayador**, **rectángulo**, **elipse**
- **Tapar**: un recuadro blanco sobre lo que quieras ocultar; con una casilla de texto encima es la
  forma de «corregir un dato» (ver por qué no se edita el texto original, más abajo)
- **Casillas de texto**: alineación izquierda/centro/derecha, texto **vertical**, cuatro tipografías
  (Open Sans, Lora serif, Roboto Mono, Caveat manuscrita), tamaño, negrita y color; se mueven,
  redimensionan y editan al tocarlas
- **Firma manuscrita**: se dibuja con el dedo o el ratón, se guarda y se reutiliza

### ¿Por qué no se edita el texto original?
Un PDF no guarda párrafos: guarda glifos colocados uno a uno en coordenadas absolutas, normalmente
con fuentes incrustadas en subconjunto (solo los caracteres que aparecen). Reescribir una frase
exige reconocer las líneas, tener la fuente completa, recalcular el ajuste del párrafo y reescribir
el flujo de contenido sin romper el resto: es lo que hacen Acrobat o Foxit con motores propietarios
enormes. Las librerías libres compatibles con la constitución no lo hacen; «tapar y reescribir»
cubre la mayoría de los casos reales.

## 🧱 Cómo se renderizan y escriben los PDF

El renderizado es el **nativo de cada plataforma**: `android.graphics.pdf.PdfRenderer` (API 21+)
en Android y `Windows.Data.Pdf` en Windows. Ninguna librería de terceros pinta las páginas.

Las herramientas que escriben PDF usan **[PDFsharp](https://github.com/empira/PDFsharp)** (MIT),
con la tipografía Open Sans que la app ya lleva para los textos que añade (numeración, marca de
agua). Todo es MIT o Apache 2.0 y se recoge en `THIRD-PARTY-NOTICES.md`.

Cada página se rasteriza bajo demanda al ancho que ocupa en pantalla y al nivel de zoom actual,
por lo que la memoria no crece con la longitud del documento.

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

También puedes abrir un PDF desde un gestor de archivos o el correo con **"Abrir con → PDF Editor"**.

## 🐛 Solución de problemas

### El PDF no se abre
- Los PDF **protegidos con contraseña** se abren pidiendo la contraseña (en Android, a partir de
  Android 15; en Windows, siempre). La herramienta «Quitar contraseña» crea una copia sin cifrar.
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
- **AOSP** por `PdfRenderer` y **Microsoft** por `Windows.Data.Pdf`: el lector no necesita ninguna librería de PDF
- **empira Software** por PDFsharp (MIT), con el que se escriben los PDF de las herramientas
