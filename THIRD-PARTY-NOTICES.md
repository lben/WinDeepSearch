# Third-party notices

WinDeepSearch is MIT licensed (see `LICENSE`). The installer also contains these components, each under its own
license:

| Component | License | Source |
|---|---|---|
| .NET runtime and WPF (including System.Numerics.Tensors), © .NET Foundation and Contributors | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| RapidOcrNet 4.2.0, © BobLd, based on RapidOCR by RapidAI | Apache-2.0 | https://github.com/BobLd/RapidOcrNet |
| PaddleOCR PP-OCRv5 models (bundled with RapidOcrNet), © PaddlePaddle Authors | Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR |
| ONNX Runtime 1.29.0, © Microsoft Corporation | MIT | https://github.com/microsoft/onnxruntime |
| SkiaSharp 3.119.1, © Microsoft Corporation; includes Skia, © Google LLC | MIT; Skia: BSD-3-Clause | https://github.com/mono/SkiaSharp, https://skia.org |
| Clipper2 2.0.0, © Angus Johnson | Boost Software License 1.0 | https://github.com/AngusJohnson/Clipper2 |
| PDFium build 157.0.8100 from pdfium-binaries, © The PDFium Authors | PDFium: BSD-3-Clause; build: Apache-2.0 | https://pdfium.googlesource.com/pdfium, https://github.com/bblanchon/pdfium-binaries |

The full license texts are at the links above. The test files in `tests/WinDeepSearch.Core.Tests/Legacy` come from
Apache POI (Apache-2.0, see the `NOTICE.txt` there); they are not part of the installer.
