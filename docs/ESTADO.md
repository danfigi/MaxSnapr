# Estado de la implementación

MaxSnapr cubre el conjunto de funciones habitual de un capturador profesional. Es una implementación propia: no reutiliza código ni recursos de terceros, y el nombre, los iconos (Segoe Fluent, del sistema), la paleta y los diseños son propios. Los componentes de código abierto que usa están en [TERCEROS.md](TERCEROS.md).

La autoprueba (`MaxSnapr.exe --selftest`) verifica 63 comprobaciones y genera capturas de todas las ventanas. Entre ellas: que todos los iconos tengan un símbolo existente en la fuente, que el vídeo lleve sonido audible (con un tono sintético), los fondos de la cámara, el modelo de segmentación, que la grabación siga a la cámara cuando se mueve y que el servidor del móvil rechace todo lo que no lleve su token.

## Fase 1: MVP ✔
Proceso residente en el área de notificación, atajos globales, captura (región, ventana, pantalla), editor con herramientas básicas, bandeja, biblioteca, exportación PNG/JPG/BMP/GIF/TIFF/PDF, preferencias, temas claro y oscuro.

## Fase 2: captura y editor avanzados ✔
- **Modos de captura**
  - Región, ventana, objeto de la interfaz (UI Automation), pantalla completa, todas las pantallas y ventana activa.
  - Región de tamaño fijo, ventana a un tamaño concreto, mano alzada, varias áreas y menú desplegable con submenús.
  - Desde el portapapeles y desde archivo.
- **Captura con desplazamiento** automática (rueda simulada) y panorámica manual vertical u horizontal. Une los fotogramas y conserva una sola vez la cabecera y el pie fijos.
- **Programación:** retraso de hasta 30 s, capturas a intervalos (cada N segundos, M veces) y repetición de la última captura.
- **Al capturar:** efectos (borde, sombra, borde rasgado), carpeta de salida con nombre automático, cursor, portapapeles y editor.
- **Preajustes** con atajo propio, importación y exportación.
- **Selector y avisos:** lupa de píxeles con color, cuentagotas de pantalla y widget anclado al borde superior.
- **Herramientas:** recortar con proporciones, recortar franja (unión recta, en sierra u ondulada) y sello (iconos, cursores y etiquetas propias). También relleno, lupa, foco, borrador, mover y favoritos.
- **Selección de píxeles:** rectángulo, elipse o mano alzada. Admite mover, copiar, borrar y recortar, con relleno inteligente del hueco.
- **Objetos:** flechas curvas, pincel con edición de puntos, agrupar, alinear, voltear, organizar, compactar la selección o todo, y seleccionar por tipo.
- **Edición:** menú contextual, cuadrícula de píxeles, corrector ortográfico del sistema y barra de herramientas configurable.
- **Estilos:** estilos rápidos propios, favoritos, temas de color (crear, editar, importar y exportar) y sombra configurable (distancia, ángulo, desenfoque, opacidad).
- **Efectos**
  - Borde y sombra.
  - Bordes rasgados, serrados, dentados, ondulados, fundidos y en bisel.
  - Perspectiva y esquina doblada.
  - Filtros: grises, invertir, sepia, posterizar, alto contraste, monocromo, suavizar, enfocar, viñeta y pixelar.
  - Brillo, contraste, saturación, matiz y gamma; sustitución o intercambio de colores.
  - Marca de agua de texto o con logotipo en nueve posiciones.
  - Información de captura (fecha, aplicación, ventana, sitio web, sistema) y PPP de exportación.
- **Imagen:** tamaño de imagen, tamaño del lienzo (márgenes o tamaño total con anclaje en 9 direcciones), color del lienzo, giro libre, recorte automático de bordes y ajuste del lienzo a los objetos.
- **Biblioteca**
  - Filtros por fecha, tipo (imagen, vídeo, GIF), bandeja, fijadas, aplicación, sitio web y etiquetas.
  - Búsqueda también en el texto reconocido por OCR en segundo plano.
  - Detalles (Alt+Intro), duplicar, importar, copia de seguridad y restauración.
- **Formatos y salidas:** PDF de varias páginas e ICO. Destinos: carpeta con patrones de nombre (`{nombre} {fecha} {hora} {app} {ventana} {ancho} {alto} {n}…`), programas propios, FTP/FTPS e impresión (normal, rápida, a tamaño real y horizontal).
- **Entorno:** supervisión opcional del portapapeles, inicio con Windows, bienvenida y consejos, diagnóstico y restablecer preferencias.

