# MaxSnapr: guía de desarrollo

Documento interno: cómo compilar, probar y publicar. El README es para los usuarios.

Aplicación WPF para Windows (C#, .NET 8). Los iconos son glifos de la fuente del sistema *Segoe Fluent Icons*; la paleta, los diseños y el logotipo son propios. El logotipo se dibuja por código (`AppIcon.Render`): `MaxSnapr.exe --write-icon <ruta.ico>` regenera `src/MaxSnapr/Assets/MaxSnapr.ico` con cada tamaño de 16 a 256 px, y `--write-icon <ruta.png> [px]` lo guarda como PNG.

Las funciones implementadas y lo que queda fuera de alcance están en [ESTADO.md](ESTADO.md); los componentes de terceros, en [TERCEROS.md](TERCEROS.md).

## Compilar

Hace falta el SDK de .NET 8.

```bash
dotnet build src/MaxSnapr/MaxSnapr.csproj -c Debug
```

Ejecutable de un solo archivo con .NET incluido (el mismo de las Releases):

```bash
dotnet publish src/MaxSnapr/MaxSnapr.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o artifacts/publish
```

El instalador se genera con [Inno Setup 6](https://jrsoftware.org/isinfo.php) a partir de ese ejecutable:

```bash
iscc /DAppVersion=1.0.0 installer/MaxSnapr.iss
```

## Ofuscación

Las versiones publicadas se compilan con `-p:Obfuscate=true`, que pasa [Obfuscar](https://github.com/obfuscar/obfuscar) (MIT, fijado en `.config/dotnet-tools.json`; `dotnet tool restore` lo instala) sobre el ensamblado antes de empaquetarlo. La configuración está en `src/MaxSnapr/obfuscar.xml`. Renombra los tipos, métodos y campos privados e internos, y deja intacto lo que se busca por nombre:
- la API pública, que usan XAML y los enlaces de datos;
- las propiedades, porque los ajustes, la biblioteca y los `.snapr` son JSON con sus nombres;
- los nombres referenciados desde el XAML compilado.

Las llamadas a Windows se renombran, pero conservan su punto de entrada.

Antes de publicar, compruebe el ejecutable ofuscado con la autoprueba y que guarda los datos igual que la versión normal:

```bash
dotnet publish src/MaxSnapr/MaxSnapr.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Obfuscate=true -o artifacts/obf
tools/selftest.sh normal && tools/selftest.sh obf artifacts/obf/MaxSnapr.exe
python tools/compare_saved_data.py "%TEMP%\maxsnapr-tests\normal\home" "%TEMP%\maxsnapr-tests\obf\home"
```

El mapa de nombres (`obj/obfuscar-Release/out/Mapping.txt`) permite interpretar un error del ejecutable ofuscado. Está excluido de Git y en GitHub solo se guarda como artefacto privado de cada versión: no lo publique nunca.

## Aviso de actualizaciones

Al arrancar (como mucho una vez al día) la app consulta, de forma anónima, la última Release de [danfigi/MaxSnapr-descargas](https://github.com/danfigi/MaxSnapr-descargas), el repositorio público solo de descargas. Si es más nueva, muestra sus novedades y puede descargar e instalar el instalador. Solo lo acepta desde esa página por HTTPS y si su SHA-256 coincide con `SHA256SUMS.txt`. Se desactiva en *Preferencias* y también está en *Ayuda › Buscar actualizaciones* (`Services/Updates.cs`, `Views/UpdateWindow.cs`).

El texto de cada versión sale de su apartado `## X.Y.Z` en [NOVEDADES.md](../NOVEDADES.md): escríbalo antes de crear la etiqueta.

El flujo de publicación sube la versión al repositorio privado y a la página pública. Para la pública usa el secreto `DESCARGAS_TOKEN`: un token *fine-grained* limitado a `danfigi/MaxSnapr-descargas` con permiso **Contents: Read and write**. Si falta o caduca, el flujo falla con un aviso y la página pública no se actualiza.

## Publicar una versión

Actualice `<Version>` en `src/MaxSnapr/MaxSnapr.csproj`, añada el apartado de la versión a `NOVEDADES.md` y suba una etiqueta `vX.Y.Z`:

```bash
git tag -a v1.0.3 -m "MaxSnapr 1.0.3" && git push origin v1.0.3
```

El flujo [Publicar versión](../.github/workflows/release.yml) comprueba las dependencias, compila el ejecutable portátil y el instalador, calcula sus sumas SHA-256 y crea la Release. Lanzado a mano desde Actions, solo deja los archivos como artefactos para probarlos.

## Pruebas

La autoprueba cubre, entre otras cosas:
- la captura y las 20 herramientas, los efectos, el guardado sin pérdidas y el OCR;
- el vídeo (grabar, recortar, cortar, combinar, GIF) con sonido audible, la cámara, sus fondos y el modelo de segmentación;
- la captura con desplazamiento, la guía de pasos, las plantillas, la ocultación inteligente y la edición de texto;
- el servidor del móvil frente a peticiones sin token, todos los iconos y la resistencia del widget del borde.

Al final genera todas las ventanas como PNG. Use siempre una carpeta aislada para no tocar los ajustes, la biblioteca ni los atajos reales:

```bash
MAXSNAPR_HOME="C:\temp\maxsnapr-test\home" MAXSNAPR_NO_HOTKEYS=1 artifacts/publish/MaxSnapr.exe --selftest "C:\temp\maxsnapr-test\out"
```

`tools/selftest.sh <nombre> [exe]` compila el proyecto, ejecuta la prueba en `%TEMP%\maxsnapr-tests\<nombre>` y muestra los fallos. El código de salida es el número de fallos y el detalle queda en `out\report.txt`. La prueba nunca guarda imágenes del escritorio: el vídeo, la cámara y el selector se prueban con contenido sintético.

Diagnóstico sin guardar imagen ni sonido: `MaxSnapr.exe --probe-devices <archivo.txt>` comprueba cámaras y micrófonos y mide los fps con cada fondo. `--segment-test <imagen> <carpeta>` aplica los fondos a una foto de muestra.

## Dependencias

NuGet: `NAudio.Wasapi` (audio del sistema y del micrófono), `QRCoder` (código QR para el móvil) y `Microsoft.ML.OnnxRuntime` (fondos de la cámara, con el modelo PP-HumanSeg incluido). El resto usa las API de Windows: GDI, WIC, Media Foundation (`Windows.Media.Transcoding` y `Editing`), OCR, FaceDetector y UI Automation.

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/MaxSnapr/Capture` | Captura GDI multimonitor y por DPI, selector con sus modos, desplazamiento y unión, captura de pasos, servicio de captura |
| `src/MaxSnapr/Video` | Grabador (MediaStreamSource + MediaTranscoder), audio y cámara, fondos, barra de preparación, HUD, dibujar en pantalla, edición de vídeo, codificador GIF |
| `src/MaxSnapr/Editor/Model` | Documento (imagen base, objetos, deshacer/rehacer, `.snapr`), objetos, estilos, temas y plantillas |
| `src/MaxSnapr/Editor/Tools` | Herramientas de objetos y de píxeles |
| `src/MaxSnapr/Editor/Raster` | Operaciones de píxel, efectos, perspectiva, filtros |
| `src/MaxSnapr/Editor/Smart` | OCR con geometría, ocultación inteligente, editar texto, simplificar, plantillas |
| `src/MaxSnapr/Views` | Editor, propiedades, efectos, biblioteca, reproductor, ventanas de captura, crear, lotes, temas, preferencias, móvil, widget del borde, apoyo |
| `src/MaxSnapr/Services` | Ajustes, portapapeles, exportación, compartir (Outlook, Office, Slack, FTP, HTTP, carpetas sincronizadas), atajos, diagnóstico |
| `installer/` | Script de Inno Setup |
| `tools/` | Autoprueba, banner de presentación y utilidades |
| `docs/` | Estado, componentes de terceros, imágenes del README y esta guía |

Los documentos `.snapr` son un ZIP con la imagen original (`base.png`), los objetos editables (`document.json`) y una vista previa.
