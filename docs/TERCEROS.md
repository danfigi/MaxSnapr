# Componentes de terceros

MaxSnapr no contiene código ni recursos de otros productos comerciales. Usa estos componentes de código abierto, cuyas licencias permiten redistribuirlos:

| Componente | Uso | Licencia | Origen |
|---|---|---|---|
| **PP-HumanSeg** (`human_segmentation_pphumanseg_2023mar.onnx`) | Separar a la persona del fondo en la cámara (desenfoque y fondos virtuales) | Apache 2.0. © PaddlePaddle Authors; distribuido por OpenCV Model Zoo. Texto completo en `src/MaxSnapr/Assets/Models/LICENSE-pphumanseg.txt` | https://huggingface.co/opencv/human_segmentation_pphumanseg |
| **ONNX Runtime** (`Microsoft.ML.OnnxRuntime`) | Ejecutar el modelo en la CPU | MIT. © Microsoft Corporation | https://github.com/microsoft/onnxruntime |
| **NAudio** (`NAudio.Wasapi`) | Audio del sistema y del micrófono | MIT. © Mark Heath | https://github.com/naudio/NAudio |
| **QRCoder** | Código QR para recibir desde el móvil | MIT. © Raffael Herrmann | https://github.com/codebude/QRCoder |

El modelo va dentro del ejecutable sin modificar (SHA-256 `552d8a984054e59b5d773d24b9b12022b22046ceb2bbc4c9aaeaceb36a9ddf24`). Los fondos incluidos (Estudio, Atardecer, Luces nocturnas, Menta y MaxSnapr) se dibujan por código y son originales.
