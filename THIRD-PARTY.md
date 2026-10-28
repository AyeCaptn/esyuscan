# Upstream components

esyuscan's original code is MIT-licensed. Dependency licenses remain separate.

- **NAPS2 SDK, Images, Images.Gtk, and Internals 1.6.0:** LGPL-2.1-or-later,
  copyright NAPS2 contributors. Source and license:
  <https://github.com/cyanfish/naps2/tree/f914a4ec2af090aca4fa55eb3242ec91641482c3>.
  The SDK is used unmodified; its license notices are included in the local package.
- **NAPS2.PdfSharp 1.0.2:** MIT, the unmodified SDK's PdfSharpCore fork for
  PDF rotation, metadata/security verification and the upstream PDF exporter.
  <https://github.com/cyanfish/naps2-pdfsharp/tree/8d30f069e6cef9ee9b001372a94432de7f6a3146>.
- **libtiff (system supplied):** upstream TIFF license (BSD-style), loaded by
  `NAPS2.Images.Gtk` for single/multipage TIFF. esyuscan does not bundle a separate
  copy. <https://libtiff.gitlab.io/libtiff/misc.html#tiff-software-copyrights>.
- **ZXing.Net 0.16.11** (already bundled transitively by NAPS2): Apache-2.0.
  NAPS2 performs QR/Code 128 detection; tests generate sheets with its writer.
  <https://github.com/micjahn/ZXing.Net>. No new NuGet dependency is added.
- **CUPS and xdg-utils (optional, system supplied):** external desktop commands,
  not bundled. CUPS is Apache-2.0 with its upstream exceptions; xdg-utils is MIT.
  <https://github.com/OpenPrinting/cups>, <https://gitlab.freedesktop.org/xdg/xdg-utils>.
- **Avalonia:** MIT. <https://github.com/AvaloniaUI/Avalonia>.
- **NAPS2.Pdfium.Binaries 1.3.0:** Apache-2.0 packaging.
  <https://github.com/cyanfish/naps2-pdfium>. PDFium retains its upstream BSD-style
  and third-party licenses: <https://pdfium.googlesource.com/pdfium/+/refs/heads/main/LICENSE>.
- **HarfBuzzSharp and SkiaSharp:** MIT; their native components carry their own
  upstream licenses. <https://github.com/mono/SkiaSharp>.
- **Tesseract (optional, system/user supplied):** Apache-2.0, including upstream
  notices; <https://github.com/tesseract-ocr/tesseract/blob/main/LICENSE>.
  Its native dependencies (including Leptonica) retain their own licenses and are
  managed by the distribution or the user supplying the executable. esyuscan uses the
  unmodified SDK's subprocess OCR engine and does not bundle Tesseract binaries.
- **Tesseract language models (optional, system/user supplied):** official
  `tessdata_fast` / `tessdata_best` models are Apache-2.0; retain their license and
  notices when copying or redistributing data:
  <https://github.com/tesseract-ocr/tessdata_fast/blob/main/LICENSE>,
  <https://github.com/tesseract-ocr/tessdata_best/blob/main/LICENSE>.
  esyuscan does not download or bundle models. See [provisioning](docs/searchable-pdf.md).
- **Poppler (test tool only):** `pdftotext` verifies PDF text extraction in OCR
  integration tests; it is supplied by the developer's distribution and is not
  bundled or required by the app. <https://poppler.freedesktop.org/>.
- **SANE backends (optional, system supplied):** the SDK loads `libsane.so.1` for
  USB/SANE scanning. Backend and firmware licenses vary; distribution packages
  supply their notices. esyuscan does not bundle SANE or proprietary drivers/firmware.
  Upstream source and licensing: <https://gitlab.com/sane-project/backends>.
  See [runtime requirements](docs/scanners.md) for ARM64/x64 provisioning.
- **GTK, GdkPixbuf, and HarfBuzz:** system libraries, supplied by the distribution.

The NuGet lock files record the full dependency graph. Before redistributing a
release, include the applicable notices and source-availability information for
all bundled dependencies. The local PKGBUILD is not a public-release workflow.
