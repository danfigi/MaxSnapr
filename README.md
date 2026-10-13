# MaxSnapr

Aplicación de captura de pantalla, anotación y grabación de vídeo para Windows (C# / WPF, .NET 8). Es una aplicación **original** con el conjunto de funciones de un capturador profesional. No contiene código, iconos, sellos, plantillas ni otros recursos de terceros. Los iconos son glifos de la fuente del sistema *Segoe Fluent Icons* y la paleta y los diseños son propios.

El nombre y el logotipo, un visor de captura con una huella, son un homenaje a Max. El icono se dibuja por código (`AppIcon.Render`). `MaxSnapr.exe --write-icon <ruta.ico>` regenera `src/MaxSnapr/Assets/MaxSnapr.ico` con cada tamaño, de 16 a 256 px, dibujado de forma nativa.

Las funciones implementadas, y lo que queda fuera de alcance con su motivo, están en [docs/ESTADO.md](docs/ESTADO.md).

## Uso rápido

Compile el ejecutable (ver [Compilar](#compilar)) y ábralo:

```bash
dist/MaxSnapr.exe
```

| Acción | Atajo |
|---|---|
| Capturar (en cualquier aplicación) | `Impr Pant`; si está ocupado, `Ctrl+Mayús+C` |
| Repetir la última captura | `Ctrl+Mayús+R` |
| Extraer texto de la pantalla | `Ctrl+Mayús+T` |
| Grabar vídeo | `Ctrl+Mayús+V`. Tras elegir la zona aparece la barra de preparación (cámara, micrófono, audio del sistema y opciones; `Intro` graba). Durante la grabación, `Mayús+F9` pausa y `Mayús+F10` detiene; si están ocupados, `Ctrl+Mayús+F9` y `Ctrl+Mayús+F10` |
| Captura de pasos (iniciar / detener) | `Ctrl+Mayús+P` |
| Todos los atajos del editor | `F1` |

- **Selector:** arrastre para una región, haga clic para una ventana o pulse `Intro`/`F` para la pantalla completa. La barra flotante permite capturar, copiar, extraer texto, grabar vídeo, capturar con desplazamiento o hacer una panorámica.
- **Ventana de captura:** clic en el icono del área de notificación. Ofrece todos los modos, el vídeo con audio y cámara, el retraso, la repetición automática, los efectos y los preajustes.
- **Biblioteca:** cada captura y grabación se guarda automáticamente en `Documentos\MaxSnapr\Biblioteca`. Las imágenes son `.snapr` (ZIP con la imagen original, los objetos editables en JSON y una vista previa) y los vídeos, `.mp4` o `.gif`.

Requisitos: Windows 10 19041 o posterior y el *.NET 8 Desktop Runtime*, ya instalado en este equipo. Para el OCR hace falta un idioma de Windows con reconocimiento de texto (el español lo incluye).

## Compilar

```bash
dotnet build src/MaxSnapr/MaxSnapr.csproj -c Debug
```

```bash
dotnet publish src/MaxSnapr/MaxSnapr.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Dependencias NuGet: `NAudio.Wasapi` (audio del sistema y del micrófono), `QRCoder` (código QR para el móvil) y `Microsoft.ML.OnnxRuntime` (fondos de la cámara con el modelo PP-HumanSeg incluido). Licencias en [docs/TERCEROS.md](docs/TERCEROS.md). El resto usa las API de Windows: GDI, WIC, Media Foundation (`Windows.Media.Transcoding`/`Editing`), OCR, FaceDetector y UI Automation.

## Pruebas

La autoprueba cubre la captura, las 20 herramientas, los efectos, el guardado sin pérdidas y el OCR. También el vídeo (grabar, recortar, cortar, combinar, GIF), la captura con desplazamiento, la guía de pasos, las plantillas, la ocultación inteligente y la edición de texto. Al final genera todas las ventanas como PNG. Use siempre una carpeta aislada para no tocar los ajustes, la biblioteca ni los atajos reales:

```bash
MAXSNAPR_HOME="C:\temp\maxsnapr-test\home" MAXSNAPR_NO_HOTKEYS=1 dist/MaxSnapr.exe --selftest "C:\temp\maxsnapr-test\out"
```

`tools/selftest.sh <nombre> [exe]` compila el proyecto, ejecuta la prueba en `C:\temp\maxsnapr-tests\<nombre>` y muestra los fallos.

El código de salida es el número de fallos. El detalle queda en `out\report.txt`. La prueba nunca guarda imágenes de su escritorio: el vídeo y el selector se prueban con contenido sintético.

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/MaxSnapr/Capture` | Captura GDI multimonitor y por DPI, selector con sus modos, desplazamiento y unión, captura de pasos, servicio de captura |
| `src/MaxSnapr/Video` | Grabador (MediaStreamSource + MediaTranscoder), audio y cámara, HUD, dibujar en pantalla, edición de vídeo, codificador GIF |
| `src/MaxSnapr/Editor/Model` | Documento (imagen base, objetos, deshacer/rehacer, `.snapr`), objetos, estilos, temas y plantillas |
| `src/MaxSnapr/Editor/Tools` | Herramientas de objetos y de píxeles |
| `src/MaxSnapr/Editor/Raster` | Operaciones de píxel, efectos, perspectiva, filtros |
| `src/MaxSnapr/Editor/Smart` | OCR con geometría, ocultación inteligente, editar texto, simplificar, plantillas |
| `src/MaxSnapr/Views` | Editor, propiedades, efectos, biblioteca, reproductor, ventanas de captura, crear, lotes, temas, preferencias, móvil |
| `src/MaxSnapr/Services` | Ajustes, portapapeles, exportación, compartir (Outlook, Office, Slack, FTP, HTTP, carpetas sincronizadas), atajos, diagnóstico |
| `docs/` | Inventario de funciones de la referencia y estado de la implementación |