## Fase 3: vídeo ✔
- **Barra de preparación:** aparece tras elegir la zona y antes de grabar. Tiene interruptores de cámara, micrófono y audio del sistema, con su dispositivo y medidor de nivel en vivo; la cámara se configura en forma, esquina y tamaño, con vista previa en su sitio. Las opciones incluyen fps, cursor, clics, cuenta atrás, GIF y editor. `Intro` graba y `Esc` cancela.
- **Grabación:** pantalla a MP4 H.264 con el codificador de Windows, a 15, 24, 30 o 60 fps, de una región, ventana o pantalla.
- **Audio:** del sistema (WASAPI loopback) y del micrófono, mezclados.
- **Cámara web:** imagen dentro de imagen (círculo, redondeada o rectángulo) y pantalla completa con Ctrl+Mayús+F. La propia imagen se arrastra a cualquier posición y se redimensiona con el tirador o la rueda, antes y durante la grabación; el vídeo sigue cada cambio. Con el clic derecho se cambian la cámara, el fondo, la forma, el tamaño y la posición.
- **Fondo de la cámara:** desenfoque suave o intenso, cinco fondos propios dibujados por código o una imagen del usuario. Se separa a la persona con el modelo PP-HumanSeg (Apache 2.0) en ONNX Runtime, y a 720p se mantienen 30 fps. Ver [TERCEROS.md](TERCEROS.md).
- **Cursor y control:** cursor y animación de clics, cuenta atrás, pausa y reanudar (Mayús+F9, o Ctrl+Mayús+F9 si está ocupado), detener (Mayús+F10 o Ctrl+Mayús+F10), reiniciar y cancelar. Durante la grabación se puede silenciar el micrófono y mostrar u ocultar la cámara. El HUD y el marco no aparecen en el vídeo.
- **Durante la grabación:** foto fija y dibujar en pantalla (flechas, rectángulos, pasos y pluma que se desvanecen).
- **Editor de vídeo:** reproducir, fotograma a fotograma, inicio y fin, silencio; recortar, quitar un tramo, guardar la selección como vídeo nuevo, guardar fotograma, combinar vídeos y exportar.
- **GIF:** codificador propio con paleta por fotograma y fotogramas incrementales. Perfiles pequeño, mediano, grande y personalizado. Se puede grabar directamente como GIF y recortar o quitar tramos de un GIF.
- **Captura de pasos:** cada clic captura la ventana y describe el elemento («Haga clic en «Guardar» (botón)»). Lo que se escribe se convierte en un paso, salvo en los campos de contraseña. El resultado es una guía editable con números, textos y marcas.

## Fase 4: productividad e inteligencia ✔
- **OCR:** extraer texto de la pantalla, de la captura o de una selección.
- **Ocultación inteligente:** correos, teléfonos, IBAN, tarjetas (con Luhn), DNI/NIE, IP, URL, cifras, caras (detector de Windows) y palabras propias; pixelar, desenfocar o tapar con objetos editables.
- **Editar texto de la imagen:** OCR, borrado del fondo y texto editable con su color y tamaño.
- **Simplificar:** barras redondeadas que sustituyen o cubren el texto.
- **Crear:** 12 plantillas propias, apilar, en fila y cuadrícula. Se pueden guardar documentos como plantilla e importar o exportar plantillas. También vídeo a partir de imágenes y exportación por lotes (formato, escala, ancho máximo, efectos, patrón de nombre, PDF).
- **Compartir**
  - Outlook (adjunto), MAPI o `mailto`.
  - Word, PowerPoint y Excel (COM).
  - Slack (API, con enlace), Teams (pegar), carpetas sincronizadas (OneDrive personal y de empresa, Dropbox, Google Drive, Box) y servidor HTTP propio con copia del enlace.
  - Historial de envíos. Las credenciales se cifran con DPAPI.

## Fase 5: ecosistema ✔ (parcial, ver abajo)
Widget en el borde (siempre visible: un vigilante lo devuelve a su sitio si Windows lo oculta, lo mueve, lo tapa o cambia de pantallas o de escritorio virtual; no roba el foco ni aparece en Alt+Tab), recepción desde el móvil (página web local con código QR y token), asociación de `.snapr`, «Editar con MaxSnapr» en el Explorador, bienvenida, diagnóstico y restablecer preferencias.

## Fuera de alcance, con su motivo
- **Servicios en la nube propios de otros fabricantes** (enlaces compartidos, cuentas, bibliotecas compartidas, tiendas de recursos): requieren su infraestructura.
- **OneNote, SharePoint, YouTube, Evernote y Box, Dropbox o Drive por API:** exigen registrar una aplicación OAuth con cada proveedor (identificador de cliente propio). Se cubren con las carpetas sincronizadas, Teams o Slack.
- **Eliminación de ruido del micrófono:** necesita un modelo de IA de audio con licencia compatible, que no está en Windows.
- **Recuperación de vídeos tras un cierre inesperado:** el MP4 solo es válido cuando el codificador lo finaliza.
- **Documentos de varias páginas** dentro del editor: se cubre con plantillas, la bandeja y el PDF de varias páginas.
- **Impresora virtual de captura y extensión de miniaturas del Explorador:** requieren controladores o código nativo instalado como administrador.
- **Actualizador automático, telemetría y funciones experimentales:** no hay servidor de publicación, y por diseño no se envían datos.
- **Captura HDR e idioma de la interfaz distinto del español.**
- **Formatos propietarios de otros capturadores:** MaxSnapr usa su propio `.snapr` abierto (ZIP + JSON + PNG).
